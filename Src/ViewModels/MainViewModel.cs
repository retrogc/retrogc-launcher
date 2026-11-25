using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RetroGameCard.Models;
using RetroGameCard.Services;

namespace RetroGameCard.ViewModels;

public enum SessionState
{
    Waiting,
    Loading,
    Running,
    Stopped,
    Error,
    Installer
}

/// <summary>
/// State of the main (attract / now playing) screen.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string WaitingTitle = "Insert a game card";

    private const string WaitingDescription =
        "Plug in a Retro Game Card (USB stick or SD card labelled RETROGC). " +
        "The game starts automatically as soon as the card is detected.";

    private readonly DispatcherTimer _clockTimer;

    public MainViewModel()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();
        _clockTimer.Start();

        RefreshDataVersion();
    }

    // ------------------------------------------------------------ game info

    [ObservableProperty] public partial string Title { get; set; } = WaitingTitle;
    [ObservableProperty] public partial string Description { get; set; } = WaitingDescription;
    [ObservableProperty] public partial string Platform { get; set; } = string.Empty;
    [ObservableProperty] public partial string Byline { get; set; } = string.Empty;

    /// <summary>Metadata badges (genre, players, core, version...).</summary>
    public ObservableCollection<string> Tags { get; } = [];

    public bool HasTags => Tags.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial Bitmap? Cover { get; set; }

    public bool HasCover => Cover is not null;

    // ------------------------------------------------------------ state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsLoading), nameof(IsRunning), nameof(IsStopped),
        nameof(IsError), nameof(IsInstaller), nameof(IsIdle), nameof(IsBusy), nameof(HasCard), nameof(StateLabel), nameof(MonitorText),
        nameof(HintText), nameof(ProgressValue))]
    public partial SessionState State { get; set; } = SessionState.Waiting;

    [ObservableProperty] public partial string StateDetail { get; set; } = "Monitoring removable drives…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    public bool IsWaiting => State == SessionState.Waiting;
    public bool IsLoading => State == SessionState.Loading;
    public bool IsRunning => State == SessionState.Running;
    public bool IsStopped => State == SessionState.Stopped;
    public bool IsError => State == SessionState.Error;
    public bool IsInstaller => State == SessionState.Installer;

    /// <summary>Nothing in progress, the card can be acted on (play again / install).</summary>
    public bool IsIdle => State is SessionState.Stopped or SessionState.Installer;
    public bool IsBusy => State is SessionState.Waiting or SessionState.Loading;
    public bool HasCard => State != SessionState.Waiting;

    /// <summary>Status bar fill when not indeterminate: full while playing.</summary>
    public double ProgressValue =>
        State is SessionState.Running or SessionState.Stopped or SessionState.Installer ? 100 : 0;

    public string StateLabel => State switch
    {
        SessionState.Loading => "LOADING",
        SessionState.Running => "PLAYING",
        SessionState.Stopped => "READY",
        SessionState.Error => "ERROR",
        SessionState.Installer => "INSTALLER",
        _ => "WAITING"
    };

    public string MonitorText => State == SessionState.Waiting ? "CARD MONITOR ACTIVE" : "GAME CARD DETECTED";

    public string HintText => State switch
    {
        SessionState.Stopped or SessionState.Error => "A / ENTER PLAY  ·  F11 FULLSCREEN  ·  F12 SETTINGS",
        SessionState.Installer => "ENTER INSTALL  ·  F11 FULLSCREEN  ·  F12 SETTINGS",
        _ => "F11 FULLSCREEN  ·  F12 SETTINGS"
    };

    // ------------------------------------------------------------ footer / header

    public string LauncherVersionText { get; } = $"LAUNCHER {AppInstaller.CurrentVersion}";

    [ObservableProperty] public partial string DataVersionText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial string ClockText { get; set; } = "00:00";
    [ObservableProperty] public partial string DateText { get; set; } = string.Empty;

    // ------------------------------------------------------------ transitions (UI thread)

    public void ShowWaiting()
    {
        State = SessionState.Waiting;
        Title = WaitingTitle;
        Description = WaitingDescription;
        Platform = string.Empty;
        Byline = string.Empty;
        Tags.Clear();
        OnPropertyChanged(nameof(HasTags));
        SetCover(null);
        StateDetail = "Monitoring removable drives…";
        ErrorMessage = string.Empty;
    }

    public void ShowCard(Card card, Bitmap? cover, string dataVersion)
    {
        Title = card.Name;
        Description = card.Description;
        Platform = card.Platform.ToUpperInvariant();
        Byline = string.Join("  ·  ", NonEmpty(card.Developer, card.Year));

        Tags.Clear();
        if (card.Genre.Length > 0) Tags.Add(card.Genre.ToUpperInvariant());
        if (card.Players.Length > 0) Tags.Add(FormatPlayers(card.Players));
        Tags.Add(card.RetroArchConfig.Core.ToUpperInvariant());
        if (card.SupportsMisterFpga) Tags.Add("MISTER READY");

        if (!string.Equals(card.RetroArchConfig.Version, dataVersion, StringComparison.OrdinalIgnoreCase))
            Tags.Add($"BUILT FOR RA {card.RetroArchConfig.Version}");
        OnPropertyChanged(nameof(HasTags));

        SetCover(cover);
        ErrorMessage = string.Empty;
    }

    /// <summary>An installer card (retrogc-data.rgc) is inserted instead of a game card.</summary>
    public void ShowInstallerCard(InstallerCardInfo info)
    {
        ShowWaiting();
        State = SessionState.Installer;
        Title = "Installer card";
        Platform = "RETRO GAME CARD";
        Description = info.HasUpdate
            ? "This card can update the launcher and / or the RetroArch data on this computer."
            : "Everything on this card is already installed on this computer.";

        Tags.Add($"DATA {info.DataVersion}{(info.DataInstalled ? " INSTALLED" : string.Empty)}");
        if (info.LauncherVersion is { } version)
            Tags.Add(info.LauncherIsNewer ? $"LAUNCHER {version} AVAILABLE" : $"LAUNCHER {version}");
        OnPropertyChanged(nameof(HasTags));

        StateDetail = info.HasUpdate ? "Update available. Press Enter to install." : "Up to date. Press Enter to reinstall.";
    }

    public void SetLoading(string detail)
    {
        State = SessionState.Loading;
        StateDetail = detail;
    }

    public void SetRunning(string dataVersion)
    {
        State = SessionState.Running;
        StateDetail = $"Running in RetroArch {dataVersion}";
    }

    public void SetStopped()
    {
        State = SessionState.Stopped;
        StateDetail = "RetroArch closed. Press A or Enter to play again.";
    }

    /// <summary>The card is shown and waits for A / Enter (launch delay "wait for a button", or B).</summary>
    public void SetReady()
    {
        State = SessionState.Stopped;
        StateDetail = "Press A or Enter to play.";
    }

    public void SetError(string message)
    {
        State = SessionState.Error;
        StateDetail = "Unable to start the game";
        ErrorMessage = message;
    }

    public void RefreshDataVersion()
    {
        var version = AppPaths.GetLatestDataVersion();
        HasData = version is not null;
        DataVersionText = version is null ? "NO RETROARCH DATA — PRESS F12" : $"RETROARCH DATA {version}";
    }

    // ------------------------------------------------------------ helpers

    private void SetCover(Bitmap? cover)
    {
        var previous = Cover;
        Cover = cover;
        if (!ReferenceEquals(previous, cover))
            previous?.Dispose();
    }

    private static string FormatPlayers(string players) =>
        players.Contains("player", StringComparison.OrdinalIgnoreCase)
            ? players.ToUpperInvariant()
            : players == "1"
                ? "1 PLAYER"
                : $"{players} PLAYERS";

    private static string[] NonEmpty(params string[] values) =>
        Array.FindAll(values, v => !string.IsNullOrWhiteSpace(v));

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        ClockText = now.ToString("HH:mm", culture);
        DateText = now.ToString("dddd, dd MMMM", culture).ToUpper(culture);
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        SetCover(null);
    }
}
