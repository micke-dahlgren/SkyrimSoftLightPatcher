using System.Text.Json;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class ScanServiceTests
{
    [Fact]
    public async Task ScanAsync_ReportsProgressAsFilesAreProcessed()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-scan-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        var patchableFile = Path.Combine(rootPath, "eye_example.nif");
        var failingFile = Path.Combine(rootPath, "broken_example.nif");
        await File.WriteAllTextAsync(patchableFile, "sample");
        await File.WriteAllTextAsync(failingFile, "sample");

        var meshService = new FakeNifMeshService(patchableFile, failingFile);
        var classifier = new FakeShapeClassifier();
        var scanService = new ScanService(meshService, classifier);
        var updates = new List<ScanProgressUpdate>();

        var report = await scanService.ScanAsync(
            new ScanRequest(rootPath, new PatchSettings(0.75f, 0.15f)),
            new Progress<ScanProgressUpdate>(updates.Add));

        Assert.Equal(2, report.FilesScanned);
        Assert.Equal(2, updates.Count);
        Assert.Equal([1, 2], updates.Select(static update => update.FilesScanned).ToArray());
        Assert.Equal(1, updates[^1].CandidateFiles);
        Assert.Equal(1, updates[^1].PatchableShapes);
        Assert.Equal(1, updates[^1].ErrorFiles);
        Assert.Equal(1, report.ErrorFiles);
        Assert.False(string.IsNullOrWhiteSpace(report.ScanErrorLogPath));
        Assert.True(File.Exists(report.ScanErrorLogPath));

        var logJson = await File.ReadAllTextAsync(report.ScanErrorLogPath!);
        Assert.Contains("Unsupported NIF", logJson, StringComparison.Ordinal);
        Assert.Contains("broken_example.nif", logJson, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(JsonDocument.Parse(logJson));
    }

    private sealed class FakeNifMeshService(string patchableFile, string failingFile) : INifMeshService
    {
        public Task<IReadOnlyList<NifShapeProbe>> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.Equals(filePath, failingFile, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unsupported NIF");
            }

            if (string.Equals(filePath, patchableFile, StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<NifShapeProbe> probes =
                [
                    new NifShapeProbe(
                        filePath,
                        "shape-0",
                        "Eyes",
                        new ShaderMetadata("EnvironmentMap", ["Soft_Lighting"]),
                        [@"textures\actors\character\eyes\blueeye.dds"],
                        true,
                        0.15f),
                ];

                return Task.FromResult(probes);
            }

            return Task.FromResult<IReadOnlyList<NifShapeProbe>>(Array.Empty<NifShapeProbe>());
        }

        public Task WritePatchedFileAsync(
            string sourcePath,
            string outputPath,
            IReadOnlyList<ShapePatchOperation> operations,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeShapeClassifier : IShapeClassifier
    {
        public ShapeClassification Classify(NifShapeProbe probe)
        {
            return probe.ShapeName.Contains("eye", StringComparison.OrdinalIgnoreCase)
                ? new ShapeClassification(ShapeKind.Eye, "Eye", ["Matched test eye shape."])
                : ShapeClassification.Ignore("Ignore", "Ignored by test classifier.");
        }
    }
}
