using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace RetroGameCard.Controls;

public enum InstallStage
{
    ModeSelect,
    LocalBrowse,
    Install
}

public partial class DataDownloadWindow : Window
{
    private const string ManifestUrl =
        "https://raw.githubusercontent.com/retrogc/retrogc-data/refs/heads/main/retrogc-data.rgc";

    private static readonly HttpClient SharedHttpClient = new HttpClient();

    private RetrogcManifest? _manifest;
    private RetrogcDataFile? _dataFile;
    private string? _platformKey;
    private string? _versionDir;
    private string? _tempZipPath;
    private string? _localManifestBaseDir;
    private bool _isSourceRemote;
    private string? _resolvedSourcePathOrUrl;
    private bool _isRunning;

    // Mode selection controls
    private TextBlock? _existingDataInfoTextBlock;
    private Border? _existingDataInfoBorder;
    private StackPanel? _modeButtonsPanel;
    private StackPanel? _localBrowsePanel;
    private TextBlock? _selectedManifestPathTextBlock;

    private Panel? _modeSelectionRoot;
    private Grid? _installPanel;

    // Install panel controls
    private TextBlock? _phaseTextBlock;
    private TextBlock? _statusTextBlock;
    private TextBlock? _downloadBytesTextBlock;
    private TextBlock? _extractFilesTextBlock;
    private ProgressBar? _downloadProgressBar;
    private ProgressBar? _extractProgressBar;
    private TextBlock? _downloadFileInfoTextBlock;
    private TextBlock? _downloadLocationTextBlock;
    private TextBlock? _extractionLocationTextBlock;
    private Border? _destinationStatusBorder;
    private TextBlock? _destinationStatusTextBlock;

    // Footer
    private Button? _backButton;
    private Button? _startButton;

    public DataDownloadWindow()
    {
        InitializeComponent();
        CacheControls();

        Opened += (_, _) => RefreshExistingDataInfo();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void CacheControls()
    {
        _existingDataInfoBorder = this.FindControl<Border>("ExistingDataInfoBorder");
        _existingDataInfoTextBlock = this.FindControl<TextBlock>("ExistingDataInfoTextBlock");
        _modeButtonsPanel = this.FindControl<StackPanel>("ModeButtonsPanel");
        _localBrowsePanel = this.FindControl<StackPanel>("LocalBrowsePanel");
        _selectedManifestPathTextBlock = this.FindControl<TextBlock>("SelectedManifestPathTextBlock");

        _modeSelectionRoot = this.FindControl<Panel>("ModeSelectionRoot");
        _installPanel = this.FindControl<Grid>("InstallPanel");

        _phaseTextBlock = this.FindControl<TextBlock>("PhaseTextBlock");
        _statusTextBlock = this.FindControl<TextBlock>("StatusTextBlock");
        _downloadBytesTextBlock = this.FindControl<TextBlock>("DownloadBytesTextBlock");
        _extractFilesTextBlock = this.FindControl<TextBlock>("ExtractFilesTextBlock");
        _downloadProgressBar = this.FindControl<ProgressBar>("DownloadProgressBar");
        _extractProgressBar = this.FindControl<ProgressBar>("ExtractProgressBar");
        _downloadFileInfoTextBlock = this.FindControl<TextBlock>("DownloadFileInfoTextBlock");
        _downloadLocationTextBlock = this.FindControl<TextBlock>("DownloadLocationTextBlock");
        _extractionLocationTextBlock = this.FindControl<TextBlock>("ExtractionLocationTextBlock");
        _destinationStatusBorder = this.FindControl<Border>("DestinationStatusBorder");
        _destinationStatusTextBlock = this.FindControl<TextBlock>("DestinationStatusTextBlock");

        _backButton = this.FindControl<Button>("BackButton");
        _startButton = this.FindControl<Button>("StartButton");
    }

    #region Pre-check (existing installed data)

    private void RefreshExistingDataInfo()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dataRoot = Path.Combine(localAppData, "retrogc", "data");

            if (!Directory.Exists(dataRoot))
            {
                SetExistingDataInfo("No RetroGC data detected — data is required to continue.", isWarning: true);
                return;
            }

            var versionDirs = Directory.GetDirectories(dataRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderByDescending(name => name)
                .ToList();

            if (versionDirs.Count == 0)
            {
                SetExistingDataInfo("No RetroGC data detected — data is required to continue.", isWarning: true);
            }
            else
            {
                SetExistingDataInfo($"RetroGC data v{versionDirs[0]} is already installed.", isWarning: false);
            }
        }
        catch
        {
            SetExistingDataInfo("Could not determine current data status.", isWarning: true);
        }
    }

    private void SetExistingDataInfo(string text, bool isWarning)
    {
        if (_existingDataInfoTextBlock == null || _existingDataInfoBorder == null) return;

        _existingDataInfoTextBlock.Text = text;
        _existingDataInfoBorder.Classes.Remove("status-chip");
        _existingDataInfoBorder.Classes.Remove("warning-chip");
        _existingDataInfoBorder.Classes.Add(isWarning ? "warning-chip" : "status-chip");
        _existingDataInfoBorder.IsVisible = true;
    }

    #endregion

    #region Stage / navigation

    private void GoToStage(InstallStage stage)
    {
        _modeSelectionRoot?.IsVisible = stage != InstallStage.Install;
        _modeButtonsPanel?.IsVisible = stage == InstallStage.ModeSelect;
        _localBrowsePanel?.IsVisible = stage == InstallStage.LocalBrowse;
        _installPanel?.IsVisible = stage == InstallStage.Install;
        _backButton?.IsVisible = stage != InstallStage.ModeSelect;
        _startButton?.IsVisible = stage == InstallStage.Install;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        // Reset any in-progress state and return to the initial choice screen.
        _manifest = null;
        _dataFile = null;
        _platformKey = null;
        _versionDir = null;
        _tempZipPath = null;
        _localManifestBaseDir = null;
        _resolvedSourcePathOrUrl = null;

        if (_selectedManifestPathTextBlock != null)
            _selectedManifestPathTextBlock.Text = "No file selected";

        RefreshExistingDataInfo();
        GoToStage(InstallStage.ModeSelect);
    }

    #endregion

    #region Mode selection

    private async void OnOnlineModeClick(object? sender, RoutedEventArgs e)
    {
        GoToStage(InstallStage.Install);
        await InitializeOnlineAsync();
    }

    private void OnLocalModeClick(object? sender, RoutedEventArgs e)
    {
        GoToStage(InstallStage.LocalBrowse);
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            SetStatus("File picker is not available on this platform.");
            return;
        }

        var rgcFileType = new FilePickerFileType("RetroGC manifest (*.rgc)")
        {
            Patterns = new[] { "*.rgc" }
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select retrogc-data.rgc",
            AllowMultiple = false,
            FileTypeFilter = new[] { rgcFileType, FilePickerFileTypes.All }
        });

        if (files.Count == 0) return;

        var file = files[0];
        var localPath = file.TryGetLocalPath();

        if (string.IsNullOrEmpty(localPath))
        {
            SetStatus("Could not resolve a local path for the selected file on this platform.");
            return;
        }

        if (_selectedManifestPathTextBlock != null)
            _selectedManifestPathTextBlock.Text = localPath;

        GoToStage(InstallStage.Install);
        await InitializeLocalAsync(localPath);
    }

    #endregion

    #region Initialization (online / local)

    private async Task InitializeOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            SetPhase("Initializing");
            SetStatus("Loading manifest from GitHub…");

            _localManifestBaseDir = null;

            using var response = await SharedHttpClient.GetAsync(ManifestUrl, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            _manifest = ParseManifest(json);

            await FinishInitializationAsync(ct);
        }
        catch (Exception ex)
        {
            SetPhase("Error");
            SetStatus($"Initialization failed: {ex.Message}");
            SetStartEnabled(false);
        }
    }

    private async Task InitializeLocalAsync(string manifestFilePath, CancellationToken ct = default)
    {
        try
        {
            SetPhase("Initializing");
            SetStatus($"Loading manifest from {manifestFilePath}…");

            if (!File.Exists(manifestFilePath))
            {
                SetPhase("Error");
                SetStatus("Selected manifest file does not exist.");
                SetStartEnabled(false);
                return;
            }

            _localManifestBaseDir = Path.GetDirectoryName(Path.GetFullPath(manifestFilePath));

            var json = await File.ReadAllTextAsync(manifestFilePath, ct);
            _manifest = ParseManifest(json);

            await FinishInitializationAsync(ct);
        }
        catch (Exception ex)
        {
            SetPhase("Error");
            SetStatus($"Initialization failed: {ex.Message}");
            SetStartEnabled(false);
        }
    }

    private static RetrogcManifest ParseManifest(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<RetrogcManifest>(json, options)
               ?? throw new InvalidOperationException("Failed to parse manifest.");
    }

    private async Task FinishInitializationAsync(CancellationToken ct)
    {
        if (_manifest == null) return;

        _platformKey = GetPlatformKey();
        if (_platformKey == null)
        {
            SetPhase("Error");
            SetStatus("Unsupported OS (only Windows and Linux x64 are handled).");
            SetStartEnabled(false);
            return;
        }

        _dataFile = _manifest.GetDataFile(_platformKey, "retrogc-data");
        if (_dataFile == null)
        {
            SetPhase("Error");
            SetStatus($"No data file named 'retrogc-data' for platform '{_platformKey}'.");
            SetStartEnabled(false);
            return;
        }

        _isSourceRemote = IsRemoteUrl(_dataFile.Url);

        if (_isSourceRemote)
        {
            _resolvedSourcePathOrUrl = _dataFile.Url;
        }
        else
        {
            if (string.IsNullOrEmpty(_localManifestBaseDir))
            {
                SetPhase("Error");
                SetStatus("Manifest references a relative local file, but no local manifest folder is known.");
                SetStartEnabled(false);
                return;
            }

            _resolvedSourcePathOrUrl = Path.GetFullPath(Path.Combine(_localManifestBaseDir, _dataFile.Url));

            if (!File.Exists(_resolvedSourcePathOrUrl))
            {
                SetPhase("Error");
                SetStatus($"Local data file not found: {_resolvedSourcePathOrUrl}");
                SetStartEnabled(false);
                return;
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _versionDir = Path.Combine(localAppData, "retrogc", "data", _manifest.Default);

        _tempZipPath = _isSourceRemote
            ? Path.Combine(Path.GetTempPath(),
                $"{_dataFile.Name}-{_manifest.Default}-{_platformKey}-{Guid.NewGuid():N}.zip")
            : _resolvedSourcePathOrUrl!;

        SetPhase("Ready");
        SetStatus(_isSourceRemote ? "Ready to download and install." : "Ready to install from local file.");

        UpdateMetadataUi();
        SetStartEnabled(true);
    }

    private static bool IsRemoteUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? GetPlatformKey()
    {
        if (OperatingSystem.IsWindows()) return "win-x64";
        if (OperatingSystem.IsLinux()) return "linux-x64";
        return null;
    }

    #endregion

    private void UpdateMetadataUi()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_manifest == null || _dataFile == null || _platformKey == null) return;

            if (_downloadFileInfoTextBlock != null)
                _downloadFileInfoTextBlock.Text = $"{_dataFile.Name} {_manifest.Default} ({_platformKey})";

            if (_downloadLocationTextBlock != null)
            {
                _downloadLocationTextBlock.Text = _isSourceRemote
                    ? _tempZipPath ?? "(pending)"
                    : $"Local file (no download needed): {_resolvedSourcePathOrUrl}";
            }

            if (_extractionLocationTextBlock != null && _versionDir != null)
                _extractionLocationTextBlock.Text = _versionDir;

            if (_versionDir != null && _destinationStatusBorder != null && _destinationStatusTextBlock != null)
            {
                var exists = Directory.Exists(_versionDir);

                _destinationStatusBorder.IsVisible = true;
                _destinationStatusTextBlock.Text = exists
                    ? "Destination already exists — existing data will be overwritten."
                    : "Destination does not exist — fresh install.";

                if (_startButton != null)
                    _startButton.Content = exists ? "Overwrite" : "Start";
            }
        });
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e) => await RunAsync();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(CancellationToken ct = default)
    {
        if (_isRunning) return;
        if (_manifest == null || _dataFile == null || _versionDir == null || _resolvedSourcePathOrUrl == null)
            return;

        _isRunning = true;
        SetStartEnabled(false);
        if (_backButton != null) _backButton.IsEnabled = false;
        ResetProgressVisuals();

        try
        {
            if (_isSourceRemote)
            {
                SetPhase("Downloading data");
                SetStatus($"Downloading {_dataFile.File}…");

                await DownloadFileWithProgressAsync(
                    _resolvedSourcePathOrUrl,
                    _tempZipPath!,
                    progress => UpdateDownloadProgress(progress.BytesRead, progress.TotalBytes,
                        progress.Percentage),
                    ct);
            }
            else
            {
                SetPhase("Preparing local data");
                SetStatus($"Reading local file {_dataFile.File}…");

                await ReadLocalFileWithProgressAsync(
                    _resolvedSourcePathOrUrl,
                    progress => UpdateDownloadProgress(progress.BytesRead, progress.TotalBytes,
                        progress.Percentage),
                    ct);
            }

            SetPhase("Extracting data");
            SetStatus($"Extracting to {_versionDir}…");

            await ExtractZipWithProgressAsync(
                _isSourceRemote ? _tempZipPath! : _resolvedSourcePathOrUrl,
                _versionDir,
                progress => UpdateExtractProgress(progress.FilesExtracted, progress.TotalFiles,
                    progress.Percentage),
                ct);

            if (_isSourceRemote)
            {
                try
                {
                    if (File.Exists(_tempZipPath)) File.Delete(_tempZipPath);
                }
                catch
                {
                    /* ignore cleanup errors */
                }
            }

            SetPhase("Completed");
            SetStatus($"Data installed to: {_versionDir}");
        }
        catch (Exception ex)
        {
            SetPhase("Error");
            SetStatus($"Installation failed: {ex.Message}");
        }
        finally
        {
            _isRunning = false;
            SetStartEnabled(true);
            if (_backButton != null) _backButton.IsEnabled = true;
        }
    }

    #region Download / read / extract helpers

    private static async Task DownloadFileWithProgressAsync(
        string url, string destinationPath, Action<DownloadProgressInfo> report, CancellationToken ct)
    {
        using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 8192, useAsync: true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;
            var percentage = totalBytes > 0 ? (double)totalRead / totalBytes * 100.0 : 0.0;
            report(new DownloadProgressInfo
                { BytesRead = totalRead, TotalBytes = totalBytes, Percentage = percentage });
        }
    }

    private static async Task ReadLocalFileWithProgressAsync(
        string sourcePath, Action<DownloadProgressInfo> report, CancellationToken ct)
    {
        var info = new FileInfo(sourcePath);
        var totalBytes = info.Length;

        await using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            totalRead += read;
            var percentage = totalBytes > 0 ? (double)totalRead / totalBytes * 100.0 : 0.0;
            report(new DownloadProgressInfo
                { BytesRead = totalRead, TotalBytes = totalBytes, Percentage = percentage });
        }
    }

    private static async Task ExtractZipWithProgressAsync(
        string zipPath, string destinationDirectory, Action<ExtractProgressInfo> report, CancellationToken ct)
    {
        const int maxAttempts = 10;
        const int delayMs = 250;

        await Task.Run(async () =>
        {
            int attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

                    var totalFiles = archive.Entries.Count;
                    var extracted = 0;

                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();

                        var fullPath = Path.Combine(destinationDirectory, entry.FullName);
                        var dir = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                        if (!string.IsNullOrEmpty(entry.Name))
                            await entry.ExtractToFileAsync(fullPath, overwrite: true, cancellationToken: ct);

                        extracted++;
                        var percentage = totalFiles > 0 ? (double)extracted / totalFiles * 100.0 : 0.0;

                        report(new ExtractProgressInfo
                            { FilesExtracted = extracted, TotalFiles = totalFiles, Percentage = percentage });
                    }

                    break;
                }
                catch (IOException ioEx) when (ioEx.Message.Contains("being used by another process",
                                                   StringComparison.OrdinalIgnoreCase))
                {
                    attempt++;
                    if (attempt >= maxAttempts) throw;
                    await Task.Delay(delayMs, ct);
                }
            }
        }, ct);
    }

    #endregion

    #region UI helpers

    private void ResetProgressVisuals()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_downloadProgressBar != null) _downloadProgressBar.Value = 0;
            if (_extractProgressBar != null) _extractProgressBar.Value = 0;
            if (_downloadBytesTextBlock != null) _downloadBytesTextBlock.Text = "0 B";
            if (_extractFilesTextBlock != null) _extractFilesTextBlock.Text = "Extracted 0 files";
        });
    }

    private void SetPhase(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_phaseTextBlock != null) _phaseTextBlock.Text = text;
        });

    private void SetStatus(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_statusTextBlock != null) _statusTextBlock.Text = text;
        });

    private void SetStartEnabled(bool enabled) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_startButton != null) _startButton.IsEnabled = enabled;
        });

    private void UpdateDownloadProgress(long bytesRead, long totalBytes, double percentage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_downloadProgressBar != null) _downloadProgressBar.Value = percentage;

            if (_downloadBytesTextBlock != null)
            {
                var label = _isSourceRemote ? "Downloaded" : "Read";
                _downloadBytesTextBlock.Text = totalBytes > 0
                    ? $"{label} {FormatBytes(bytesRead)} / {FormatBytes(totalBytes)}"
                    : $"{label} {FormatBytes(bytesRead)}";
            }
        });
    }

    private void UpdateExtractProgress(int filesExtracted, int totalFiles, double percentage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_extractProgressBar != null) _extractProgressBar.Value = percentage;

            if (_extractFilesTextBlock != null)
            {
                _extractFilesTextBlock.Text = totalFiles > 0
                    ? $"Extracted {filesExtracted} / {totalFiles} files"
                    : $"Extracted {filesExtracted} files";
            }
        });
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes switch
        {
            >= gb => $"{bytes / (double)gb:0.00} GB",
            >= mb => $"{bytes / (double)mb:0.00} MB",
            >= kb => $"{bytes / (double)kb:0.00} KB",
            _ => $"{bytes} B"
        };
    }

    #endregion
}

#region DTOs

public class DownloadProgressInfo
{
    public long BytesRead { get; init; }
    public long TotalBytes { get; init; }
    public double Percentage { get; init; }
}

public class ExtractProgressInfo
{
    public int FilesExtracted { get; init; }
    public int TotalFiles { get; init; }
    public double Percentage { get; init; }
}

public class RetrogcManifest
{
    public string Default { get; init; } = string.Empty;
    public List<RetrogcVersion> Versions { get; init; } = [];

    public RetrogcDataFile? GetDataFile(string platformKey, string name)
    {
        var v = Versions.Find(x => string.Equals(x.Version, Default, StringComparison.OrdinalIgnoreCase));
        if (v == null) return null;

        if (!v.Platforms.TryGetValue(platformKey, out var platform))
            return null;

        return platform.Data.Find(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

public abstract class RetrogcVersion
{
    public string Version { get; set; } = string.Empty;
    public Dictionary<string, RetrogcPlatform> Platforms { get; set; } = new();
}

public abstract class RetrogcPlatform
{
    public List<RetrogcDataFile> Data { get; set; } = new();
}

public abstract class RetrogcDataFile
{
    public string Name { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

#endregion