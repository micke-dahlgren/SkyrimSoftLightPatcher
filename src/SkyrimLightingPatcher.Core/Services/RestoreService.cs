using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class RestoreService(IBackupStore backupStore) : IRestoreService
{
    public Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        return backupStore.ListRunsAsync(rootPath, cancellationToken);
    }

    public async Task<PatchRunManifest> RestoreAsync(string runId, CancellationToken cancellationToken = default)
    {
        var manifest = await backupStore.LoadManifestAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Backup run '{runId}' was not found.");

        var failures = new List<string>();

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (string.IsNullOrWhiteSpace(file.BackupPath) || !File.Exists(file.BackupPath))
                {
                    throw new FileNotFoundException("Backup file not found.", file.BackupPath);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.FilePath)!);
                var tempPath = file.FilePath + $".restore.{manifest.RunId}.tmp";
                File.Copy(file.BackupPath, tempPath, overwrite: true);
                File.Copy(tempPath, file.FilePath, overwrite: true);
                File.Delete(tempPath);
            }
            catch (Exception exception)
            {
                failures.Add($"{file.FilePath}: {exception.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Restore completed with errors: " + string.Join(" | ", failures));
        }

        return manifest;
    }
}
