using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace RetroGameCard;

public partial class MainWindow : Window
{
    private readonly AppContext _appContext;
    private Bitmap? _previewBitmap;
    private string? _retroVersion;

    public MainWindow()
    {
        InitializeComponent();

        // create app data context
        DataContext = _appContext = new AppContext(true);

        // 
        Loaded += OnLoaded;

        // prevent closing the window (force hide)
        Closing += (s, e) =>
        {
            (s as Window)?.Hide();
            e.Cancel = true;
        };

#if !DEBUG
        SetFullScreen(true);
#endif
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        try
        {
            // check for retroarch version (and so executable)
            _retroVersion = await RetroArchInstaller.GetVersion();
            if (!string.IsNullOrEmpty(_retroVersion)) return; // ok

            // if we were not able to get version but executable exists, assume it's ok
            if (File.Exists(RetroArchInstaller.ExePath)) return; // ok

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
                // TODO: handle linux 
            }
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
        if (e.Key != Key.Escape || WindowState != WindowState.FullScreen) return;
        SetFullScreen(false);
        e.Handled = true;
    }

    public void SetFullScreen(bool isFullScreen)
    {
        WindowState = isFullScreen ? WindowState.FullScreen : WindowState.Normal;
        Cursor = isFullScreen ? new Cursor(StandardCursorType.None) : new Cursor(StandardCursorType.Arrow);
    }

    public void LoadPreview(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("LoadPreview: not found (" + path + ")");
            return;
        }

        _previewBitmap?.Dispose();
        _previewBitmap = new Bitmap(path);
        PreviewImage.Source = _previewBitmap;
    }

    public void UnloadPreview()
    {
        _previewBitmap?.Dispose();
        PreviewImage.Source = _previewBitmap = null;
    }
}