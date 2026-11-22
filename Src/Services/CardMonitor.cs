using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

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

    private CardInfo? FindVolumeByLabel(string expectedLabel)
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

                if (OperatingSystem.IsWindows() && IsDeviceGone(drive.RootDirectory.FullName))
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

    // ------------------------------------------------------------ Windows: is the device still there?

    // After a surprise removal, Windows can keep a volume (drive letter, cached label) alive while
    // files on it are still open, e.g. the ROM RetroArch is running: DriveInfo keeps "finding" the
    // card. The storage device itself is asked instead.
    private const int GoneStrikesNeeded = 2; // two polls in a row (~1 s), one odd answer never stops a game

    private string? _goneMountPoint;
    private int _goneStrikes;

    [SupportedOSPlatform("windows")]
    private bool IsDeviceGone(string mountPoint)
    {
        var error = Windows.CheckDevice(mountPoint);
        if (error == 0 || !Windows.IsGoneError(error))
        {
            // Present, or a driver that can't tell (e.g. no IOCTL support): keep today's behaviour.
            if (_goneMountPoint is not null && PathsEqual(_goneMountPoint, mountPoint))
                _goneMountPoint = null;
            return false;
        }

        if (_goneMountPoint is null || !PathsEqual(_goneMountPoint, mountPoint))
        {
            _goneMountPoint = mountPoint;
            _goneStrikes = 0;
        }

        _goneStrikes++;
        if (_goneStrikes == GoneStrikesNeeded)
            Log.Info($"CardMonitor: {mountPoint} is still listed but its device is gone (error {error})");
        return _goneStrikes >= GoneStrikesNeeded;
    }

    [SupportedOSPlatform("windows")]
    private static class Windows
    {
        private const uint ShareReadWrite = 0x3, OpenExisting = 3;
        private const uint IoctlStorageCheckVerify2 = 0x002D0800;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
            IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

        /// <summary>0 when the device of <paramref name="mountPoint"/> answers, else the Win32 error.</summary>
        public static int CheckDevice(string mountPoint)
        {
            // "E:\" -> "\\.\E:" (the volume, not its root folder), opened without any access right:
            // doesn't touch the volume. Anything but a drive letter: can't tell, report present.
            if (mountPoint.Length is < 2 or > 3 || !char.IsAsciiLetter(mountPoint[0]) || mountPoint[1] != ':' ||
                (mountPoint.Length == 3 && mountPoint[2] is not ('\\' or '/')))
                return 0;

            var volume = @"\\.\" + mountPoint[..2];
            using var handle = CreateFile(volume, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return Marshal.GetLastWin32Error();

            return DeviceIoControl(handle, IoctlStorageCheckVerify2, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)
                ? 0
                : Marshal.GetLastWin32Error();
        }

        /// <summary>Errors that mean the device or its media is not there any more.</summary>
        public static bool IsGoneError(int error) => error is
            2 or    // ERROR_FILE_NOT_FOUND
            3 or    // ERROR_PATH_NOT_FOUND
            21 or   // ERROR_NOT_READY
            433 or  // ERROR_NO_SUCH_DEVICE
            1112 or // ERROR_NO_MEDIA_IN_DRIVE
            1167;   // ERROR_DEVICE_NOT_CONNECTED
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
