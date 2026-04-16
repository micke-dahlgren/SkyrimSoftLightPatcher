using System.IO.Compression;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;
using SkyrimLightingPatcher.NiflyAdapter.Reflection;

namespace SkyrimLightingPatcher.Tests;

public sealed class SampleNifIntegrationTests
{
    private static readonly string TestDataRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "TestData");

    [Fact]
    public async Task EyeExample_Scan_FindsPatchableEyeShape()
    {
        await using var sandbox = await TestSandbox.CreateAsync("eye_example.nif");
        var meshService = new ReflectionNifMeshService();
        var probes = await meshService.ProbeAsync(sandbox.SamplePath);
        Assert.NotEmpty(probes);
        var scanService = new ScanService(meshService, new ShapeClassifier());

        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, new PatchSettings(0.75f, 0.15f)));

        var file = Assert.Single(report.Files);
        Assert.False(file.HasError, file.ErrorMessage ?? DescribeReport(report));
        Assert.True(report.PatchableEyeShapes > 0, DescribeReport(report));
        Assert.True(report.PatchableShapes > 0, DescribeReport(report));
        Assert.Contains(file.Shapes, static shape => shape.Kind == ShapeKind.Eye && shape.IsPatchCandidate);
    }

    [Fact]
    public async Task SkinExample_Scan_FindsPatchableBodyShape()
    {
        await using var sandbox = await TestSandbox.CreateAsync("skin_example.nif");
        var meshService = new ReflectionNifMeshService();
        var probes = await meshService.ProbeAsync(sandbox.SamplePath);
        Assert.NotEmpty(probes);
        var scanService = new ScanService(meshService, new ShapeClassifier());

        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, new PatchSettings(0.45f, 0.05f)));

        var file = Assert.Single(report.Files);
        Assert.False(file.HasError, file.ErrorMessage ?? DescribeReport(report));
        Assert.True(report.PatchableBodyShapes > 0, DescribeReport(report));
        Assert.True(report.PatchableShapes > 0, DescribeReport(report));
        Assert.Contains(file.Shapes, static shape => shape.Kind == ShapeKind.Body && shape.IsPatchCandidate);
    }

    [Fact]
    public async Task DoNotAlter_Scan_FindsNoPatchCandidates()
    {
        await using var sandbox = await TestSandbox.CreateAsync("do_not_alter.nif");
        var meshService = new ReflectionNifMeshService();
        var probes = await meshService.ProbeAsync(sandbox.SamplePath);
        Assert.NotEmpty(probes);
        var scanService = new ScanService(meshService, new ShapeClassifier());

        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, new PatchSettings(0.9f, 0.2f)));

        var file = Assert.Single(report.Files);
        Assert.False(file.HasError, file.ErrorMessage ?? DescribeReport(report));
        Assert.Equal(0, report.PatchableShapes);
        Assert.DoesNotContain(file.Shapes, static shape => shape.IsPatchCandidate);
    }

    [Fact]
    public async Task FaceGenExample1_Scan_FindsPatchableBodyAndSkippedEye()
    {
        await using var sandbox = await TestSandbox.CreateAsync("facegen_example_1.nif");
        var meshService = new ReflectionNifMeshService();
        var probes = await meshService.ProbeAsync(sandbox.SamplePath);
        Assert.NotEmpty(probes);

        var scanService = new ScanService(meshService, new ShapeClassifier());
        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, new PatchSettings(0.6f, 0.12f)));

        var file = Assert.Single(report.Files);
        Assert.False(file.HasError, file.ErrorMessage ?? DescribeReport(report));
        Assert.NotEmpty(file.Shapes);

        Assert.Contains(file.Shapes, shape => shape.Probe.ShapeName == "MaleHeadKhajiit" &&
                                             shape.Kind == ShapeKind.Body &&
                                             shape.IsPatchCandidate &&
                                             shape.TargetValue == 0.12f);
        Assert.Contains(file.Shapes, shape => shape.Probe.ShapeName == "MaleEyesKhajiitOrangeNarrow" &&
                                             shape.Kind == ShapeKind.Eye &&
                                             !shape.IsPatchCandidate &&
                                             !shape.Probe.HasSoftLighting);
        Assert.Contains(file.Shapes, static shape => shape.Kind == ShapeKind.Ignore);
    }

    [Fact]
    public async Task FaceGenExample2_Scan_FindsPatchableBodyAndEye()
    {
        await using var sandbox = await TestSandbox.CreateAsync("facegen_example_2.nif");
        var meshService = new ReflectionNifMeshService();
        var probes = await meshService.ProbeAsync(sandbox.SamplePath);
        Assert.NotEmpty(probes);

        var scanService = new ScanService(meshService, new ShapeClassifier());
        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, new PatchSettings(0.6f, 0.12f)));

        var file = Assert.Single(report.Files);
        Assert.False(file.HasError, file.ErrorMessage ?? DescribeReport(report));
        Assert.NotEmpty(file.Shapes);

        Assert.Contains(file.Shapes, shape => shape.Probe.ShapeName == "MaleHeadNord" &&
                                             shape.Kind == ShapeKind.Body &&
                                             shape.IsPatchCandidate &&
                                             shape.TargetValue == 0.12f);
        Assert.Contains(file.Shapes, shape => shape.Probe.ShapeName == "MaleEyesHumanGrey" &&
                                             shape.Kind == ShapeKind.Eye &&
                                             shape.IsPatchCandidate &&
                                             shape.TargetValue == 0.6f);
        Assert.Contains(file.Shapes, static shape => shape.Kind == ShapeKind.Ignore);
    }

    [Theory]
    [InlineData("eye_example.nif", 0.83f, 0.20f, ShapeKind.Eye)]
    [InlineData("skin_example.nif", 0.30f, 0.07f, ShapeKind.Body)]
    public async Task PatchExecutor_CreatesGeneratedOutputWithoutTouchingSource(string fileName, float eyeValue, float bodyValue, ShapeKind expectedKind)
    {
        await using var sandbox = await TestSandbox.CreateAsync(fileName);
        var nifMeshService = new ReflectionNifMeshService();
        var shapeClassifier = new ShapeClassifier();
        var scanService = new ScanService(nifMeshService, shapeClassifier);
        var backupStore = new BackupStore();
        var patchExecutor = new PatchExecutor(new PatchPlanner(), nifMeshService, new ScanFileResolver(), backupStore);
        var settings = new PatchSettings(eyeValue, bodyValue);
        var originalBytes = await File.ReadAllBytesAsync(sandbox.SamplePath);
        var archivePath = Path.Combine(sandbox.RootPath, "LightingEffect1 Mesh Patcher Output.zip");

        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, settings));
        Assert.True(report.PatchableShapes > 0, DescribeReport(report));

        var manifest = await patchExecutor.ExecuteAsync(report, archivePath);
        var manifestFile = Assert.Single(manifest.Files);
        Assert.Equal("Patched", manifestFile.Status);
        Assert.True(string.IsNullOrWhiteSpace(manifestFile.ErrorMessage), manifestFile.ErrorMessage);
        Assert.True(File.Exists(manifestFile.OutputPath));
        Assert.True(Directory.Exists(manifest.OutputRootPath));
        Assert.True(File.Exists(manifest.OutputArchivePath));

        var sourceBytes = await File.ReadAllBytesAsync(sandbox.SamplePath);
        var outputBytes = await File.ReadAllBytesAsync(manifestFile.OutputPath);
        var sourceRescanned = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, settings));
        var extractedRoot = ExtractArchiveToDirectory(manifest.OutputArchivePath, sandbox.RootPath, "extract");
        var outputRescanned = await scanService.ScanAsync(new ScanRequest(extractedRoot, settings));

        Assert.Equal(originalBytes, sourceBytes);
        Assert.NotEqual(originalBytes, outputBytes);
        Assert.True(sourceRescanned.PatchableShapes > 0, DescribeReport(sourceRescanned));
        Assert.Equal(0, outputRescanned.PatchableShapes);
        Assert.Contains(
            outputRescanned.Files.SelectMany(static file => file.Shapes),
            shape => shape.Kind == expectedKind &&
                     !shape.IsPatchCandidate &&
                     shape.TargetValue.HasValue &&
                     Math.Abs(shape.Probe.LightingEffect1 - shape.TargetValue.Value) <= 0.0001f);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sandbox.SamplePath));
    }

    [Fact]
    public async Task FaceGenExample1_OutputMod_OnlyPatchesBody()
    {
        await using var sandbox = await TestSandbox.CreateAsync("facegen_example_1.nif");
        var nifMeshService = new ReflectionNifMeshService();
        var scanService = new ScanService(nifMeshService, new ShapeClassifier());
        var backupStore = new BackupStore();
        var patchExecutor = new PatchExecutor(new PatchPlanner(), nifMeshService, new ScanFileResolver(), backupStore);
        var settings = new PatchSettings(0.6f, 0.12f);
        var originalBytes = await File.ReadAllBytesAsync(sandbox.SamplePath);
        var archivePath = Path.Combine(sandbox.RootPath, "LightingEffect1 Mesh Patcher Output.zip");

        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, settings));
        Assert.True(report.PatchableShapes > 0, DescribeReport(report));

        var manifest = await patchExecutor.ExecuteAsync(report, archivePath);
        var manifestFile = Assert.Single(manifest.Files);
        Assert.Equal("Patched", manifestFile.Status);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sandbox.SamplePath));
        Assert.True(File.Exists(manifestFile.OutputPath));
        Assert.True(File.Exists(manifest.OutputArchivePath));

        var extractedRoot = ExtractArchiveToDirectory(manifest.OutputArchivePath, sandbox.RootPath, "extract-facegen-1");
        var rescanned = await scanService.ScanAsync(new ScanRequest(extractedRoot, settings));
        Assert.Equal(0, rescanned.PatchableShapes);
        var shapes = rescanned.Files.SelectMany(static file => file.Shapes).ToArray();

        Assert.Contains(shapes, shape => shape.Probe.ShapeName == "MaleHeadKhajiit" &&
                                        shape.Kind == ShapeKind.Body &&
                                        Math.Abs(shape.Probe.LightingEffect1 - 0.12f) <= 0.0001f);
        Assert.Contains(shapes, shape => shape.Probe.ShapeName == "MaleEyesKhajiitOrangeNarrow" &&
                                        shape.Kind == ShapeKind.Eye &&
                                        !shape.IsPatchCandidate &&
                                        Math.Abs(shape.Probe.LightingEffect1 - 0.49f) <= 0.0001f);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sandbox.SamplePath));
    }

    [Fact]
    public async Task FaceGenExample2_OutputMod_PatchesBodyAndEye()
    {
        await using var sandbox = await TestSandbox.CreateAsync("facegen_example_2.nif");
        var nifMeshService = new ReflectionNifMeshService();
        var scanService = new ScanService(nifMeshService, new ShapeClassifier());
        var backupStore = new BackupStore();
        var patchExecutor = new PatchExecutor(new PatchPlanner(), nifMeshService, new ScanFileResolver(), backupStore);
        var settings = new PatchSettings(0.6f, 0.12f);
        var originalBytes = await File.ReadAllBytesAsync(sandbox.SamplePath);
        var archivePath = Path.Combine(sandbox.RootPath, "LightingEffect1 Mesh Patcher Output.zip");

        var report = await scanService.ScanAsync(new ScanRequest(sandbox.RootPath, settings));
        Assert.True(report.PatchableShapes >= 2, DescribeReport(report));

        var manifest = await patchExecutor.ExecuteAsync(report, archivePath);
        var manifestFile = Assert.Single(manifest.Files);
        Assert.Equal("Patched", manifestFile.Status);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sandbox.SamplePath));
        Assert.True(File.Exists(manifestFile.OutputPath));
        Assert.True(File.Exists(manifest.OutputArchivePath));

        var extractedRoot = ExtractArchiveToDirectory(manifest.OutputArchivePath, sandbox.RootPath, "extract-facegen-2");
        var rescanned = await scanService.ScanAsync(new ScanRequest(extractedRoot, settings));
        Assert.Equal(0, rescanned.PatchableShapes);
        var shapes = rescanned.Files.SelectMany(static file => file.Shapes).ToArray();

        Assert.Contains(shapes, shape => shape.Probe.ShapeName == "MaleHeadNord" &&
                                        shape.Kind == ShapeKind.Body &&
                                        Math.Abs(shape.Probe.LightingEffect1 - 0.12f) <= 0.0001f);
        Assert.Contains(shapes, shape => shape.Probe.ShapeName == "MaleEyesHumanGrey" &&
                                        shape.Kind == ShapeKind.Eye &&
                                        Math.Abs(shape.Probe.LightingEffect1 - 0.6f) <= 0.0001f);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sandbox.SamplePath));
    }

    private sealed class TestSandbox : IAsyncDisposable
    {
        private readonly string? previousAppHome;

        private TestSandbox(string rootPath, string samplePath, string appHomePath, string? previousAppHome)
        {
            RootPath = rootPath;
            SamplePath = samplePath;
            AppHomePath = appHomePath;
            this.previousAppHome = previousAppHome;
        }

        public string RootPath { get; }

        public string SamplePath { get; }

        public string AppHomePath { get; }

        public static async Task<TestSandbox> CreateAsync(string fileName)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-integration", Guid.NewGuid().ToString("N"));
            var appHomePath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
            var samplePath = Path.Combine(rootPath, fileName);
            Directory.CreateDirectory(rootPath);
            Directory.CreateDirectory(appHomePath);

            var sourcePath = Path.Combine(TestDataRoot, fileName);
            await using (var source = File.OpenRead(sourcePath))
            await using (var destination = File.Open(samplePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(destination);
            }

            var previousValue = Environment.GetEnvironmentVariable("SKYRIM_LIGHTING_PATCHER_HOME");
            Environment.SetEnvironmentVariable("SKYRIM_LIGHTING_PATCHER_HOME", appHomePath);

            return new TestSandbox(rootPath, samplePath, appHomePath, previousValue);
        }

        public ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("SKYRIM_LIGHTING_PATCHER_HOME", previousAppHome);

            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temporary test folders.
            }

            try
            {
                if (Directory.Exists(AppHomePath))
                {
                    Directory.Delete(AppHomePath, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for backup/settings data.
            }

            return ValueTask.CompletedTask;
        }
    }

    private static string DescribeReport(ScanReport report)
    {
        return string.Join(
            Environment.NewLine,
            report.Files.Select(file =>
            {
                if (file.HasError)
                {
                    return $"{Path.GetFileName(file.FilePath)} ERROR: {file.ErrorMessage}";
                }

                var shapes = string.Join(
                    " | ",
                    file.Shapes.Select(shape =>
                        $"{shape.Probe.ShapeName} kind={shape.Kind} soft={shape.Probe.HasSoftLighting} old={shape.Probe.LightingEffect1:0.###} target={(shape.TargetValue.HasValue ? shape.TargetValue.Value.ToString("0.###") : "-")} patch={shape.IsPatchCandidate}"));

                return $"{Path.GetFileName(file.FilePath)} :: {shapes}";
            }));
    }

    private static string ExtractArchiveToDirectory(string archivePath, string rootPath, string directoryName)
    {
        var extractPath = Path.Combine(rootPath, directoryName);
        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, recursive: true);
        }

        Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(archivePath, extractPath);
        return extractPath;
    }
}
