using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard;

public sealed class CardMonitor : IDisposable
{
    private readonly string _label;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cancellation = new();

    private Task? _monitorTask;
    private CardInfo? _currentVolume;
    private bool _disposed;

    public event EventHandler<VolumeEventArgs>? Inserted;
    public event EventHandler<VolumeEventArgs>? Removed;

    public bool IsRunning => _monitorTask is { IsCompleted: false };

    public CardMonitor(string label = "RETROGC", TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        _label = label;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// Starts the monitor in the background. Safe to call more than once.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return;

        _monitorTask = MonitorAsync(_cancellation.Token);
    }

    /// <summary>
    /// Stops the monitor and waits for its background task to exit.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed)
            return;

        await _cancellation.CancelAsync();

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during normal shutdown.
            }
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine("CardMonitor: starting monitor task (label: "
                              + _label + ", poll interval: " + _pollInterval + ")");

            while (!cancellationToken.IsCancellationRequested)
            {
                var foundVolume = FindVolumeByLabel(_label);

                if (_currentVolume is null && foundVolume is not null)
                {
                    _currentVolume = foundVolume;
                    Inserted?.Invoke(this, new VolumeEventArgs(foundVolume));
                }
                else if (_currentVolume is not null && foundVolume is null)
                {
                    var removedVolume = _currentVolume;
                    _currentVolume = null;
                    Removed?.Invoke(this, new VolumeEventArgs(removedVolume));
                }
                else if (_currentVolume is not null &&
                         foundVolume is not null &&
                         !PathsEqual(_currentVolume.MountPoint, foundVolume.MountPoint))
                {
                    var removedVolume = _currentVolume;
                    _currentVolume = foundVolume;

                    Removed?.Invoke(this, new VolumeEventArgs(removedVolume));
                    Inserted?.Invoke(this, new VolumeEventArgs(foundVolume));
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when StopAsync() or Dispose() is called.
        }
    }

    private static CardInfo? FindVolumeByLabel(string expectedLabel)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;
                
                var label = OperatingSystem.IsLinux() ? Path.GetFileName(drive.RootDirectory.FullName) : drive.VolumeLabel;
                if (!string.Equals(label, expectedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                return new CardInfo(
                    MountPoint: drive.RootDirectory.FullName,
                    Label: drive.VolumeLabel,
                    FileSystem: drive.DriveFormat,
                    DriveType: drive.DriveType);
            }
            catch (IOException)
            {
                // Device was removed while its properties were read.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore volumes whose metadata is unavailable.
            }
        }

        return null;
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}

public sealed record CardInfo(
    string MountPoint,
    string Label,
    string FileSystem,
    DriveType DriveType);

public sealed class VolumeEventArgs(CardInfo volume) : EventArgs
{
    public CardInfo Volume { get; } = volume;
}