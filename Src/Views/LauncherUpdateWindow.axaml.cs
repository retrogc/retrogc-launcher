using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroGameCard.Services;

namespace RetroGameCard.Views;

/// <summary>
/// Offers to install the running launcher (started from a download...) as the installed one, or to
/// replace an older installed one. Closes with true once installed.
/// </summary>
public partial class LauncherUpdateWindow : Window
{
    private readonly string? _sourceExecutable;
    private bool _installing;

    /// <summary>True once the launcher was installed (the window is then closed).</summary>
    public bool Installed { get; private set; }

    // Designer / XAML loader.
    public LauncherUpdateWindow() : this(null, new Version(1, 0, 0), new AppInstaller.InstalledLauncher(true, new Version(0, 9, 0)))
    {
    }

    public LauncherUpdateWindow(string? sourceExecutable, Version current, AppInstaller.InstalledLauncher installed)
    {
        _sourceExecutable = sourceExecutable;
        InitializeComponent();

        var version = current.ToString(3);
        if (!installed.IsInstalled)
        {
            TitleText.Text = $"Install the launcher {version} on this computer?";
            MessageText.Text = "It gets a menu shortcut and is used from now on. Your games and settings are kept.";
            InstalledText.Text = "Not installed";
        }
        else
        {
            TitleText.Text = $"Update the launcher to {version}?";
            MessageText.Text = "This launcher replaces the installed one, which is closed if it is running " +
                               "(and the game it runs). Your games and settings are kept.";
            InstalledText.Text = installed.Version is { } old ? old.ToString(3) : "Unknown version (older)";
        }

        CurrentText.Text = version;
        PathText.Text = sourceExecutable;

        // A click while installing must not close the window half way.
        Closing += (_, e) =>
        {
            if (_installing)
                e.Cancel = true;
        };
    }

    private void OnLaterClicked(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (_installing || _sourceExecutable is null)
            return;

        _installing = true;
        InstallButton.IsEnabled = LaterButton.IsEnabled = false;
        BusyText.IsVisible = true;
        ErrorText.IsVisible = false;

        try
        {
            var source = _sourceExecutable;
            await Task.Run(async () =>
            {
                // The installed launcher can't be running while it is replaced and restarted.
                AppInstaller.StopInstalledInstances();
                await AppInstaller.InstallAsync(source, AppInstaller.CurrentVersion, CancellationToken.None);
            });

            _installing = false;
            Installed = true;
            Close(true);
        }
        catch (Exception exception)
        {
            Log.Error("Launcher installation failed", exception);
            _installing = false;
            BusyText.IsVisible = false;
            ErrorText.Text = $"Installation failed: {exception.Message}";
            ErrorText.IsVisible = true;
            InstallButton.IsEnabled = LaterButton.IsEnabled = true;
        }
    }
}
