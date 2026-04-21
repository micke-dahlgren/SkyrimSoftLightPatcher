using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SkyrimGlowingMeshPatcher.Core.Interfaces;
using SkyrimGlowingMeshPatcher.Core.Models;
using SkyrimGlowingMeshPatcher.Core.Utilities;

namespace SkyrimGlowingMeshPatcher.Core.Services;

public sealed class PatchExecutor(
    IPatchPlanner patchPlanner,
    INifMeshService nifMeshService,
    IScanFileResolver scanFileResolver,
    IBackupStore backupStore,
    IDiskSpaceMonitor? optionalDiskSpaceMonitor = null) : IPatchExecutor
{
    private const long MinimumReservedFreeBytes = 256L * 1024 * 1024;
    private const long MinimumPatchStageEstimateBytes = 64L * 1024 * 1024;
    private const long MinimumExtractionStageEstimateBytes = 16L * 1024 * 1024;
    private const long MinimumPerFileEstimateBytes = 1L * 1024 * 1024;
    private const long PerFileOutputOverheadBytes = 256L * 1024;
    private const long ManifestWriteEstimateBytes = 1L * 1024 * 1024;
    private const long BackupManifestWriteEstimateBytes = 512L * 1024;
    private const long MinimumArchiveEstimateBytes = 16L * 1024 * 1024;
    private const long ArchiveOverheadBytes = 8L * 1024 * 1024;
    private const long ArchiveExtractionPerFileEstimateBytes = 512L * 1024;
    private const long ErrorLogWriteEstimateBytes = 512L * 1024;

    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IDiskSpaceMonitor diskSpaceMonitor = optionalDiskSpaceMonitor ?? new DiskSpaceMonitor();

    public async Task<PatchRunManifest> ExecuteAsync(
        ScanReport report,
        string outputArchivePath,
        string outputRootPath,
        IProgress<PatchProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputRootPath))
        {
            throw new InvalidOperationException("Patch destination folder is required.");
        }

        var plan = patchPlanner.CreatePlan(report);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var resolvedOutputRootPath = Path.GetFullPath(outputRootPath);
        var replacedExistingOutput = File.Exists(outputArchivePath) || Directory.Exists(resolvedOutputRootPath);
        var fileRecords = new List<FilePatchRecord>();
        var filesProcessed = 0;
        var successfulFiles = 0;
        var failedFiles = 0;

        using var reservationScope = new DiskSpaceReservationScope();
        EnsureStageCapacity(
            reservationScope,
            PatchExecutionStages.PreparingOutput,
            resolvedOutputRootPath,
            EstimatePatchedOutputBytes(plan));
        EnsureReservedSpace(
            reservationScope,
            PatchExecutionStages.PreparingOutput,
            Path.GetDirectoryName(resolvedOutputRootPath) ?? resolvedOutputRootPath);

        var extractedSourcesPath = GetExtractedSourcesPath();
        var extractionEstimateBytes = EstimateExtractionCacheBytes(plan);
        if (extractionEstimateBytes > 0)
        {
            EnsureStageCapacity(
                reservationScope,
                PatchExecutionStages.WritingPatchedFiles,
                extractedSourcesPath,
                extractionEstimateBytes);
            EnsureReservedSpace(
                reservationScope,
                PatchExecutionStages.WritingPatchedFiles,
                extractedSourcesPath);
        }

        try
        {
            PrepareOutputRoot(report.Request.RootPath, resolvedOutputRootPath, outputArchivePath);
            ReportProgress(progress, string.Empty, filesProcessed, plan.FileCount, successfulFiles, failedFiles);

            foreach (var filePlan in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(progress, filePlan.FilePath, filesProcessed, plan.FileCount, successfulFiles, failedFiles);

                var outputPath = Path.Combine(
                    resolvedOutputRootPath,
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

                    EnsureStageCapacity(
                        reservationScope,
                        PatchExecutionStages.WritingPatchedFiles,
                        outputPath,
                        EstimatePatchedFileBytes(sourcePath));

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
                catch (LowDiskSpaceException)
                {
                    throw;
                }
                catch (Exception exception) when (IsDiskFullException(exception))
                {
                    throw CreateLowDiskSpaceException(
                        PatchExecutionStages.WritingPatchedFiles,
                        filePlan.Source.Kind == MeshSourceKind.Archive ? extractedSourcesPath : outputPath,
                        MinimumPerFileEstimateBytes,
                        exception);
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
                resolvedOutputRootPath,
                outputArchivePath,
                PatchOutputPaths.OutputModName,
                replacedExistingOutput,
                DateTimeOffset.Now,
                report.Request.Settings,
                fileRecords);

            var patchErrorLogContent = BuildPatchErrorLogContent(manifest);
            if (patchErrorLogContent is not null)
            {
                var errorLogPath = PatchOutputPaths.GetPatchErrorLogPath(manifest.OutputRootPath);
                EnsureStageCapacity(
                    reservationScope,
                    PatchExecutionStages.WritingOutputManifest,
                    errorLogPath,
                    ErrorLogWriteEstimateBytes);
                await WriteTextFileAsync(errorLogPath, patchErrorLogContent, cancellationToken).ConfigureAwait(false);
            }

            ReportProgress(progress, ProgressMessageFinalizingManifest, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
            var outputManifestPath = PatchOutputPaths.GetManifestCopyPath(manifest.OutputRootPath);
            EnsureStageCapacity(
                reservationScope,
                PatchExecutionStages.WritingOutputManifest,
                outputManifestPath,
                ManifestWriteEstimateBytes);
            await WriteOutputManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

            ReportProgress(progress, ProgressMessageCreatingArchive, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
            var archiveEstimateBytes = EstimateArchiveBytes(resolvedOutputRootPath);
            EnsureStageCapacity(
                reservationScope,
                PatchExecutionStages.CreatingArchive,
                outputArchivePath,
                archiveEstimateBytes);
            EnsureReservedSpace(
                reservationScope,
                PatchExecutionStages.CreatingArchive,
                Path.GetDirectoryName(outputArchivePath) ?? outputArchivePath);
            CreateArchive(resolvedOutputRootPath, outputArchivePath, archiveEstimateBytes);
            if (patchErrorLogContent is not null)
            {
                await TryWriteArchiveErrorLogAsync(outputArchivePath, patchErrorLogContent, cancellationToken).ConfigureAwait(false);
            }

            ReportProgress(progress, ProgressMessageWritingRunManifest, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
            var backupManifestPath = PatchOutputPaths.GetBackupManifestPath(runId);
            EnsureStageCapacity(
                reservationScope,
                PatchExecutionStages.WritingRunManifest,
                backupManifestPath,
                BackupManifestWriteEstimateBytes);
            EnsureReservedSpace(
                reservationScope,
                PatchExecutionStages.WritingRunManifest,
                Path.GetDirectoryName(backupManifestPath) ?? backupManifestPath);
            await backupStore.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);

            ReportProgress(progress, ProgressMessageCompleted, filesProcessed, plan.FileCount, successfulFiles, failedFiles);
            return manifest;
        }
        finally
        {
            await TryCleanupExtractedSourcesCacheAsync().ConfigureAwait(false);
        }
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

    private static void PrepareOutputRoot(string rootPath, string outputRootPath, string outputArchivePath)
    {
        if (Directory.Exists(outputRootPath))
        {
            if (!PatchOutputPaths.IsManagedOutputRoot(rootPath, outputRootPath, outputArchivePath))
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

    private static string? BuildPatchErrorLogContent(PatchRunManifest manifest)
    {
        var failedFiles = manifest.Files
            .Where(static file => string.Equals(file.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (failedFiles.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Skyrim Glowing Mesh Patcher - error log");
        builder.AppendLine($"Run ID: {manifest.RunId}");
        builder.AppendLine($"Created: {manifest.Timestamp:O}");
        builder.AppendLine($"Failed files: {failedFiles.Length}");
        builder.AppendLine();

        for (var index = 0; index < failedFiles.Length; index++)
        {
            var failedFile = failedFiles[index];
            builder.AppendLine($"{index + 1}. Source: {failedFile.FilePath}");
            builder.AppendLine($"   Output: {failedFile.OutputPath}");
            if (!string.IsNullOrWhiteSpace(failedFile.SourceModName))
            {
                builder.AppendLine($"   Mod: {failedFile.SourceModName}");
            }

            var errorMessage = string.IsNullOrWhiteSpace(failedFile.ErrorMessage)
                ? "Unknown error."
                : failedFile.ErrorMessage.Trim();
            builder.AppendLine($"   Error: {errorMessage}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static async Task WriteTextFileAsync(string outputPath, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteArchiveErrorLogAsync(
        string outputArchivePath,
        string patchErrorLogContent,
        CancellationToken cancellationToken)
    {
        var errorLogPath = PatchOutputPaths.GetArchiveErrorLogPath(outputArchivePath);

        try
        {
            await WriteTextFileAsync(errorLogPath, patchErrorLogContent, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort companion file: primary copy is still included in output mod/zip.
        }
    }

    private void CreateArchive(string outputRootPath, string outputArchivePath, long archiveEstimateBytes)
    {
        var tempArchivePath = outputArchivePath + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputArchivePath)!);

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
        catch (Exception exception) when (IsDiskFullException(exception))
        {
            TryDeleteIfExists(tempArchivePath);
            throw CreateLowDiskSpaceException(PatchExecutionStages.CreatingArchive, outputArchivePath, archiveEstimateBytes, exception);
        }
        catch (Exception exception)
        {
            TryDeleteIfExists(tempArchivePath);
            throw new PatchArchiveCreationException(outputRootPath, outputArchivePath, exception);
        }
    }

    private void EnsureStageCapacity(
        DiskSpaceReservationScope reservationScope,
        string stageName,
        string targetPath,
        long estimatedBytes)
    {
        var normalizedTargetPath = Path.GetFullPath(targetPath);
        var normalizedEstimate = Math.Max(1, estimatedBytes);
        var reserveBytes = reservationScope.IsReservedForDrive(normalizedTargetPath) ? 0 : MinimumReservedFreeBytes;
        var requiredBytes = SafeAdd(normalizedEstimate, reserveBytes);
        var availableBytes = GetAvailableBytes(stageName, normalizedTargetPath);

        if (availableBytes >= requiredBytes)
        {
            return;
        }

        throw CreateLowDiskSpaceException(stageName, normalizedTargetPath, requiredBytes, availableBytes);
    }

    private void EnsureReservedSpace(
        DiskSpaceReservationScope reservationScope,
        string stageName,
        string reservationTargetPath)
    {
        if (reservationScope.IsReservedForDrive(reservationTargetPath))
        {
            return;
        }

        try
        {
            var reservation = diskSpaceMonitor.ReserveSpace(
                stageName,
                reservationTargetPath,
                $"patch-{stageName}",
                MinimumReservedFreeBytes);
            reservationScope.Add(reservationTargetPath, reservation);
        }
        catch (Exception exception) when (IsDiskFullException(exception))
        {
            throw CreateLowDiskSpaceException(
                stageName,
                reservationTargetPath,
                MinimumReservedFreeBytes,
                exception);
        }
    }

    private long GetAvailableBytes(string stageName, string targetPath)
    {
        return Math.Max(0, diskSpaceMonitor.GetAvailableBytes(stageName, targetPath));
    }

    private LowDiskSpaceException CreateLowDiskSpaceException(
        string stageName,
        string targetPath,
        long requiredBytes,
        Exception? innerException = null)
    {
        return CreateLowDiskSpaceException(
            stageName,
            targetPath,
            requiredBytes,
            GetAvailableBytes(stageName, targetPath),
            innerException);
    }

    private static LowDiskSpaceException CreateLowDiskSpaceException(
        string stageName,
        string targetPath,
        long requiredBytes,
        long availableBytes,
        Exception? innerException = null)
    {
        var extractedSourcesPath = GetExtractedSourcesPath();
        var recoveryHint =
            $"Managed cache: '{extractedSourcesPath}'. You can also pick a destination on another drive.";
        var quickCleanupCommand =
            $"Remove-Item -LiteralPath '{EscapePowerShellSingleQuotedPath(extractedSourcesPath)}' -Recurse -Force";

        return new LowDiskSpaceException(
            stageName,
            targetPath,
            requiredBytes,
            availableBytes,
            recoveryHint,
            quickCleanupCommand,
            innerException);
    }

    private static long EstimatePatchedOutputBytes(PatchPlan plan)
    {
        var total = 0L;

        foreach (var filePlan in plan.Files)
        {
            var sourceSize = TryGetFileSize(filePlan.Source.LocalPath) ?? TryGetFileSize(filePlan.FilePath) ?? MinimumPerFileEstimateBytes;
            var perFileEstimate = Math.Max(MinimumPerFileEstimateBytes, SafeAdd(sourceSize, PerFileOutputOverheadBytes));
            total = SafeAdd(total, perFileEstimate);
        }

        return Math.Max(MinimumPatchStageEstimateBytes, total);
    }

    private static long EstimateExtractionCacheBytes(PatchPlan plan)
    {
        var archiveSourceCount = plan.Files.Count(file => file.Source.Kind == MeshSourceKind.Archive);
        if (archiveSourceCount == 0)
        {
            return 0;
        }

        var estimate = archiveSourceCount * ArchiveExtractionPerFileEstimateBytes;
        return Math.Max(MinimumExtractionStageEstimateBytes, estimate);
    }

    private static long EstimatePatchedFileBytes(string sourcePath)
    {
        var sourceSize = TryGetFileSize(sourcePath) ?? MinimumPerFileEstimateBytes;
        return Math.Max(MinimumPerFileEstimateBytes, SafeAdd(sourceSize, PerFileOutputOverheadBytes));
    }

    private static long EstimateArchiveBytes(string outputRootPath)
    {
        var outputBytes = EstimateDirectoryBytes(outputRootPath);
        return Math.Max(MinimumArchiveEstimateBytes, SafeAdd(outputBytes, ArchiveOverheadBytes));
    }

    private static long EstimateDirectoryBytes(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        var total = 0L;
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            total = SafeAdd(total, TryGetFileSize(filePath) ?? 0);
        }

        return total;
    }

    private static long? TryGetFileSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return null;
        }
    }

    private static long SafeAdd(long left, long right)
    {
        if (left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    private static string GetExtractedSourcesPath()
    {
        return Path.Combine(PatchOutputPaths.GetApplicationHomeDirectory(), "ExtractedSources");
    }

    private async Task TryCleanupExtractedSourcesCacheAsync()
    {
        try
        {
            await scanFileResolver.CleanupExtractedSourcesAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string EscapePowerShellSingleQuotedPath(string path)
    {
        return path.Replace("'", "''", StringComparison.Ordinal);
    }

    private static bool IsDiskFullException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is not IOException ioException)
            {
                continue;
            }

            var lowWord = ioException.HResult & 0xFFFF;
            if (lowWord is 0x70 or 0x27)
            {
                return true;
            }

            var message = ioException.Message;
            if (message.Contains("no space left", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string GetDriveRoot(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"Unable to resolve disk root for '{targetPath}'.");
        }

        return root;
    }

    private sealed class DiskSpaceReservationScope : IDisposable
    {
        private readonly Dictionary<string, IDisposable> reservations = new(StringComparer.OrdinalIgnoreCase);

        public bool IsReservedForDrive(string targetPath)
        {
            return reservations.ContainsKey(GetDriveRoot(targetPath));
        }

        public void Add(string targetPath, IDisposable reservation)
        {
            var driveRoot = GetDriveRoot(targetPath);
            if (reservations.ContainsKey(driveRoot))
            {
                reservation.Dispose();
                return;
            }

            reservations.Add(driveRoot, reservation);
        }

        public void Dispose()
        {
            foreach (var reservation in reservations.Values)
            {
                reservation.Dispose();
            }

            reservations.Clear();
        }
    }
}
