using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RetroGameCard.ViewModels;

namespace RetroGameCard.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Action? _dataChanged;

    // Designer / XAML loader.
    public SettingsWindow() : this(new SettingsViewModel(), null)
    {
    }

    public SettingsWindow(SettingsViewModel viewModel, Action? dataChanged)
    {
        _viewModel = viewModel;
        _dataChanged = dataChanged;
        DataContext = viewModel;
        InitializeComponent();
    }

    private async void OnBrowseRetroArchClicked(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
            return;

        var filter = OperatingSystem.IsWindows()
            ? new FilePickerFileType("RetroArch") { Patterns = ["retroarch.exe", "*.exe"] }
            : new FilePickerFileType("RetroArch") { Patterns = ["retroarch", "*.AppImage", "*"] };

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the RetroArch executable",
            AllowMultiple = false,
            FileTypeFilter = [filter, FilePickerFileTypes.All]
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            _viewModel.ExecutablePath = path;
    }

    private async void OnManageDataClicked(object? sender, RoutedEventArgs e)
    {
        await new DataDownloadWindow(new DataInstallerViewModel()).ShowDialog<bool>(this);
        _viewModel.RefreshDataInfo();
        _dataChanged?.Invoke();
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveAsync())
            Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();
}
