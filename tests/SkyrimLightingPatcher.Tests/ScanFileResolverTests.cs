using System.Text;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class ScanFileResolverTests
{
    private static readonly string TestDataRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "TestData");

    [Fact]
    public async Task ResolveFilePathsAsync_VortexStagingFolder_OnlyReturnsDeployedMeshWinners()
    {
        var rootPath = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(rootPath, "__vortex_staging_folder"), "marker");

        var activeMesh = Path.Combine(rootPath, "Active Eye Mod", "meshes", "actors", "character", "eyes", "blue.nif");
        var inactiveMesh = Path.Combine(rootPath, "Inactive Eye Mod", "meshes", "actors", "character", "eyes", "green.nif");
        var losingMesh = Path.Combine(rootPath, "Active Eye Mod", "meshes", "actors", "character", "eyes", "unused.nif");

        Directory.CreateDirectory(Path.GetDirectoryName(activeMesh)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inactiveMesh)!);
        await File.WriteAllTextAsync(activeMesh, "active");
        await File.WriteAllTextAsync(inactiveMesh, "inactive");
        await File.WriteAllTextAsync(losingMesh, "losing");

        var payload = new Dictionary<string, object?>
        {
            ["stagingPath"] = rootPath,
            ["files"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["relPath"] = @"meshes\actors\character\eyes\blue.nif",
                    ["source"] = "Active Eye Mod",
                    ["target"] = string.Empty,
                    ["time"] = 1L,
                },
                new Dictionary<string, object?>
                {
                    ["relPath"] = "example.esp",
                    ["source"] = "Active Eye Mod",
                    ["target"] = string.Empty,
                    ["time"] = 2L,
                },
            },
        };

        await File.WriteAllBytesAsync(Path.Combine(rootPath, "vortex.deployment.msgpack"), EncodeMessagePack(payload));

        var resolver = new ScanFileResolver();

        var files = await resolver.ResolveFilePathsAsync(rootPath);

        var file = Assert.Single(files);
        Assert.Equal(MeshSourceKind.Loose, file.Kind);
        Assert.Equal(activeMesh, file.LocalPath);
        Assert.Equal("Active Eye Mod", file.SourceModName);
    }

    [Fact]
    public async Task ResolveFilePathsAsync_NonVortexFolder_FallsBackToRecursiveEnumeration()
    {
        var rootPath = CreateTempDirectory();
        var firstMesh = Path.Combine(rootPath, "meshes", "first.nif");
        var secondMesh = Path.Combine(rootPath, "nested", "meshes", "second.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(firstMesh)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondMesh)!);
        await File.WriteAllTextAsync(firstMesh, "one");
        await File.WriteAllTextAsync(secondMesh, "two");

        var resolver = new ScanFileResolver();

        var files = await resolver.ResolveFilePathsAsync(rootPath);

        Assert.Equal(
            new[] { firstMesh, secondMesh }.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            files.Select(static file => file.LocalPath).ToArray());
    }

    [Fact]
    public async Task ResolveFilePathsAsync_VortexFallback_IncludesLooseMeshesWhenNoDeploymentManifestExists()
    {
        var rootPath = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(rootPath, "__vortex_staging_folder"), "marker");

        var sourceMesh = Path.Combine(rootPath, "Active Eye Mod", "meshes", "eyes", "blue.nif");
        var generatedMesh = Path.Combine(rootPath, "LightingEffect1 Mesh Patcher Output", "meshes", "eyes", "green.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMesh)!);
        Directory.CreateDirectory(Path.GetDirectoryName(generatedMesh)!);
        await File.WriteAllTextAsync(sourceMesh, "source");
        await File.WriteAllTextAsync(generatedMesh, "generated");

        var resolver = new ScanFileResolver();

        var files = await resolver.ResolveFilePathsAsync(rootPath);

        Assert.Equal(
            new[] { generatedMesh, sourceMesh }.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            files.Select(static file => file.LocalPath).ToArray());
    }

    [Fact]
    public async Task ResolveFilePathsAsync_LooseFileOverridesArchiveEntryWithSameOutputPath()
    {
        var rootPath = CreateTempDirectory();
        var looseMesh = Path.Combine(rootPath, "meshes", "actors", "character", "eyes", "shared.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(looseMesh)!);
        await File.WriteAllTextAsync(looseMesh, "loose");

        var archivePath = Path.Combine(rootPath, "Skyrim - Meshes0.bsa");
        await TestBsaArchiveBuilder.CreateFromFilesAsync(
            archivePath,
            (@"meshes\actors\character\eyes\shared.nif", Path.Combine(TestDataRoot, "eye_example.nif")));

        var resolver = new ScanFileResolver();

        var files = await resolver.ResolveFilePathsAsync(rootPath);

        var file = Assert.Single(files);
        Assert.Equal(MeshSourceKind.Loose, file.Kind);
        Assert.Equal(looseMesh, file.LocalPath);
        Assert.Equal(looseMesh, await resolver.MaterializeSourceAsync(file));
    }

    [Fact]
    public async Task ResolveFilePathsAsync_VortexStagingFolder_ArchiveSourcesAreLimitedToActiveDeploymentMods()
    {
        var rootPath = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(rootPath, "__vortex_staging_folder"), "marker");

        var activeArchive = Path.Combine(rootPath, "Active Eye Mod", "activeeyes.bsa");
        var inactiveArchive = Path.Combine(rootPath, "Inactive Eye Mod", "inactiveeyes.bsa");
        Directory.CreateDirectory(Path.GetDirectoryName(activeArchive)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inactiveArchive)!);

        await TestBsaArchiveBuilder.CreateFromFilesAsync(
            activeArchive,
            (@"meshes\actors\character\eyes\blue.nif", Path.Combine(TestDataRoot, "eye_example.nif")));
        await TestBsaArchiveBuilder.CreateFromFilesAsync(
            inactiveArchive,
            (@"meshes\actors\character\eyes\green.nif", Path.Combine(TestDataRoot, "eye_example.nif")));

        var payload = new Dictionary<string, object?>
        {
            ["stagingPath"] = rootPath,
            ["files"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["relPath"] = "activeeyes.esp",
                    ["source"] = "Active Eye Mod",
                    ["target"] = string.Empty,
                    ["time"] = 1L,
                },
            },
        };

        await File.WriteAllBytesAsync(Path.Combine(rootPath, "vortex.deployment.msgpack"), EncodeMessagePack(payload));

        var resolver = new ScanFileResolver();

        var files = await resolver.ResolveFilePathsAsync(rootPath);

        var file = Assert.Single(files);
        Assert.Equal(MeshSourceKind.Archive, file.Kind);
        Assert.Equal("Active Eye Mod", file.SourceModName);
        Assert.Contains("activeeyes.bsa", file.DisplayPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveFilePathsAsync_VortexStagingFolder_IncludesArchivesFromDeployedDataRoot()
    {
        var rootPath = CreateTempDirectory();
        var deploymentDataRoot = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(rootPath, "__vortex_staging_folder"), "marker");

        var archivePath = Path.Combine(deploymentDataRoot, "Skyrim - Meshes0.bsa");
        await TestBsaArchiveBuilder.CreateFromFilesAsync(
            archivePath,
            (@"meshes\actors\character\eyes\blue.nif", Path.Combine(TestDataRoot, "eye_example.nif")));

        var payload = new Dictionary<string, object?>
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
        };

        await File.WriteAllBytesAsync(Path.Combine(rootPath, "vortex.deployment.msgpack"), EncodeMessagePack(payload));

        var resolver = new ScanFileResolver();

        var files = await resolver.ResolveFilePathsAsync(rootPath);

        var file = Assert.Single(files);
        Assert.Equal(MeshSourceKind.Archive, file.Kind);
        Assert.Null(file.SourceModName);
        Assert.Equal(archivePath, file.ArchivePath);
        Assert.Contains(Path.GetFileName(deploymentDataRoot), file.DisplayPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "skyrim-lighting-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
        var bytes = Encoding.UTF8.GetBytes(value);
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
