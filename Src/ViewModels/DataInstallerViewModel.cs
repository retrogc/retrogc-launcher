using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroGameCard.Services;

namespace RetroGameCard.ViewModels;

public enum InstallerStage
{
    Source,
    Preparing,
    Review,
    Installing,
    Done
}

/// <summary>One entry of the installer's step rail.</summary>
public sealed partial class InstallerStep(int number, string title, string caption) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Caption { get; } = caption;
    public bool IsLast { get; init; }

    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial bool IsError { get; set; }
}

/// <summary>
/// Data package installer wizard: choose source → review → install → done.
/// </summary>
public sealed partial class DataInstallerViewModel : ObservableObject
{
    // Weight of the transfer phase in the overall progress bar (the rest is extraction).
    private const double TransferWeight = 0.65;

    private CancellationTokenSource? _cts;
    private DataInstallPlan? _plan;

    public DataInstallerViewModel()
    {
        Steps =
        [
            new InstallerStep(1, "Source", "Online or local file"),
            new InstallerStep(2, "Review", "Check what gets installed"),
            new InstallerStep(3, "Install", "Download and extract"),
            new InstallerStep(4, "Done", "Ready to play") { IsLast = true }
        ];

        var installed = AppPaths.GetLatestDataVersion();
        HasInstalledData = installed is not null;
        InstalledText = installed is null
            ? "No RetroArch data installed yet. It is required to launch games."
            : $"RetroArch data {installed} is installed. Installing again repairs or updates it.";

        UpdateSteps();
    }

    /// <summary>Raised when the window should close; the argument tells whether data was installed.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Set by the view: opens a file picker and returns the selected manifest path.</summary>
    public Func<Task<string?>>? PickManifestAsync { get; set; }

    public ObservableCollection<InstallerStep> Steps { get; }

    public bool HasInstalledData { get; }
    public string InstalledText { get; }

    // ------------------------------------------------------------ stage

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceStage), nameof(IsPreparingStage), nameof(IsReviewStage),
        nameof(IsInstallingStage), nameof(IsDoneStage), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand), nameof(BackCommand), nameof(InstallCommand),
        nameof(CancelCommand))]
    public partial InstallerStage Stage { get; set; } = InstallerStage.Source;

    public bool IsSourceStage => Stage == InstallerStage.Source;
    public bool IsPreparingStage => Stage == InstallerStage.Preparing;
    public bool IsReviewStage => Stage == InstallerStage.Review;
    public bool IsInstallingStage => Stage == InstallerStage.Installing;
    public bool IsDoneStage => Stage == InstallerStage.Done;
    public bool IsBusy => Stage is InstallerStage.Preparing or InstallerStage.Installing;

    partial void OnStageChanged(InstallerStage value) => UpdateSteps();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    partial void OnErrorMessageChanged(string value) => UpdateSteps();

    // ------------------------------------------------------------ source

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalSource))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    public partial bool IsOnlineSource { get; set; } = true;

    public bool IsLocalSource
    {
        get => !IsOnlineSource;
        set => IsOnlineSource = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalManifest))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    public partial string LocalManifestPath { get; set; } = string.Empty;

    public bool HasLocalManifest => LocalManifestPath.Length > 0;

    // ------------------------------------------------------------ review

    [ObservableProperty] public partial string PlanTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string PlanVersion { get; set; } = string.Empty;
    [ObservableProperty] public partial string PlanPlatform { get; set; } = string.Empty;
    [ObservableProperty] public partial string PlanSource { get; set; } = string.Empty;
    [ObservableProperty] public partial string PlanSourceKind { get; set; } = string.Empty;
    [ObservableProperty] public partial string PlanDestination { get; set; } = string.Empty;
    [ObservableProperty] public partial bool PlanOverwrites { get; set; }
    [ObservableProperty] public partial string InstallLabel { get; set; } = "Install";

    // ------------------------------------------------------------ progress

    [ObservableProperty] public partial string PhaseTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string PhaseDetail { get; set; } = string.Empty;
    [ObservableProperty] public partial double OverallPercent { get; set; }
    [ObservableProperty] public partial double TransferPercent { get; set; }
    [ObservableProperty] public partial double ExtractPercent { get; set; }
    [ObservableProperty] public partial string TransferLabel { get; set; } = "DOWNLOAD";
    [ObservableProperty] public partial string TransferText { get; set; } = "—";
    [ObservableProperty] public partial string SpeedText { get; set; } = "—";
    [ObservableProperty] public partial string EtaText { get; set; } = "—";
    [ObservableProperty] public partial string ExtractText { get; set; } = "—";
    [ObservableProperty] public partial bool IsTransferActive { get; set; }
    [ObservableProperty] public partial bool IsExtractActive { get; set; }

    // ------------------------------------------------------------ commands

    [RelayCommand]
    private void SelectOnline() => IsOnlineSource = true;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        IsOnlineSource = false;
        if (PickManifestAsync is null)
            return;

        var path = await PickManifestAsync();
        if (!string.IsNullOrEmpty(path))
            LocalManifestPath = path;
    }

    private bool CanContinue() => IsSourceStage && (IsOnlineSource || HasLocalManifest);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        ErrorMessage = string.Empty;
        Stage = InstallerStage.Preparing;
        PhaseTitle = IsOnlineSource ? "Fetching package list…" : "Reading manifest…";
        PhaseDetail = IsOnlineSource ? DataInstaller.ManifestUrl : LocalManifestPath;

        _cts = new CancellationTokenSource();
        try
        {
            _plan = IsOnlineSource
                ? await DataInstaller.PrepareOnlineAsync(_cts.Token)
                : await DataInstaller.PrepareLocalAsync(LocalManifestPath, _cts.Token);

            ShowPlan(_plan);
            Stage = InstallerStage.Review;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Stage = InstallerStage.Source;
        }
        catch (Exception exception)
        {
            Log.Error("Data manifest", exception);
            Stage = InstallerStage.Source;
            ErrorMessage = $"Could not prepare the installation: {exception.Message}";
        }
        finally
        {
            DisposeCts();
        }
    }

    private bool CanGoBack() => IsReviewStage;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        ErrorMessage = string.Empty;
        _plan = null;
        Stage = InstallerStage.Source;
    }

    private bool CanInstall() => IsReviewStage && _plan is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var plan = _plan!;
        ErrorMessage = string.Empty;
        ResetProgress(plan);
        Stage = InstallerStage.Installing;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var archive = plan.IsRemote ? DataInstaller.GetDownloadPath(plan) : plan.Source;

        try
        {
            IsTransferActive = true;
            var transfer = new Progress<TransferProgress>(OnTransferProgress);
            if (plan.IsRemote)
            {
                PhaseTitle = "Downloading";
                PhaseDetail = plan.Source;
                await DataInstaller.DownloadAsync(plan.Source, archive, transfer, ct);
            }
            else
            {
                PhaseTitle = "Verifying archive";
                PhaseDetail = plan.Source;
                await DataInstaller.VerifyLocalAsync(plan.Source, transfer, ct);
            }

            IsTransferActive = false;
            IsExtractActive = true;
            PhaseTitle = "Extracting";
            PhaseDetail = plan.Destination;
            await DataInstaller.ExtractAsync(archive, plan.Destination, new Progress<ExtractProgress>(OnExtractProgress), ct);
            IsExtractActive = false;

            OverallPercent = 100;
            Stage = InstallerStage.Done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Stage = InstallerStage.Review;
            ErrorMessage = "Installation cancelled. Files already extracted were kept; install again to complete it.";
        }
        catch (Exception exception)
        {
            Log.Error("Data install", exception);
            Stage = InstallerStage.Review;
            InstallLabel = "Retry";
            ErrorMessage = $"Installation failed: {exception.Message}";
        }
        finally
        {
            IsTransferActive = IsExtractActive = false;
            if (plan.IsRemote)
                DataInstaller.TryDelete(archive);
            DisposeCts();
        }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        CloseRequested?.Invoke(this, IsDoneStage);
    }

    /// <summary>Called by the view when the window is being closed by the user.</summary>
    public void OnClosing() => _cts?.Cancel();

    // ------------------------------------------------------------ helpers

    private void ShowPlan(DataInstallPlan plan)
    {
        PlanTitle = plan.PackageName;
        PlanVersion = plan.Version;
        PlanPlatform = plan.Platform;
        PlanSource = plan.Source;
        PlanSourceKind = plan.IsRemote ? "DOWNLOAD FROM" : "LOCAL ARCHIVE";
        PlanDestination = plan.Destination;
        PlanOverwrites = plan.DestinationExists;
        InstallLabel = plan.DestinationExists ? "Reinstall" : "Install";
        InstallCommand.NotifyCanExecuteChanged();
    }

    private void ResetProgress(DataInstallPlan plan)
    {
        OverallPercent = TransferPercent = ExtractPercent = 0;
        TransferLabel = plan.IsRemote ? "DOWNLOAD" : "VERIFY";
        TransferText = SpeedText = EtaText = ExtractText = "—";
    }

    private void OnTransferProgress(TransferProgress p)
    {
        TransferPercent = p.Fraction * 100;
        OverallPercent = p.Fraction * TransferWeight * 100;
        TransferText = p.TotalBytes is > 0
            ? $"{FormatBytes(p.BytesDone)} of {FormatBytes(p.TotalBytes.Value)}"
            : FormatBytes(p.BytesDone);
        SpeedText = $"{FormatBytes((long)p.BytesPerSecond)}/s";
        EtaText = p.Remaining is { } remaining ? FormatDuration(remaining) : "—";
    }

    private void OnExtractProgress(ExtractProgress p)
    {
        ExtractPercent = p.Fraction * 100;
        OverallPercent = (TransferWeight + p.Fraction * (1 - TransferWeight)) * 100;
        ExtractText = $"{p.EntriesDone:N0} of {p.TotalEntries:N0} files";
        PhaseDetail = p.CurrentEntry;
    }

    private void UpdateSteps()
    {
        // rail index: Source=0, Review=1, Install=2, Done=3
        var current = Stage switch
        {
            InstallerStage.Source or InstallerStage.Preparing => 0,
            InstallerStage.Review => 1,
            InstallerStage.Installing => 2,
            _ => 3
        };

        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            step.IsDone = i < current || Stage == InstallerStage.Done;
            step.IsActive = i == current && Stage != InstallerStage.Done;
            step.IsError = i == current && HasError;
        }
    }

    private void DisposeCts()
    {
        _cts?.Dispose();
        _cts = null;
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes switch
        {
            >= (long)gb => $"{bytes / gb:0.00} GB",
            >= (long)mb => $"{bytes / mb:0.0} MB",
            >= (long)kb => $"{bytes / kb:0} KB",
            _ => $"{bytes} B"
        };
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min left"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s left"
        : $"{Math.Max(1, (int)t.TotalSeconds)} s left";
}
