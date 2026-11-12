using System;
using System.Diagnostics;

namespace RetroGameCard.Services;

/// <summary>
/// Minimal console logger with timestamps.
/// </summary>
public static class Log
{
    public static void Info(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    public static void Error(string message, Exception? exception = null) =>
        Console.Error.WriteLine(exception is null
            ? $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}"
            : $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}: {exception.Message}");

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
