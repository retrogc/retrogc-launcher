using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RetroGameCard.Services;

/// <summary>
/// Minimal console logger with timestamps, optionally also written to <see cref="FilePath"/>
/// (Settings → Log to file).
/// </summary>
public static class Log
{
    public static string FilePath { get; } = Path.Combine(AppPaths.SettingsDirectory, "launcher.log");
    private static string OldFilePath { get; } = Path.Combine(AppPaths.SettingsDirectory, "launcher.old.log");

    private static readonly Lock FileLock = new();
    private static StreamWriter? _file;
    private static bool _rotated;

    public static void Info(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
        WriteFile(line);
    }

    public static void Error(string message, Exception? exception = null)
    {
        var line = exception is null
            ? $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}"
            : $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}: {exception.Message}";
        Console.Error.WriteLine(line);
        WriteFile(line);
    }

    /// <summary>
    /// Turns the log file on or off. The first time it is turned on in a run, the previous log is kept
    /// as launcher.old.log. Never throws: a file that can't be written just isn't.
    /// </summary>
    public static void SetFileLogging(bool enabled)
    {
        lock (FileLock)
        {
            if (!enabled)
            {
                CloseFile();
                return;
            }

            if (_file is not null)
                return;

            try
            {
                Directory.CreateDirectory(AppPaths.SettingsDirectory);
                if (!_rotated)
                {
                    _rotated = true;
                    if (File.Exists(FilePath))
                        File.Move(FilePath, OldFilePath, overwrite: true);
                }

                // Shared, so the file can be read (or copied) while the launcher runs.
                var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _file = new StreamWriter(stream) { AutoFlush = true };
                _file.WriteLine($"----- {DateTime.Now:yyyy-MM-dd HH:mm:ss} log started -----");
            }
            catch (Exception exception)
            {
                CloseFile();
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: Could not open the log file '{FilePath}': {exception.Message}");
            }
        }
    }

    private static void WriteFile(string line)
    {
        lock (FileLock)
        {
            if (_file is null)
                return;

            try
            {
                _file.WriteLine(line);
            }
            catch (Exception exception)
            {
                // Disk full, file removed...: stop writing, keep logging to the console.
                CloseFile();
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: Log file disabled: {exception.Message}");
            }
        }
    }

    private static void CloseFile()
    {
        try
        {
            _file?.Dispose();
        }
        catch (Exception)
        {
            // best effort
        }

        _file = null;
    }

    /// <summary>Shows a desktop notification (Linux only, best effort).</summary>
    public static void Notify(string message)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                UseShellExecute = false,
                ArgumentList = { "--expire-time", "2000", "--app-name", "RetroGC", "RetroGC", message }
            });
        }
        catch (Exception exception)
        {
            // notify-send is optional, a missing binary must never break a game launch.
            Error("notify-send failed", exception);
        }
    }
}
