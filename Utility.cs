using System;
using System.Diagnostics;

namespace RetroGameCard;

public abstract class Utility
{
    public static string GetRetroArchExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return @"C:\Program Files\RetroArch\retroarch.exe";
        }

        if (OperatingSystem.IsLinux())
        {
            // Works when `retroarch` is in PATH.
            return "retroarch";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "/Applications/RetroArch.app/Contents/MacOS/RetroArch";
        }

        throw new PlatformNotSupportedException();
    }

    public static void NotifyLinux(string message)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "notify-send",
            UseShellExecute = false
        };

        psi.ArgumentList.Add("--expire-time=3000");
        psi.ArgumentList.Add("RetroGameCard");
        psi.ArgumentList.Add(message);

        Process.Start(psi);
    }
}