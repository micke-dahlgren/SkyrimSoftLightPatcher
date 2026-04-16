namespace SkyrimLightingPatcher.Core.Models;

public sealed record PatchCandidate(
    string FilePath,
    string ShapeKey,
    string ShapeName,
    ShapeKind Kind,
    float OldValue,
    float NewValue);

public sealed record ShapePatchOperation(
    string ShapeKey,
    string ShapeName,
    ShapeKind Kind,
    float OldValue,
    float NewValue);

public sealed record FilePatchPlan(
    string FilePath,
    MeshSource Source,
    IReadOnlyList<PatchCandidate> Candidates);

public sealed record PatchPlan(
    ScanRequest Request,
    IReadOnlyList<FilePatchPlan> Files,
    IReadOnlyList<PatchCandidate> Candidates)
{
    public int FileCount => Files.Count;

    public int ShapeCount => Candidates.Count;
}

public sealed record PatchedShapeRecord(
    string ShapeKey,
    string ShapeName,
    ShapeKind Kind,
    float OldValue,
    float NewValue);

public sealed record FilePatchRecord(
    string FilePath,
    string OutputPath,
    string BackupPath,
    string Status,
    IReadOnlyList<PatchedShapeRecord> Shapes,
    string? SourceModName = null,
    string? ErrorMessage = null);

public sealed record PatchRunManifest(
    string RunId,
    string RootPath,
    string OutputRootPath,
    string OutputArchivePath,
    string OutputModName,
    bool ReplacedExistingOutput,
    DateTimeOffset Timestamp,
    PatchSettings Settings,
    IReadOnlyList<FilePatchRecord> Files)
{
    public int ShapeCount => Files.Sum(static file => file.Shapes.Count);
}

public sealed record BackupRunInfo(
    string RunId,
    string RootPath,
    string OutputRootPath,
    string OutputArchivePath,
    string OutputModName,
    DateTimeOffset Timestamp,
    int FileCount,
    int ShapeCount,
    string ManifestPath);
