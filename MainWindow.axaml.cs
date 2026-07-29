using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RetroGameCard.Controls;

namespace RetroGameCard;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clockTimer;
    private Bitmap? _previewBitmap;
    private string? _retroVersion;

    private static readonly IBrush ReadyColor =
        Brush.Parse("#5CE1B7");

    private static readonly IBrush WaitingColor =
        Brush.Parse("#F2BF5E");

    public MainWindow()
    {
        Console.WriteLine("MainWindow");

        InitializeComponent();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _clockTimer.Tick += (_, _) => UpdateClock();

        // 
        Loaded += OnLoaded;

        Opened += (_, _) =>
        {
            Console.WriteLine("MainWindow::OnOpened...");
            UpdateClock();
            _clockTimer.Start();
        };

        // prevent closing the window (force hide)
        Closing += (s, e) =>
        {
            (s as Window)?.Hide();
            e.Cancel = true;
        };

        Closed += (_, _) => _clockTimer.Stop();

        SetWindowState(App.Settings.Main.StartupWindowState);
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        try
        {
            Console.WriteLine("MainWindow::OnLoaded...");

            // check for retroarch version (and so executable)
            _retroVersion = await RetroArchInstaller.GetVersion();
            if (!string.IsNullOrEmpty(_retroVersion))
            {
                ShowWaitingForGame();
                return; // ok
            }

            // if we were not able to get version but executable exists, assume it's ok
            if (File.Exists(App.Settings.RetroArch.ExecutablePath))
            {
                ShowWaitingForGame();
                return; // ok
            }

            // retroarch executable not found
            if (OperatingSystem.IsWindows())
            {
                // ask user for retroarch installation
                SetWindowState(StartupWindowState.Normal);
                var ok = await RetroArchInstaller.InstallAsync(this);
                Console.WriteLine("retroarch installation: " + ok);
            }
            else
            {
                Console.WriteLine("retroarch installation not found");
                // TODO: show help
            }

            ShowWaitingForGame();
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private async void OnMainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.F11 or Key.F12)) return;

        if (e.Key == Key.F12)
        {
            ShowSettings();
            e.Handled = true;
            return;
        }

        switch (WindowState)
        {
            case WindowState.FullScreen:
                SetWindowState(StartupWindowState.Normal);
                break;
            case WindowState.Normal:
                SetWindowState(StartupWindowState.Fullscreen);
                break;
            case WindowState.Minimized:
            case WindowState.Maximized:
            default:
                break;
        }

        e.Handled = true;
    }

    public void SetWindowState(StartupWindowState state)
    {
        Console.WriteLine("MainWindow::SetWindowState: " + state);
        switch (state)
        {
            case StartupWindowState.Fullscreen:
                WindowState = WindowState.FullScreen;
                Cursor = new Cursor(StandardCursorType.None);
                Show();
                break;
            case StartupWindowState.Normal:
                WindowState = WindowState.Normal;
                Cursor = new Cursor(StandardCursorType.Arrow);
                Show();
                break;
            case StartupWindowState.Minimized:
            default:
                //WindowState = WindowState.Minimized;
                Hide();
                break;
        }
    }

    public async void ShowSettings()
    {
        try
        {
            Show();
            var window = new SettingsWindow(App.Settings);
            await window.ShowDialog(this);
        }
        catch (Exception)
        {
            // ignored
        }
    }

    public void ShowWaitingForGame()
    {
        GameTitleText.Text = "Waiting for game";
        GameDescriptionText.Text =
            "Insert an SD card or select a game. The launcher is monitoring for a compatible Retro Game Card.";

        YearText.Text = "WAITING";
        PlatformText.Text = "WAITING";
        CoreText.Text = "WAITING";
        VersionText.Text = "WAITING";

        HeaderStatusText.Text = "SD CARD MONITOR ACTIVE";
        StatusText.Text = "WAITING";
        StatusDetailText.Text = "WAITING FOR GAME";
        CardStatusText.Text = "NO CARD DETECTED";

        PreviewCaptionText.Text = "GAME PREVIEW";
        PreviewIndexText.Text = "—";

        RetroArchVersionText.Text = $"RetroArch {_retroVersion}";

        PreviewImage.Source = null;
        NoPreviewText.IsVisible = true;

        SdActivityLight.Fill = WaitingColor;
        GameStateLight.Fill = WaitingColor;

        WaitingProgress.IsIndeterminate = true;
    }

    public void ShowGame(string title, string description, string platform,
        string year, string core, string preview, string version)
    {
        GameTitleText.Text = title;
        GameDescriptionText.Text = description;

        YearText.Text = year.ToUpperInvariant();
        PlatformText.Text = platform.ToUpperInvariant();
        CoreText.Text = core.ToUpperInvariant();
        VersionText.Text = version.ToUpperInvariant();

        HeaderStatusText.Text = "GAME CARD DETECTED";
        StatusText.Text = "READY";
        StatusDetailText.Text = "RUNNING";
        CardStatusText.Text = "RetroGameCard v." + version.ToUpperInvariant();

        PreviewCaptionText.Text = title;
        PreviewIndexText.Text = "READY";

        RetroArchVersionText.Text = $"RetroArch {_retroVersion}";

        if (File.Exists(preview))
        {
            _previewBitmap?.Dispose();
            _previewBitmap = new Bitmap(preview);
            PreviewImage.Source = _previewBitmap;
        }

        NoPreviewText.IsVisible = PreviewImage.Source is null;

        SdActivityLight.Fill = ReadyColor;
        GameStateLight.Fill = ReadyColor;

        WaitingProgress.IsIndeterminate = false;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;

        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString(
            "dddd, dd MMMM",
            culture
        ).ToUpper(culture);
    }
}