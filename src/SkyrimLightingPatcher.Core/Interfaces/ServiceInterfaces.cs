using SkyrimLightingPatcher.Core.Models;

namespace SkyrimLightingPatcher.Core.Interfaces;

public interface INifMeshService
{
    Task<IReadOnlyList<NifShapeProbe>> ProbeAsync(string filePath, CancellationToken cancellationToken = default);

    Task WritePatchedFileAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyList<ShapePatchOperation> operations,
        CancellationToken cancellationToken = default);
}

public interface IShapeClassifier
{
    ShapeClassification Classify(NifShapeProbe probe);
}

public interface IScanService
{
    Task<ScanReport> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IScanFileResolver
{
    Task<IReadOnlyList<MeshSource>> ResolveFilePathsAsync(
        string rootPath,
        string? skyrimDataPath = null,
        ModManagerKind modManager = ModManagerKind.Vortex,
        CancellationToken cancellationToken = default);

    Task<string> MaterializeSourceAsync(MeshSource source, CancellationToken cancellationToken = default);
}

public interface IPatchPlanner
{
    PatchPlan CreatePlan(ScanReport report);
}

public interface IPatchExecutor
{
    Task<PatchRunManifest> ExecuteAsync(
        ScanReport report,
        string outputArchivePath,
        IProgress<PatchProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IBackupStore
{
    Task<string> BackupFileAsync(string runId, string rootPath, string filePath, CancellationToken cancellationToken = default);

    Task WriteManifestAsync(PatchRunManifest manifest, CancellationToken cancellationToken = default);

    Task<PatchRunManifest?> LoadManifestAsync(string runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default);
}

public interface IRestoreService
{
    Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default);

    Task<PatchRunManifest> RestoreAsync(string runId, CancellationToken cancellationToken = default);
}

public interface IOutputModService
{
    Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default);

    Task<PatchRunManifest> DeleteAsync(string runId, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IVortexPathResolver
{
    Task<VortexStagingFolder?> TryResolveSkyrimSeAsync(CancellationToken cancellationToken = default);

    Task<string?> TryResolveSkyrimDataPathAsync(CancellationToken cancellationToken = default);
}

public interface IModOrganizer2PathResolver
{
    Task<ModOrganizer2Instance?> TryResolveSkyrimSeAsync(
        ModOrganizer2InstanceKind? preferredKind = null,
        CancellationToken cancellationToken = default);
}
