using System;

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
}