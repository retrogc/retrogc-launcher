using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RetroGameCard.Controls;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow()
    {
        _settings = new AppSettings();
        InitializeComponent();
        LoadSettingsIntoControls();
    }

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadSettingsIntoControls();
    }

    private void LoadSettingsIntoControls()
    {
        StartupWindowStateComboBox.SelectedIndex =
            _settings.Main.StartupWindowState switch
            {
                StartupWindowState.Fullscreen => 0,
                StartupWindowState.Normal => 1,
                StartupWindowState.Minimized => 2,
                _ => 0
            };

        RetroArchFullscreenToggle.IsChecked =
            _settings.RetroArch.StartFullscreen;

        CustomRetroArchCheckBox.IsChecked =
            _settings.RetroArch.UseCustomExecutable;

        RetroArchPathTextBox.Text =
            _settings.RetroArch.ExecutablePath;

        RetroArchIntegerScalingToggle.IsChecked =
            _settings.RetroArch.IntegerScaling;

        RetroArchShaderComboBox.SelectedIndex =
            _settings.RetroArch.Shader switch
            {
                Shader.None => 0,
                Shader.Bilinear => 1,
                Shader.Crt => 2,
                Shader.Retro => 2,
                Shader.Scanline => 2,
                _ => 1
            };

        SettingsFileText.Text = AppSettings.SettingsPath;
    }

    private async void OnBrowseRetroArchClicked(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            return;
        }

        var fileTypes = OperatingSystem.IsWindows()
            ? new[]
            {
                new FilePickerFileType("Executables")
                {
                    Patterns = ["*.exe"],
                    MimeTypes = ["application/vnd.microsoft.portable-executable"]
                }
            }
            : new[]
            {
                new FilePickerFileType("Executables")
                {
                    Patterns = ["*"]
                }
            };

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select RetroArch executable",
                AllowMultiple = false,
                FileTypeFilter = fileTypes
            });

        var selectedFile = files.FirstOrDefault();
        if (selectedFile?.TryGetLocalPath() is not string path)
        {
            return;
        }

        RetroArchPathTextBox.Text = path;
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        // validate custom retroarch executable if any
        if (CustomRetroArchCheckBox.IsChecked == true
            && !File.Exists(RetroArchPathTextBox.Text?.Trim()))
        {
            // error
            CustomRetroArchCheckBox.IsChecked = false;
            RetroArchPathTextBox.Text = string.Empty;
        }

        _settings.Main.StartupWindowState =
            StartupWindowStateComboBox.SelectedIndex switch
            {
                1 => StartupWindowState.Normal,
                2 => StartupWindowState.Minimized,
                _ => StartupWindowState.Fullscreen
            };

        _settings.RetroArch.StartFullscreen =
            RetroArchFullscreenToggle.IsChecked == true;

        _settings.RetroArch.UseCustomExecutable =
            CustomRetroArchCheckBox.IsChecked == true;

        _settings.RetroArch.ExecutablePath =
            RetroArchPathTextBox.Text?.Trim() ?? "";

        _settings.RetroArch.IntegerScaling =
            RetroArchIntegerScalingToggle.IsChecked == true;

        _settings.RetroArch.Shader =
            RetroArchShaderComboBox.SelectedIndex switch
            {
                0 => Shader.None,
                1 => Shader.Bilinear,
                2 => Shader.Crt,
                3 => Shader.Retro,
                4 => Shader.Scanline,
                _ => Shader.Bilinear
            };

        await _settings.SaveAsync();

        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}