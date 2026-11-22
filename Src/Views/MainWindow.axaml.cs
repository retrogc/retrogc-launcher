using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using RetroGameCard.Models;
using RetroGameCard.Services;
using RetroGameCard.ViewModels;

namespace RetroGameCard.Views;

public partial class MainWindow : Window
{
    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    private readonly MainViewModel _viewModel;
    private readonly GameSession? _session;
    private bool _dialogOpen;
    private InstallerCardInfo? _installerCard;

    // Designer / XAML loader.
    public MainWindow() : this(new MainViewModel(), null)
    {
    }

    public MainWindow(MainViewModel viewModel, GameSession? session)
    {
        _viewModel = viewModel;
        _session = session;
        DataContext = viewModel;
        InitializeComponent();

        KeyDown += OnKeyDown;
        Opened += OnOpened;

        if (_session is not null)
            _session.InstallerCardInserted += OnInstallerCardInserted;

        // Closing the window only hides it: the launcher keeps running in the tray.
        Closing += (_, e) =>
        {
            if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                return;

            e.Cancel = true;
            Hide();
        };
    }

    private bool _started;

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
            return;
        _started = true;

        // Started from an installer card: install the launcher (and the data) on this computer.
        if (InstallerCard.FindLaunchManifest() is { } manifest)
        {
            if (await RunCardSetupAsync(manifest))
                return;
        }
        else
        {
            // Started from elsewhere (a download...): offer to install / update the installed launcher.
            if (await OfferLauncherInstallAsync())
                return;

            // First run: offer the data package before watching for cards.
            if (AppPaths.GetLatestDataVersion() is null)
                await ShowDataInstallerAsync();
        }

        _session?.Start();
    }

    /// <summary>
    /// The launcher was started from an installer card. Returns true when the launcher was
    /// installed and this (card) instance is shutting down.
    /// </summary>
    private async Task<bool> RunCardSetupAsync(string manifest)
    {
        var dataInstalled = false;
        try
        {
            dataInstalled = (await InstallerCard.LoadAsync(manifest)).DataInstalled;
        }
        catch (Exception exception)
        {
            // The wizard reports manifest errors itself.
            Log.Error("Installer card manifest", exception);
        }

        Log.Info($"Started from an installer card: {manifest}");
        var installer = await ShowDataInstallerAsync(new InstallerOptions(
            LocalManifestPath: manifest,
            LauncherPath: AppPaths.CurrentExecutable,
            LauncherVersion: AppInstaller.CurrentVersion,
            InstallLauncher: true,
            InstallData: !dataInstalled));

        return installer is { LauncherInstalled: true } && SwitchToInstalledLauncher();
    }

    private async void OnInstallerCardInserted(object? sender, InstallerCardInfo info)
    {
        _installerCard = info;

        // Only ask when the card brings something new.
        if (info.HasUpdate)
            await ShowInstallerCardAsync(info);
    }

    private async Task ShowInstallerCardAsync(InstallerCardInfo info)
    {
        // Ask by default for what is new; with nothing new (Enter pressed) offer a data reinstall.
        var installLauncher = info.LauncherIsNewer;
        var installData = !info.DataInstalled || !installLauncher;

        var installer = await ShowDataInstallerAsync(new InstallerOptions(
            LocalManifestPath: info.ManifestPath,
            LauncherPath: info.LauncherPath,
            LauncherVersion: info.LauncherVersion,
            InstallLauncher: installLauncher,
            InstallData: installData));

        if (installer is { LauncherInstalled: true })
            SwitchToInstalledLauncher();
    }

    /// <summary>
    /// This launcher isn't the installed one: when nothing is installed, or an older / unknown version is,
    /// ask to install it. Returns true when it was installed and this instance is shutting down.
    /// </summary>
    private async Task<bool> OfferLauncherInstallAsync()
    {
        var current = AppInstaller.CurrentVersion;
        if (!AppInstaller.NeedsInstallOffer(out var installed) || AppPaths.CurrentExecutable is not { } executable)
            return false;

        Log.Info($"Launcher {current} not installed (installed: " +
                 $"{(installed.IsInstalled ? installed.Version?.ToString(3) ?? "unknown version" : "none")}): asking");

        if (_dialogOpen)
            return false;

        bool accepted;
        _dialogOpen = true;
        try
        {
            Cursor = ArrowCursor;
            var window = new LauncherUpdateWindow(executable, current, installed);
            if (IsVisible)
            {
                await window.ShowDialog(this);
            }
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }

            accepted = window.Installed;
        }
        finally
        {
            _dialogOpen = false;
            RestoreCursor();
        }

        if (!accepted)
        {
            Log.Info("Launcher installation declined, running from " + executable);
            return false;
        }

        // The installed launcher was stopped (if running): start the new one in any case.
        return SwitchToInstalledLauncher(force: true);
    }

    /// <summary>
    /// After the launcher was installed or updated: start the installed copy and exit. When another
    /// launcher is already running, this instance only exits (that launcher keeps running), unless
    /// <paramref name="force"/> (the other one was stopped to be replaced).
    /// </summary>
    private static bool SwitchToInstalledLauncher(bool force = false)
    {
        try
        {
            if (force || AppInstaller.OwnsInstanceLock || AppPaths.IsRunningInstalled)
            {
                AppInstaller.ReleaseInstanceLock();
                AppInstaller.LaunchInstalled();
            }
            else
            {
                Log.Info("Another launcher is running: not starting the installed launcher");
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not start the installed launcher", exception);
            return false;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        return true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F12:
                ShowSettings();
                break;
            case Key.Escape or Key.F11:
                ApplyWindowState(WindowState == WindowState.FullScreen
                    ? StartupWindowState.Normal
                    : StartupWindowState.Fullscreen);
                break;
            case Key.Enter or Key.Space when _viewModel.IsStopped || _viewModel.IsError:
                _session?.Relaunch();
                break;
            case Key.Enter or Key.Space when _session is { IsCountingDown: true }:
                _session.SkipCountdown();
                break;
            case Key.Enter or Key.Space when _viewModel.IsInstaller && _installerCard is not null:
                _ = ShowInstallerCardAsync(_installerCard);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    public void ApplyWindowState(StartupWindowState state)
    {
        switch (state)
        {
            case StartupWindowState.Fullscreen:
                WindowState = WindowState.FullScreen;
                Cursor = HiddenCursor;
                Show();
                Activate();
                break;
            case StartupWindowState.Normal:
                WindowState = WindowState.Normal;
                Cursor = ArrowCursor;
                Show();
                Activate();
                break;
            default:
                Hide();
                break;
        }
    }

    public async void ShowSettings()
    {
        if (_dialogOpen)
            return;

        try
        {
            _dialogOpen = true;
            if (!IsVisible)
                ApplyWindowState(StartupWindowState.Normal);

            // Dialogs need a pointer even in fullscreen.
            Cursor = ArrowCursor;
            var window = new SettingsWindow(new SettingsViewModel(App.Settings), _viewModel.RefreshDataVersion);
            await window.ShowDialog(this);
        }
        catch (Exception exception)
        {
            Log.Error("Settings window", exception);
        }
        finally
        {
            _dialogOpen = false;
            RestoreCursor();
        }
    }

    /// <summary>
    /// Opens the data package installer as a modal dialog. Returns null when a dialog is already open.
    /// </summary>
    public async Task<DataInstallerViewModel?> ShowDataInstallerAsync(InstallerOptions? options = null)
    {
        if (_dialogOpen)
            return null;

        _dialogOpen = true;
        try
        {
            var owner = IsVisible ? this : null;
            Cursor = ArrowCursor;

            var viewModel = new DataInstallerViewModel(options);
            var window = new DataDownloadWindow(viewModel);
            if (owner is not null)
                await window.ShowDialog<bool>(owner);
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }

            return viewModel;
        }
        catch (Exception exception)
        {
            Log.Error("Data installer window", exception);
            return null;
        }
        finally
        {
            _dialogOpen = false;
            _viewModel.RefreshDataVersion();
            RestoreCursor();
        }
    }

    private void RestoreCursor() =>
        Cursor = WindowState == WindowState.FullScreen ? HiddenCursor : ArrowCursor;
}
