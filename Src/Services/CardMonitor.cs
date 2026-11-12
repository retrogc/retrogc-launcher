using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard.Services;

/// <summary>
/// Polls the mounted volumes for one with a given label (RETROGC).
/// Events are raised on a thread-pool thread.
/// </summary>
public sealed class CardMonitor : IAsyncDisposable
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

    /// <summary>The currently detected card volume, if any.</summary>
    public CardInfo? Current => Volatile.Read(ref _currentVolume);

    public CardMonitor(string label = "RETROGC", TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        _label = label;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Starts polling in the background. Safe to call more than once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            return;

        // Task.Run: drive enumeration must never run on (and stall) the UI thread.
        _monitorTask = Task.Run(() => MonitorAsync(_cancellation.Token));
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        Log.Info($"CardMonitor: watching for label '{_label}' every {_pollInterval.TotalMilliseconds} ms");

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            do
            {
                Poll();
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private void Poll()
    {
        CardInfo? found;
        try
        {
            found = FindVolumeByLabel(_label);
        }
        catch (Exception exception)
        {
            Log.Error("CardMonitor: drive enumeration failed", exception);
            return;
        }

        var current = _currentVolume;
        if (current is null && found is null)
            return;

        if (current is not null && found is not null && PathsEqual(current.MountPoint, found.MountPoint))
            return;

        Volatile.Write(ref _currentVolume, found);

        if (current is not null)
            Raise(Removed, current);
        if (found is not null)
            Raise(Inserted, found);
    }

    private void Raise(EventHandler<VolumeEventArgs>? handler, CardInfo volume)
    {
        try
        {
            handler?.Invoke(this, new VolumeEventArgs(volume));
        }
        catch (Exception exception)
        {
            Log.Error("CardMonitor: event handler failed", exception);
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

                // On Linux, DriveInfo.VolumeLabel is the mount point; desktop automounters
                // mount removable volumes under a folder named after their label.
                var label = OperatingSystem.IsLinux()
                    ? Path.GetFileName(Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName))
                    : drive.VolumeLabel;

                if (!string.Equals(label, expectedLabel, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new CardInfo(
                    MountPoint: drive.RootDirectory.FullName,
                    Label: label,
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

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _cancellation.CancelAsync();

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

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
