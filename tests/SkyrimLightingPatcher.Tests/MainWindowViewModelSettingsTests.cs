using SkyrimLightingPatcher.App.ViewModels;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;

namespace SkyrimLightingPatcher.Tests;

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

    private sealed class NoOpPatchExecutor : IPatchExecutor
    {
        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
