using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard;

public sealed class RetroArchLauncher : IDisposable
{
    private readonly Lock _sync = new();
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
        string exePath,
        string homePath,
        string retroPath,
        string romPath,
        string corePath,
        bool fullscreen,
        bool integerScaling,
        string shader = "")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);

        var fullRomPath = Path.GetFullPath(romPath);
        if (!File.Exists(fullRomPath))
        {
            throw new FileNotFoundException($"ROM file does not exist: {fullRomPath}", fullRomPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = retroPath,
            UseShellExecute = false,
            CreateNoWindow = true,
#if DEBUG
            RedirectStandardOutput = true,
            RedirectStandardError = true
#endif
        };

        // override some retroarch config that can't be set via command line
        var cfgPath = Path.Combine(retroPath, "retrogc.cfg");
        var settings = "video_shader_enable = \"true\"" + Environment.NewLine;
        settings += "video_fullscreen = \"" + fullscreen.ToString().ToLower() + "\"" + Environment.NewLine;
        if (integerScaling) settings += "video_scale_integer = \"true\"" + Environment.NewLine;
        File.WriteAllText(cfgPath, settings);
        startInfo.ArgumentList.Add("--appendconfig");
        startInfo.ArgumentList.Add(cfgPath);

        // retroarch: set env
        startInfo.Environment["HOME"] = homePath;
        startInfo.Environment["LIBRETRO_DIRECTORY"] = retroPath;
        startInfo.Environment["LIBRETRO_ASSETS_DIRECTORY"]
            = Path.Combine(retroPath, "assets");
        startInfo.Environment["LIBRETRO_AUTOCONFIG_DIRECTORY"]
            = Path.Combine(retroPath, "autoconfig");
        startInfo.Environment["LIBRETRO_CHEATS_DIRECTORY"]
            = Path.Combine(retroPath, "cheats");
        startInfo.Environment["LIBRETRO_DATABASE_DIRECTORY"]
            = Path.Combine(retroPath, "database");
        startInfo.Environment["LIBRETRO_SYSTEM_DIRECTORY"]
            = Path.Combine(retroPath, "system");
        startInfo.Environment["LIBRETRO_VIDEO_FILTER_DIRECTORY"]
            = Path.Combine(retroPath, "filters");
        startInfo.Environment["LIBRETRO_VIDEO_SHADER_DIRECTORY"]
            = Path.Combine(retroPath, "shaders");

#if DEBUG
        startInfo.ArgumentList.Add("-v");
#endif

        // retroarch: handle shader setting
        if (!string.IsNullOrEmpty(shader))
        {
            startInfo.ArgumentList.Add("--set-shader");
            startInfo.ArgumentList.Add(shader);
        }

        // retroarch: fullscreen
        if (fullscreen)
        {
            startInfo.ArgumentList.Add("--fullscreen");
        }

        // retroarch: load specific libretro core.
        if (!string.IsNullOrWhiteSpace(corePath))
        {
            startInfo.ArgumentList.Add("-L");
            startInfo.ArgumentList.Add(Path.GetFullPath(corePath));
        }

        // ROM path must be the final RetroArch argument.
        startInfo.ArgumentList.Add(fullRomPath);

        Console.WriteLine("Sarting retroarch...");
        Console.WriteLine($" - HOME: {startInfo.Environment["HOME"]}");
        Console.WriteLine($" - LIBRETRO_DIRECTORY: {startInfo.Environment["LIBRETRO_DIRECTORY"]}");
        Console.WriteLine($" - WORKING DIR: {startInfo.WorkingDirectory}");
        Console.WriteLine($" - EXECUTABLE: {startInfo.FileName}");
        Console.WriteLine($" - ARGUMENTS: {string.Join(" ", startInfo.ArgumentList)}");

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

#if DEBUG
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.WriteLine(e.Data);
        };
#endif
        process.Exited += OnProcessExited;

        lock (_sync)
        {
            ThrowIfRunning();

            if (!process.Start())
            {
                process.Exited -= OnProcessExited;
                process.Dispose();

                throw new InvalidOperationException($"Unable to launch '{exePath}'.");
            }

#if DEBUG
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
#endif

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