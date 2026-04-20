using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SkyrimLightingPatcher.App.Utilities;
using SkyrimLightingPatcher.App.ViewModels;

namespace SkyrimLightingPatcher.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;

    public MainWindow()
        : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = this.viewModel = viewModel;
    }

    private async void Window_OnOpened(object? sender, EventArgs e)
    {
        await viewModel.InitializeAsync();
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!viewModel.IsPatching)
        {
            return;
        }

        e.Cancel = true;
        viewModel.NotifyCloseBlockedDuringPatch();
    }

    private async void BrowseRootFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select the Skyrim mesh root folder",
        };
        var startFolder = await TryResolveStartFolderAsync(viewModel.RootPath);
        if (startFolder is not null)
        {
            options.SuggestedStartLocation = startFolder;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);

        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await viewModel.SetRootPathAsync(localPath);
        }
    }

    private async void SetOutputDestination_OnClick(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select output destination",
        };
        var startFolder = await TryResolveStartFolderAsync(viewModel.OutputDestinationPath);
        if (startFolder is null)
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            startFolder = await TryResolveStartFolderAsync(documentsPath);
        }
        if (startFolder is not null)
        {
            options.SuggestedStartLocation = startFolder;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);

        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        await viewModel.SetOutputDestinationPathAsync(localPath);
    }

    private async void BrowseSkyrimDataFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var options = new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select Skyrim Data folder",
        };
        var startFolder = await TryResolveStartFolderAsync(viewModel.SkyrimDataPath);
        if (startFolder is not null)
        {
            options.SuggestedStartLocation = startFolder;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(options);

        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        await viewModel.SetSkyrimDataPathAsync(localPath);
    }

    private async Task<IStorageFolder?> TryResolveStartFolderAsync(string? assignedPath)
    {
        var resolvedPath = FolderPickerStartPathResolver.ResolveExistingFolderPath(assignedPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return null;
        }

        return await StorageProvider.TryGetFolderFromPathAsync(resolvedPath);
    }
}
