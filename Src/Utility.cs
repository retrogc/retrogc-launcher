using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard;

public abstract class Utility
{
    public static void Notify(string message)
    {
        if (!OperatingSystem.IsLinux()) return;

        var psi = new ProcessStartInfo
        {
            FileName = "notify-send",
            UseShellExecute = false,
            ArgumentList =
            {
                "--expire-time", "2000",
                "RetroGC",
                message
            }
        };

        Process.Start(psi);
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
            //Console.WriteLine("RunProcessAsync: OUT: " + e.Data);
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

    public static async Task CopyFileAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        const int bufferSize = 1024 * 1024; // 1 MiB

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await source.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    public static async Task<bool> ExtractZipAsync(
        string sourcePath,
        string destinationPath,
        bool useStaging = true,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            //throw new FileNotFoundException("ZIP archive not found.", sourcePath);
            Console.WriteLine($"ZIP archive not found: {sourcePath}");
            return false;
        }

        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);

        var parent = Path.GetDirectoryName(destinationPath);
        if (parent is null)
        {
            //throw new InvalidOperationException("The destination must have a parent directory.");
            Console.WriteLine($"The destination must have a parent directory: {destinationPath}");
            return false;
        }

        Directory.CreateDirectory(parent);

        var stagingPath = useStaging
            ? Path.Combine(parent, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp")
            : destinationPath;

        try
        {
            await ZipFile.ExtractToDirectoryAsync(
                sourcePath,
                stagingPath,
                overwriteFiles: overwrite,
                cancellationToken);

            if (useStaging)
            {
                if (Directory.Exists(destinationPath))
                    Directory.Delete(destinationPath, recursive: true);

                Directory.Move(stagingPath, destinationPath);
            }

            return true;
        }
        catch
        {
            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);
            //throw;
        }

        return false;
    }

    public static async Task<string?> GetRetroArchVersion(string path)
    {
        if (!File.Exists(path)) return null;

        var (output, error, exitCode) = await RunProcessAsync(path, "-V");
        if (string.IsNullOrWhiteSpace(output)) return null;

        var match = Regex.Match(output, @"^Version:\s*(.+)$", RegexOptions.Multiline);
        if (!match.Success) return null;

        var version = match.Groups[1].Value.Trim();
        Console.WriteLine($"GetRetroArchVersion: executable found at {path}, version: {version}");

        return version;
    }
}