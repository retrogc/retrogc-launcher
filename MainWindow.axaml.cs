using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace RetroGameCard;

public partial class MainWindow : Window
{
    private readonly AppContext _appContext;
    private Bitmap? _previewBitmap;

    public MainWindow()
    {
        Console.WriteLine("MainWindow...");
        InitializeComponent();

#if !DEBUG
        // hide window at startup
        Loaded += (s, _) => { (s as Window)?.Hide(); };
#endif

        //Opened += (_, _) => _timer?.Start();

        // prevent closing the window (force hide)
        Closing += (s, e) =>
        {
            //_timer?.Stop();
            (s as Window)?.Hide();
            e.Cancel = true;
        };

        // create app data context
        DataContext = _appContext = new AppContext(true);
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