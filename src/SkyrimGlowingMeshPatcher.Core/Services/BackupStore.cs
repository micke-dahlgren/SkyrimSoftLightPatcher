using System.Text.Json;
using SkyrimGlowingMeshPatcher.Core.Interfaces;
using SkyrimGlowingMeshPatcher.Core.Models;
using SkyrimGlowingMeshPatcher.Core.Utilities;

namespace SkyrimGlowingMeshPatcher.Core.Services;

public sealed class BackupStore : IBackupStore
{
    private const int MaxRunManifestsPerRoot = 20;

    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string backupsRoot = Path.Combine(
        PatchOutputPaths.GetApplicationHomeDirectory(),
        PatchOutputPaths.BackupsFolderName);

    public async Task WriteManifestAsync(PatchRunManifest manifest, CancellationToken cancellationToken = default)
    {
        var manifestPath = GetManifestPath(manifest.RunId);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

        await using var stream = File.Open(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, serializerOptions, cancellationToken).ConfigureAwait(false);
        await PruneRunFoldersAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PatchRunManifest?> LoadManifestAsync(string runId, CancellationToken cancellationToken = default)
    {
        var manifestPath = GetManifestPath(runId);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<PatchRunManifest>(stream, serializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(backupsRoot))
        {
            return Array.Empty<BackupRunInfo>();
        }

        await PruneRunFoldersAsync(cancellationToken).ConfigureAwait(false);

        var normalizedRoot = PathUtility.NormalizeForComparison(rootPath);
        var results = new List<BackupRunInfo>();

        foreach (var runDirectory in Directory.EnumerateDirectories(backupsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(runDirectory, PatchOutputPaths.BackupManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<PatchRunManifest>(stream, serializerOptions, cancellationToken).ConfigureAwait(false);
                if (manifest is null)
                {
                    continue;
                }

                if (!string.Equals(PathUtility.NormalizeForComparison(manifest.RootPath), normalizedRoot, StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new BackupRunInfo(
                    manifest.RunId,
                    manifest.RootPath,
                    manifest.OutputRootPath,
                    manifest.OutputArchivePath,
                    manifest.OutputModName,
                    manifest.Timestamp,
                    manifest.Files.Count,
                    manifest.ShapeCount,
                    manifestPath));
            }
            catch
            {
                // Ignore malformed manifests so one bad backup does not break the UI.
            }
        }

        return results
            .OrderByDescending(static run => run.Timestamp)
            .ToArray();
    }

    private string GetManifestPath(string runId)
    {
        return PatchOutputPaths.GetBackupManifestPath(runId);
    }

    private async Task PruneRunFoldersAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(backupsRoot))
        {
            return;
        }

        var retainedRuns = new List<RetainedRun>();

        foreach (var runDirectory in Directory.EnumerateDirectories(backupsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(runDirectory, PatchOutputPaths.BackupManifestFileName);
            if (!File.Exists(manifestPath))
            {
                TryDeleteRunDirectory(runDirectory);
                continue;
            }

            PatchRunManifest? manifest;
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                manifest = await JsonSerializer.DeserializeAsync<PatchRunManifest>(stream, serializerOptions, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Keep malformed manifests for manual inspection; they are ignored by listing.
                continue;
            }

            if (manifest is null)
            {
                TryDeleteRunDirectory(runDirectory);
                continue;
            }

            if (!HasAnyOutput(manifest))
            {
                TryDeleteRunDirectory(runDirectory);
                continue;
            }

            retainedRuns.Add(new RetainedRun(runDirectory, manifest));
        }

        foreach (var runToDelete in retainedRuns
                     .GroupBy(static run => PathUtility.NormalizeForComparison(run.Manifest.RootPath))
                     .SelectMany(static group => group
                         .OrderByDescending(static run => run.Manifest.Timestamp)
                         .Skip(MaxRunManifestsPerRoot)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteRunDirectory(runToDelete.RunDirectory);
        }
    }

    private static bool HasAnyOutput(PatchRunManifest manifest)
    {
        return (!string.IsNullOrWhiteSpace(manifest.OutputArchivePath) && File.Exists(manifest.OutputArchivePath)) ||
               (!string.IsNullOrWhiteSpace(manifest.OutputRootPath) && Directory.Exists(manifest.OutputRootPath));
    }

    private static void TryDeleteRunDirectory(string runDirectory)
    {
        try
        {
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private sealed record RetainedRun(string RunDirectory, PatchRunManifest Manifest);
}
