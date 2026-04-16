using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class PatchPlannerTests
{
    private readonly PatchPlanner planner = new();

    [Fact]
    public void CreatePlan_IncludesOnlyPatchCandidates()
    {
        var report = ScanReport.Create(
            new ScanRequest(@"C:\Mods\Meshes", new PatchSettings(0.3f, 0.1f)),
            [
                new FileScanResult(
                    @"C:\Mods\Meshes\a.nif",
                    [
                        new ShapeScanResult(CreateProbe(@"C:\Mods\Meshes\a.nif", "Eye"), ShapeKind.Eye, true, 0.3f, "Eye", ["Eligible"]),
                        new ShapeScanResult(CreateProbe(@"C:\Mods\Meshes\a.nif", "Body"), ShapeKind.Body, false, 0.1f, "Body", ["Already matches"]),
                    ]),
            ]);

        var plan = planner.CreatePlan(report);

        Assert.Single(plan.Files);
        Assert.Single(plan.Candidates);
        Assert.Equal("Eye", plan.Candidates[0].ShapeName);
    }

    [Fact]
    public void CreatePlan_GroupsCandidatesByFile()
    {
        var report = ScanReport.Create(
            new ScanRequest(@"C:\Mods\Meshes", new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    @"C:\Mods\Meshes\a.nif",
                    [new ShapeScanResult(CreateProbe(@"C:\Mods\Meshes\a.nif", "Eye"), ShapeKind.Eye, true, 0.4f, "Eye", ["Eligible"])]),
                new FileScanResult(
                    @"C:\Mods\Meshes\b.nif",
                    [new ShapeScanResult(CreateProbe(@"C:\Mods\Meshes\b.nif", "Body"), ShapeKind.Body, true, 0.2f, "Body", ["Eligible"])]),
            ]);

        var plan = planner.CreatePlan(report);

        Assert.Equal(2, plan.FileCount);
        Assert.Equal(2, plan.ShapeCount);
    }

    private static NifShapeProbe CreateProbe(string filePath, string shapeName)
    {
        return new NifShapeProbe(
            filePath,
            $"{Path.GetFileName(filePath)}:{shapeName}",
            shapeName,
            ShaderMetadata.Empty,
            Array.Empty<string>(),
            HasSoftLighting: true,
            LightingEffect1: 0.1f);
    }
}
