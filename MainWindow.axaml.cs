using System;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RetroGameCard;

public partial class MainWindow : Window
{
    private readonly AppContext _appContext;
    private readonly DispatcherTimer _clockTimer;
    private Bitmap? _previewBitmap;
    private string? _retroVersion;

    private static readonly IBrush ReadyColor =
        Brush.Parse("#5CE1B7");

    private static readonly IBrush WaitingColor =
        Brush.Parse("#F2BF5E");

    public MainWindow()
    {
        InitializeComponent();

        // create app data context
        DataContext = _appContext = new AppContext(true);

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _clockTimer.Tick += (_, _) => UpdateClock();

        // 
        Loaded += OnLoaded;

        Opened += (_, _) =>
        {
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

//#if !DEBUG
        SetFullScreen(true);
//#endif
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        try
        {
            // check for retroarch version (and so executable)
            _retroVersion = await RetroArchInstaller.GetVersion();
            if (!string.IsNullOrEmpty(_retroVersion))
            {
                ShowWaitingForGame();
                return; // ok
            }

            // if we were not able to get version but executable exists, assume it's ok
            if (File.Exists(Utility.GetRetroArchExecutable()))
            {
                ShowWaitingForGame();
                return; // ok
            }

            if (OperatingSystem.IsWindows())
            {
                // ask user for retroarch installation
                Opened += async (s, _) =>
                {
                    var ok = await RetroArchInstaller.InstallAsync(s as Window);
                    Console.WriteLine("retroarch installation: " + ok);
                };
            }
            else
            {
                Console.WriteLine("retroarch installation not found");
                // TODO: show help
            }

            ShowWaitingForGame();
#if !DEBUG
            // hide window at startup
            //if (RetroArchInstaller.ExePathExists)
            //    Loaded += (s, _) => { (s as Window)?.Hide(); };
#endif
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private void OnMainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.F11)) return;

        SetFullScreen(WindowState != WindowState.FullScreen);
        e.Handled = true;
    }

    public void SetFullScreen(bool isFullScreen)
    {
        WindowState = isFullScreen ? WindowState.FullScreen : WindowState.Normal;
        Cursor = isFullScreen ? new Cursor(StandardCursorType.None) : new Cursor(StandardCursorType.Arrow);
    }

    public void ShowWaitingForGame()
    {
        GameTitleText.Text = "Waiting for game";
        GameDescriptionText.Text =
            "Insert an SD card or select a game. The launcher is monitoring for a compatible Retro Game Card.";

        YearText.Text = "WAITING";
        PlatformText.Text = "WAITING";
        CoreText.Text = "WAITING";

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
        string year, string core, string preview)
    {
        GameTitleText.Text = title;
        GameDescriptionText.Text = description;

        YearText.Text = year.ToUpperInvariant();
        PlatformText.Text = platform.ToUpperInvariant();
        CoreText.Text = core.ToUpperInvariant();

        HeaderStatusText.Text = "GAME CARD DETECTED";
        StatusText.Text = "READY";
        StatusDetailText.Text = "RUNNING";
        CardStatusText.Text = "CARD READY";

        PreviewCaptionText.Text = "platform".ToUpperInvariant();
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