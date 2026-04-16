using System.Text.Json;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class BackupStore : IBackupStore
{
    private const string ManifestFileName = "manifest.json";
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string backupsRoot = Path.Combine(PatchOutputPaths.GetApplicationHomeDirectory(), "Backups");

    public async Task<string> BackupFileAsync(string runId, string rootPath, string filePath, CancellationToken cancellationToken = default)
    {
        var runFilesRoot = Path.Combine(backupsRoot, runId, "files");
        var relativePath = PathUtility.GetRelativeOrFileName(rootPath, filePath);
        var backupPath = Path.Combine(runFilesRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

        await using var sourceStream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destinationStream = File.Open(backupPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);

        return backupPath;
    }

    public async Task WriteManifestAsync(PatchRunManifest manifest, CancellationToken cancellationToken = default)
    {
        var manifestPath = GetManifestPath(manifest.RunId);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

        await using var stream = File.Open(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, serializerOptions, cancellationToken).ConfigureAwait(false);
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

        var normalizedRoot = PathUtility.NormalizeForComparison(rootPath);
        var results = new List<BackupRunInfo>();

        foreach (var runDirectory in Directory.EnumerateDirectories(backupsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(runDirectory, ManifestFileName);
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
        return Path.Combine(backupsRoot, runId, ManifestFileName);
    }
}
