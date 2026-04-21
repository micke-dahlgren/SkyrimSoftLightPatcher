using System.Diagnostics;
using System.Text;
using System.Threading;
using SkyrimGlowingMeshPatcher.App.Models;
using SkyrimGlowingMeshPatcher.Core.Models;
using SkyrimGlowingMeshPatcher.Core.Utilities;

namespace SkyrimGlowingMeshPatcher.App.ViewModels;

public partial class MainWindowViewModel
{
    private const string TechnicalErrorLinkPrefix = "We saved the error output to";

    private async Task ScanAsync()
    {
        ApplySelectedDebugFaultMode();

        if (!Directory.Exists(RootPath))
        {
            SetStatusError("Please choose a valid source folder before scanning.");
            return;
        }

        hasScanStarted = true;
        OnPropertyChanged(nameof(HasScanStarted));
        OnPropertyChanged(nameof(ShowScanSections));
        OnPropertyChanged(nameof(HasScanResults));

        scanCancellationTokenSource?.Dispose();
        scanCancellationTokenSource = new CancellationTokenSource();
        scanStopRequested = false;

        var scanCompleted = false;
        await RunBusyOperationAsync("Scanning meshes...", async () =>
        {
            IsScanning = true;
            IsSettingsLocked = true;
            ResetScanPreview(clearScanStarted: false);
            var progress = new Progress<ScanProgressUpdate>(ApplyProgress);
            var report = await scanService.ScanAsync(
                new ScanRequest(
                    RootPath,
                    CreatePatchSettings(),
                    HasSkyrimDataPath ? SkyrimDataPath : null,
                    GetSelectedModManagerKind()),
                progress,
                scanCancellationTokenSource.Token);
            currentReport = report;
            LatestScanErrorLogPath = report.ScanErrorLogPath;
            ApplyReport(report);
            await LoadCurrentOutputAsync();
            var totalPatchableShapes = report.PatchableEyeShapes + report.PatchableBodyShapes + report.PatchableOtherShapes;
            var baseMessage = $"Scan complete. Found {report.CandidateFiles} file(s) to patch ({totalPatchableShapes} shape(s)).";
            var statusMessage = report.ErrorFiles > 0
                ? !string.IsNullOrWhiteSpace(report.ScanErrorLogPath)
                    ? $"{baseMessage} Could not read {report.ErrorFiles} file(s). You can still patch the rest. Open Error Log if you want details."
                    : $"{baseMessage} Could not read {report.ErrorFiles} file(s). You can still patch the rest."
                : baseMessage;
            if (report.ErrorFiles > 0)
            {
                SetStatusError(statusMessage);
            }
            else
            {
                SetStatusSuccess(statusMessage);
            }
            scanCompleted = true;
        },
        onCanceled: () =>
        {
            if (!scanStopRequested)
            {
                SetStatusError("Scan canceled.");
            }
        },
        onFinally: () =>
        {
            IsScanning = false;
            scanCancellationTokenSource?.Dispose();
            scanCancellationTokenSource = null;
            scanStopRequested = false;
        });
        if (scanCompleted)
        {
            BusyStateText = "Scan complete";
            BusyStateColor = "#B9F6CA";
        }
    }

    private async Task DetectVortexAsync()
    {
        var manager = GetSelectedModManagerKind();
        var managerLabel = GetSelectedModManagerLabel();
        var preferredMo2Kind = GetSelectedModOrganizer2InstanceKind();

        await RunBusyOperationAsync($"Looking for {managerLabel} folders...", async () =>
        {
            var detected = manager == ModManagerKind.ModOrganizer2
                ? await TryDetectAndApplyModOrganizer2RootAsync(forceStatusMessage: true, preferredMo2Kind)
                : await TryDetectAndApplyVortexRootAsync(forceStatusMessage: true);
            if (!detected)
            {
                SetStatusError($"Couldn't find your Skyrim folder automatically for {managerLabel}. Please choose it manually.");
            }
        });
    }

    private async Task DetectSkyrimDataAsync()
    {
        await RunBusyOperationAsync("Looking for Skyrim Data folder...", async () =>
        {
            var dataPath = await vortexPathResolver.TryResolveSkyrimDataPathAsync();
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                SetStatusError("Couldn't find your Skyrim Data folder automatically. Please choose it manually.");
                return;
            }

            SkyrimDataPath = dataPath;
            SetStatusInfo($"Found Skyrim Data folder: {SkyrimDataPath}");
        });
    }

    private async Task PatchAsync()
    {
        ApplySelectedDebugFaultMode();

        if (currentReport is null)
        {
            SetStatusError("Please run a scan first.");
            return;
        }

        var selectedReport = CreateSelectedReport();
        if (selectedReport is null || selectedReport.PatchableShapes == 0)
        {
            SetStatusError("Select at least one file to patch.");
            return;
        }

        if (!HasOutputDestination)
        {
            SetStatusError("Please choose where to save the patch first.");
            return;
        }

        var outputArchivePath = CreateOutputArchivePath(OutputDestinationPath);
        var outputRootPath = Path.Combine(OutputDestinationPath, PatchOutputPaths.OutputModName);
        patchCancellationTokenSource?.Dispose();
        patchCancellationTokenSource = new CancellationTokenSource();
        patchStopRequested = false;
        canStopPatch = true;
        PatchProgressFileText = string.Empty;
        RefreshCommandState();

        IsPatching = true;
        var patchProgressToken = BeginPatchProgressTracking();
        await RunBusyOperationAsync("Patching meshes...", async () =>
        {
            var patchProgress = new Progress<PatchProgressUpdate>(update => ApplyPatchProgress(update, patchProgressToken));
            PatchRunManifest manifest;
            try
            {
                manifest = await patchExecutor.ExecuteAsync(
                    selectedReport,
                    outputArchivePath,
                    outputRootPath,
                    patchProgress,
                    patchCancellationTokenSource.Token);
            }
            catch (LowDiskSpaceException lowDiskException)
            {
                InvalidatePatchProgressTracking();
                HandleLowDiskSpaceFailure(lowDiskException, outputRootPath);
                return;
            }
            catch (PatchArchiveCreationException archiveException)
            {
                InvalidatePatchProgressTracking();
                HandleArchiveCreationFailure(archiveException);
                return;
            }

            InvalidatePatchProgressTracking();
            _ = TryCleanupGeneratedOutputRoot(manifest, out _);
            var writtenFiles = manifest.Files.Count(static file => file.Status == "Patched");
            var failedFiles = manifest.Files.Count(static file => file.Status == "Failed");
            var replacementText = manifest.ReplacedExistingOutput ? "Patch updated." : "Patch complete.";
            var failedSummary = failedFiles > 0 ? $" Could not patch {failedFiles} file(s)." : string.Empty;
            var failedLogHint = failedFiles > 0
                ? $" See {PatchOutputPaths.PatchErrorLogFileName} next to the zip for details."
                : string.Empty;
            var managerInstructions = GetSelectedModManagerKind() == ModManagerKind.ModOrganizer2
                ? "Install the zip in Mod Organizer 2 and place it below your source mods."
                : "Install the zip in Vortex and load it after your source mods.";
            var statusMessage =
                $"{replacementText} Patched {writtenFiles} file(s).{failedSummary}{failedLogHint} Zip saved to: {manifest.OutputArchivePath}. {managerInstructions}";
            if (failedFiles > 0)
            {
                SetStatusError(statusMessage);
            }
            else
            {
                SetStatusSuccess(statusMessage);
            }
            CurrentOutputPath = manifest.OutputArchivePath;
            await LoadCurrentOutputAsync();
            hasPatchedInSession = true;
            patchRunDirty = false;
            OnPropertyChanged(nameof(HasPatchOutputVisible));
            RefreshCommandState();
        },
        onCanceled: () =>
        {
            InvalidatePatchProgressTracking();
            if (patchStopRequested)
            {
                SetStatusInfo("Patch canceled.");
            }
            else
            {
                SetStatusError("Patch stopped before finishing.");
            }
        },
        onFinally: () =>
        {
            InvalidatePatchProgressTracking();
            PatchProgressFileText = string.Empty;
            IsPatching = false;
            patchCancellationTokenSource?.Dispose();
            patchCancellationTokenSource = null;
            patchStopRequested = false;
            canStopPatch = true;
        });
    }

    private async Task LoadCurrentOutputAsync()
    {
        CurrentOutputPath = null;

        if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath))
        {
            return;
        }

        var runs = await outputModService.ListRunsAsync(RootPath);
        CurrentOutputPath = runs
            .OrderByDescending(static run => run.Timestamp)
            .Select(run =>
                !string.IsNullOrWhiteSpace(run.OutputArchivePath) && File.Exists(run.OutputArchivePath)
                    ? run.OutputArchivePath
                    : run.OutputRootPath)
            .FirstOrDefault();
    }

    private void OpenOutputFolder()
    {
        if (!CanOpenOutputFolder())
        {
            return;
        }

        try
        {
            var pathToOpen = File.Exists(CurrentOutputPath)
                ? Path.GetDirectoryName(CurrentOutputPath)
                : CurrentOutputPath;

            Process.Start(new ProcessStartInfo
            {
                FileName = pathToOpen!,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            SetStatusError("Couldn't open the output folder. Please open it in File Explorer.");
        }
    }

    private void OpenErrorLog()
    {
        if (!CanOpenErrorLog())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = LatestScanErrorLogPath!,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            SetStatusError("Couldn't open the error log. Please open it from the shown path.");
        }
    }

    private void OpenStatusLink()
    {
        if (!CanOpenStatusLink())
        {
            return;
        }

        try
        {
            if (File.Exists(StatusLinkPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{StatusLinkPath}\"",
                    UseShellExecute = true,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = StatusLinkPath!,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            SetStatusError("Couldn't open that path. Please open it in File Explorer.");
        }
    }

    private async Task RunBusyOperationAsync(
        string busyMessage,
        Func<Task> action,
        Action? onCanceled = null,
        Action? onFinally = null)
    {
        try
        {
            IsBusy = true;
            BusyStateText = busyMessage;
            BusyStateColor = "#A9D7FF";
            SetStatusInfo(busyMessage);
            await action();
        }
        catch (OperationCanceledException)
        {
            onCanceled?.Invoke();
        }
        catch (Exception exception)
        {
            var userMessage = ToUserFriendlyErrorMessage(exception);
            if (ShouldWriteTechnicalErrorLog(exception))
            {
                var technicalLogPath = TryWriteTechnicalErrorLog(exception);
                if (!string.IsNullOrWhiteSpace(technicalLogPath))
                {
                    SetStatusError(userMessage, TechnicalErrorLinkPrefix, technicalLogPath);
                    return;
                }
            }

            SetStatusError(userMessage);
        }
        finally
        {
            BusyStateText = "Ready";
            BusyStateColor = "#D7C29E";
            IsBusy = false;
            onFinally?.Invoke();
        }
    }

    private void ApplyReport(ScanReport report)
    {
        ModGroups.Clear();
        var useSourceModGrouping = report.PreviewFiles.Any(file => !string.IsNullOrWhiteSpace(file.Source?.SourceModName));

        foreach (var group in report.PreviewFiles
                     .GroupBy(file => GetPreviewGroupName(report.Request.RootPath, file, useSourceModGrouping), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var fileItems = group
                .OrderBy(file => GetPreviewSortKey(report.Request.RootPath, file, useSourceModGrouping), StringComparer.OrdinalIgnoreCase)
                .Select(file => new FileScanItemViewModel
                {
                    SourceResult = file,
                    DisplayPath = GetPreviewDisplayPath(report.Request.RootPath, file, useSourceModGrouping),
                    Summary = $"{file.PatchCandidateCount} patchable shape(s)",
                    PatchCandidateCountText = $"{file.PatchCandidateCount} patchable",
                    IsExpanded = false,
                    Shapes = file.PatchCandidateShapes.Select(shape => new ShapeScanItemViewModel
                    {
                        ShapeName = string.IsNullOrWhiteSpace(shape.Probe.ShapeName) ? "(unnamed shape)" : shape.Probe.ShapeName,
                        KindText = shape.Kind.ToString(),
                        ValueText = FormatValueText(shape),
                        DecisionText = "Patch candidate",
                        ReasonSummary = string.Join(" ", shape.Reasons),
                    }).ToArray(),
                })
                .ToArray();

            var groupViewModel = new ModScanGroupViewModel(group.Key, fileItems);
            groupViewModel.SelectionChanged += HandleSelectionChanged;
            ModGroups.Add(groupViewModel);
        }

        FilesScanned = report.FilesScanned;
        PatchableEyeShapes = report.PatchableEyeShapes;
        PatchableBodyShapes = report.PatchableBodyShapes;
        PatchableOtherShapes = report.PatchableOtherShapes;
        ErrorFiles = report.ErrorFiles;
        HasNoResults = ModGroups.Count == 0;
        EmptyResultsMessage = report.FilesScanned == 0
            ? "No scan results yet. Pick a folder and run Scan."
            : "No patchable meshes found with your current settings. Click Reset, adjust your options, then scan again.";
        patchRunDirty = true;
        UpdateSelectionSummary();
        RefreshCommandState();
    }

    private void ApplyProgress(ScanProgressUpdate progress)
    {
        var totalFiles = Math.Max(1, progress.TotalFiles);
        var percent = (int)Math.Round(progress.FilesScanned * 100.0 / totalFiles);
        FilesScanned = progress.FilesScanned;
        PatchableEyeShapes = progress.PatchableEyeShapes;
        PatchableBodyShapes = progress.PatchableBodyShapes;
        PatchableOtherShapes = progress.PatchableOtherShapes;
        ErrorFiles = progress.ErrorFiles;
        BusyStateText = $"Scanning meshes... {progress.FilesScanned}/{progress.TotalFiles} ({percent}%)";
        BusyStateColor = "#A9D7FF";
        SetStatusInfo($"Scanning meshes... {progress.FilesScanned}/{progress.TotalFiles} file(s) scanned ({percent}%).");
    }

    private void ApplyPatchProgress(PatchProgressUpdate progress, long patchProgressToken)
    {
        if (!IsPatchProgressTrackingActive(patchProgressToken))
        {
            return;
        }

        var totalFiles = Math.Max(1, progress.TotalFiles);
        var percent = (int)Math.Round(progress.FilesProcessed * 100.0 / totalFiles);
        var hasReachedFinalization = progress.TotalFiles > 0 && progress.FilesProcessed >= progress.TotalFiles;
        var shouldAllowStopPatch = !hasReachedFinalization;
        if (canStopPatch != shouldAllowStopPatch)
        {
            canStopPatch = shouldAllowStopPatch;
            RefreshCommandState();
        }

        var remainingFiles = Math.Max(0, progress.TotalFiles - progress.FilesProcessed);
        BusyStateText = progress.FilesProcessed >= progress.TotalFiles && progress.TotalFiles > 0
            ? $"Creating mod file... ({percent}%)"
            : $"Patching... {progress.FilesProcessed}/{progress.TotalFiles} ({percent}%)";
        BusyStateColor = "#A9D7FF";

        var currentFilePath = progress.CurrentFilePath;
        if (!string.IsNullOrWhiteSpace(currentFilePath) &&
            currentFilePath.StartsWith("__status__:", StringComparison.Ordinal))
        {
            PatchProgressFileText = string.Empty;
            var statusText = NormalizePatchStatusText(currentFilePath["__status__:".Length..].Trim());
            BusyStateText = statusText;
            SetStatusInfo($"{statusText} {progress.FilesProcessed}/{progress.TotalFiles} file(s) patched ({percent}%), {remainingFiles} remaining.");
            return;
        }

        var currentFileName = string.IsNullOrWhiteSpace(currentFilePath)
            ? "Preparing patch output"
            : Path.GetFileName(currentFilePath);
        PatchProgressFileText = $"Current file: {currentFileName}";

        SetStatusInfo($"Patching meshes... {progress.FilesProcessed}/{progress.TotalFiles} file(s) processed ({percent}%), {remainingFiles} remaining. Current: {currentFileName}.");
    }

    private long BeginPatchProgressTracking()
    {
        var token = Interlocked.Increment(ref nextPatchProgressToken);
        Interlocked.Exchange(ref activePatchProgressToken, token);
        return token;
    }

    private void InvalidatePatchProgressTracking()
    {
        Interlocked.Exchange(ref activePatchProgressToken, 0);
    }

    private bool IsPatchProgressTrackingActive(long token)
    {
        return token != 0 && Interlocked.Read(ref activePatchProgressToken) == token;
    }

    private static string NormalizePatchStatusText(string statusText)
    {
        if (statusText.StartsWith("Creating output archive", StringComparison.OrdinalIgnoreCase))
        {
            return "Creating mod file (.zip)...";
        }

        if (statusText.StartsWith("Finalizing patch manifest", StringComparison.OrdinalIgnoreCase))
        {
            return "Getting patch files ready...";
        }

        if (statusText.StartsWith("Recording patch run metadata", StringComparison.OrdinalIgnoreCase))
        {
            return "Finishing patch...";
        }

        return statusText;
    }

    private void HandleArchiveCreationFailure(PatchArchiveCreationException archiveException)
    {
        var outputRootPath = archiveException.OutputRootPath;
        if (!string.IsNullOrWhiteSpace(outputRootPath))
        {
            CurrentOutputPath = outputRootPath;
            hasPatchedInSession = Directory.Exists(outputRootPath);
            OnPropertyChanged(nameof(HasPatchOutputVisible));
        }

        SetStatusError(
            $"Patch finished, but we couldn't create the zip file. Your patched files are saved in: {archiveException.OutputRootPath}. Please zip that folder manually before installing it.");
        patchRunDirty = true;
        RefreshCommandState();
    }

    private void HandleLowDiskSpaceFailure(LowDiskSpaceException lowDiskException, string outputRootPath)
    {
        if (!string.IsNullOrWhiteSpace(outputRootPath) && Directory.Exists(outputRootPath))
        {
            CurrentOutputPath = outputRootPath;
            hasPatchedInSession = true;
            OnPropertyChanged(nameof(HasPatchOutputVisible));
        }

        SetStatusError(BuildLowDiskStatusMessage(lowDiskException));
        patchRunDirty = true;
        RefreshCommandState();
    }

    public string GetCloseBlockedMessage()
    {
        if (IsPatching &&
            (BusyStateText.StartsWith("Creating mod file", StringComparison.OrdinalIgnoreCase) ||
             BusyStateText.StartsWith("Getting patch files ready", StringComparison.OrdinalIgnoreCase) ||
             BusyStateText.StartsWith("Finishing patch", StringComparison.OrdinalIgnoreCase)))
        {
            return "Still creating your zip file. Please wait, or click Stop Patch before closing.";
        }

        return "Patch is still running. Please wait for it to finish, or click Stop Patch before closing.";
    }

    public void NotifyCloseBlockedDuringPatch()
    {
        if (!IsPatching)
        {
            return;
        }

        SetStatusError(GetCloseBlockedMessage());
    }

    private static string CreateOutputArchivePath(string outputDestinationPath)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var seed = $"{DateTimeOffset.UtcNow:O}|{Guid.NewGuid():N}|{attempt}";
            var archiveName = PatchOutputPaths.CreateStampedArchiveFileName(seed);
            var archivePath = Path.Combine(outputDestinationPath, archiveName);
            if (!File.Exists(archivePath))
            {
                return archivePath;
            }
        }

        throw new InvalidOperationException("Unable to create a unique output archive name in the selected destination.");
    }

    private static bool TryCleanupGeneratedOutputRoot(PatchRunManifest manifest, out string cleanupError)
    {
        cleanupError = string.Empty;

        if (string.IsNullOrWhiteSpace(manifest.OutputRootPath) || !Directory.Exists(manifest.OutputRootPath))
        {
            return true;
        }

        if (!PatchOutputPaths.IsManagedOutputRoot(manifest.RootPath, manifest.OutputRootPath, manifest.OutputArchivePath))
        {
            cleanupError = "Skipped cleanup because the output folder path is unmanaged.";
            return false;
        }

        try
        {
            Directory.Delete(manifest.OutputRootPath, recursive: true);
            return true;
        }
        catch (Exception exception)
        {
            cleanupError = $"Could not remove it automatically ({exception.Message}).";
            return false;
        }
    }

    private void ResetScanPreview(bool clearScanStarted = true)
    {
        currentReport = null;
        ModGroups.Clear();
        FilesScanned = 0;
        PatchableEyeShapes = 0;
        PatchableBodyShapes = 0;
        PatchableOtherShapes = 0;
        ErrorFiles = 0;
        LatestScanErrorLogPath = null;
        HasNoResults = true;
        EmptyResultsMessage = string.Empty;
        SelectionSummaryText = string.Empty;
        if (clearScanStarted)
        {
            hasScanStarted = false;
            OnPropertyChanged(nameof(HasScanStarted));
            OnPropertyChanged(nameof(ShowScanSections));
            OnPropertyChanged(nameof(HasScanResults));
        }
        RefreshCommandState();
    }

    private void ResetScan()
    {
        ResetScanPreview();
        IsSettingsLocked = false;
        patchRunDirty = true;
        SetStatusInfo("Scan reset. Update your options and scan again.");
        BusyStateText = "Ready";
        BusyStateColor = "#D7C29E";
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        RefreshCommandState();
    }

    private void StopScanAndReset()
    {
        scanStopRequested = true;
        scanCancellationTokenSource?.Cancel();
        ResetScan();
    }

    private void StopPatch()
    {
        patchStopRequested = true;
        patchCancellationTokenSource?.Cancel();
    }

    private ScanReport? CreateSelectedReport()
    {
        if (currentReport is null)
        {
            return null;
        }

        var selectedFiles = ModGroups
            .SelectMany(static group => group.Files)
            .Where(static file => file.IsSelected)
            .Select(static file => file.SourceResult)
            .ToArray();

        return ScanReport.Create(currentReport.Request, selectedFiles);
    }

    private void HandleSelectionChanged(object? sender, EventArgs e)
    {
        patchRunDirty = true;
        UpdateSelectionSummary();
        RefreshCommandState();
    }

    private void UpdateSelectionSummary()
    {
        var selectedGroups = ModGroups.Count(static group => group.SelectedFileCount > 0);
        var selectedFiles = ModGroups.Sum(static group => group.SelectedFileCount);
        var selectedShapes = ModGroups.Sum(static group => group.SelectedShapeCount);

        SelectionSummaryText = selectedFiles == 0
            ? "No files selected for patching."
            : $"{selectedGroups} mod(s), {selectedFiles} file(s), and {selectedShapes} shape(s) selected for patching.";
    }

    private static string GetPreviewGroupName(string rootPath, FileScanResult file, bool useSourceModGrouping)
    {
        if (TryGetGameDataMeshCategory(file, out var dataCategory))
        {
            return dataCategory;
        }

        if (useSourceModGrouping && !string.IsNullOrWhiteSpace(file.Source?.SourceModName))
        {
            return file.Source.SourceModName!;
        }

        var relativePath = GetRelativePath(rootPath, file.FilePath);
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        if (useSourceModGrouping && segments.Length > 1)
        {
            return segments[0];
        }

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
        return string.IsNullOrWhiteSpace(rootName) ? "Selected Root" : rootName;
    }

    private static string GetPreviewDisplayPath(string rootPath, FileScanResult file, bool useSourceModGrouping)
    {
        if (TryGetGameDataDisplayPath(file, out var dataDisplayPath))
        {
            return dataDisplayPath;
        }

        if (file.Source is not null)
        {
            if (!useSourceModGrouping || string.IsNullOrWhiteSpace(file.Source.SourceModName))
            {
                return file.Source.DisplayPath;
            }

            return TrimLeadingModSegment(file.Source.DisplayPath, file.Source.SourceModName);
        }

        var relativePath = GetRelativePath(rootPath, file.FilePath);

        if (!useSourceModGrouping)
        {
            return relativePath;
        }

        var separatorIndex = relativePath.IndexOfAny(['\\', '/']);
        return separatorIndex >= 0 && separatorIndex < relativePath.Length - 1
            ? relativePath[(separatorIndex + 1)..]
            : relativePath;
    }

    private static string GetPreviewSortKey(string rootPath, FileScanResult file, bool useSourceModGrouping)
    {
        return GetPreviewDisplayPath(rootPath, file, useSourceModGrouping);
    }

    private static string FormatValueText(ShapeScanResult shape)
    {
        if (shape.TargetValue1.HasValue && shape.TargetValue2.HasValue)
        {
            return $"LE1 {shape.Probe.LightingEffect1:0.000}->{shape.TargetValue1.Value:0.000}, LE2 {shape.Probe.LightingEffect2:0.000}->{shape.TargetValue2.Value:0.000}";
        }

        if (shape.TargetValue1.HasValue)
        {
            return $"LE1 {shape.Probe.LightingEffect1:0.000}->{shape.TargetValue1.Value:0.000}";
        }

        if (shape.TargetValue2.HasValue)
        {
            return $"LE2 {shape.Probe.LightingEffect2:0.000}->{shape.TargetValue2.Value:0.000}";
        }

        return "No value change";
    }

    private static bool TryGetGameDataMeshCategory(FileScanResult file, out string category)
    {
        category = string.Empty;
        if (file.Source is null ||
            file.Source.Kind != MeshSourceKind.Archive ||
            !string.IsNullOrWhiteSpace(file.Source.SourceModName))
        {
            return false;
        }

        var entryPath = NormalizeSlashes(file.Source.ArchiveEntryPath ?? string.Empty);
        if (!entryPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativeMeshPath = entryPath["meshes\\".Length..];
        var topFolder = relativeMeshPath.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(topFolder))
        {
            return false;
        }

        category = $"Data/{topFolder}";
        return true;
    }

    private static bool TryGetGameDataDisplayPath(FileScanResult file, out string displayPath)
    {
        displayPath = string.Empty;
        if (file.Source is null ||
            file.Source.Kind != MeshSourceKind.Archive ||
            !string.IsNullOrWhiteSpace(file.Source.SourceModName))
        {
            return false;
        }

        var archiveName = Path.GetFileName(file.Source.ArchivePath);
        if (string.IsNullOrWhiteSpace(archiveName))
        {
            return false;
        }

        var entryPath = NormalizeSlashes(file.Source.ArchiveEntryPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        displayPath = $"{archiveName} -> {entryPath}";
        return true;
    }

    private static string TrimLeadingModSegment(string displayPath, string sourceModName)
    {
        var normalizedDisplayPath = NormalizeSlashes(displayPath);
        var prefix = NormalizeSlashes(sourceModName).TrimEnd('\\') + "\\";
        return normalizedDisplayPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedDisplayPath[prefix.Length..]
            : displayPath;
    }

    private static string NormalizeSlashes(string path)
    {
        return path.Replace('/', '\\');
    }

    private static string GetRelativePath(string rootPath, string filePath)
    {
        try
        {
            return Path.GetRelativePath(rootPath, filePath);
        }
        catch
        {
            return filePath;
        }
    }

    private PatchSettings CreatePatchSettings()
    {
        return new PatchSettings(1.0f, 1.0f, EnableOther, 1.0f, EnableEye, EnableBody);
    }

    private async Task PersistSettingsAsync()
    {
        var persistedPatchSettings = new PatchSettings(
            EyeValue: 1.0f,
            BodyValue: 1.0f,
            EnableOther: EnableOther,
            OtherValue: 1.0f,
            EnableEye: EnableEye,
            EnableBody: EnableBody);
        await settingsStore.SaveAsync(new AppSettings(null, persistedPatchSettings));
    }

    private void PersistSettingsInBackground()
    {
        if (!initialized || suppressSettingsPersistence)
        {
            return;
        }

        _ = PersistSettingsSafeAsync();
    }

    private async Task PersistSettingsSafeAsync()
    {
        try
        {
            await PersistSettingsAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to persist app settings: {exception.Message}");
        }
    }

    private async Task<bool> TryDetectAndApplyVortexRootAsync(bool forceStatusMessage)
    {
        var detectedFolder = await vortexPathResolver.TryResolveSkyrimSeAsync();
        if (detectedFolder is null)
        {
            return false;
        }

        var currentRootIsSame = !string.IsNullOrWhiteSpace(RootPath) &&
                                Directory.Exists(RootPath) &&
                                string.Equals(
                                    Path.GetFullPath(RootPath),
                                    Path.GetFullPath(detectedFolder.RootPath),
                                    StringComparison.OrdinalIgnoreCase);

        RootPath = detectedFolder.RootPath;
        if (!HasSkyrimDataPath)
        {
            var detectedDataPath = await vortexPathResolver.TryResolveSkyrimDataPathAsync();
            if (!string.IsNullOrWhiteSpace(detectedDataPath))
            {
                SkyrimDataPath = detectedDataPath;
            }
        }
        CurrentOutputPath = null;
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        IsSettingsLocked = false;
        ResetScanPreview();

        if (forceStatusMessage || !currentRootIsSame)
        {
            SetStatusInfo($"Using this folder: {RootPath}");
        }

        await PersistSettingsSafeAsync();
        return true;
    }

    private async Task<bool> TryDetectAndApplyModOrganizer2RootAsync(
        bool forceStatusMessage,
        ModOrganizer2InstanceKind? preferredKind)
    {
        var detectedInstance = await modOrganizer2PathResolver.TryResolveSkyrimSeAsync(preferredKind);
        if (detectedInstance is null)
        {
            return false;
        }

        var currentRootIsSame = !string.IsNullOrWhiteSpace(RootPath) &&
                                Directory.Exists(RootPath) &&
                                string.Equals(
                                    Path.GetFullPath(RootPath),
                                    Path.GetFullPath(detectedInstance.InstancePath),
                                    StringComparison.OrdinalIgnoreCase);

        RootPath = detectedInstance.InstancePath;
        if (!HasSkyrimDataPath)
        {
            var detectedDataPath = await vortexPathResolver.TryResolveSkyrimDataPathAsync();
            if (!string.IsNullOrWhiteSpace(detectedDataPath))
            {
                SkyrimDataPath = detectedDataPath;
            }
        }

        CurrentOutputPath = null;
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        IsSettingsLocked = false;
        ResetScanPreview();

        if (forceStatusMessage || !currentRootIsSame)
        {
            var profileSuffix = string.IsNullOrWhiteSpace(detectedInstance.SelectedProfileName)
                ? string.Empty
                : $" Profile: {detectedInstance.SelectedProfileName}.";
            SetStatusInfo($"Using this folder: {RootPath}.{profileSuffix}");
        }

        await PersistSettingsSafeAsync();
        return true;
    }

    private bool CanScan()
    {
        return !IsBusy && HasRootPath && HasOutputDestination && HasAnyEnabledCategory;
    }

    private bool CanStopScan()
    {
        return IsScanning;
    }

    private bool CanPatch()
    {
        return !IsBusy && !IsPatching && HasOutputDestination && patchRunDirty && GetSelectedShapeCount() > 0;
    }

    private bool CanStopPatch()
    {
        return IsPatching && canStopPatch;
    }

    private bool CanResetScan()
    {
        return !IsBusy && IsSettingsLocked;
    }

    private int GetSelectedShapeCount()
    {
        return ModGroups.Sum(static group => group.SelectedShapeCount);
    }

    private bool CanOpenOutputFolder()
    {
        return !IsBusy &&
               !string.IsNullOrWhiteSpace(CurrentOutputPath) &&
               (File.Exists(CurrentOutputPath) || Directory.Exists(CurrentOutputPath));
    }

    private bool CanOpenErrorLog()
    {
        return !IsBusy &&
               !string.IsNullOrWhiteSpace(LatestScanErrorLogPath) &&
               File.Exists(LatestScanErrorLogPath);
    }

    private bool CanOpenStatusLink()
    {
        return !IsBusy &&
               !string.IsNullOrWhiteSpace(StatusLinkPath) &&
               (File.Exists(StatusLinkPath) || Directory.Exists(StatusLinkPath));
    }

    private void RefreshCommandState()
    {
        DetectVortexCommand.NotifyCanExecuteChanged();
        DetectSkyrimDataCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        StopScanCommand.NotifyCanExecuteChanged();
        PatchCommand.NotifyCanExecuteChanged();
        StopPatchCommand.NotifyCanExecuteChanged();
        ResetScanCommand.NotifyCanExecuteChanged();
        OpenErrorLogCommand.NotifyCanExecuteChanged();
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        OpenStatusLinkCommand.NotifyCanExecuteChanged();
    }

    private void SetStatusInfo(string message)
    {
        SetStatus(message, "#A9D7FF");
    }

    private void SetStatusError(string message)
    {
        SetStatus(message, "#FFB3B3");
    }

    private void SetStatusError(string message, string linkPrefix, string linkPath)
    {
        SetStatus(message, "#FFB3B3", linkPrefix, linkPath);
    }

    private void SetStatusSuccess(string message)
    {
        SetStatus(message, "#B9F6CA");
    }

    private void SetStatus(
        string message,
        string color,
        string? linkPrefix = null,
        string? linkPath = null)
    {
        StatusMessage = message;
        StatusColor = color;
        StatusLinkPrefix = !string.IsNullOrWhiteSpace(linkPath) && !string.IsNullOrWhiteSpace(linkPrefix)
            ? linkPrefix
            : string.Empty;
        StatusLinkPath = !string.IsNullOrWhiteSpace(linkPath)
            ? linkPath
            : null;
    }

    private static string ToUserFriendlyErrorMessage(Exception exception)
    {
        if (exception is LowDiskSpaceException lowDiskSpaceException)
        {
            return BuildLowDiskStatusMessage(lowDiskSpaceException);
        }

        if (exception is PatchArchiveCreationException patchArchiveCreationException)
        {
            return $"Patch finished, but we couldn't create the zip file. Your patched files are saved in: {patchArchiveCreationException.OutputRootPath}. Please zip that folder manually before installing it.";
        }

        return "Something went wrong. Please try again.";
    }

    private static bool ShouldWriteTechnicalErrorLog(Exception exception)
    {
        return exception is not LowDiskSpaceException and not PatchArchiveCreationException;
    }

    private static string? TryWriteTechnicalErrorLog(Exception exception)
    {
        try
        {
            var appHome = PatchOutputPaths.GetApplicationHomeDirectory();
            var errorLogPath = Path.Combine(appHome, PatchOutputPaths.PatchErrorLogFileName);
            var errorLogContent = BuildTechnicalErrorLogContent(exception);
            Directory.CreateDirectory(Path.GetDirectoryName(errorLogPath)!);
            File.WriteAllText(errorLogPath, errorLogContent);
            return errorLogPath;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTechnicalErrorLogContent(Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Skyrim Glowing Mesh Patcher - technical error log");
        builder.AppendLine($"Created: {DateTimeOffset.Now:O}");
        builder.AppendLine();

        var depth = 0;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            depth++;
            builder.AppendLine($"Exception #{depth}: {current.GetType().FullName}");
            builder.AppendLine($"Message: {current.Message}");
            builder.AppendLine("Stack trace:");
            builder.AppendLine(string.IsNullOrWhiteSpace(current.StackTrace) ? "(none)" : current.StackTrace);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildLowDiskStatusMessage(LowDiskSpaceException exception)
    {
        var stageText = ToUserFriendlyStageName(exception.StageName);
        var requiredText = FormatBytes(exception.RequiredBytes);
        var availableText = FormatBytes(exception.AvailableBytes);
        var neededBytes = Math.Max(0, exception.RequiredBytes - exception.AvailableBytes);
        var neededText = FormatBytes(neededBytes);
        return $"Not enough disk space while {stageText}. Need about {requiredText} but only {availableText} is available at {exception.TargetPath}. Free at least {neededText} and run Patch again.";
    }

    private static string ToUserFriendlyStageName(string stageName)
    {
        return stageName switch
        {
            PatchExecutionStages.PreparingOutput => "preparing the output folder",
            PatchExecutionStages.WritingPatchedFiles => "writing patched files",
            PatchExecutionStages.WritingOutputManifest => "saving patch details",
            PatchExecutionStages.CreatingArchive => "creating the zip file",
            PatchExecutionStages.WritingRunManifest => "finishing the patch",
            _ => stageName,
        };
    }

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unitIndex = 0;
        var size = (double)value;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private ModManagerKind GetSelectedModManagerKind()
    {
        return SelectedModManager.StartsWith("Mod Organizer 2", StringComparison.OrdinalIgnoreCase)
            ? ModManagerKind.ModOrganizer2
            : ModManagerKind.Vortex;
    }

    private ModOrganizer2InstanceKind? GetSelectedModOrganizer2InstanceKind()
    {
        if (string.Equals(SelectedModManager, "Mod Organizer 2 (Global)", StringComparison.OrdinalIgnoreCase))
        {
            return ModOrganizer2InstanceKind.Global;
        }

        if (string.Equals(SelectedModManager, "Mod Organizer 2 (Portable)", StringComparison.OrdinalIgnoreCase))
        {
            return ModOrganizer2InstanceKind.Portable;
        }

        return null;
    }

    private string GetSelectedModManagerLabel()
    {
        return GetSelectedModManagerKind() == ModManagerKind.ModOrganizer2
            ? SelectedModManager
            : "Vortex";
    }

}
