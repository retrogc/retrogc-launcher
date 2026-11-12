using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RetroGameCard.ViewModels;

namespace RetroGameCard.Views;

/// <summary>
/// Data package installer. Returns <c>true</c> from ShowDialog when data was installed.
/// </summary>
public partial class DataDownloadWindow : Window
{
    private readonly DataInstallerViewModel _viewModel;

    // Designer / XAML loader.
    public DataDownloadWindow() : this(new DataInstallerViewModel())
    {
    }

    public DataDownloadWindow(DataInstallerViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.PickManifestAsync = PickManifestAsync;
        _viewModel.CloseRequested += (_, installed) => Close(installed);
        DataContext = viewModel;
        InitializeComponent();

        // Closing mid-download cancels the transfer (partial files are cleaned up).
        Closing += (_, _) => _viewModel.OnClosing();
    }

    private async Task<string?> PickManifestAsync()
    {
        if (!StorageProvider.CanOpen)
            return null;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select retrogc-data.rgc",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("RetroGC data manifest") { Patterns = ["*.rgc"] },
                FilePickerFileTypes.All
            ]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
