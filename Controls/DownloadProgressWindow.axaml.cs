using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RetroGameCard.Controls;

public partial class DownloadProgressWindow : Window
{
    public event Action? CancelRequested;

    public DownloadProgressWindow()
    {
        InitializeComponent();
    }

    public void UpdateProgress(DownloadProgressInfo info)
    {
        ProgressBar.Value = info.Percentage;

        var received = info.BytesReceived / 1024.0 / 1024.0;
        var total = info.TotalBytes > 0 ? info.TotalBytes / 1024.0 / 1024.0 : 0;

        StatusText.Text = "Downloading RetroArch installer...";
        DetailText.Text = info.TotalBytes > 0
            ? $"{received:0.0} MB / {total:0.0} MB @ {info.SpeedMbPerSec:0.00} MB/s"
            : $"{received:0.0} MB downloaded @ {info.SpeedMbPerSec:0.00} MB/s";
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke();
    }
}