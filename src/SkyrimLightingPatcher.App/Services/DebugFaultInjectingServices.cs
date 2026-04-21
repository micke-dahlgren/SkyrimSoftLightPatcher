using SkyrimLightingPatcher.App.Models;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.App.Services;

public sealed class DebugFaultInjectingDiskSpaceMonitor(DebugFaultState faultState, IDiskSpaceMonitor innerMonitor) : IDiskSpaceMonitor
{
    public long GetAvailableBytes(string stageName, string targetPath)
    {
        var injectedLowDiskStage = GetInjectedLowDiskStage(faultState.PatchFailureMode);
        if (!string.IsNullOrWhiteSpace(injectedLowDiskStage) &&
            string.Equals(injectedLowDiskStage, stageName, StringComparison.Ordinal))
        {
            return 0;
        }

        return innerMonitor.GetAvailableBytes(stageName, targetPath);
    }

    public IDisposable ReserveSpace(string stageName, string targetPath, string reservationName, long bytes)
    {
        return innerMonitor.ReserveSpace(stageName, targetPath, reservationName, bytes);
    }

    private static string? GetInjectedLowDiskStage(DebugPatchFailureMode mode)
    {
        return mode switch
        {
            DebugPatchFailureMode.PatchLowDiskWritingOutputManifest => PatchExecutionStages.WritingOutputManifest,
            DebugPatchFailureMode.PatchLowDiskCreatingArchive => PatchExecutionStages.CreatingArchive,
            DebugPatchFailureMode.PatchLowDiskWritingRunManifest => PatchExecutionStages.WritingRunManifest,
            _ => null,
        };
    }
}

public sealed class DebugFaultInjectingPatchExecutor(DebugFaultState faultState, IPatchExecutor innerExecutor) : IPatchExecutor
{
    private const string StatusPrefix = "__status__:";
    private const string StatusFinalizingManifest = "Finalizing patch manifest";
    private const string StatusCreatingArchive = "Creating output archive";
    private const string StatusWritingRunManifest = "Recording patch run metadata";

    public Task<PatchRunManifest> ExecuteAsync(
        ScanReport report,
        string outputArchivePath,
        IProgress<PatchProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default,
        string? outputRootPath = null)
    {
        faultState.BeginPatchRun();

        var patchFailureMode = faultState.PatchFailureMode;
        switch (patchFailureMode)
        {
            case DebugPatchFailureMode.PatchLowDiskPreparingOutput:
                throw DebugFaultExceptionFactory.CreateLowDiskSpaceException(
                    PatchExecutionStages.PreparingOutput,
                    ResolveOutputRootPath(outputArchivePath, outputRootPath));
            case DebugPatchFailureMode.PatchArchiveCreationFailure:
                throw CreateArchiveFailure(outputArchivePath, outputRootPath);
            case DebugPatchFailureMode.PatchUnexpectedFailure:
                throw new InvalidOperationException("Debug fault injection: simulated unexpected failure during patching.");
        }

        var wrappedProgress = patchFailureMode is DebugPatchFailureMode.PatchLowDiskWritingOutputManifest
            or DebugPatchFailureMode.PatchLowDiskCreatingArchive
            or DebugPatchFailureMode.PatchLowDiskWritingRunManifest
            ? new DebugFaultInjectingPatchProgress(
                progress,
                patchFailureMode,
                outputArchivePath,
                ResolveOutputRootPath(outputArchivePath, outputRootPath))
            : progress;

        return innerExecutor.ExecuteAsync(report, outputArchivePath, wrappedProgress, cancellationToken, outputRootPath);
    }

    private static PatchArchiveCreationException CreateArchiveFailure(string outputArchivePath, string? outputRootPath)
    {
        var resolvedOutputRootPath = ResolveOutputRootPath(outputArchivePath, outputRootPath);
        Directory.CreateDirectory(resolvedOutputRootPath);
        File.WriteAllText(Path.Combine(resolvedOutputRootPath, "debug-fault.nif"), "debug-fault");
        return new PatchArchiveCreationException(
            resolvedOutputRootPath,
            outputArchivePath,
            new IOException("Debug fault injection: simulated archive creation failure."));
    }

    private static string ResolveOutputRootPath(string outputArchivePath, string? outputRootPath)
    {
        if (!string.IsNullOrWhiteSpace(outputRootPath))
        {
            return Path.GetFullPath(outputRootPath);
        }

        var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(outputArchivePath))
                               ?? PatchOutputPaths.GetApplicationHomeDirectory();
        return Path.Combine(archiveDirectory, PatchOutputPaths.OutputModName);
    }

    private sealed class DebugFaultInjectingPatchProgress(
        IProgress<PatchProgressUpdate>? innerProgress,
        DebugPatchFailureMode patchFailureMode,
        string outputArchivePath,
        string outputRootPath) : IProgress<PatchProgressUpdate>
    {
        private bool hasInjectedFailure;

        public void Report(PatchProgressUpdate value)
        {
            if (!hasInjectedFailure &&
                ShouldInjectLowDiskFailure(value.CurrentFilePath, patchFailureMode))
            {
                hasInjectedFailure = true;
                throw patchFailureMode switch
                {
                    DebugPatchFailureMode.PatchLowDiskWritingOutputManifest =>
                        DebugFaultExceptionFactory.CreateLowDiskSpaceException(
                            PatchExecutionStages.WritingOutputManifest,
                            Path.Combine(outputRootPath, PatchOutputPaths.OutputManifestFileName)),
                    DebugPatchFailureMode.PatchLowDiskCreatingArchive =>
                        DebugFaultExceptionFactory.CreateLowDiskSpaceException(
                            PatchExecutionStages.CreatingArchive,
                            outputArchivePath),
                    DebugPatchFailureMode.PatchLowDiskWritingRunManifest =>
                        DebugFaultExceptionFactory.CreateLowDiskSpaceException(
                            PatchExecutionStages.WritingRunManifest,
                            Path.Combine(
                                PatchOutputPaths.GetApplicationHomeDirectory(),
                                PatchOutputPaths.BackupsFolderName,
                                "debug-fault-run",
                                PatchOutputPaths.BackupManifestFileName)),
                    _ => throw new InvalidOperationException("Unexpected debug patch fault mode."),
                };
            }

            innerProgress?.Report(value);
        }

        private static bool ShouldInjectLowDiskFailure(string currentFilePath, DebugPatchFailureMode mode)
        {
            if (!currentFilePath.StartsWith(StatusPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            return mode switch
            {
                DebugPatchFailureMode.PatchLowDiskWritingOutputManifest =>
                    currentFilePath.Contains(StatusFinalizingManifest, StringComparison.OrdinalIgnoreCase),
                DebugPatchFailureMode.PatchLowDiskCreatingArchive =>
                    currentFilePath.Contains(StatusCreatingArchive, StringComparison.OrdinalIgnoreCase),
                DebugPatchFailureMode.PatchLowDiskWritingRunManifest =>
                    currentFilePath.Contains(StatusWritingRunManifest, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
    }
}

public sealed class DebugFaultInjectingNifMeshService(DebugFaultState faultState, INifMeshService innerService) : INifMeshService
{
    public Task<IReadOnlyList<NifShapeProbe>> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return innerService.ProbeAsync(filePath, cancellationToken);
    }

    public Task WritePatchedFileAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyList<ShapePatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        if (faultState.TryConsumeLowDiskPatchedWriteFailure())
        {
            throw DebugFaultExceptionFactory.CreateLowDiskSpaceException(
                PatchExecutionStages.WritingPatchedFiles,
                outputPath);
        }

        if (faultState.TryConsumeSingleFilePatchFailure())
        {
            throw new IOException("Debug fault injection: simulated single-file patch write failure.");
        }

        return innerService.WritePatchedFileAsync(sourcePath, outputPath, operations, cancellationToken);
    }
}

public sealed class DebugFaultInjectingScanService(DebugFaultState faultState, IScanService innerService) : IScanService
{
    public async Task<ScanReport> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (faultState.ScanFailureMode == DebugPatchFailureMode.ScanUnexpectedFailure)
        {
            throw new InvalidOperationException("Debug fault injection: simulated unexpected failure during scan.");
        }

        var report = await innerService.ScanAsync(request, progress, cancellationToken).ConfigureAwait(false);
        if (faultState.ScanFailureMode != DebugPatchFailureMode.ScanInjectSingleErrorFile)
        {
            return report;
        }

        var injectedErrorFile = new FileScanResult(
            Path.Combine(request.RootPath, "meshes", "__debug_fault__", "scan_error.nif"),
            [],
            "Debug fault injection: simulated scan read failure.");
        var files = report.Files.Concat([injectedErrorFile]).ToArray();
        var augmented = ScanReport.Create(report.Request, files);
        return augmented with { ScanErrorLogPath = report.ScanErrorLogPath };
    }
}

internal static class DebugFaultExceptionFactory
{
    private const long SimulatedRequiredBytes = 256L * 1024 * 1024;

    public static LowDiskSpaceException CreateLowDiskSpaceException(string stageName, string targetPath)
    {
        var normalizedTargetPath = Path.GetFullPath(targetPath);
        var appHome = PatchOutputPaths.GetApplicationHomeDirectory();
        var extractedSourcesPath = Path.Combine(appHome, "ExtractedSources");
        var generatedModsPath = Path.Combine(appHome, "GeneratedMods");
        var recoveryHint =
            $"Managed cache: '{extractedSourcesPath}'. Generated output folder: '{generatedModsPath}'. You can also pick a destination on another drive.";
        var quickCleanupCommand =
            $"Remove-Item -LiteralPath '{EscapePowerShellSingleQuotedPath(extractedSourcesPath)}' -Recurse -Force";

        return new LowDiskSpaceException(
            stageName,
            normalizedTargetPath,
            SimulatedRequiredBytes,
            0,
            recoveryHint,
            quickCleanupCommand);
    }

    private static string EscapePowerShellSingleQuotedPath(string path)
    {
        return path.Replace("'", "''", StringComparison.Ordinal);
    }
}
