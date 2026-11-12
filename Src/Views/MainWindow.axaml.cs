using System;
using System.Threading.Tasks;
using Avalonia.Controls;
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

        // First run: offer the data package before watching for cards.
        if (AppPaths.GetLatestDataVersion() is null)
            await ShowDataInstallerAsync();

        _session?.Start();
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

    /// <summary>Opens the data package installer as a modal dialog.</summary>
    public async Task ShowDataInstallerAsync()
    {
        var owner = IsVisible ? this : null;
        Cursor = ArrowCursor;

        var window = new DataDownloadWindow(new DataInstallerViewModel());
        if (owner is not null)
            await window.ShowDialog<bool>(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        _viewModel.RefreshDataVersion();
        RestoreCursor();
    }

    private void RestoreCursor() =>
        Cursor = WindowState == WindowState.FullScreen ? HiddenCursor : ArrowCursor;
}
