using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class OutputModService(IBackupStore backupStore) : IOutputModService
{
    public async Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var runs = await backupStore.ListRunsAsync(rootPath, cancellationToken).ConfigureAwait(false);
        return runs
            .Where(static run =>
                (!string.IsNullOrWhiteSpace(run.OutputArchivePath) && File.Exists(run.OutputArchivePath)) ||
                (!string.IsNullOrWhiteSpace(run.OutputRootPath) && Directory.Exists(run.OutputRootPath)))
            .GroupBy(static run => PathUtility.NormalizeForComparison(
                !string.IsNullOrWhiteSpace(run.OutputArchivePath) ? run.OutputArchivePath : run.OutputRootPath))
            .Select(static group => group.OrderByDescending(static run => run.Timestamp).First())
            .OrderByDescending(static run => run.Timestamp)
            .ToArray();
    }

    public async Task<PatchRunManifest> DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        var manifest = await backupStore.LoadManifestAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Patch output run '{runId}' was not found.");

        if (string.IsNullOrWhiteSpace(manifest.OutputRootPath))
        {
            throw new InvalidOperationException("The selected patch run does not have a generated output folder.");
        }

        if (!PatchOutputPaths.IsManagedOutputRoot(manifest.RootPath, manifest.OutputRootPath, manifest.OutputArchivePath))
        {
            throw new InvalidOperationException("Refusing to delete an unmanaged output folder.");
        }

        if (Directory.Exists(manifest.OutputRootPath))
        {
            Directory.Delete(manifest.OutputRootPath, recursive: true);
        }

        return manifest;
    }
}
