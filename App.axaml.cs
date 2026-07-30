using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace RetroGameCard;

public class App : Application, IDisposable
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

            // wait for main window before starting monitoring
            _mainWindow.Loaded += async (_, _) =>
            {
                await Task.Delay(2000);
                _retroArch.Exited += OnRetroArchExited;
                _monitor.Inserted += OnCardInserted;
                _monitor.Removed += OnCardRemoved;
                _monitor.Start();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnCardInserted(object? sender, VolumeEventArgs e)
    {
        try
        {
            Console.WriteLine($"OnCardInserted: {e.Volume.MountPoint}");

            var configFile = Directory
                .EnumerateFiles(e.Volume.MountPoint, "*.rgc", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (configFile is null)
            {
                Console.WriteLine($" No config file (.rgc) found in: {e.Volume.MountPoint}");
                return;
            }

            // load config file
            var card = await Game.LoadAsync(configFile);
            var exePath = card.RetroArchExePath;

            // check for retroarch executable
            if (!File.Exists(exePath))
                throw new FileNotFoundException(" RetroArch executable not found.", exePath);

            if (!File.Exists(card.RomPath))
                throw new FileNotFoundException(" RetroArch rom not found.", card.RomPath);

            // on linux, copy embedded retroarch app image to an executable location
            if (OperatingSystem.IsLinux() && exePath.EndsWith(".AppImage"))
            {
                exePath = Path.Combine(AppSettings.SettingsDirectory, card.RetroArchExe);
                Console.WriteLine($" Copying {card.RetroArchExePath} to {exePath}");
                Directory.CreateDirectory(AppSettings.SettingsDirectory);
                await Utility.CopyFileAsync(card.RetroArchExePath, exePath);
                File.SetUnixFileMode(exePath,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }

            // get retroarch version
            var retroVersion = await Utility.GetRetroArchVersion(exePath);

            Utility.Notify(" Loading " + card.Name);
            Console.WriteLine($"  game: {card.Name}");
            Console.WriteLine($"  rom:  {card.RomPath}");
            Console.WriteLine($"  core: {card.CorePath}");
            Console.WriteLine($"  retroarch: {exePath} ({retroVersion})");
            Console.WriteLine($"  card version: {card.Version}");

            // load game data to main window
            _mainWindow?.ShowGame(
                card.Name, card.Description, card.Platform,
                card.Year, Path.GetFileName(card.Core),
                card.ImagePath, card.Version, retroVersion);

            // start retroarch
            _retroArch.Start(
                retroArchExecutable: exePath,
                romPath: card.RomPath,
                corePath: card.CorePath,
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

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        _mainWindow?.ShowSettings();
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public async void Dispose()
    {
        try
        {
            _monitor.Inserted -= OnCardInserted;
            _monitor.Removed -= OnCardRemoved;
            _retroArch.Exited -= OnRetroArchExited;
            await _retroArch.DisposeAsync();
            await _monitor.StopAsync();
            _monitor.Dispose();
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString());
        }

        GC.SuppressFinalize(this);
    }
}