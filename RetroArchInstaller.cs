using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using RetroGameCard.Controls;

namespace RetroGameCard;

public class DownloadProgressInfo
{
    public double Percentage { get; set; }
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedMbPerSec { get; set; }
}

public static class RetroArchInstaller
{
    public const string DownloadUrl =
        "https://buildbot.libretro.com/stable/1.22.2/windows/x86_64/RetroArch-Win64-setup.exe";

    public static async Task<string?> GetVersion()
    {
        var exePath = Utility.GetRetroArchExecutable();
        if (!File.Exists(exePath))
            return null;

        var (output, error, exitCode) = await Utility.RunProcessAsync(exePath, "-V");
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = Regex.Match(output, @"^Version:\s*(.+)$", RegexOptions.Multiline);
        if (!match.Success)
            return null;

        var version = match.Groups[1].Value.Trim();
        Console.WriteLine($"RetroArch executable found at {exePath}, version: {version}");

        return version;
    }

    /// <summary>
    /// Checks if RetroArch exists. If not, prompts the user and installs it.
    /// Returns true if RetroArch is available (already present or freshly installed).
    /// </summary>
    public static async Task<bool> InstallAsync(Window? owner)
    {
        var exePath = Utility.GetRetroArchExecutable();
        if (File.Exists(exePath)) return true;

        var confirmed = await ConfirmDialog.ShowAsync(
            owner,
            "RetroArch Not Found",
            $"RetroArch was not found at:\n{exePath}\n\nDo you want to download and install it now?");

        if (!confirmed)
            return false;

        var progressWindow = new DownloadProgressWindow();
        var cts = new CancellationTokenSource();
        progressWindow.CancelRequested += () => cts.Cancel();

        var showTask = progressWindow.ShowDialog(owner);

        var installerPath = Path.Combine(Path.GetTempPath(), "RetroArch-Win64-setup.exe");
        var success = false;

        try
        {
            await DownloadFileAsync(DownloadUrl, installerPath, progressWindow, cts.Token);
            progressWindow.SetStatus("Launching installer...");
            success = await RunInstallerAsync(installerPath);
        }
        catch (OperationCanceledException)
        {
            progressWindow.SetStatus("Download cancelled.");
        }
        catch (Exception ex)
        {
            progressWindow.SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            await Task.Delay(800); // let the user see the final status
            progressWindow.Close();
            await showTask;

            if (File.Exists(installerPath))
            {
                try
                {
                    File.Delete(installerPath);
                }
                catch
                {
                    /* ignore cleanup errors */
                }
            }
        }

        return success && File.Exists(exePath);
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        DownloadProgressWindow progressWindow,
        CancellationToken token)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

        await using var httpStream = await response.Content.ReadAsStreamAsync(token);
        await using var fileStream = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        var sw = Stopwatch.StartNew();

        while ((bytesRead = await httpStream.ReadAsync(buffer, token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
            totalRead += bytesRead;

            var info = new DownloadProgressInfo
            {
                BytesReceived = totalRead,
                TotalBytes = totalBytes,
                Percentage = totalBytes > 0 ? (double)totalRead / totalBytes * 100.0 : 0,
                SpeedMbPerSec = sw.Elapsed.TotalSeconds > 0
                    ? totalRead / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds
                    : 0
            };

            await Dispatcher.UIThread.InvokeAsync(() => progressWindow.UpdateProgress(info));
        }
    }

    private static Task<bool> RunInstallerAsync(string installerPath)
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                // NSIS silent install; remove "/S" to see the installer UI
                //Arguments = "/S",
                UseShellExecute = true,
                Verb = "runas" // request elevation, RetroArch installs to Program Files-like path
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();
            return process is { ExitCode: 0 };
        });
    }
}