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
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

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

    private static readonly HttpClient SharedHttpClient = new();

    private RetrogcManifest? _manifest;
    private RetrogcDataFile? _dataFile;
    private string? _platformKey;
    private string? _versionDir;
    private string? _tempZipPath;
    private string? _localManifestBaseDir;
    private bool _isSourceRemote;
    private string? _resolvedSourcePathOrUrl;
    private bool _isRunning;
    private InstallStage _currentStage = InstallStage.ModeSelect;
    private bool _hasError;
    private bool _isPrepared;
    private bool _isCompleted;

    private static readonly IBrush IdleBrush = Brush.Parse("#5E6A7E");
    private static readonly IBrush ActiveBrush = Brush.Parse("#5CE1B7");
    private static readonly IBrush ErrorBrush = Brush.Parse("#F2777A");

    // Mode selection controls
    private TextBlock? _existingDataInfoTextBlock;
    private Border? _existingDataInfoBorder;
    private Border? _localBrowsePanel;
    private TextBlock? _selectedManifestPathTextBlock;
    private Button? _onlineModeButton;
    private Button? _localModeButton;

    private Panel? _modeSelectionRoot;
    private Panel? _installPanel;

    // Step indicator
    private Border? _stepSource;
    private Border? _stepPrepare;
    private Border? _stepInstall;
    private TextBlock? _stepSourceText;
    private TextBlock? _stepPrepareText;
    private TextBlock? _stepInstallText;
    private Rectangle? _stepLine1;
    private Rectangle? _stepLine2;

    // Install panel controls
    private TextBlock? _phaseTextBlock;
    private TextBlock? _statusTextBlock;
    private Ellipse? _phaseLight;
    private TextBlock? _downloadBytesTextBlock;
    private TextBlock? _downloadPercentTextBlock;
    private TextBlock? _extractFilesTextBlock;
    private TextBlock? _extractPercentTextBlock;
    private TextBlock? _downloadStepTitleTextBlock;
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
        _localBrowsePanel = this.FindControl<Border>("LocalBrowsePanel");
        _selectedManifestPathTextBlock = this.FindControl<TextBlock>("SelectedManifestPathTextBlock");
        _onlineModeButton = this.FindControl<Button>("OnlineModeButton");
        _localModeButton = this.FindControl<Button>("LocalModeButton");

        _modeSelectionRoot = this.FindControl<Panel>("ModeSelectionRoot");
        _installPanel = this.FindControl<Panel>("InstallPanel");

        _stepSource = this.FindControl<Border>("StepSource");
        _stepPrepare = this.FindControl<Border>("StepPrepare");
        _stepInstall = this.FindControl<Border>("StepInstall");
        _stepSourceText = this.FindControl<TextBlock>("StepSourceText");
        _stepPrepareText = this.FindControl<TextBlock>("StepPrepareText");
        _stepInstallText = this.FindControl<TextBlock>("StepInstallText");
        _stepLine1 = this.FindControl<Rectangle>("StepLine1");
        _stepLine2 = this.FindControl<Rectangle>("StepLine2");

        _phaseTextBlock = this.FindControl<TextBlock>("PhaseTextBlock");
        _statusTextBlock = this.FindControl<TextBlock>("StatusTextBlock");
        _phaseLight = this.FindControl<Ellipse>("PhaseLight");
        _downloadBytesTextBlock = this.FindControl<TextBlock>("DownloadBytesTextBlock");
        _downloadPercentTextBlock = this.FindControl<TextBlock>("DownloadPercentTextBlock");
        _extractFilesTextBlock = this.FindControl<TextBlock>("ExtractFilesTextBlock");
        _extractPercentTextBlock = this.FindControl<TextBlock>("ExtractPercentTextBlock");
        _downloadStepTitleTextBlock = this.FindControl<TextBlock>("DownloadStepTitleTextBlock");
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
        var versions = GetDataVersions();
        if (versions.Count == 0)
        {
            SetExistingDataInfo("No RetroGC data detected — data is required to continue.", isWarning: true);
        }
        else
        {
            SetExistingDataInfo($"RetroGC data v{versions[0]} is already installed.", isWarning: false);
        }
    }

    private void SetExistingDataInfo(string text, bool isWarning)
    {
        if (_existingDataInfoTextBlock == null || _existingDataInfoBorder == null) return;

        _existingDataInfoTextBlock.Text = text;
        _existingDataInfoBorder.Classes.Remove("ok");
        _existingDataInfoBorder.Classes.Remove("warning");
        _existingDataInfoBorder.Classes.Add(isWarning ? "warning" : "ok");
        _existingDataInfoBorder.IsVisible = true;
    }

    #endregion

    #region Step indicator

    private enum StepState
    {
        Pending,
        Active,
        Done,
        Error
    }

    private void SetStepClasses(Border? step, StepState state)
    {
        if (step == null) return;

        step.Classes.Remove("active");
        step.Classes.Remove("done");
        step.Classes.Remove("error");

        if (state == StepState.Active) step.Classes.Add("active");
        else if (state == StepState.Done) step.Classes.Add("done");
        else if (state == StepState.Error) step.Classes.Add("error");
    }

    /// <summary>
    /// Swaps the step badge between its number and a checkmark once the step is done.
    /// </summary>
    private static void SetStepBadge(TextBlock? text, string number, string label, bool done)
    {
        if (text == null) return;

        text.Text = done ? $"✓ {label}" : $"{number} · {label}";
    }

    private void SetStepLineClasses(Rectangle? line, bool done)
    {
        if (line == null) return;

        line.Classes.Remove("done");
        if (done) line.Classes.Add("done");
    }

    /// <summary>
    /// Reflects the current flow state in the header step indicator.
    /// </summary>
    private void UpdateStepIndicator(InstallStage stage)
    {
        StepState source, prepare, install;

        if (stage != InstallStage.Install)
        {
            source = StepState.Active;
            prepare = StepState.Pending;
            install = StepState.Pending;
        }
        else if (!_isPrepared)
        {
            // Still resolving the manifest: that is the "prepare" step.
            source = StepState.Done;
            prepare = _hasError ? StepState.Error : StepState.Active;
            install = StepState.Pending;
        }
        else
        {
            source = StepState.Done;
            prepare = StepState.Done;
            install = _hasError ? StepState.Error
                : _isCompleted ? StepState.Done
                : StepState.Active;
        }

        SetStepClasses(_stepSource, source);
        SetStepClasses(_stepPrepare, prepare);
        SetStepClasses(_stepInstall, install);

        SetStepBadge(_stepSourceText, "1", "SOURCE", source == StepState.Done);
        SetStepBadge(_stepPrepareText, "2", "PREPARE", prepare == StepState.Done);
        SetStepBadge(_stepInstallText, "3", "INSTALL", install == StepState.Done);

        SetStepLineClasses(_stepLine1, source == StepState.Done);
        SetStepLineClasses(_stepLine2, prepare == StepState.Done);
    }

    #endregion

    #region Stage / navigation

    private void GoToStage(InstallStage stage)
    {
        _currentStage = stage;
        if (stage != InstallStage.Install)
        {
            _hasError = false;
            _isPrepared = false;
            _isCompleted = false;
        }

        _modeSelectionRoot?.IsVisible = stage != InstallStage.Install;
        _localBrowsePanel?.IsVisible = stage == InstallStage.LocalBrowse;
        _installPanel?.IsVisible = stage == InstallStage.Install;
        _backButton?.IsVisible = stage != InstallStage.ModeSelect;
        _startButton?.IsVisible = stage == InstallStage.Install;

        // The local file row lives under the source cards, so both cards stay visible;
        // the picked one is highlighted to show which source is being configured.
        _onlineModeButton?.Classes.Remove("selected");
        _localModeButton?.Classes.Remove("selected");
        if (stage == InstallStage.LocalBrowse) _localModeButton?.Classes.Add("selected");

        UpdateStepIndicator(stage);
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

        _hasError = false;
        _isPrepared = false;
        _isCompleted = false;
        SetStartLabel("Start");
        SetPhase("Idle");
        SetStatus("Ready.");
        ResetProgressVisuals();

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

    /// <summary>
    /// Reports a failure that prevents the installation from starting.
    /// </summary>
    private void ReportInitializationError(string message)
    {
        _hasError = true;
        SetPhase("Error");
        SetStatus(message);
        SetStartEnabled(false);
        UpdateStepIndicator(_currentStage);
    }

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
            ReportInitializationError($"Initialization failed: {ex.Message}");
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
                ReportInitializationError("Selected manifest file does not exist.");
                return;
            }

            _localManifestBaseDir = Path.GetDirectoryName(Path.GetFullPath(manifestFilePath));

            var json = await File.ReadAllTextAsync(manifestFilePath, ct);
            _manifest = ParseManifest(json);

            await FinishInitializationAsync(ct);
        }
        catch (Exception ex)
        {
            ReportInitializationError($"Initialization failed: {ex.Message}");
        }
    }

    private static RetrogcManifest ParseManifest(string json)
    {
        return JsonSerializer.Deserialize(json, AppJsonContext.Default.RetrogcManifest)
               ?? throw new InvalidOperationException("Failed to parse manifest.");
    }

    private async Task FinishInitializationAsync(CancellationToken ct)
    {
        if (_manifest == null) return;

        _platformKey = GetPlatformKey();
        if (_platformKey == null)
        {
            ReportInitializationError("Unsupported OS (only Windows and Linux x64 are handled).");
            return;
        }

        _dataFile = _manifest.GetDataFile(_platformKey, "retrogc-data");
        if (_dataFile == null)
        {
            ReportInitializationError($"No data file named 'retrogc-data' for platform '{_platformKey}'.");
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
                ReportInitializationError(
                    "Manifest references a relative local file, but no local manifest folder is known.");
                return;
            }

            _resolvedSourcePathOrUrl = Path.GetFullPath(Path.Combine(_localManifestBaseDir, _dataFile.Url));

            if (!File.Exists(_resolvedSourcePathOrUrl))
            {
                ReportInitializationError($"Local data file not found: {_resolvedSourcePathOrUrl}");
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

        // Preparation succeeded: the source is resolved and the install can start.
        _isPrepared = true;
        _hasError = false;
        _isCompleted = false;
        UpdateStepIndicator(_currentStage);

        UpdateMetadataUi();
        SetStartEnabled(true);
    }

    private static bool IsRemoteUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? GetPlatformKey()
    {
        if (OperatingSystem.IsWindows()) return "win-x64";
        return OperatingSystem.IsLinux() ? "linux-x64" : null;
    }

    #endregion

    private void UpdateMetadataUi()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_manifest == null || _dataFile == null || _platformKey == null) return;

            _downloadFileInfoTextBlock?.Text = $"{_dataFile.Name} {_manifest.Default} ({_platformKey})";

            // Show the actual origin the user picked, not the internal temp file.
            _downloadLocationTextBlock?.Text = _isSourceRemote
                ? _dataFile.Url
                : _resolvedSourcePathOrUrl ?? "(pending)";

            if (_downloadStepTitleTextBlock != null)
                _downloadStepTitleTextBlock.Text = _isSourceRemote ? "DOWNLOAD" : "READ";

            if (_extractionLocationTextBlock != null && _versionDir != null)
                _extractionLocationTextBlock.Text = _versionDir;

            if (_versionDir == null || _destinationStatusBorder == null || _destinationStatusTextBlock == null) return;

            var exists = Directory.Exists(_versionDir);

            _destinationStatusBorder.IsVisible = true;
            _destinationStatusBorder.Classes.Remove("ok");
            _destinationStatusBorder.Classes.Remove("warning");
            _destinationStatusBorder.Classes.Add(exists ? "warning" : "ok");
            _destinationStatusTextBlock.Text = exists
                ? "Destination already exists — existing data will be overwritten."
                : "Destination does not exist — fresh install.";

            SetStartLabel(exists ? "Overwrite" : "Start");
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
        _hasError = false;
        _isCompleted = false;
        SetStartEnabled(false);
        _backButton?.IsEnabled = false;
        ResetProgressVisuals();
        UpdateStepIndicator(_currentStage);

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

            _isRunning = false;
            _isCompleted = true;
            _hasError = false;
            UpdateStepIndicator(_currentStage);

            // Only auto-close once the data is really installed; failures stay on screen.
            Hide();
        }
        catch (Exception ex)
        {
            _isRunning = false;
            _hasError = true;
            _isCompleted = false;
            SetPhase("Error");
            SetStatus($"Installation failed: {ex.Message}");
            SetStartEnabled(true);
            SetStartLabel("Retry");
            _backButton?.IsEnabled = true;
            UpdateStepIndicator(_currentStage);
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
            var attempt = 0;
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
            _downloadProgressBar?.Value = 0;
            _extractProgressBar?.Value = 0;
            _downloadBytesTextBlock?.Text = "0 B";
            _extractFilesTextBlock?.Text = "Extracted 0 files";
            _downloadPercentTextBlock?.Text = "0%";
            _extractPercentTextBlock?.Text = "0%";
        });
    }

    private void SetPhase(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            _phaseTextBlock?.Text = text;

            if (_phaseLight == null) return;

            _phaseLight.Fill = text switch
            {
                "Error" => ErrorBrush,
                "Idle" => IdleBrush,
                _ => ActiveBrush
            };
        });

    private void SetStatus(string text) =>
        Dispatcher.UIThread.Post(() => { _statusTextBlock?.Text = text; });

    private void SetStartEnabled(bool enabled) =>
        Dispatcher.UIThread.Post(() => { _startButton?.IsEnabled = enabled; });

    private void SetStartLabel(string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_startButton != null) _startButton.Content = text;
        });

    private void UpdateDownloadProgress(long bytesRead, long totalBytes, double percentage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _downloadProgressBar?.Value = percentage;
            _downloadPercentTextBlock?.Text = $"{percentage:0}%";

            if (_downloadBytesTextBlock == null) return;

            var label = _isSourceRemote ? "Downloaded" : "Read";
            _downloadBytesTextBlock.Text = totalBytes > 0
                ? $"{label} {FormatBytes(bytesRead)} / {FormatBytes(totalBytes)}"
                : $"{label} {FormatBytes(bytesRead)}";
        });
    }

    private void UpdateExtractProgress(int filesExtracted, int totalFiles, double percentage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _extractProgressBar?.Value = percentage;
            _extractPercentTextBlock?.Text = $"{percentage:0}%";

            _extractFilesTextBlock?.Text = totalFiles > 0
                ? $"Extracted {filesExtracted} / {totalFiles} files"
                : $"Extracted {filesExtracted} files";
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

    #region Utility

    public static List<string?> GetDataVersions()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dataRoot = Path.Combine(localAppData, "retrogc", "data");

            if (!Directory.Exists(dataRoot))
            {
                return [];
            }

            return Directory.GetDirectories(dataRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderByDescending(name => name)
                .ToList();
        }
        catch
        {
            return [];
        }
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

public sealed class RetrogcManifest
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

public sealed class RetrogcVersion
{
    public string Version { get; set; } = string.Empty;
    public Dictionary<string, RetrogcPlatform> Platforms { get; set; } = [];
}

public sealed class RetrogcPlatform
{
    public List<RetrogcDataFile> Data { get; set; } = [];
}

public sealed class RetrogcDataFile
{
    public string Name { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

#endregion