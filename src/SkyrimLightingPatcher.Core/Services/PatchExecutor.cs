using System.IO.Compression;
using System.Text.Json;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class PatchExecutor(
    IPatchPlanner patchPlanner,
    INifMeshService nifMeshService,
    IScanFileResolver scanFileResolver,
    IBackupStore backupStore) : IPatchExecutor
{
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<PatchRunManifest> ExecuteAsync(
        ScanReport report,
        string outputArchivePath,
        IProgress<PatchProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = patchPlanner.CreatePlan(report);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var outputRootPath = PatchOutputPaths.GetOutputRootPath(report.Request.RootPath);
        var replacedExistingOutput = File.Exists(outputArchivePath);
        var fileRecords = new List<FilePatchRecord>();
        var filesProcessed = 0;
        var successfulFiles = 0;
        var failedFiles = 0;

        PrepareOutputRoot(report.Request.RootPath, outputRootPath);
        ReportProgress(progress, string.Empty, filesProcessed, plan.FileCount, successfulFiles, failedFiles);

        foreach (var filePlan in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, filePlan.FilePath, filesProcessed, plan.FileCount, successfulFiles, failedFiles);

            var outputPath = Path.Combine(
                outputRootPath,
                filePlan.Source.OutputRelativePath);

            try
            {
                var sourcePath = await scanFileResolver.MaterializeSourceAsync(filePlan.Source, cancellationToken).ConfigureAwait(false);
                var operations = filePlan.Candidates
                    .Select(candidate => new ShapePatchOperation(
                        candidate.ShapeKey,
                        candidate.ShapeName,
                        candidate.Kind,
                        candidate.OldValue1,
                        candidate.NewValue1,
                        candidate.OldValue2,
                        candidate.NewValue2,
                        candidate.ClearSoftRimBackFlags))
                    .ToArray();

                await nifMeshService
                    .WritePatchedFileAsync(sourcePath, outputPath, operations, cancellationToken)
                    .ConfigureAwait(false);

                fileRecords.Add(new FilePatchRecord(
                    filePlan.FilePath,
                    outputPath,
                    string.Empty,
                    "Patched",
                    operations.Select(static op => new PatchedShapeRecord(op.ShapeKey, op.ShapeName, op.Kind, op.OldValue1, op.NewValue1, op.OldValue2, op.NewValue2, op.ClearSoftRimBackFlags)).ToArray(),
                    filePlan.Source.SourceModName));
                successfulFiles++;
            }
            catch (Exception exception)
            {
                fileRecords.Add(new FilePatchRecord(
                    filePlan.FilePath,
                    outputPath,
                    string.Empty,
                    "Failed",
                    Array.Empty<PatchedShapeRecord>(),
                    filePlan.Source.SourceModName,
                    exception.Message));
                failedFiles++;
            }

            filesProcessed++;
            ReportProgress(progress, filePlan.FilePath, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
        }

        var manifest = new PatchRunManifest(
            runId,
            report.Request.RootPath,
            outputRootPath,
            outputArchivePath,
            PatchOutputPaths.OutputModName,
            replacedExistingOutput,
            DateTimeOffset.Now,
            report.Request.Settings,
            fileRecords);

        ReportProgress(progress, ProgressMessageFinalizingManifest, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
        await WriteOutputManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, ProgressMessageCreatingArchive, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
        CreateArchive(outputRootPath, outputArchivePath);
        ReportProgress(progress, ProgressMessageWritingRunManifest, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
        await backupStore.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, ProgressMessageCompleted, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
        return manifest;
    }

    private const string ProgressMessageFinalizingManifest = "__status__: Finalizing patch manifest...";
    private const string ProgressMessageCreatingArchive = "__status__: Creating output archive (.zip)...";
    private const string ProgressMessageWritingRunManifest = "__status__: Recording patch run metadata...";
    private const string ProgressMessageCompleted = "__status__: Patch output complete.";

    private static void ReportProgress(
        IProgress<PatchProgressUpdate>? progress,
        string currentFilePath,
        int filesProcessed,
        int totalFiles,
        int successfulFiles,
        int failedFiles)
    {
        progress?.Report(new PatchProgressUpdate(
            currentFilePath,
            filesProcessed,
            totalFiles,
            successfulFiles,
            failedFiles));
    }

    private static void PrepareOutputRoot(string rootPath, string outputRootPath)
    {
        if (Directory.Exists(outputRootPath))
        {
            if (!PatchOutputPaths.IsManagedOutputRoot(rootPath, outputRootPath))
            {
                throw new InvalidOperationException("Refusing to replace an unmanaged output folder.");
            }

            Directory.Delete(outputRootPath, recursive: true);
        }

        Directory.CreateDirectory(outputRootPath);
    }

    private async Task WriteOutputManifestAsync(PatchRunManifest manifest, CancellationToken cancellationToken)
    {
        var manifestPath = PatchOutputPaths.GetManifestCopyPath(manifest.OutputRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

        await using var stream = File.Open(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, serializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void CreateArchive(string outputRootPath, string outputArchivePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputArchivePath)!);
        var tempArchivePath = outputArchivePath + ".tmp";

        if (File.Exists(tempArchivePath))
        {
            File.Delete(tempArchivePath);
        }

        ZipFile.CreateFromDirectory(outputRootPath, tempArchivePath, CompressionLevel.Optimal, includeBaseDirectory: false);

        if (File.Exists(outputArchivePath))
        {
            File.Delete(outputArchivePath);
        }

        File.Move(tempArchivePath, outputArchivePath);
    }
}
