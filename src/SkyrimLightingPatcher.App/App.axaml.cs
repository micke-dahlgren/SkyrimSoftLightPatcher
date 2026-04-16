using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SkyrimLightingPatcher.App.ViewModels;
using SkyrimLightingPatcher.Core.Services;
using SkyrimLightingPatcher.NiflyAdapter.Reflection;

namespace SkyrimLightingPatcher.App;

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
            var nifMeshService = new ReflectionNifMeshService();
            var scanFileResolver = new ScanFileResolver();
            var scanService = new ScanService(nifMeshService, shapeClassifier, scanFileResolver);
            var patchPlanner = new PatchPlanner();
            var backupStore = new BackupStore();
            var patchExecutor = new PatchExecutor(patchPlanner, nifMeshService, scanFileResolver, backupStore);
            var outputModService = new OutputModService(backupStore);
            var vortexPathResolver = new VortexPathResolver();

            var mainWindowViewModel = new MainWindowViewModel(
                settingsStore,
                scanService,
                patchExecutor,
                outputModService,
                vortexPathResolver);

            desktop.MainWindow = new MainWindow(mainWindowViewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
