using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    private async void BrowseRootFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select the Skyrim mesh root folder",
        });

        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            await viewModel.SetRootPathAsync(localPath);
        }
    }

    private async void SetOutputDestination_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select output destination",
        });

        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        await viewModel.SetOutputDestinationPathAsync(localPath);
    }
}
