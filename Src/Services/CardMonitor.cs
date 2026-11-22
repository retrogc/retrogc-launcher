using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace RetroGameCard.Services;

/// <summary>
/// Watches the mounted volumes for one with a given label (RETROGC).
/// Linux: polls every 500 ms. Windows: woken by device notifications (what Explorer reacts to), with a
/// 2 s fallback poll; the card's removal comes from a notification on its volume, and drive queries
/// (which can block ~20 s on a USB stick being plugged / pulled) never block the watcher.
/// Events are raised on a thread-pool thread.
/// </summary>
public sealed class CardMonitor : IAsyncDisposable
{
    private static readonly TimeSpan WindowsPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DriveQueryTimeout = TimeSpan.FromSeconds(2);

    private readonly string _label;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

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
        _pollInterval = pollInterval ?? (OperatingSystem.IsWindows() ? WindowsPollInterval : TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Starts watching in the background. Safe to call more than once.</summary>
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
        Log.Info($"CardMonitor: watching for label '{_label}' (poll every {_pollInterval.TotalMilliseconds} ms)");

        if (OperatingSystem.IsWindows())
            StartWindowsEvents();

        try
        {
            while (true)
            {
                if (OperatingSystem.IsWindows())
                    await PollWindowsAsync().ConfigureAwait(false);
                else
                    PollLinux();

                try
                {
                    // Woken early by a device notification (Windows).
                    await _wake.WaitAsync(_pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                StopWindowsEvents();
        }
    }

    /// <summary>Makes the watcher check now (called from device notifications).</summary>
    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (Exception exception) when (exception is SemaphoreFullException or ObjectDisposedException)
        {
            // already woken / shutting down
        }
    }

    private void Update(CardInfo? found, string? lostReason = null)
    {
        var current = _currentVolume;
        if (current is null && found is null)
            return;

        if (current is not null && found is not null && PathsEqual(current.MountPoint, found.MountPoint))
            return;

        if (current is not null)
            Log.Info($"CardMonitor: {_label} no longer found at {current.MountPoint}{(lostReason is null ? "" : $" ({lostReason})")}");
        if (found is not null)
            Log.Info($"CardMonitor: {_label} found at {found.MountPoint} ({found.FileSystem}, {found.DriveType})");

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

    // ------------------------------------------------------------ Linux (and the shared drive query)

    private void PollLinux()
    {
        CardInfo? found = null;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (QueryDrive(drive) is { } card)
                {
                    found = card;
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error("CardMonitor: drive enumeration failed", exception);
            return;
        }

        Update(found);
    }

    /// <summary>The card on <paramref name="drive"/>, or null. These calls can block on a dying device.</summary>
    private CardInfo? QueryDrive(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady)
                return null;

            // On Linux, DriveInfo.VolumeLabel is the mount point; desktop automounters
            // mount removable volumes under a folder named after their label.
            var label = OperatingSystem.IsLinux()
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName))
                : drive.VolumeLabel;

            if (!string.Equals(label, _label, StringComparison.OrdinalIgnoreCase))
                return null;

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

        return null;
    }

    // ------------------------------------------------------------ Windows

    private WindowsDeviceEvents? _volumeEvents, _diskEvents;

    /// <summary>Drive queries still running, at most one per drive (a blocked call can't be cancelled).</summary>
    private readonly Dictionary<string, (Task<CardInfo?> Task, Stopwatch Watch, bool Reported)> _queries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mount points whose card was reported removed by a notification but which are still listed
    /// (stale volume kept by files RetroArch has open): ignored until they leave the drive list.</summary>
    private readonly HashSet<string> _removedMountPoints = new(StringComparer.OrdinalIgnoreCase);

    private VolumeWatch? _watch;
    private volatile bool _arrivalSinceRemoval;

    /// <summary>Removal notifications for the current card's volume.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class VolumeWatch(string mountPoint, SafeFileHandle handle) : IDisposable
    {
        public string MountPoint { get; } = mountPoint;
        public SafeFileHandle Handle { get; } = handle;
        public WindowsDeviceEvents? Events { get; set; }
        public volatile bool Removed;      // removal pending / complete
        public volatile bool Released;     // handle closed for a query remove
        public volatile bool RemoveFailed; // query remove refused: watch it again

        public void Dispose()
        {
            Events?.Dispose();
            Handle.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private void StartWindowsEvents()
    {
        try
        {
            _volumeEvents = WindowsDeviceEvents.RegisterInterface(WindowsDeviceEvents.VolumeInterface, OnInterfaceEvent);
            _diskEvents = WindowsDeviceEvents.RegisterInterface(WindowsDeviceEvents.DiskInterface, OnInterfaceEvent);
            Log.Info("CardMonitor: device notifications enabled");
        }
        catch (Exception exception)
        {
            Log.Error("CardMonitor: device notifications unavailable, polling only", exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private void StopWindowsEvents()
    {
        _volumeEvents?.Dispose();
        _diskEvents?.Dispose();
        _watch?.Dispose();
        _watch = null;
    }

    private void OnInterfaceEvent(DeviceAction action)
    {
        if (action == DeviceAction.InterfaceArrival)
            _arrivalSinceRemoval = true;
        Wake();
    }

    [SupportedOSPlatform("windows")]
    private async Task PollWindowsAsync()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives(); // GetLogicalDrives: never touches the devices
        }
        catch (Exception exception)
        {
            Log.Error("CardMonitor: drive enumeration failed", exception);
            return;
        }

        var roots = drives.Select(d => d.RootDirectory.FullName).ToList();

        // 1. The current card's volume reported its removal: removed now, nothing is asked to the drive.
        var current = _currentVolume;
        if (_watch is { Removed: true } removedWatch)
        {
            Log.Info($"CardMonitor: {removedWatch.MountPoint} removed (device notification)");
            _removedMountPoints.Add(removedWatch.MountPoint);
            _arrivalSinceRemoval = false;
            DisposeWatch();
            if (current is not null && PathsEqual(current.MountPoint, removedWatch.MountPoint))
            {
                Update(null, "device notification");
                current = null;
            }
        }

        // A removed card's mount point counts again once it left the drive list, or once a volume
        // arrived since (the card plugged in again, possibly on the same letter).
        _removedMountPoints.RemoveWhere(root => !roots.Contains(root, StringComparer.OrdinalIgnoreCase) || _arrivalSinceRemoval);

        // 2. Query every drive, without ever waiting more than DriveQueryTimeout. Network drives are
        //    skipped (a card never is one): querying a share whose server doesn't answer blocks ~20 s.
        //    DriveType (GetDriveType) itself never touches the device or the network.
        var candidates = drives.Where(d => !_removedMountPoints.Contains(d.RootDirectory.FullName) &&
                                           SafeDriveType(d) != DriveType.Network).ToList();
        foreach (var drive in candidates)
        {
            var root = drive.RootDirectory.FullName;
            if (!_queries.ContainsKey(root))
                _queries[root] = (Task.Run(() => QueryDrive(drive)), Stopwatch.StartNew(), false);
        }

        var running = candidates.Select(d => _queries[d.RootDirectory.FullName].Task).ToArray();
        try
        {
            await Task.WhenAll(running).WaitAsync(DriveQueryTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // some drive is slow: handled per drive below
        }

        // 3. First drive holding the card. A query still running on the current card's drive keeps the
        //    card (its removal comes from the notification), anywhere else it is just skipped for now.
        CardInfo? found = null;
        var currentPending = false;
        foreach (var drive in candidates)
        {
            var root = drive.RootDirectory.FullName;
            var query = _queries[root];
            if (!query.Task.IsCompleted)
            {
                if (!query.Reported && query.Watch.Elapsed > DriveQueryTimeout)
                {
                    Log.Info($"CardMonitor: query of {root} is taking more than {DriveQueryTimeout.TotalSeconds:0} s, skipped meanwhile");
                    _queries[root] = query with { Reported = true };
                }

                if (current is not null && PathsEqual(current.MountPoint, root))
                    currentPending = true;
                continue;
            }

            _queries.Remove(root);
            if (query.Reported)
                Log.Info($"CardMonitor: query of {root} took {query.Watch.Elapsed.TotalSeconds:0.0} s");

            if (found is null && query.Task.Result is { } card)
                found = card;
        }

        if (found is null && currentPending)
            found = current;

        // Forget finished queries of drives that left the list.
        foreach (var root in _queries.Where(q => q.Value.Task.IsCompleted && !roots.Contains(q.Key, StringComparer.OrdinalIgnoreCase))
                     .Select(q => q.Key).ToList())
            _queries.Remove(root);

        Update(found);
        UpdateWatch(_currentVolume);
    }

    private static DriveType SafeDriveType(DriveInfo drive)
    {
        try
        {
            return drive.DriveType;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DriveType.Unknown;
        }
    }

    /// <summary>Keeps a removal notification registered on the current card's volume.</summary>
    [SupportedOSPlatform("windows")]
    private void UpdateWatch(CardInfo? card)
    {
        // While a "Safely remove" is in progress (handle released, no answer yet), nothing is reopened:
        // a new handle would make Windows refuse the eject.
        if (_watch is not null && (card is null || !PathsEqual(_watch.MountPoint, card.MountPoint) || _watch.RemoveFailed))
        {
            if (_watch.RemoveFailed)
                Log.Info($"CardMonitor: removal of {_watch.MountPoint} was refused, watching it again");
            DisposeWatch();
        }

        if (card is null || _watch is not null)
            return;

        SafeFileHandle? handle = null;
        try
        {
            handle = WindowsDeviceEvents.OpenVolume(card.MountPoint);
            var watch = new VolumeWatch(card.MountPoint, handle);
            watch.Events = WindowsDeviceEvents.RegisterHandle(handle, action => OnVolumeEvent(watch, action));
            _watch = watch;
            Log.Info($"CardMonitor: watching {card.MountPoint} for removal (device notification)");
        }
        catch (Exception exception)
        {
            handle?.Dispose();
            // Polling still detects the removal, only slower.
            Log.Error($"CardMonitor: can't watch {card.MountPoint} for removal", exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnVolumeEvent(VolumeWatch watch, DeviceAction action)
    {
        switch (action)
        {
            case DeviceAction.QueryRemove:
                // "Safely remove": our handle must go, or Windows refuses the eject.
                watch.Released = true;
                watch.Handle.Dispose();
                break;
            case DeviceAction.QueryRemoveFailed:
                watch.RemoveFailed = true;
                break;
            case DeviceAction.RemovePending:
            case DeviceAction.RemoveComplete:
                watch.Removed = true;
                break;
            default:
                return;
        }

        Wake();
    }

    [SupportedOSPlatform("windows")]
    private void DisposeWatch()
    {
        _watch?.Dispose();
        _watch = null;
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
        _wake.Dispose();
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
