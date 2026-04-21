using SkyrimGlowingMeshPatcher.App.ViewModels;
using SkyrimGlowingMeshPatcher.App.Models;
using SkyrimGlowingMeshPatcher.Core.Interfaces;
using SkyrimGlowingMeshPatcher.Core.Models;
using SkyrimGlowingMeshPatcher.Core.Utilities;

namespace SkyrimGlowingMeshPatcher.Tests;

public sealed class MainWindowViewModelSettingsTests
{
    [Fact]
    public async Task InitializeAsync_LoadsCategorySettingsButAlwaysClearsRootPath()
    {
        var savedRootPath = CreateTempDirectory();
        var settingsStore = new RecordingSettingsStore(new AppSettings(
            savedRootPath,
            new PatchSettings(
                EyeValue: 1.0f,
                BodyValue: 1.0f,
                EnableOther: true,
                OtherValue: 1.0f,
                EnableEye: false,
                EnableBody: true)));

        var viewModel = CreateViewModel(settingsStore);
        await viewModel.InitializeAsync();

        Assert.Equal(string.Empty, viewModel.RootPath);
        Assert.False(viewModel.EnableEye);
        Assert.True(viewModel.EnableBody);
        Assert.True(viewModel.EnableOther);
    }

    [Fact]
    public async Task InitializeAsync_IgnoresStoredRootPathWhenDirectoryIsMissing()
    {
        var missingRootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-missing-root", Guid.NewGuid().ToString("N"));
        var settingsStore = new RecordingSettingsStore(new AppSettings(missingRootPath, PatchSettings.Default));
        var viewModel = CreateViewModel(settingsStore);

        await viewModel.InitializeAsync();

        Assert.Equal(string.Empty, viewModel.RootPath);
    }

    [Fact]
    public async Task SetRootPathAndCategoryToggles_PersistsCurrentSettings()
    {
        var rootPath = CreateTempDirectory();
        var settingsStore = new RecordingSettingsStore(AppSettings.Default);
        var viewModel = CreateViewModel(settingsStore);
        await viewModel.InitializeAsync();

        await viewModel.SetRootPathAsync(rootPath);
        viewModel.EnableEye = false;
        viewModel.EnableBody = false;
        viewModel.EnableOther = true;

        await WaitForSaveCountAsync(settingsStore, 4);
        var persisted = settingsStore.LatestSavedSettings;
        Assert.NotNull(persisted);
        Assert.Null(persisted!.LastRootPath);
        Assert.False(persisted.PatchSettings.EnableEye);
        Assert.False(persisted.PatchSettings.EnableBody);
        Assert.True(persisted.PatchSettings.EnableOther);
    }

    [Fact]
    public async Task PatchCommand_WhenArchiveCreationFails_ShowsManualArchiveGuidanceAndKeepsOutputFolderVisible()
    {
        var rootPath = CreateTempDirectory();
        var outputDestination = CreateTempDirectory();
        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                sourceFile,
                                "sample:Eye",
                                "Eye",
                                ShaderMetadata.Empty,
                                Array.Empty<string>(),
                                true,
                                0.1f),
                            ShapeKind.Eye,
                            true,
                            0.4f,
                            "Eye",
                            ["Eligible"]),
                    ]),
            ]);

        var settingsStore = new RecordingSettingsStore(new AppSettings(null, PatchSettings.Default));
        var failingPatchExecutor = new ArchiveFailurePatchExecutor();
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new FixedScanService(report),
            failingPatchExecutor,
            new NoOpOutputModService(),
            new NoOpVortexPathResolver(),
            new NoOpModOrganizer2PathResolver());

        await viewModel.InitializeAsync();
        viewModel.EnableEye = true;
        await viewModel.SetRootPathAsync(rootPath);
        await viewModel.SetOutputDestinationPathAsync(outputDestination);

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.PatchCommand.ExecuteAsync(null);

        var expectedOutputRoot = Path.Combine(outputDestination, "Glowing Mesh Patcher Output");
        Assert.True(Directory.Exists(expectedOutputRoot));
        Assert.Equal(expectedOutputRoot, viewModel.CurrentOutputPath);
        Assert.True(viewModel.HasPatchOutputVisible);
        Assert.Contains("zip that folder manually", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedOutputRoot, viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(failingPatchExecutor.LastOutputArchivePath);
        Assert.Equal(outputDestination, Path.GetDirectoryName(failingPatchExecutor.LastOutputArchivePath)!);
        Assert.Matches("^GlowingMeshPatch_[0-9a-f]{6}\\.zip$", Path.GetFileName(failingPatchExecutor.LastOutputArchivePath));
    }

    [Fact]
    public void NotifyCloseBlockedDuringPatch_UsesArchiveSpecificWarningWhenFinalizingArchive()
    {
        var viewModel = CreateViewModel(new RecordingSettingsStore(AppSettings.Default));
        viewModel.IsPatching = true;
        viewModel.BusyStateText = "Creating mod file (.zip)...";

        viewModel.NotifyCloseBlockedDuringPatch();

        Assert.Contains("creating your zip file", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchCommand_ReappliesSelectedDebugScenarioAtPatchStart()
    {
        var rootPath = CreateTempDirectory();
        var outputDestination = CreateTempDirectory();
        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                sourceFile,
                                "sample:Eye",
                                "Eye",
                                ShaderMetadata.Empty,
                                Array.Empty<string>(),
                                true,
                                0.1f),
                            ShapeKind.Eye,
                            true,
                            0.4f,
                            "Eye",
                            ["Eligible"]),
                    ]),
            ]);

        var debugFaultState = new DebugFaultState();
        var patchExecutor = new CapturingPatchExecutor(debugFaultState);
        var viewModel = new MainWindowViewModel(
            new RecordingSettingsStore(new AppSettings(null, PatchSettings.Default)),
            new FixedScanService(report),
            patchExecutor,
            new NoOpOutputModService(),
            new NoOpVortexPathResolver(),
            new NoOpModOrganizer2PathResolver(),
            debugFaultState);

        await viewModel.InitializeAsync();
        viewModel.EnableEye = true;
        await viewModel.SetRootPathAsync(rootPath);
        await viewModel.SetOutputDestinationPathAsync(outputDestination);
        await viewModel.ScanCommand.ExecuteAsync(null);

        var selectedScenario = viewModel.DebugFaultOptions.First(option =>
            option.Mode == DebugPatchFailureMode.PatchLowDiskWritingRunManifest);
        viewModel.SelectedDebugFaultOption = selectedScenario;

        // Simulate state drift to verify PatchAsync re-applies selection before execution.
        debugFaultState.PatchFailureMode = DebugPatchFailureMode.None;
        debugFaultState.ScanFailureMode = DebugPatchFailureMode.None;

        await viewModel.PatchCommand.ExecuteAsync(null);

        Assert.Equal(DebugPatchFailureMode.PatchLowDiskWritingRunManifest, patchExecutor.ObservedPatchModeAtExecution);
    }

    [Fact]
    public async Task ScanCommand_WhenReportHasErrorsWithoutLogPath_ShowsExplicitErrorCountInStatusMessage()
    {
        var rootPath = CreateTempDirectory();
        var outputDestination = CreateTempDirectory();
        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, PatchSettings.Default),
            [
                new FileScanResult(
                    sourceFile,
                    [],
                    "Simulated scan read failure"),
            ]);

        var viewModel = new MainWindowViewModel(
            new RecordingSettingsStore(new AppSettings(null, PatchSettings.Default)),
            new FixedScanService(report),
            new NoOpPatchExecutor(),
            new NoOpOutputModService(),
            new NoOpVortexPathResolver(),
            new NoOpModOrganizer2PathResolver());

        await viewModel.InitializeAsync();
        viewModel.EnableEye = true;
        await viewModel.SetRootPathAsync(rootPath);
        await viewModel.SetOutputDestinationPathAsync(outputDestination);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Contains("Could not read 1 file(s).", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#FFB3B3", viewModel.StatusColor);
    }

    [Fact]
    public async Task PatchCommand_WhenLowDiskFailureOccurs_IgnoresLateProgressUpdates()
    {
        var rootPath = CreateTempDirectory();
        var outputDestination = CreateTempDirectory();
        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                sourceFile,
                                "sample:Eye",
                                "Eye",
                                ShaderMetadata.Empty,
                                Array.Empty<string>(),
                                true,
                                0.1f),
                            ShapeKind.Eye,
                            true,
                            0.4f,
                            "Eye",
                            ["Eligible"]),
                    ]),
            ]);

        var viewModel = new MainWindowViewModel(
            new RecordingSettingsStore(new AppSettings(null, PatchSettings.Default)),
            new FixedScanService(report),
            new StaleProgressAfterFailurePatchExecutor(),
            new NoOpOutputModService(),
            new NoOpVortexPathResolver(),
            new NoOpModOrganizer2PathResolver());

        await viewModel.InitializeAsync();
        viewModel.EnableEye = true;
        await viewModel.SetRootPathAsync(rootPath);
        await viewModel.SetOutputDestinationPathAsync(outputDestination);

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.PatchCommand.ExecuteAsync(null);
        await Task.Delay(250);

        Assert.Contains("Not enough disk space while writing patched files.", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Patching meshes...", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#FFB3B3", viewModel.StatusColor);
    }

    [Fact]
    public async Task PatchCommand_WhenUnexpectedFailureOccurs_WritesTechnicalErrorLogForBugReports()
    {
        var rootPath = CreateTempDirectory();
        var outputDestination = CreateTempDirectory();
        var appHome = CreateTempDirectory();
        using var appHomeScope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                sourceFile,
                                "sample:Eye",
                                "Eye",
                                ShaderMetadata.Empty,
                                Array.Empty<string>(),
                                true,
                                0.1f),
                            ShapeKind.Eye,
                            true,
                            0.4f,
                            "Eye",
                            ["Eligible"]),
                    ]),
            ]);

        var viewModel = new MainWindowViewModel(
            new RecordingSettingsStore(new AppSettings(null, PatchSettings.Default)),
            new FixedScanService(report),
            new UnexpectedFailurePatchExecutor(),
            new NoOpOutputModService(),
            new NoOpVortexPathResolver(),
            new NoOpModOrganizer2PathResolver());

        await viewModel.InitializeAsync();
        viewModel.EnableEye = true;
        await viewModel.SetRootPathAsync(rootPath);
        await viewModel.SetOutputDestinationPathAsync(outputDestination);

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.PatchCommand.ExecuteAsync(null);

        var errorLogPath = Path.Combine(appHome, PatchOutputPaths.PatchErrorLogFileName);

        Assert.Contains("Something went wrong. Please try again.", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("We saved the error output to", viewModel.StatusLinkPrefix);
        Assert.Equal(errorLogPath, viewModel.StatusLinkPath);
        Assert.True(viewModel.HasStatusLinkPath);

        Assert.True(File.Exists(errorLogPath));
        var errorLogContent = await File.ReadAllTextAsync(errorLogPath);
        Assert.Contains("InvalidOperationException", errorLogContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Simulated unexpected patch failure for test.", errorLogContent, StringComparison.OrdinalIgnoreCase);
    }

    private static MainWindowViewModel CreateViewModel(ISettingsStore settingsStore)
    {
        return new MainWindowViewModel(
            settingsStore,
            new NoOpScanService(),
            new NoOpPatchExecutor(),
            new NoOpOutputModService(),
            new NoOpVortexPathResolver(),
            new NoOpModOrganizer2PathResolver());
    }

    private static async Task WaitForSaveCountAsync(RecordingSettingsStore settingsStore, int expectedCount)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (settingsStore.SaveCount >= expectedCount)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for {expectedCount} saved settings entries. Current count: {settingsStore.SaveCount}.");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "skyrim-lighting-mainwindow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingSettingsStore(AppSettings loadedSettings) : ISettingsStore
    {
        private readonly object gate = new();
        private readonly List<AppSettings> savedSettings = [];

        public int SaveCount
        {
            get
            {
                lock (gate)
                {
                    return savedSettings.Count;
                }
            }
        }

        public AppSettings? LatestSavedSettings
        {
            get
            {
                lock (gate)
                {
                    return savedSettings.LastOrDefault();
                }
            }
        }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(loadedSettings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                savedSettings.Add(settings);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoOpScanService : IScanService
    {
        public Task<ScanReport> ScanAsync(
            ScanRequest request,
            IProgress<ScanProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedScanService(ScanReport report) : IScanService
    {
        public Task<ScanReport> ScanAsync(
            ScanRequest request,
            IProgress<ScanProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(report);
        }
    }

    private sealed class ArchiveFailurePatchExecutor : IPatchExecutor
    {
        public string? LastOutputArchivePath { get; private set; }

        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            string outputRootPath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            LastOutputArchivePath = outputArchivePath;
            var resolvedOutputRootPath = outputRootPath;
            Directory.CreateDirectory(resolvedOutputRootPath);
            File.WriteAllText(Path.Combine(resolvedOutputRootPath, "sample.nif"), "patched");
            throw new PatchArchiveCreationException(
                resolvedOutputRootPath,
                outputArchivePath,
                new IOException("Test archive failure"));
        }
    }

    private sealed class NoOpPatchExecutor : IPatchExecutor
    {
        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            string outputRootPath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingPatchExecutor(DebugFaultState debugFaultState) : IPatchExecutor
    {
        public DebugPatchFailureMode ObservedPatchModeAtExecution { get; private set; } = DebugPatchFailureMode.None;

        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            string outputRootPath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ObservedPatchModeAtExecution = debugFaultState.PatchFailureMode;
            var resolvedOutputRootPath = outputRootPath;
            Directory.CreateDirectory(resolvedOutputRootPath);
            File.WriteAllText(outputArchivePath, "zip");
            return Task.FromResult(new PatchRunManifest(
                Guid.NewGuid().ToString("N"),
                report.Request.RootPath,
                resolvedOutputRootPath,
                outputArchivePath,
                "Glowing Mesh Patcher Output",
                false,
                DateTimeOffset.Now,
                report.Request.Settings,
                []));
        }
    }

    private sealed class StaleProgressAfterFailurePatchExecutor : IPatchExecutor
    {
        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            string outputRootPath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(75);
                progress?.Report(new PatchProgressUpdate("C:\\stale\\bearstatic.nif", 0, 813, 0, 0));
            });

            throw new LowDiskSpaceException(
                PatchExecutionStages.WritingPatchedFiles,
                outputArchivePath,
                256L * 1024 * 1024,
                0,
                "Test low disk");
        }
    }

    private sealed class UnexpectedFailurePatchExecutor : IPatchExecutor
    {
        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            string outputRootPath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated unexpected patch failure for test.");
        }
    }

    private sealed class NoOpOutputModService : IOutputModService
    {
        public Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<BackupRunInfo>>([]);
        }

        public Task<PatchRunManifest> DeleteAsync(string runId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NoOpVortexPathResolver : IVortexPathResolver
    {
        public Task<VortexStagingFolder?> TryResolveSkyrimSeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<VortexStagingFolder?>(null);
        }

        public Task<string?> TryResolveSkyrimDataPathAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class NoOpModOrganizer2PathResolver : IModOrganizer2PathResolver
    {
        public Task<ModOrganizer2Instance?> TryResolveSkyrimSeAsync(
            ModOrganizer2InstanceKind? preferredKind = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ModOrganizer2Instance?>(null);
        }
    }
}
