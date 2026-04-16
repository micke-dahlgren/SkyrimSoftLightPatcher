using System.IO.Compression;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;
using SkyrimLightingPatcher.NiflyAdapter.Reflection;

namespace SkyrimLightingPatcher.Tests;

public sealed class BsaIntegrationTests
{
    private static readonly string TestDataRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "TestData");

    [Fact]
    public async Task ArchiveOnlyScan_FindsPatchableEyeShape()
    {
        var rootPath = CreateTempDirectory("bsa-scan");
        var appHome = CreateTempDirectory("bsa-scan-home");
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);

        var archivePath = Path.Combine(rootPath, "Skyrim - Meshes0.bsa");
        await TestBsaArchiveBuilder.CreateFromFilesAsync(
            archivePath,
            (@"meshes\actors\character\eyes\eye_example.nif", Path.Combine(TestDataRoot, "eye_example.nif")));

        var meshService = new ReflectionNifMeshService();
        var scanService = new ScanService(meshService, new ShapeClassifier(), new ScanFileResolver());

        var report = await scanService.ScanAsync(new ScanRequest(rootPath, new PatchSettings(0.82f, 0.15f)));

        var file = Assert.Single(report.Files);
        Assert.False(file.HasError, file.ErrorMessage);
        Assert.Equal(MeshSourceKind.Archive, file.Source?.Kind);
        Assert.True(report.PatchableEyeShapes > 0, DescribeReport(report));
        Assert.Contains("Skyrim - Meshes0.bsa", file.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VortexStagingScan_IncludesArchiveOnlyMeshesFromDeployedDataRoot()
    {
        var rootPath = CreateTempDirectory("vortex-bsa-scan");
        var deploymentDataRoot = CreateTempDirectory("vortex-bsa-data");
        var appHome = CreateTempDirectory("vortex-bsa-home");
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);

        await File.WriteAllTextAsync(Path.Combine(rootPath, "__vortex_staging_folder"), "marker");

        var archivePath = Path.Combine(deploymentDataRoot, "Skyrim - Meshes0.bsa");
        await TestBsaArchiveBuilder.CreateFromFilesAsync(
            archivePath,
            (@"meshes\actors\character\eyes\eye_example.nif", Path.Combine(TestDataRoot, "eye_example.nif")));

        await File.WriteAllBytesAsync(
            Path.Combine(rootPath, "vortex.deployment.msgpack"),
            EncodeMessagePack(new Dictionary<string, object?>
            {
                ["stagingPath"] = rootPath,
                ["files"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["relPath"] = "activeeyes.esp",
                        ["source"] = "Active Eye Mod",
                        ["target"] = Path.Combine(deploymentDataRoot, "activeeyes.esp"),
                        ["time"] = 1L,
                    },
                },
            }));

        var meshService = new ReflectionNifMeshService();
        var scanService = new ScanService(meshService, new ShapeClassifier(), new ScanFileResolver());

        var report = await scanService.ScanAsync(new ScanRequest(rootPath, new PatchSettings(0.82f, 0.15f)));

        var file = Assert.Single(report.Files);
        Assert.False(file.HasError, file.ErrorMessage);
        Assert.Equal(MeshSourceKind.Archive, file.Source?.Kind);
        Assert.Equal(archivePath, file.Source?.ArchivePath);
        Assert.True(report.PatchableEyeShapes > 0, DescribeReport(report));
    }

    [Fact]
    public async Task PatchExecutor_ArchiveBackedMesh_WritesLooseOverrideToGeneratedArchive()
    {
        var rootPath = CreateTempDirectory("bsa-patch");
        var appHome = CreateTempDirectory("bsa-patch-home");
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);

        var archivePath = Path.Combine(rootPath, "Skyrim - Meshes0.bsa");
        await TestBsaArchiveBuilder.CreateFromFilesAsync(
            archivePath,
            (@"meshes\actors\character\eyes\eye_example.nif", Path.Combine(TestDataRoot, "eye_example.nif")));

        var meshService = new ReflectionNifMeshService();
        var scanService = new ScanService(meshService, new ShapeClassifier(), new ScanFileResolver());
        var patchExecutor = new PatchExecutor(new PatchPlanner(), meshService, new ScanFileResolver(), new BackupStore());
        var settings = new PatchSettings(0.78f, 0.15f);
        var outputArchivePath = Path.Combine(rootPath, "LightingEffect1 Mesh Patcher Output.zip");

        var report = await scanService.ScanAsync(new ScanRequest(rootPath, settings));
        Assert.True(report.PatchableEyeShapes > 0, DescribeReport(report));

        var manifest = await patchExecutor.ExecuteAsync(report, outputArchivePath);

        var patchedFile = Assert.Single(manifest.Files);
        Assert.Equal("Patched", patchedFile.Status);
        Assert.True(File.Exists(manifest.OutputArchivePath));
        Assert.True(File.Exists(patchedFile.OutputPath));
        Assert.Empty(Directory.EnumerateFiles(rootPath, "*.nif", SearchOption.AllDirectories));

        using var archive = ZipFile.OpenRead(manifest.OutputArchivePath);
        var zipEntry = Assert.Single(archive.Entries.Where(static entry =>
            entry.FullName.Replace('/', '\\').Equals(@"meshes\actors\character\eyes\eye_example.nif", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(zipEntry);

        var extractedRoot = Path.Combine(rootPath, "extract-bsa-patch");
        ZipFile.ExtractToDirectory(manifest.OutputArchivePath, extractedRoot);
        var rescanned = await scanService.ScanAsync(new ScanRequest(extractedRoot, settings));
        Assert.Equal(0, rescanned.PatchableShapes);
        Assert.Contains(
            rescanned.Files.SelectMany(static file => file.Shapes),
            static shape => shape.Kind == ShapeKind.Eye &&
                            !shape.IsPatchCandidate &&
                            shape.TargetValue.HasValue &&
                            Math.Abs(shape.Probe.LightingEffect1 - shape.TargetValue.Value) <= 0.0001f);
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string DescribeReport(ScanReport report)
    {
        return string.Join(
            Environment.NewLine,
            report.Files.Select(file =>
            {
                if (file.HasError)
                {
                    return $"{file.FilePath} ERROR: {file.ErrorMessage}";
                }

                return $"{file.FilePath} :: " + string.Join(
                    " | ",
                    file.Shapes.Select(shape =>
                        $"{shape.Probe.ShapeName} kind={shape.Kind} patch={shape.IsPatchCandidate} old={shape.Probe.LightingEffect1:0.###} target={(shape.TargetValue.HasValue ? shape.TargetValue.Value.ToString("0.###") : "-")}"));
            }));
    }

    private static byte[] EncodeMessagePack(object? value)
    {
        using var stream = new MemoryStream();
        WriteValue(stream, value);
        return stream.ToArray();
    }

    private static void WriteValue(Stream stream, object? value)
    {
        switch (value)
        {
            case null:
                stream.WriteByte(0xc0);
                return;
            case bool boolValue:
                stream.WriteByte(boolValue ? (byte)0xc3 : (byte)0xc2);
                return;
            case byte byteValue:
                WriteUnsigned(stream, byteValue);
                return;
            case short shortValue:
                WriteSigned(stream, shortValue);
                return;
            case int intValue:
                WriteSigned(stream, intValue);
                return;
            case long longValue:
                WriteSigned(stream, longValue);
                return;
            case string stringValue:
                WriteString(stream, stringValue);
                return;
            case Dictionary<string, object?> dictionary:
                WriteMap(stream, dictionary);
                return;
            case object?[] array:
                WriteArray(stream, array);
                return;
            default:
                throw new InvalidOperationException($"Unsupported MessagePack test value: {value.GetType().FullName}");
        }
    }

    private static void WriteMap(Stream stream, Dictionary<string, object?> dictionary)
    {
        WriteCollectionHeader(stream, dictionary.Count, 0x80, 0xde);
        foreach (var (key, value) in dictionary)
        {
            WriteString(stream, key);
            WriteValue(stream, value);
        }
    }

    private static void WriteArray(Stream stream, object?[] array)
    {
        WriteCollectionHeader(stream, array.Length, 0x90, 0xdc);
        foreach (var item in array)
        {
            WriteValue(stream, item);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (bytes.Length < 32)
        {
            stream.WriteByte((byte)(0xa0 | bytes.Length));
        }
        else if (bytes.Length <= byte.MaxValue)
        {
            stream.WriteByte(0xd9);
            stream.WriteByte((byte)bytes.Length);
        }
        else
        {
            stream.WriteByte(0xda);
            WriteBigEndian(stream, (ushort)bytes.Length);
        }

        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteUnsigned(Stream stream, byte value)
    {
        if (value <= 0x7f)
        {
            stream.WriteByte(value);
            return;
        }

        stream.WriteByte(0xcc);
        stream.WriteByte(value);
    }

    private static void WriteSigned(Stream stream, long value)
    {
        if (value >= 0 && value <= 0x7f)
        {
            stream.WriteByte((byte)value);
            return;
        }

        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
        {
            stream.WriteByte(0xd0);
            stream.WriteByte(unchecked((byte)(sbyte)value));
            return;
        }

        if (value >= short.MinValue && value <= short.MaxValue)
        {
            stream.WriteByte(0xd1);
            WriteBigEndian(stream, unchecked((ushort)(short)value));
            return;
        }

        if (value >= int.MinValue && value <= int.MaxValue)
        {
            stream.WriteByte(0xd2);
            WriteBigEndian(stream, unchecked((uint)(int)value));
            return;
        }

        stream.WriteByte(0xd3);
        WriteBigEndian(stream, unchecked((ulong)value));
    }

    private static void WriteCollectionHeader(Stream stream, int count, byte fixBase, byte extendedPrefix)
    {
        if (count < 16)
        {
            stream.WriteByte((byte)(fixBase | count));
            return;
        }

        stream.WriteByte(extendedPrefix);
        WriteBigEndian(stream, (ushort)count);
    }

    private static void WriteBigEndian(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBigEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteBigEndian(Stream stream, ulong value)
    {
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            stream.WriteByte((byte)(value >> shift));
        }
    }
}
