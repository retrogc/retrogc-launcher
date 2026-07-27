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
    private readonly CardMonitor _monitor = new("RETROGC");
    private readonly RetroArchLauncher _retroArch = new();
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = _mainWindow = new MainWindow();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += DesktopOnShutdownRequested;
            desktop.Exit += OnExit;

            // set app context to main window context
            DataContext = desktop.MainWindow.DataContext;

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

            Utility.NotifyLinux("Loading " + game.Name);

            Console.WriteLine($"game: {game.Name}");
            Console.WriteLine($"rom:  {game.RomPath}");
            Console.WriteLine($"core: {game.CorePath}");

            // load preview image
            _mainWindow?.LoadPreview(game.ImagePath);

            _retroArch.Start(
                retroArchExecutable: Utility.GetRetroArchExecutable(),
                romPath: game.RomPath,
                corePath: game.CorePath);
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

            _mainWindow?.UnloadPreview();

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
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.MainWindow ??= new MainWindow();
        desktop.MainWindow.WindowState = WindowState.Normal;
        desktop.MainWindow.Show();
    }

    private void OnHideClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow == null) return;
        desktop.MainWindow.WindowState = WindowState.Minimized;
        desktop.MainWindow.Hide();
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