using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyrimGlowingMeshPatcher.App.Models;
using SkyrimGlowingMeshPatcher.Core.Interfaces;
using SkyrimGlowingMeshPatcher.Core.Models;
using SkyrimGlowingMeshPatcher.Core.Utilities;

namespace SkyrimGlowingMeshPatcher.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsStore settingsStore;
    private readonly IScanService scanService;
    private readonly IPatchExecutor patchExecutor;
    private readonly IOutputModService outputModService;
    private readonly IVortexPathResolver vortexPathResolver;
    private readonly IModOrganizer2PathResolver modOrganizer2PathResolver;
    private readonly DebugFaultState? debugFaultState;
    private ScanReport? currentReport;
    private bool initialized;
    private CancellationTokenSource? scanCancellationTokenSource;
    private CancellationTokenSource? patchCancellationTokenSource;
    private bool scanStopRequested;
    private bool patchStopRequested;
    private bool hasPatchedInSession;
    private bool hasScanStarted;
    private bool patchRunDirty = true;
    private bool suppressSettingsPersistence;
    private bool canStopPatch = true;
    private long nextPatchProgressToken;
    private long activePatchProgressToken;

    public MainWindowViewModel()
    {
        settingsStore = new DesignTimeSettingsStore();
        scanService = new DesignTimeScanService();
        patchExecutor = new DesignTimePatchExecutor();
        outputModService = new DesignTimeOutputModService();
        vortexPathResolver = new DesignTimeVortexPathResolver();
        modOrganizer2PathResolver = new DesignTimeModOrganizer2PathResolver();
        debugFaultState = null;

        DetectVortexCommand = new AsyncRelayCommand(DetectVortexAsync, () => !IsBusy);
        DetectSkyrimDataCommand = new AsyncRelayCommand(DetectSkyrimDataAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        StopScanCommand = new RelayCommand(StopScanAndReset, CanStopScan);
        PatchCommand = new AsyncRelayCommand(PatchAsync, CanPatch);
        StopPatchCommand = new RelayCommand(StopPatch, CanStopPatch);
        ResetScanCommand = new RelayCommand(ResetScan, CanResetScan);
        OpenErrorLogCommand = new RelayCommand(OpenErrorLog, CanOpenErrorLog);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, CanOpenOutputFolder);
        OpenStatusLinkCommand = new RelayCommand(OpenStatusLink, CanOpenStatusLink);
        SelectedDebugFaultOption = DebugFaultOptions.FirstOrDefault();
    }

    public MainWindowViewModel(
        ISettingsStore settingsStore,
        IScanService scanService,
        IPatchExecutor patchExecutor,
        IOutputModService outputModService,
        IVortexPathResolver vortexPathResolver,
        IModOrganizer2PathResolver modOrganizer2PathResolver,
        DebugFaultState? debugFaultState = null)
    {
        this.settingsStore = settingsStore;
        this.scanService = scanService;
        this.patchExecutor = patchExecutor;
        this.outputModService = outputModService;
        this.vortexPathResolver = vortexPathResolver;
        this.modOrganizer2PathResolver = modOrganizer2PathResolver;
        this.debugFaultState = debugFaultState;

        DetectVortexCommand = new AsyncRelayCommand(DetectVortexAsync, () => !IsBusy);
        DetectSkyrimDataCommand = new AsyncRelayCommand(DetectSkyrimDataAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        StopScanCommand = new RelayCommand(StopScanAndReset, CanStopScan);
        PatchCommand = new AsyncRelayCommand(PatchAsync, CanPatch);
        StopPatchCommand = new RelayCommand(StopPatch, CanStopPatch);
        ResetScanCommand = new RelayCommand(ResetScan, CanResetScan);
        OpenErrorLogCommand = new RelayCommand(OpenErrorLog, CanOpenErrorLog);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, CanOpenOutputFolder);
        OpenStatusLinkCommand = new RelayCommand(OpenStatusLink, CanOpenStatusLink);
        SelectedDebugFaultOption = DebugFaultOptions.FirstOrDefault();
    }

    public ObservableCollection<ModScanGroupViewModel> ModGroups { get; } = [];
    public IReadOnlyList<string> ModManagerOptions { get; } =
    [
        "Vortex",
        "Mod Organizer 2 (Global)",
        "Mod Organizer 2 (Portable)",
    ];
    public IReadOnlyList<DebugFaultOption> DebugFaultOptions { get; } =
    [
        new(DebugPatchFailureMode.None, "None (normal behavior)"),
        new(DebugPatchFailureMode.PatchLowDiskPreparingOutput, "Patch: low disk while preparing output folder"),
        new(DebugPatchFailureMode.PatchLowDiskWritingPatchedFiles, "Patch: low disk while writing patched files"),
        new(DebugPatchFailureMode.PatchLowDiskWritingOutputManifest, "Patch: low disk while writing patch manifest"),
        new(DebugPatchFailureMode.PatchLowDiskCreatingArchive, "Patch: low disk while creating output archive"),
        new(DebugPatchFailureMode.PatchLowDiskWritingRunManifest, "Patch: low disk while writing run manifest"),
        new(DebugPatchFailureMode.PatchInjectSingleFileFailure, "Patch: inject one file failure, continue run"),
        new(DebugPatchFailureMode.PatchArchiveCreationFailure, "Patch: archive creation failure after loose output"),
        new(DebugPatchFailureMode.PatchUnexpectedFailure, "Patch: unexpected fatal error"),
        new(DebugPatchFailureMode.ScanInjectSingleErrorFile, "Scan: inject one per-file scan error"),
        new(DebugPatchFailureMode.ScanUnexpectedFailure, "Scan: unexpected fatal error"),
    ];

    public IAsyncRelayCommand DetectVortexCommand { get; }
    public IAsyncRelayCommand DetectSkyrimDataCommand { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IRelayCommand StopScanCommand { get; }

    public IAsyncRelayCommand PatchCommand { get; }

    public IRelayCommand StopPatchCommand { get; }

    public IRelayCommand ResetScanCommand { get; }

    public IRelayCommand OpenErrorLogCommand { get; }

    public IRelayCommand OpenOutputFolderCommand { get; }

    public IRelayCommand OpenStatusLinkCommand { get; }

    [ObservableProperty]
    private string rootPath = string.Empty;

    [ObservableProperty]
    private string selectedModManager = "Vortex";

    [ObservableProperty]
    private bool enableEye = false;

    [ObservableProperty]
    private bool enableBody = false;

    [ObservableProperty]
    private bool enableOther = false;

    [ObservableProperty]
    private string outputDestinationPath = string.Empty;

    [ObservableProperty]
    private string skyrimDataPath = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isSettingsLocked;

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private bool isPatching;

    [ObservableProperty]
    private string statusMessage = "Choose your mod folder and patch destination, then click Scan.";

    [ObservableProperty]
    private string statusColor = "#A9D7FF";

    [ObservableProperty]
    private string statusLinkPrefix = string.Empty;

    [ObservableProperty]
    private string? statusLinkPath;

    [ObservableProperty]
    private string busyStateText = "Ready";

    [ObservableProperty]
    private string busyStateColor = "#D7C29E";

    [ObservableProperty]
    private string patchProgressFileText = string.Empty;

    [ObservableProperty]
    private int filesScanned;

    [ObservableProperty]
    private int patchableEyeShapes;

    [ObservableProperty]
    private int patchableBodyShapes;

    [ObservableProperty]
    private int patchableOtherShapes;

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

    [ObservableProperty]
    private DebugFaultOption? selectedDebugFaultOption;

    public bool HasGeneratedOutput =>
        !string.IsNullOrWhiteSpace(CurrentOutputPath) &&
        (File.Exists(CurrentOutputPath) || Directory.Exists(CurrentOutputPath));

    public bool HasPatchOutputVisible => hasPatchedInSession && HasGeneratedOutput;

    public bool HasRootPath => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);

    public bool HasOutputDestination => !string.IsNullOrWhiteSpace(OutputDestinationPath) && Directory.Exists(OutputDestinationPath);
    public bool HasSkyrimDataPath => !string.IsNullOrWhiteSpace(SkyrimDataPath) && Directory.Exists(SkyrimDataPath);

    public bool HasConfiguredRoots => HasRootPath && HasOutputDestination;

    public bool HasScanErrorLog =>
        !string.IsNullOrWhiteSpace(LatestScanErrorLogPath) &&
        File.Exists(LatestScanErrorLogPath);

    public bool HasScanStarted => hasScanStarted;

    public bool ShowScanSections => HasConfiguredRoots && HasScanStarted;

    public bool HasScanResults => ShowScanSections;

    public bool HasSelectionSummary => !string.IsNullOrWhiteSpace(SelectionSummaryText);

    public bool HasPatchProgressFileText => !string.IsNullOrWhiteSpace(PatchProgressFileText);

    public bool HasStatusAlert =>
        !string.IsNullOrWhiteSpace(StatusMessage) &&
        IsAlertStatusColor(StatusColor);

    public bool HasStatusLinkPath => !string.IsNullOrWhiteSpace(StatusLinkPath);

    public bool IsDebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public bool CanEditSettings => HasConfiguredRoots && !IsSettingsLocked;
    public bool IsFolderSectionEnabled => !IsScanning && !IsPatching;
    public bool HasAnyEnabledCategory => EnableEye || EnableBody || EnableOther;

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        suppressSettingsPersistence = true;
        try
        {
            var loadedSettings = await settingsStore.LoadAsync();
            var loadedPatchSettings = loadedSettings.PatchSettings.ClampToSafeRange();
            RootPath = string.Empty;
            EnableEye = loadedPatchSettings.EnableEye;
            EnableBody = loadedPatchSettings.EnableBody;
            EnableOther = loadedPatchSettings.EnableOther;
        }
        catch
        {
            RootPath = string.Empty;
            EnableEye = false;
            EnableBody = false;
            EnableOther = false;
        }
        finally
        {
            suppressSettingsPersistence = false;
        }

        OutputDestinationPath = string.Empty;
        SkyrimDataPath = string.Empty;
        CurrentOutputPath = null;
        SetStatusInfo("Choose your mod folder and patch destination, then click Scan.");
        hasPatchedInSession = false;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        _ = TryAutoDetectSkyrimDataPathAsync();
    }

    private async Task TryAutoDetectSkyrimDataPathAsync()
    {
        try
        {
            var detected = await vortexPathResolver.TryResolveSkyrimDataPathAsync();
            if (!string.IsNullOrWhiteSpace(detected))
            {
                SkyrimDataPath = detected;
            }
        }
        catch
        {
            // Keep startup resilient if auto-detection fails.
        }
    }

    public async Task SetRootPathAsync(string path)
    {
        RootPath = path;
        CurrentOutputPath = null;
        hasPatchedInSession = false;
        patchRunDirty = true;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        IsSettingsLocked = false;
        ResetScanPreview();
        RefreshCommandState();
        await PersistSettingsSafeAsync();
    }

    public Task SetOutputDestinationPathAsync(string path)
    {
        OutputDestinationPath = path;
        hasPatchedInSession = false;
        patchRunDirty = true;
        OnPropertyChanged(nameof(HasPatchOutputVisible));
        RefreshCommandState();
        return Task.CompletedTask;
    }

    public Task SetSkyrimDataPathAsync(string path)
    {
        SkyrimDataPath = path;
        patchRunDirty = true;
        RefreshCommandState();
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

    partial void OnEnableEyeChanged(bool value)
    {
        patchRunDirty = true;
        PersistSettingsInBackground();
        OnPropertyChanged(nameof(HasAnyEnabledCategory));
        RefreshCommandState();
    }

    partial void OnSelectedModManagerChanged(string value)
    {
        patchRunDirty = true;
        RefreshCommandState();
    }

    partial void OnEnableBodyChanged(bool value)
    {
        patchRunDirty = true;
        PersistSettingsInBackground();
        OnPropertyChanged(nameof(HasAnyEnabledCategory));
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

    partial void OnSkyrimDataPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasSkyrimDataPath));
        RefreshCommandState();
    }

    partial void OnEnableOtherChanged(bool value)
    {
        patchRunDirty = true;
        PersistSettingsInBackground();
        OnPropertyChanged(nameof(HasAnyEnabledCategory));
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

    partial void OnPatchProgressFileTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPatchProgressFileText));
    }

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusAlert));
    }

    partial void OnStatusColorChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatusAlert));
    }

    partial void OnStatusLinkPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasStatusLinkPath));
        RefreshCommandState();
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
        OnPropertyChanged(nameof(IsFolderSectionEnabled));
        RefreshCommandState();
    }

    partial void OnIsPatchingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFolderSectionEnabled));
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

    partial void OnSelectedDebugFaultOptionChanged(DebugFaultOption? value)
    {
        ApplySelectedDebugFaultMode();

        // Changing debug fault scenarios should always allow another patch run,
        // even when no scan/filter inputs changed since the previous run.
        patchRunDirty = true;
        RefreshCommandState();
    }

    private void ApplySelectedDebugFaultMode()
    {
        if (debugFaultState is null)
        {
            return;
        }

        var selectedMode = SelectedDebugFaultOption?.Mode ?? DebugPatchFailureMode.None;
        debugFaultState.PatchFailureMode = selectedMode;
        debugFaultState.ScanFailureMode = selectedMode;
    }

    private static bool IsAlertStatusColor(string statusColor)
    {
        return string.Equals(statusColor, "#FFB3B3", StringComparison.OrdinalIgnoreCase);
    }

}
