using System;
using System.Diagnostics;

namespace RetroGameCard;

public abstract class Utility
{
    public static string GetRetroArchExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return @"C:\RetroArch-Win64\retroarch.exe";
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

    public static void Notify(string message)
    {
        if (OperatingSystem.IsLinux())
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
}