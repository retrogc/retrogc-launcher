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
            var card = await Card.LoadAsync(configFile);
            var exePath = card.RetroArchExePath;

            // override retroarch executable if asked
            if (Settings.RetroArch.UseCustomExecutable)
            {
                exePath = Settings.RetroArch.ExecutablePath;
            }

            // check for retroarch executable archive / AppImage (mandatory files)
            if (!File.Exists(exePath))
                throw new FileNotFoundException(" RetroArch not found.", exePath);

            if (!File.Exists(card.CorePath))
                throw new FileNotFoundException(" RetroArch core not found.", card.CorePath);

            if (!File.Exists(card.RomPath))
                throw new FileNotFoundException(" RetroArch rom not found.", card.RomPath);

            // set ui waiting/loading state (TODO: show loading stuff in ui)
            _mainWindow?.ShowGame(
                card.Name, card.Description, card.Platform,
                card.Year, Path.GetFileName(card.Core),
                card.ImagePath, card.Version, "STARTING");

            // copy embedded retroarch app image / zip to app cache path if needed
            if (!Settings.RetroArch.UseCustomExecutable &&
                exePath.ToLower().EndsWith(".appimage") || exePath.ToLower().EndsWith(".zip"))
            {
                var exeFound = false;

                // check if retroarch executable exists and if sha match card sha
                exePath = Path.Combine(AppSettings.SettingsDirectory, card.RetroArchExe);
                var cacheHashPath = exePath + ".sha256";
                var cardHashPath = card.RetroArchExePath + ".sha256";

                // error out if retroarch sha is missing on card
                if (!File.Exists(cardHashPath))
                    throw new FileNotFoundException(
                        $"card is missing retroarch checksum ({cardHashPath})", cardHashPath);

                if (File.Exists(cacheHashPath))
                {
                    Console.WriteLine($" retroarch sha found at {cacheHashPath}, comparing with {cardHashPath}");
                    // get hash
                    var cacheHash = (await File.ReadAllTextAsync(cacheHashPath)).Trim().ToLower();
                    var cardHash = (await File.ReadAllTextAsync(cardHashPath)).Trim().ToLower();
                    Console.WriteLine(" - cached hash: " + cacheHash);
                    Console.WriteLine(" - card hash: " + cardHash);
                    if (string.Equals(cacheHash, cardHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // no need to cache the card embedded retroarch executable again
                        exeFound = true;
                        exePath = OperatingSystem.IsWindows()
                            ? Path.Combine(AppSettings.SettingsDirectory, "retroarch", "retroarch.exe")
                            : Path.Combine(AppSettings.SettingsDirectory, card.RetroArchExe);
                        Console.WriteLine(
                            $" retroarch card and cache sha match, using cached executable at {exePath}");
                    }
                }

                if (!exeFound)
                {
                    // sha differ, overwrite cached retroarch with card one
                    Console.WriteLine($" retroarch card and cache sha mismatch, restoring from card...");

                    if (OperatingSystem.IsWindows())
                    {
                        var extractPath = Path.Combine(AppSettings.SettingsDirectory, "retroarch");
                        Console.WriteLine($" Extracting {card.RetroArchExePath} to {extractPath}");
                        Directory.CreateDirectory(AppSettings.SettingsDirectory);
                        await Utility.ExtractZipAsync(card.RetroArchExePath, extractPath);
                        exePath = Path.Combine(extractPath, "retroarch.exe");

                        Console.WriteLine($" Extracting {card.RetroArchDataPath} to {extractPath}");
                        await Utility.ExtractZipAsync(card.RetroArchDataPath, extractPath);
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        Console.WriteLine($" Copying {card.RetroArchExePath} to {exePath}");
                        exePath = Path.Combine(AppSettings.SettingsDirectory, card.RetroArchExe);
                        Directory.CreateDirectory(AppSettings.SettingsDirectory);
                        await Utility.CopyFileAsync(card.RetroArchExePath, exePath);
                        File.SetUnixFileMode(exePath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                        var dataPath = Path.Combine(AppSettings.SettingsDirectory, ".config", "retroarch");
                        Console.WriteLine($" Extracting {card.RetroArchDataPath} to {dataPath}");
                        Directory.CreateDirectory(dataPath);
                        await Utility.ExtractZipAsync(card.RetroArchDataPath, dataPath);
                    }

                    Console.WriteLine($" Copying {cardHashPath} to {cacheHashPath}");
                    await Utility.CopyFileAsync(cardHashPath, cacheHashPath);
                }
            }

            // get retroarch version
            var retroVersion = await Utility.GetRetroArchVersion(exePath) ?? "UNKNOWN";

            Utility.Notify(" Loading " + card.Name);
            Console.WriteLine($"  game: {card.Name}");
            Console.WriteLine($"  rom:  {card.RomPath}");
            Console.WriteLine($"  core: {card.CorePath}");
            Console.WriteLine($"  retroarch: {exePath} ({retroVersion})");
            Console.WriteLine($"  card version: {card.Version}");

            // set ui waiting/loading state (TODO: show loading stuff in ui)
            _mainWindow?.ShowGame(
                card.Name, card.Description, card.Platform,
                card.Year, Path.GetFileName(card.Core),
                card.ImagePath, card.Version, retroVersion);

            // start retroarch
            _retroArch.Start(
                retroArchExecutable: exePath,
                romPath: card.RomPath,
                corePath: card.CorePath,
                fullscreen: Settings.RetroArch.StartFullscreen,
                shader: AppSettings.GetShaderPath(Settings.RetroArch.Shader));
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
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