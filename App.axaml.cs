using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RetroGameCard;

public class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    private readonly CardMonitor _monitor = new("RETROGC");
    private readonly RetroArchLauncher _retroArch = new();
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        Console.WriteLine("App::Initialize");
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // load settings
            Console.WriteLine("App::OnFrameworkInitializationCompleted: loading settings...");
            Settings = await AppSettings.LoadAsync();

            // create main window
            desktop.MainWindow = _mainWindow = new MainWindow();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += DesktopOnShutdownRequested;
            desktop.Exit += OnExit;

            _retroArch.Exited += OnRetroArchExited;
            _monitor.Inserted += OnCardInserted;
            _monitor.Removed += OnCardRemoved;
            _monitor.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnCardInserted(object? sender, VolumeEventArgs e)
    {
        try
        {
            Console.WriteLine($"card inserted: {e.Volume.MountPoint}");

            var configFile = Directory
                .EnumerateFiles(e.Volume.MountPoint, "*.rgc", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (configFile is null)
            {
                Console.WriteLine($"No config file (.rgc) found in: {e.Volume.MountPoint}");
                return;
            }

            var game = await Game.LoadAsync(configFile);

            if (!File.Exists(game.RomPath))
                throw new FileNotFoundException("ROM not found.", game.RomPath);

            Utility.Notify("Loading " + game.Name);

            Console.WriteLine($"game: {game.Name}");
            Console.WriteLine($"rom:  {game.RomPath}");
            Console.WriteLine($"core: {game.CorePath}");

            // load preview image
            _mainWindow?.ShowGame(
                game.Name, game.Description, game.Platform,
                game.Year, game.Core, game.ImagePath);

            _retroArch.Start(
                retroArchExecutable: Settings.RetroArch.ExecutablePath,
                romPath: game.RomPath,
                corePath: game.CorePath,
                fullscreen: Settings.RetroArch.StartFullscreen);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"Unable to launch RetroArch: {exception.Message}");
        }
    }

    private async void OnCardRemoved(object? sender, VolumeEventArgs e)
    {
        try
        {
            Console.WriteLine($"card removed: {e.Volume.MountPoint}");

            _mainWindow?.ShowWaitingForGame();

            // RequestClose() is optional. For removal, normally kill immediately:
            await _retroArch.KillAsync();
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"Unable to launch RetroArch: {exception.Message}");
        }
    }

    private void OnRetroArchExited(object? sender, RetroArchExitedEventArgs e)
    {
        Console.WriteLine($"RetroArch exited (PID {e.ProcessId}).");
    }

    private void DesktopOnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        Console.WriteLine("App.DesktopOnShutdownRequested");

        // save app context
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            //((AppContext)desktop.MainWindow?.DataContext!).Save();
        }

        //ToastNotificationManagerCompat.Uninstall();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs _)
    {
        // save app context
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            //((AppContext)desktop.MainWindow?.DataContext!).Save();
        }

        //ToastNotificationManagerCompat.Uninstall();
    }

    private void OnTrayIconClicked(object? sender, EventArgs _)
    {
        //AppContext.OpenCommand();
    }

    private void OnOpenClicked(object? sender, EventArgs e)
    {
        _mainWindow?.SetWindowState(StartupWindowState.Normal);
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _monitor.Inserted -= OnCardInserted;
        _monitor.Removed -= OnCardRemoved;
        _retroArch.Exited -= OnRetroArchExited;

        await _retroArch.DisposeAsync();
        await _monitor.StopAsync();
        _monitor.Dispose();
    }

    public void Dispose()
    {
        _monitor.Dispose();
    }
}