using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RetroGameCard;

public sealed class RetroArchLauncher : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false } process
                    ? process.Id
                    : null;
            }
        }
    }

    public event EventHandler<RetroArchExitedEventArgs>? Exited;

    [DllImport("User32.dll", SetLastError = true)]
    static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    /// <summary>
    /// Starts RetroArch and opens the supplied ROM.
    /// Throws if RetroArch is already running through this launcher.
    /// </summary>
    public void Start(
        string retroArchExecutable,
        string romPath,
        string? corePath = null,
        string? workingDirectory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentException.ThrowIfNullOrWhiteSpace(retroArchExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);

        var fullRomPath = Path.GetFullPath(romPath);
        if (!File.Exists(fullRomPath))
        {
            throw new FileNotFoundException(
                $"ROM file does not exist: {fullRomPath}",
                fullRomPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = retroArchExecutable,
            WorkingDirectory = workingDirectory
                               ?? Path.GetDirectoryName(fullRomPath)
                               ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };

        // Optional: load a specific libretro core.
        if (!string.IsNullOrWhiteSpace(corePath))
        {
            startInfo.ArgumentList.Add("-L");
            startInfo.ArgumentList.Add(Path.GetFullPath(corePath));
        }

        // ROM path must be the final RetroArch argument.
        startInfo.ArgumentList.Add(fullRomPath);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Exited += OnProcessExited;

        lock (_sync)
        {
            ThrowIfRunning();

            if (!process.Start())
            {
                process.Exited -= OnProcessExited;
                process.Dispose();

                throw new InvalidOperationException(
                    $"Unable to launch '{retroArchExecutable}'.");
            }

            if (OperatingSystem.IsWindows())
            {
                SwitchToThisWindow(process.MainWindowHandle, true);
            }

            _process = process;
        }
    }

    /// <summary>
    /// Requests a normal shutdown for the RetroArch GUI.
    /// Returns false when no launched RetroArch process is running.
    /// </summary>
    public bool RequestClose()
    {
        Process? process;

        lock (_sync)
        {
            process = GetRunningProcess();
        }

        if (process is null)
            return false;

        try
        {
            return process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Force-kills RetroArch and its descendant processes.
    /// Use this after removal of the RETROGC card if RetroArch did not exit
    /// cleanly through RequestClose().
    /// </summary>
    public async Task<bool> KillAsync()
    {
        Process? process;

        lock (_sync)
        {
            process = GetRunningProcess();
        }

        if (process is null)
            return false;

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return true;
        }
        catch (InvalidOperationException)
        {
            // It exited between the HasExited test and Kill().
            return false;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitedProcess = (Process)sender!;
        int processId;

        try
        {
            processId = exitedProcess.Id;
        }
        catch
        {
            processId = -1;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_process, exitedProcess))
            {
                _process = null;
            }
        }

        Exited?.Invoke(this, new RetroArchExitedEventArgs(processId));

        exitedProcess.Exited -= OnProcessExited;
        exitedProcess.Dispose();
    }

    private Process? GetRunningProcess()
    {
        return _process is { HasExited: false } process ? process : null;
    }

    private void ThrowIfRunning()
    {
        if (GetRunningProcess() is not null)
        {
            throw new InvalidOperationException(
                "RetroArch is already running.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await KillAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            Process? process;

            lock (_sync)
            {
                process = GetRunningProcess();
            }

            if (process is not null)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }
}

public sealed class RetroArchExitedEventArgs(int processId) : EventArgs
{
    public int ProcessId { get; } = processId;
}