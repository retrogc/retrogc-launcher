using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard.Services;

/// <summary>
/// Options for a single RetroArch launch.
/// </summary>
public sealed record RetroArchLaunchOptions(
    RetroArchEnvironment Environment,
    string RomPath,
    string CorePath,
    bool Fullscreen,
    bool IntegerScaling,
    string Shader);

/// <summary>
/// Starts / stops a single RetroArch process.
/// </summary>
public sealed class RetroArchLauncher : IAsyncDisposable
{
    private readonly Lock _sync = new();
    private Process? _process;
    private bool _disposed;

    public event EventHandler<RetroArchExitedEventArgs>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return GetRunningProcess() is not null;
        }
    }

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    /// <summary>
    /// Starts RetroArch with the supplied ROM. Throws if it is already running.
    /// </summary>
    public void Start(RetroArchLaunchOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var env = options.Environment;
        var romPath = Path.GetFullPath(options.RomPath);

        if (!File.Exists(env.ExecutablePath))
            throw new FileNotFoundException($"RetroArch executable not found: {env.ExecutablePath}", env.ExecutablePath);
        if (!File.Exists(romPath))
            throw new FileNotFoundException($"ROM not found: {romPath}", romPath);
        if (!File.Exists(options.CorePath))
            throw new FileNotFoundException($"Core not installed: {Path.GetFileName(options.CorePath)}", options.CorePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = env.ExecutablePath,
            WorkingDirectory = Directory.Exists(env.RetroPath)
                ? env.RetroPath
                : Path.GetDirectoryName(env.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
#if DEBUG
            RedirectStandardOutput = true,
            RedirectStandardError = true
#endif
        };

        // Settings that can't be passed on the command line go into an appended config.
        Directory.CreateDirectory(env.RetroPath);
        var cfgPath = Path.Combine(env.RetroPath, "retrogc.cfg");
        var cfg = new StringBuilder()
            .AppendLine("video_shader_enable = \"true\"")
            .AppendLine($"video_fullscreen = \"{(options.Fullscreen ? "true" : "false")}\"")
            .AppendLine($"video_scale_integer = \"{(options.IntegerScaling ? "true" : "false")}\"");
        File.WriteAllText(cfgPath, cfg.ToString());

        startInfo.ArgumentList.Add("--appendconfig");
        startInfo.ArgumentList.Add(cfgPath);

        if (env.HomePath is not null)
            startInfo.Environment["HOME"] = env.HomePath;

        startInfo.Environment["LIBRETRO_DIRECTORY"] = env.RetroPath;
        startInfo.Environment["LIBRETRO_ASSETS_DIRECTORY"] = Path.Combine(env.RetroPath, "assets");
        startInfo.Environment["LIBRETRO_AUTOCONFIG_DIRECTORY"] = Path.Combine(env.RetroPath, "autoconfig");
        startInfo.Environment["LIBRETRO_CHEATS_DIRECTORY"] = Path.Combine(env.RetroPath, "cheats");
        startInfo.Environment["LIBRETRO_DATABASE_DIRECTORY"] = Path.Combine(env.RetroPath, "database");
        startInfo.Environment["LIBRETRO_SYSTEM_DIRECTORY"] = env.SystemPath;
        startInfo.Environment["LIBRETRO_VIDEO_FILTER_DIRECTORY"] = Path.Combine(env.RetroPath, "filters");
        startInfo.Environment["LIBRETRO_VIDEO_SHADER_DIRECTORY"] = Path.Combine(env.RetroPath, "shaders");

#if DEBUG
        startInfo.ArgumentList.Add("-v");
#endif

        if (!string.IsNullOrEmpty(options.Shader))
        {
            startInfo.ArgumentList.Add("--set-shader");
            startInfo.ArgumentList.Add(options.Shader);
        }

        if (options.Fullscreen)
            startInfo.ArgumentList.Add("--fullscreen");

        startInfo.ArgumentList.Add("-L");
        startInfo.ArgumentList.Add(Path.GetFullPath(options.CorePath));

        // The ROM must be the last argument.
        startInfo.ArgumentList.Add(romPath);

        Log.Info("Starting RetroArch");
        Log.Info($"  HOME:      {env.HomePath ?? "(unchanged)"}");
        Log.Info($"  RETROPATH: {env.RetroPath}");
        Log.Info($"  EXE:       {startInfo.FileName}");
        Log.Info($"  ARGS:      {string.Join(' ', startInfo.ArgumentList)}");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
#if DEBUG
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
#endif
        process.Exited += OnProcessExited;

        lock (_sync)
        {
            if (GetRunningProcess() is not null)
            {
                process.Dispose();
                throw new InvalidOperationException("RetroArch is already running.");
            }

            if (!process.Start())
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
                throw new InvalidOperationException($"Unable to launch '{env.ExecutablePath}'.");
            }

#if DEBUG
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
#endif
            _process = process;
        }

        if (OperatingSystem.IsWindows())
            _ = BringToFrontAsync(process);
    }

    /// <summary>
    /// Asks RetroArch to close, then kills it (and its children) if it is still alive after
    /// <paramref name="grace"/>. Returns false when nothing was running.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan grace)
    {
        Process? process;
        lock (_sync)
            process = GetRunningProcess();

        if (process is null)
            return false;

        try
        {
            if (process.CloseMainWindow())
            {
                using var timeout = new CancellationTokenSource(grace);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    // fall through to kill
                }
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return true;
        }
        catch (InvalidOperationException)
        {
            // exited in between
            return false;
        }
    }

    private static async Task BringToFrontAsync(Process process)
    {
        // The main window handle only exists once RetroArch has created its window.
        try
        {
            for (var i = 0; i < 50 && !process.HasExited; i++)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    SwitchToThisWindow(process.MainWindowHandle, true);
                    return;
                }

                await Task.Delay(100);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Could not bring RetroArch to front", exception);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exited = (Process)sender!;
        int exitCode;
        try
        {
            exitCode = exited.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_process, exited))
                _process = null;
        }

        exited.Exited -= OnProcessExited;
        exited.Dispose();

        Exited?.Invoke(this, new RetroArchExitedEventArgs(exitCode));
    }

    private Process? GetRunningProcess() => _process is { HasExited: false } process ? process : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync(TimeSpan.FromSeconds(1));
    }
}

public sealed class RetroArchExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}
