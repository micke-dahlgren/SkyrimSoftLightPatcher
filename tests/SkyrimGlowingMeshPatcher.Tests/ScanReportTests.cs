using SkyrimGlowingMeshPatcher.Core.Models;

namespace SkyrimGlowingMeshPatcher.Tests;

public sealed class ScanReportTests
{
    [Fact]
    public void PreviewFiles_OnlyIncludesFilesWithPatchCandidates()
    {
        var request = new ScanRequest(@"C:\Meshes", new PatchSettings(0.4f, 0.2f));
        var report = ScanReport.Create(
            request,
            [
                new FileScanResult(
                    @"C:\Meshes\patchable.nif",
                    [
                        CreateShapeResult("eye-shape", ShapeKind.Eye, isPatchCandidate: true, currentValue: 0.1f, targetValue: 0.4f),
                        CreateShapeResult("ignored-shape", ShapeKind.Ignore, isPatchCandidate: false, currentValue: 0.0f, targetValue: null),
                    ]),
                new FileScanResult(
                    @"C:\Meshes\unchanged.nif",
                    [
                        CreateShapeResult("body-shape", ShapeKind.Body, isPatchCandidate: false, currentValue: 0.2f, targetValue: 0.2f),
                    ]),
            ]);

        var previewFiles = report.PreviewFiles;

        var file = Assert.Single(previewFiles);
        Assert.Equal(@"C:\Meshes\patchable.nif", file.FilePath);
        Assert.Single(file.PatchCandidateShapes);
        Assert.Equal("eye-shape", file.PatchCandidateShapes[0].Probe.ShapeName);
    }

    private static ShapeScanResult CreateShapeResult(
        string shapeName,
        ShapeKind kind,
        bool isPatchCandidate,
        float currentValue,
        float? targetValue)
    {
        return new ShapeScanResult(
            new NifShapeProbe(
                @"C:\Meshes\sample.nif",
                shapeName,
                shapeName,
                ShaderMetadata.Empty,
                [],
                isPatchCandidate,
                currentValue),
            kind,
            isPatchCandidate,
            targetValue,
            kind.ToString(),
            ["Test"]);
    }
}
