using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class PatchPlanner : IPatchPlanner
{
    public PatchPlan CreatePlan(ScanReport report)
    {
        var filePlans = report.Files
            .Where(static file => !file.HasError)
            .Select(file => CreateFilePlan(report.Request.RootPath, file))
            .Where(static plan => plan is not null)
            .Cast<FilePatchPlan>()
            .OrderBy(static plan => plan.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var candidates = filePlans
            .SelectMany(static plan => plan.Candidates)
            .ToArray();

        return new PatchPlan(report.Request, filePlans, candidates);
    }

    private static FilePatchPlan? CreateFilePlan(string rootPath, FileScanResult file)
    {
        var source = file.Source ?? new MeshSource(
            file.FilePath,
            file.FilePath,
            PatchOutputPaths.GetOutputRelativePath(rootPath, file.FilePath),
            file.FilePath,
            MeshSourceKind.Loose);

        var candidates = file.Shapes
            .Select(shape => CreateCandidate(file.FilePath, shape))
            .Where(static candidate => candidate is not null)
            .Cast<PatchCandidate>()
            .ToArray();

        return candidates.Length == 0
            ? null
            : new FilePatchPlan(file.FilePath, source, candidates);
    }

    private static PatchCandidate? CreateCandidate(string filePath, ShapeScanResult scanResult)
    {
        if (!scanResult.IsPatchCandidate || !scanResult.TargetValue1.HasValue)
        {
            return null;
        }

        return new PatchCandidate(
            filePath,
            scanResult.Probe.ShapeKey,
            scanResult.Probe.ShapeName,
            scanResult.Kind,
            scanResult.Probe.LightingEffect1,
            scanResult.TargetValue1!.Value,
            scanResult.TargetValue2.HasValue ? scanResult.Probe.LightingEffect2 : null,
            scanResult.TargetValue2);
    }
}
