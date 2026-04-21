using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SkyrimGlowingMeshPatcher.App.Models;
using SkyrimGlowingMeshPatcher.App.Services;
using SkyrimGlowingMeshPatcher.App.ViewModels;
using SkyrimGlowingMeshPatcher.Core.Services;
using SkyrimGlowingMeshPatcher.NiflyAdapter.Reflection;

namespace SkyrimGlowingMeshPatcher.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsStore = new JsonSettingsStore();
            var shapeClassifier = new ShapeClassifier();
            var debugFaultState = new DebugFaultState();
            var coreNifMeshService = new ReflectionNifMeshService();
            var scanFileResolver = new ScanFileResolver();
            var diskSpaceMonitor = new DiskSpaceMonitor();
            var patchPlanner = new PatchPlanner();
            var backupStore = new BackupStore();

#if DEBUG
            var nifMeshService = new DebugFaultInjectingNifMeshService(debugFaultState, coreNifMeshService);
            var scanService = new DebugFaultInjectingScanService(
                debugFaultState,
                new ScanService(nifMeshService, shapeClassifier, scanFileResolver));
            var patchExecutor = new DebugFaultInjectingPatchExecutor(
                debugFaultState,
                new PatchExecutor(
                    patchPlanner,
                    nifMeshService,
                    scanFileResolver,
                    backupStore,
                    new DebugFaultInjectingDiskSpaceMonitor(debugFaultState, diskSpaceMonitor)));
#else
            var nifMeshService = coreNifMeshService;
            var scanService = new ScanService(nifMeshService, shapeClassifier, scanFileResolver);
            var patchExecutor = new PatchExecutor(patchPlanner, nifMeshService, scanFileResolver, backupStore, diskSpaceMonitor);
#endif

            var outputModService = new OutputModService(backupStore);
            var vortexPathResolver = new VortexPathResolver();
            var modOrganizer2PathResolver = new ModOrganizer2PathResolver();

            var mainWindowViewModel = new MainWindowViewModel(
                settingsStore,
                scanService,
                patchExecutor,
                outputModService,
                vortexPathResolver,
                modOrganizer2PathResolver,
                debugFaultState);

            desktop.MainWindow = new MainWindow(mainWindowViewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
