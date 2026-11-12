using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetroGameCard.Models;

namespace RetroGameCard.Services;

/// <summary>A resolved, ready to run data package installation.</summary>
public sealed record DataInstallPlan(
    string PackageName,
    string Version,
    string Platform,
    string Source,
    bool IsRemote,
    string Destination)
{
    public bool DestinationExists => Directory.Exists(Destination);
}

/// <summary>Byte transfer progress (download or local read).</summary>
public readonly record struct TransferProgress(long BytesDone, long? TotalBytes, double BytesPerSecond)
{
    public double Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesDone / TotalBytes.Value, 0, 1) : 0;

    public TimeSpan? Remaining => TotalBytes is > 0 && BytesPerSecond > 1
        ? TimeSpan.FromSeconds((TotalBytes.Value - BytesDone) / BytesPerSecond)
        : null;
}

/// <summary>Archive extraction progress.</summary>
public readonly record struct ExtractProgress(int EntriesDone, int TotalEntries, string CurrentEntry)
{
    public double Fraction => TotalEntries > 0 ? (double)EntriesDone / TotalEntries : 0;
}

/// <summary>
/// Downloads and installs RetroArch data packages, independently of any UI.
/// </summary>
public static class DataInstaller
{
    public const string ManifestUrl =
        "https://raw.githubusercontent.com/retrogc/retrogc-data/refs/heads/main/retrogc-data.rgc";

    private const string DataPackageName = "retrogc-data";
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private static readonly HttpClient Http = CreateHttpClient();

    public static string? PlatformKey =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsLinux() ? "linux-x64"
        : null;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("retrogc-launcher");
        return client;
    }

    // ---------------------------------------------------------------- manifest

    public static async Task<DataInstallPlan> PrepareOnlineAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var json = await Http.GetStringAsync(ManifestUrl, timeout.Token);
        return CreatePlan(ParseManifest(json), manifestDirectory: null);
    }

    public static async Task<DataInstallPlan> PrepareLocalAsync(string manifestPath, CancellationToken ct)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The selected manifest does not exist.", manifestPath);

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        return CreatePlan(ParseManifest(json), Path.GetDirectoryName(Path.GetFullPath(manifestPath)));
    }

    private static DataManifest ParseManifest(string json) =>
        JsonSerializer.Deserialize(json, AppJsonContext.Default.DataManifest)
        ?? throw new InvalidDataException("The data manifest is empty.");

    private static DataInstallPlan CreatePlan(DataManifest manifest, string? manifestDirectory)
    {
        if (string.IsNullOrWhiteSpace(manifest.Default))
            throw new InvalidDataException("The data manifest has no default version.");

        var platform = PlatformKey
                       ?? throw new PlatformNotSupportedException("Only Windows and Linux x64 are supported.");

        var dataFile = manifest.GetDataFile(platform, DataPackageName)
                       ?? throw new InvalidDataException(
                           $"No '{DataPackageName}' package for {platform} in version {manifest.Default}.");

        string source;
        var isRemote = Uri.TryCreate(dataFile.Url, UriKind.Absolute, out var uri)
                       && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        if (isRemote)
        {
            source = dataFile.Url;
        }
        else
        {
            if (manifestDirectory is null)
                throw new InvalidDataException("The online manifest references a local file.");

            source = Path.GetFullPath(Path.Combine(manifestDirectory, dataFile.Url));
            if (!File.Exists(source))
                throw new FileNotFoundException($"Data archive not found: {source}", source);
        }

        // The version becomes a folder name: never let it point elsewhere.
        if (manifest.Default.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || manifest.Default.StartsWith('.'))
            throw new InvalidDataException($"Invalid data version '{manifest.Default}'.");

        return new DataInstallPlan(
            PackageName: string.IsNullOrWhiteSpace(dataFile.File) ? dataFile.Name : dataFile.File,
            Version: manifest.Default,
            Platform: platform,
            Source: source,
            IsRemote: isRemote,
            Destination: AppPaths.GetDataDirectory(manifest.Default));
    }

    // ---------------------------------------------------------------- download

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>. Progress is reported
    /// at most every 100 ms (plus once at the end), so the UI is never flooded.
    /// </summary>
    public static async Task DownloadAsync(
        string url, string destination, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await CopyWithProgressAsync(source, target, response.Content.Headers.ContentLength, progress, ct);
    }

    /// <summary>Reads a local archive once (verifies it is readable and gives byte progress).</summary>
    public static async Task VerifyLocalAsync(string path, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await CopyWithProgressAsync(source, Stream.Null, source.Length, progress, ct);
    }

    private static async Task CopyWithProgressAsync(
        Stream source, Stream target, long? total, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        long done = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;

            if (clock.Elapsed - lastReport < ReportInterval)
                continue;

            lastReport = clock.Elapsed;
            progress.Report(new TransferProgress(done, total, done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
        }

        progress.Report(new TransferProgress(done, total ?? done, done / Math.Max(clock.Elapsed.TotalSeconds, 0.001)));
    }

    // ---------------------------------------------------------------- extract

    /// <summary>
    /// Extracts <paramref name="zipPath"/> over <paramref name="destination"/>. Existing files are
    /// overwritten in place, so user content (saves, configs) not part of the archive is kept.
    /// </summary>
    public static Task ExtractAsync(
        string zipPath, string destination, IProgress<ExtractProgress> progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var root = Path.GetFullPath(destination);
            var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            Directory.CreateDirectory(root);

            await using var archive = await OpenArchiveAsync(zipPath, ct);
            var total = archive.Entries.Count;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();

                var entry = archive.Entries[i];
                var fullPath = Path.GetFullPath(Path.Combine(root, entry.FullName));

                // Zip-slip protection.
                if (!fullPath.StartsWith(rootWithSeparator, comparison) && fullPath != root)
                    throw new InvalidDataException($"Archive entry escapes the destination: {entry.FullName}");

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(fullPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await entry.ExtractToFileAsync(fullPath, overwrite: true, ct);
                }

                if (clock.Elapsed - lastReport >= ReportInterval || i == total - 1)
                {
                    lastReport = clock.Elapsed;
                    progress.Report(new ExtractProgress(i + 1, total, entry.FullName));
                }
            }
        }, ct);

    private static async Task<ZipArchive> OpenArchiveAsync(string zipPath, CancellationToken ct)
    {
        // A freshly written archive can briefly be locked by an antivirus scanner (Windows).
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ZipFile.OpenReadAsync(zipPath, ct);
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(250, ct);
            }
        }
    }

    /// <summary>Path of the temporary download file for a plan.</summary>
    public static string GetDownloadPath(DataInstallPlan plan) =>
        Path.Combine(AppPaths.DownloadDirectory, $"{DataPackageName}-{plan.Version}-{plan.Platform}.zip.part");

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            Log.Error($"Could not delete '{path}'", exception);
        }
    }
}
