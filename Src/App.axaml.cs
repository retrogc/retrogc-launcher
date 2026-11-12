using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RetroGameCard.Models;
using RetroGameCard.Services;
using RetroGameCard.ViewModels;
using RetroGameCard.Views;

namespace RetroGameCard;

public class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private GameSession? _session;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings = await AppSettings.LoadAsync();

            _mainViewModel = new MainViewModel();
            _session = new GameSession(_mainViewModel, () => Settings);
            _mainWindow = new MainWindow(_mainViewModel, _session);

            desktop.MainWindow = _mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnExit;

            // Without RetroArch data the window must show up so the installer can be offered.
            var state = Settings.Main.StartupWindowState;
            if (state == StartupWindowState.Minimized && AppPaths.GetLatestDataVersion() is null)
                state = StartupWindowState.Normal;

            _mainWindow.ApplyWindowState(state);

            // A hidden window never raises Opened, which is where monitoring normally starts.
            if (state == StartupWindowState.Minimized)
                _session.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // Stop the monitor and close RetroArch before the process ends.
        try
        {
            if (_session is not null)
                Task.Run(async () => await _session.DisposeAsync()).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            Log.Error("Shutdown", exception);
        }

        _mainViewModel?.Dispose();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) =>
        _mainWindow?.ApplyWindowState(StartupWindowState.Normal);

    private void OnOpenClicked(object? sender, EventArgs e) =>
        _mainWindow?.ApplyWindowState(StartupWindowState.Normal);

    private void OnSettingsClicked(object? sender, EventArgs e) => _mainWindow?.ShowSettings();

    private void OnExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
