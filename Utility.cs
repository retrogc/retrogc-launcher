using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

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
            return "/usr/bin/retroarch";
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

    public static async Task<(string StdOut, string StdErr, int ExitCode)> RunProcessAsync(
        string fileName, string arguments = "")
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.WriteLine("RunProcessAsync: OUT: " + e.Data);
            stdOutBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Console.WriteLine("RunProcessAsync: ERR: " + e.Data);
            stdErrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        return (stdOutBuilder.ToString(), stdErrBuilder.ToString(), process.ExitCode);
    }
}