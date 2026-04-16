using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimLightingPatcher.App.Models;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsStore settingsStore;
    private readonly IScanService scanService;
    private readonly IPatchExecutor patchExecutor;
    private readonly IOutputModService outputModService;
    private readonly IVortexPathResolver vortexPathResolver;
    private ScanReport? currentReport;
    private bool initialized;
    private CancellationTokenSource? scanCancellationTokenSource;
    private CancellationTokenSource? patchCancellationTokenSource;
    private bool scanStopRequested;
    private bool patchStopRequested;
    private bool hasPatchedInSession;
    private bool hasScanStarted;

    public MainWindowViewModel()
    {
        settingsStore = new DesignTimeSettingsStore();
        scanService = new DesignTimeScanService();
        patchExecutor = new DesignTimePatchExecutor();
        outputModService = new DesignTimeOutputModService();
        vortexPathResolver = new DesignTimeVortexPathResolver();

        DetectVortexCommand = new AsyncRelayCommand(DetectVortexAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        StopScanCommand = new RelayCommand(StopScanAndReset, CanStopScan);
        PatchCommand = new AsyncRelayCommand(PatchAsync, CanPatch);
        StopPatchCommand = new RelayCommand(StopPatch, CanStopPatch);
        ResetScanCommand = new RelayCommand(ResetScan, CanResetScan);
        OpenErrorLogCommand = new RelayCommand(OpenErrorLog, CanOpenErrorLog);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, CanOpenOutputFolder);
    }

    public MainWindowViewModel(
        ISettingsStore settingsStore,
        IScanService scanService,
        IPatchExecutor patchExecutor,
        IOutputModService outputModService,
        IVortexPathResolver vortexPathResolver)
    {
        this.settingsStore = settingsStore;
        this.scanService = scanService;
        this.patchExecutor = patchExecutor;
        this.outputModService = outputModService;
        this.vortexPathResolver = vortexPathResolver;

        DetectVortexCommand = new AsyncRelayCommand(DetectVortexAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        StopScanCommand = new RelayCommand(StopScanAndReset, CanStopScan);
        PatchCommand = new AsyncRelayCommand(PatchAsync, CanPatch);
        StopPatchCommand = new RelayCommand(StopPatch, CanStopPatch);
        ResetScanCommand = new RelayCommand(ResetScan, CanResetScan);
        OpenErrorLogCommand = new RelayCommand(OpenErrorLog, CanOpenErrorLog);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, CanOpenOutputFolder);
    }

    public ObservableCollection<ModScanGroupViewModel> ModGroups { get; } = [];

    public IAsyncRelayCommand DetectVortexCommand { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IRelayCommand StopScanCommand { get; }

    public IAsyncRelayCommand PatchCommand { get; }

    public IRelayCommand StopPatchCommand { get; }

    public IRelayCommand ResetScanCommand { get; }

    public IRelayCommand OpenErrorLogCommand { get; }

    public IRelayCommand OpenOutputFolderCommand { get; }

    [ObservableProperty]
    private string rootPath = string.Empty;

    [ObservableProperty]
    private double eyeValue = 0.5;

    [ObservableProperty]
    private double bodyValue = 0.3;

    [ObservableProperty]
    private string outputDestinationPath = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isSettingsLocked;

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private bool isPatching;

    [ObservableProperty]
    private string statusMessage = "Select a mesh root and output destination, then run Scan to preview patchable meshes.";

    [ObservableProperty]
    private string statusColor = "#A9D7FF";

    [ObservableProperty]
    private string busyStateText = "Ready";

    [ObservableProperty]
    private string busyStateColor = "#D7C29E";

    [ObservableProperty]
    private int filesScanned;

    [ObservableProperty]
    private int patchableEyeShapes;

    [ObservableProperty]
    private int patchableBodyShapes;

    [ObservableProperty]
    private int errorFiles;

    [ObservableProperty]
    private bool hasNoResults = true;

    [ObservableProperty]
    private string emptyResultsMessage = "No scan results yet. Pick a folder and run Scan.";

    [ObservableProperty]
    private string selectionSummaryText = "All detected patchable files are currently selected.";

    [ObservableProperty]
    private string? currentOutputPath;

    [ObservableProperty]
    private string? latestScanErrorLogPath;

    public bool HasGeneratedOutput =>
        !string.IsNullOrWhiteSpace(CurrentOutputPath) &&
        (File.Exists(CurrentOutputPath) || Directory.Exists(CurrentOutputPath));

    public bool HasPatchOutputVisible => hasPatchedInSession && HasGeneratedOutput;

    public bool HasRootPath => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);

    public bool HasOutputDestination => !string.IsNullOrWhiteSpace(OutputDestinationPath) && Directory.Exists(OutputDestinationPath);

    public bool HasConfiguredRoots => HasRootPath && HasOutputDestination;

    public bool HasScanErrorLog =>
        !string.IsNullOrWhiteSpace(LatestScanErrorLogPath) &&
        File.Exists(LatestScanErrorLogPath);

    public bool HasScanStarted => hasScanStarted;

    public bool ShowScanSections => HasConfiguredRoots && HasScanStarted;

    public bool HasScanResults => ShowScanSections && !HasNoResults;

    public bool HasSelectionSummary => !string.IsNullOrWhiteSpace(SelectionSummaryText);

    public bool CanEditSettings => HasConfiguredRoots && !IsSettingsLocked;

    public Task InitializeAsync()
    {
        if (initialized)
        {
            return Task.CompletedTask;
        }

        initialized = true;
        RootPath = string.Empty;
        EyeValue = 0.5;
        BodyValue = 0.3;
        OutputDestinationPath = string.Empty;
        CurrentOutputPath = null;
        StatusMessage = "Select a mesh root and output destination, then run Scan.";
        StatusColor = "#A9D7FF";
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        return Task.CompletedTask;
    }

    public Task SetRootPathAsync(string path)
    {
        RootPath = path;
        CurrentOutputPath = null;
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        IsSettingsLocked = false;
        ResetScanPreview();
        return Task.CompletedTask;
    }

    public Task SetOutputDestinationPathAsync(string path)
    {
        OutputDestinationPath = path;
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        return Task.CompletedTask;
    }

    partial void OnRootPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasRootPath));
        OnPropertyChanged(nameof(HasConfiguredRoots));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(ShowScanSections));
        OnPropertyChanged(nameof(HasScanResults));
        RefreshCommandState();
    }

    partial void OnEyeValueChanged(double value)
    {
        RefreshCommandState();
    }

    partial void OnBodyValueChanged(double value)
    {
        RefreshCommandState();
    }

    partial void OnOutputDestinationPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasOutputDestination));
        OnPropertyChanged(nameof(HasConfiguredRoots));
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(ShowScanSections));
        OnPropertyChanged(nameof(HasScanResults));
        RefreshCommandState();
    }

    partial void OnHasNoResultsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasScanResults));
    }

    partial void OnSelectionSummaryTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSelectionSummary));
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommandState();
    }

    partial void OnIsSettingsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditSettings));
        RefreshCommandState();
    }

    partial void OnIsScanningChanged(bool value)
    {
        RefreshCommandState();
    }

    partial void OnIsPatchingChanged(bool value)
    {
        RefreshCommandState();
    }

    partial void OnCurrentOutputPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasGeneratedOutput));
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        RefreshCommandState();
    }

    partial void OnLatestScanErrorLogPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScanErrorLog));
        RefreshCommandState();
    }

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
                new ScanRequest(RootPath, CreatePatchSettings()),
                progress,
                scanCancellationTokenSource.Token);
            currentReport = report;
            LatestScanErrorLogPath = report.ScanErrorLogPath;
            ApplyReport(report);
            await LoadCurrentOutputAsync();
            var baseMessage =
                $"Scan complete. Found {report.PatchableEyeShapes} patchable eye shape(s) and {report.PatchableBodyShapes} patchable body shape(s) across {report.CandidateFiles} file(s).";
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
        await RunBusyOperationAsync("Looking for a Vortex staging folder...", async () =>
        {
            var detected = await TryDetectAndApplyVortexRootAsync(forceStatusMessage: true);
            if (!detected)
            {
                SetStatusError("No Skyrim SE Vortex staging folder was detected.");
            }
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

        var outputArchivePath = Path.Combine(OutputDestinationPath, $"{PatchOutputPaths.OutputModName}.zip");
        patchCancellationTokenSource?.Dispose();
        patchCancellationTokenSource = new CancellationTokenSource();
        patchStopRequested = false;

        IsPatching = true;
        await RunBusyOperationAsync("Patching meshes...", async () =>
        {
            var patchProgress = new Progress<PatchProgressUpdate>(ApplyPatchProgress);
            var manifest = await patchExecutor.ExecuteAsync(
                selectedReport,
                outputArchivePath,
                patchProgress,
                patchCancellationTokenSource.Token);
            var writtenFiles = manifest.Files.Count(static file => file.Status == "Patched");
            var failedFiles = manifest.Files.Count(static file => file.Status == "Failed");
            var replacementText = manifest.ReplacedExistingOutput ? "Replaced existing output mod." : "Created output mod.";
            StatusMessage =
                $"{replacementText} Wrote {writtenFiles} file(s), failed {failedFiles}. Archive: {manifest.OutputArchivePath}. Import that archive into Vortex and make it win conflicts against the source mods. Rebuild if your mesh setup changes.";
            StatusColor = failedFiles > 0 ? "#FFB3B3" : "#B9F6CA";
            CurrentOutputPath = manifest.OutputArchivePath;
            await LoadCurrentOutputAsync();
            hasPatchedInSession = true;
            OnPropertyChanged(nameof(HasPatchOutputVisible));
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
        var isVortexStagingRoot = File.Exists(Path.Combine(report.Request.RootPath, "__vortex_staging_folder"));

        foreach (var group in report.PreviewFiles
                     .GroupBy(file => GetPreviewGroupName(report.Request.RootPath, file, isVortexStagingRoot), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var fileItems = group
                .OrderBy(file => GetPreviewSortKey(report.Request.RootPath, file, isVortexStagingRoot), StringComparer.OrdinalIgnoreCase)
                .Select(file => new FileScanItemViewModel
                {
                    SourceResult = file,
                    DisplayPath = GetPreviewDisplayPath(report.Request.RootPath, file, isVortexStagingRoot),
                    Summary = $"{file.PatchCandidateCount} patchable shape(s)",
                    PatchCandidateCountText = $"{file.PatchCandidateCount} patchable",
                    IsExpanded = false,
                    Shapes = file.PatchCandidateShapes.Select(shape => new ShapeScanItemViewModel
                    {
                        ShapeName = string.IsNullOrWhiteSpace(shape.Probe.ShapeName) ? "(unnamed shape)" : shape.Probe.ShapeName,
                        KindText = shape.Kind.ToString(),
                        ValueText = $"{shape.Probe.LightingEffect1:0.000} -> {shape.TargetValue!.Value:0.000}",
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
        ErrorFiles = report.ErrorFiles;
        HasNoResults = ModGroups.Count == 0;
        EmptyResultsMessage = report.FilesScanned == 0
            ? "No scan results yet. Pick a folder and run Scan."
            : "No patchable meshes found for the current folder and values.";
        UpdateSelectionSummary();
        RefreshCommandState();
    }

    private void ApplyProgress(ScanProgressUpdate progress)
    {
        FilesScanned = progress.FilesScanned;
        PatchableEyeShapes = progress.PatchableEyeShapes;
        PatchableBodyShapes = progress.PatchableBodyShapes;
        ErrorFiles = progress.ErrorFiles;
        BusyStateText = $"Scanning meshes... {progress.FilesScanned} scanned";
        BusyStateColor = "#A9D7FF";
        StatusMessage = $"Scanning meshes... {progress.FilesScanned} file(s) scanned";
        StatusColor = "#A9D7FF";
    }

    private void ApplyPatchProgress(PatchProgressUpdate progress)
    {
        var remainingFiles = Math.Max(0, progress.TotalFiles - progress.FilesProcessed);
        BusyStateText = $"Patching... {progress.FilesProcessed}/{progress.TotalFiles}";
        BusyStateColor = "#A9D7FF";

        var currentFilePath = progress.CurrentFilePath;
        if (!string.IsNullOrWhiteSpace(currentFilePath) &&
            currentFilePath.StartsWith("__status__:", StringComparison.Ordinal))
        {
            var statusText = currentFilePath["__status__:".Length..].Trim();
            StatusMessage = $"{statusText} {progress.FilesProcessed}/{progress.TotalFiles} file(s) patched, {remainingFiles} remaining.";
            StatusColor = "#A9D7FF";
            return;
        }

        var currentFileName = string.IsNullOrWhiteSpace(currentFilePath)
            ? "Preparing patch output"
            : Path.GetFileName(currentFilePath);

        StatusMessage =
            $"Patching meshes... {progress.FilesProcessed}/{progress.TotalFiles} file(s) processed, {remainingFiles} remaining. Current: {currentFileName}.";
        StatusColor = "#A9D7FF";
    }

    private void ResetScanPreview(bool clearScanStarted = true)
    {
        currentReport = null;
        ModGroups.Clear();
        FilesScanned = 0;
        PatchableEyeShapes = 0;
        PatchableBodyShapes = 0;
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
        SetStatusInfo("Scan reset. You can adjust values and scan again.");
        BusyStateText = "Ready";
        BusyStateColor = "#D7C29E";
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
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

    private static string GetPreviewGroupName(string rootPath, FileScanResult file, bool isVortexStagingRoot)
    {
        if (isVortexStagingRoot && !string.IsNullOrWhiteSpace(file.Source?.SourceModName))
        {
            return file.Source.SourceModName!;
        }

        var relativePath = GetRelativePath(rootPath, file.FilePath);
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        if (isVortexStagingRoot && segments.Length > 1)
        {
            return segments[0];
        }

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
        return string.IsNullOrWhiteSpace(rootName) ? "Selected Root" : rootName;
    }

    private static string GetPreviewDisplayPath(string rootPath, FileScanResult file, bool isVortexStagingRoot)
    {
        if (file.Source is not null)
        {
            if (!isVortexStagingRoot || string.IsNullOrWhiteSpace(file.Source.SourceModName))
            {
                return file.Source.DisplayPath;
            }

            return TrimLeadingModSegment(file.Source.DisplayPath, file.Source.SourceModName);
        }

        var relativePath = GetRelativePath(rootPath, file.FilePath);

        if (!isVortexStagingRoot)
        {
            return relativePath;
        }

        var separatorIndex = relativePath.IndexOfAny(['\\', '/']);
        return separatorIndex >= 0 && separatorIndex < relativePath.Length - 1
            ? relativePath[(separatorIndex + 1)..]
            : relativePath;
    }

    private static string GetPreviewSortKey(string rootPath, FileScanResult file, bool isVortexStagingRoot)
    {
        return GetPreviewDisplayPath(rootPath, file, isVortexStagingRoot);
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
        return new PatchSettings((float)EyeValue, (float)BodyValue).ClampToSafeRange();
    }

    private async Task PersistSettingsAsync()
    {
        await settingsStore.SaveAsync(new AppSettings(string.Empty, new PatchSettings(0.5f, 0.3f)));
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
        CurrentOutputPath = null;
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        IsSettingsLocked = false;
        ResetScanPreview();

        if (forceStatusMessage || !currentRootIsSame)
        {
            SetStatusInfo($"{detectedFolder.Source} Using {RootPath}.");
        }

        return true;
    }

    private bool CanScan()
    {
        return !IsBusy && HasRootPath && HasOutputDestination;
    }

    private bool CanStopScan()
    {
        return IsScanning;
    }

    private bool CanPatch()
    {
        return !IsBusy && !IsPatching && HasOutputDestination && GetSelectedShapeCount() > 0;
    }

    private bool CanStopPatch()
    {
        return IsPatching;
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

    private void SetStatusSuccess(string message)
    {
        StatusMessage = message;
        StatusColor = "#B9F6CA";
    }

    private void SetStatusError(string message)
    {
        StatusMessage = message;
        StatusColor = "#FFB3B3";
    }

    private sealed class DesignTimeSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppSettings(
                @"C:\Users\Example\AppData\Roaming\Vortex\skyrimse\mods",
                new PatchSettings(0.5f, 0.3f)));
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class DesignTimeScanService : IScanService
    {
        public Task<ScanReport> ScanAsync(
            ScanRequest request,
            IProgress<ScanProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FileScanResult[] files =
            [
                new FileScanResult(
                    Path.Combine(request.RootPath, "Aesthetic Eye Pack", "meshes", "actors", "character", "eyes", "blue.nif"),
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                "example.nif",
                                "shape-0",
                                "Eyes",
                                new ShaderMetadata("EnvironmentMap", ["Soft_Lighting"]),
                                [@"textures\actors\character\eyes\blueeye.dds"],
                                true,
                                0.20f),
                            ShapeKind.Eye,
                            true,
                            request.Settings.EyeValue,
                            "Eye",
                            ["Matched eye texture directory.", "Eligible for patching."]),
                    ]),
                new FileScanResult(
                    Path.Combine(request.RootPath, "Skysight Skins", "meshes", "actors", "character", "facegendata", "facegeom", "npc_01.nif"),
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                "example.nif",
                                "shape-1",
                                "FemaleHead",
                                new ShaderMetadata("Face", ["Soft_Lighting"]),
                                [@"textures\actors\character\femalebody_1.dds"],
                                true,
                                0.15f),
                            ShapeKind.Body,
                            true,
                            request.Settings.BodyValue,
                            "Body",
                            ["Matched skin-like texture path.", "Eligible for patching."]),
                    ]),
            ];

            progress?.Report(new ScanProgressUpdate(files[0].FilePath, 1, 1, 1, 0, 1, 0));
            progress?.Report(new ScanProgressUpdate(files[1].FilePath, 2, 2, 1, 1, 2, 0));

            return Task.FromResult(ScanReport.Create(request, files));
        }
    }

    private sealed class DesignTimePatchExecutor : IPatchExecutor
    {
        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new PatchProgressUpdate("example-1.nif", 0, 2, 0, 0));
            progress?.Report(new PatchProgressUpdate("example-1.nif", 1, 2, 1, 0));
            progress?.Report(new PatchProgressUpdate("example-2.nif", 2, 2, 2, 0));
            progress?.Report(new PatchProgressUpdate("__status__: Creating output archive (.zip)...", 2, 2, 2, 0));
            return Task.FromResult(new PatchRunManifest(
                "design-time",
                report.Request.RootPath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimLightingPatcher", "GeneratedMods", "Soft Light Mesh Patcher Output"),
                outputArchivePath,
                "Soft Light Mesh Patcher Output",
                false,
                DateTimeOffset.Now,
                report.Request.Settings,
                []));
        }
    }

    private sealed class DesignTimeOutputModService : IOutputModService
    {
        public Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<BackupRunInfo> runs =
            [
                new BackupRunInfo(
                    "202604130001",
                    rootPath,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimLightingPatcher", "GeneratedMods", "Soft Light Mesh Patcher Output"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Soft Light Mesh Patcher Output.zip"),
                    "Soft Light Mesh Patcher Output",
                    DateTimeOffset.Now.AddMinutes(-30),
                    4,
                    9,
                    "manifest.json"),
            ];

            return Task.FromResult(runs);
        }

        public Task<PatchRunManifest> DeleteAsync(string runId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DesignTimeVortexPathResolver : IVortexPathResolver
    {
        public Task<VortexStagingFolder?> TryResolveSkyrimSeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<VortexStagingFolder?>(new VortexStagingFolder(
                @"C:\Users\Example\AppData\Roaming\Vortex\skyrimse\mods",
                "Detected Vortex Skyrim SE staging folder."));
        }
    }
}
