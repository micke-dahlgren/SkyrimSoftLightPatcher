using System.Diagnostics;
using SkyrimLightingPatcher.App.Models;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.App.ViewModels;

public partial class MainWindowViewModel
{
    private async Task ScanAsync()
    {
        if (!Directory.Exists(RootPath))
        {
            SetStatusError("Choose a valid folder before scanning.");
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
            var baseMessage =
                $"Scan complete. Found {report.PatchableEyeShapes} patchable eye shape(s), {report.PatchableBodyShapes} patchable body shape(s), and {report.PatchableOtherShapes} patchable other shape(s) across {report.CandidateFiles} file(s).";
            StatusMessage = report.ErrorFiles > 0 && !string.IsNullOrWhiteSpace(report.ScanErrorLogPath)
                ? $"{baseMessage} Encountered {report.ErrorFiles} error file(s). Error log: {report.ScanErrorLogPath}"
                : baseMessage;
            StatusColor = report.ErrorFiles > 0 ? "#FFB3B3" : "#B9F6CA";
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
                SetStatusError($"No Skyrim SE {managerLabel} folder was detected.");
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
                SetStatusError("Could not auto-detect Skyrim Data folder.");
                return;
            }

            SkyrimDataPath = dataPath;
            SetStatusInfo($"Detected Skyrim Data folder: {SkyrimDataPath}");
        });
    }

    private async Task PatchAsync()
    {
        if (currentReport is null)
        {
            SetStatusError("Run a scan before patching.");
            return;
        }

        var selectedReport = CreateSelectedReport();
        if (selectedReport is null || selectedReport.PatchableShapes == 0)
        {
            SetStatusError("Select at least one patchable file before patching.");
            return;
        }

        if (!HasOutputDestination)
        {
            SetStatusError("Select an output destination folder before patching.");
            return;
        }

        var outputArchivePath = CreateOutputArchivePath(OutputDestinationPath);
        var outputRootPath = Path.Combine(OutputDestinationPath, PatchOutputPaths.OutputModName);
        patchCancellationTokenSource?.Dispose();
        patchCancellationTokenSource = new CancellationTokenSource();
        patchStopRequested = false;
        canStopPatch = true;
        RefreshCommandState();

        IsPatching = true;
        await RunBusyOperationAsync("Patching meshes...", async () =>
        {
            var patchProgress = new Progress<PatchProgressUpdate>(ApplyPatchProgress);
            PatchRunManifest manifest;
            try
            {
                manifest = await patchExecutor.ExecuteAsync(
                    selectedReport,
                    outputArchivePath,
                    patchProgress,
                    patchCancellationTokenSource.Token,
                    outputRootPath);
            }
            catch (LowDiskSpaceException lowDiskException)
            {
                HandleLowDiskSpaceFailure(lowDiskException, outputRootPath);
                return;
            }
            catch (PatchArchiveCreationException archiveException)
            {
                HandleArchiveCreationFailure(archiveException);
                return;
            }

            var cleanupSucceeded = TryCleanupGeneratedOutputRoot(manifest, out var cleanupError);
            var writtenFiles = manifest.Files.Count(static file => file.Status == "Patched");
            var failedFiles = manifest.Files.Count(static file => file.Status == "Failed");
            var replacementText = manifest.ReplacedExistingOutput ? "Replaced existing output mod." : "Created output mod.";
            var managerInstructions = GetSelectedModManagerKind() == ModManagerKind.ModOrganizer2
                ? "Install that archive in Mod Organizer 2 and place it below the source mods so it wins conflicts."
                : "Import that archive into Vortex and make it win conflicts against the source mods.";
            var cleanupMessage = cleanupSucceeded
                ? " Removed temporary loose files folder."
                : $" Kept loose files folder: {manifest.OutputRootPath}. {cleanupError}";
            StatusMessage =
                $"{replacementText} Wrote {writtenFiles} file(s), failed {failedFiles}. Archive: {manifest.OutputArchivePath}.{cleanupMessage} {managerInstructions} Rebuild if your mesh setup changes.";
            StatusColor = failedFiles > 0 ? "#FFB3B3" : "#B9F6CA";
            CurrentOutputPath = manifest.OutputArchivePath;
            await LoadCurrentOutputAsync();
            hasPatchedInSession = true;
            patchRunDirty = false;
            OnPropertyChanged(nameof(HasPatchOutputVisible));
            RefreshCommandState();
        },
        onCanceled: () =>
        {
            if (patchStopRequested)
            {
                SetStatusInfo("Patch stopped.");
            }
            else
            {
                SetStatusError("Patch canceled.");
            }
        },
        onFinally: () =>
        {
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
        catch (Exception exception)
        {
            SetStatusError($"Unable to open the generated output folder: {exception.Message}");
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
        catch (Exception exception)
        {
            SetStatusError($"Unable to open the scan error log: {exception.Message}");
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
            SetStatusError(exception.Message);
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
            : "No patchable meshes found for the current folder and values. Click Reset to adjust settings, then scan again.";
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
        StatusMessage = $"Scanning meshes... {progress.FilesScanned}/{progress.TotalFiles} file(s) scanned ({percent}%).";
        StatusColor = "#A9D7FF";
    }

    private void ApplyPatchProgress(PatchProgressUpdate progress)
    {
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
            var statusText = NormalizePatchStatusText(currentFilePath["__status__:".Length..].Trim());
            BusyStateText = statusText;
            StatusMessage =
                $"{statusText} {progress.FilesProcessed}/{progress.TotalFiles} file(s) patched ({percent}%), {remainingFiles} remaining.";
            StatusColor = "#A9D7FF";
            return;
        }

        var currentFileName = string.IsNullOrWhiteSpace(currentFilePath)
            ? "Preparing patch output"
            : Path.GetFileName(currentFilePath);

        StatusMessage =
            $"Patching meshes... {progress.FilesProcessed}/{progress.TotalFiles} file(s) processed ({percent}%), {remainingFiles} remaining. Current: {currentFileName}.";
        StatusColor = "#A9D7FF";
    }

    private static string NormalizePatchStatusText(string statusText)
    {
        if (statusText.StartsWith("Creating output archive", StringComparison.OrdinalIgnoreCase))
        {
            return "Creating mod file (.zip)...";
        }

        if (statusText.StartsWith("Finalizing patch manifest", StringComparison.OrdinalIgnoreCase))
        {
            return "Preparing mod files...";
        }

        if (statusText.StartsWith("Recording patch run metadata", StringComparison.OrdinalIgnoreCase))
        {
            return "Saving patch metadata...";
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

        var archiveError = archiveException.InnerException?.Message ?? archiveException.Message;
        StatusMessage =
            $"Patched files were created, but creating the archive failed: {archiveError}. Loose files are in {archiveException.OutputRootPath}. Create a .zip or .7z from that folder before installing it as a mod.";
        StatusColor = "#FFB3B3";
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

        StatusMessage = lowDiskException.Message;
        StatusColor = "#FFB3B3";
        patchRunDirty = true;
        RefreshCommandState();
    }

    public string GetCloseBlockedMessage()
    {
        if (IsPatching &&
            (BusyStateText.StartsWith("Creating mod file", StringComparison.OrdinalIgnoreCase) ||
             BusyStateText.StartsWith("Preparing mod files", StringComparison.OrdinalIgnoreCase) ||
             BusyStateText.StartsWith("Saving patch metadata", StringComparison.OrdinalIgnoreCase)))
        {
            return "Still creating the mod archive. Please wait for completion or click Stop Patch before closing.";
        }

        return "Patch is still running. Please wait for completion or click Stop Patch before closing.";
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
        SetStatusInfo("Scan reset. You can adjust values and scan again.");
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
            SetStatusInfo($"{detectedFolder.Source} Using {RootPath}.");
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
            SetStatusInfo($"{detectedInstance.Source} Using {RootPath}.{profileSuffix}");
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
    }

    private void SetStatusInfo(string message)
    {
        StatusMessage = message;
        StatusColor = "#A9D7FF";
    }

    private void SetStatusError(string message)
    {
        StatusMessage = message;
        StatusColor = "#FFB3B3";
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
