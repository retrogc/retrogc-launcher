using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using RetroGameCard.Models;
using RetroGameCard.Services;

namespace RetroGameCard.ViewModels;

public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Editable copy of <see cref="AppSettings"/>; changes are applied only on <see cref="SaveAsync"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public SettingsViewModel() : this(new AppSettings())
    {
        // design-time
    }

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;

        SelectedStartupState = Array.Find(StartupStates, o => o.Value == settings.Main.StartupWindowState)
                               ?? StartupStates[0];
        SelectedShader = Array.Find(Shaders, o => o.Value == settings.RetroArch.Shader) ?? Shaders[1];
        StartFullscreen = settings.RetroArch.StartFullscreen;
        IntegerScaling = settings.RetroArch.IntegerScaling;
        UseCustomExecutable = settings.RetroArch.UseCustomExecutable;
        ExecutablePath = settings.RetroArch.ExecutablePath;

        RefreshDataInfo();
    }

    public Option<StartupWindowState>[] StartupStates { get; } =
    [
        new("Fullscreen", StartupWindowState.Fullscreen),
        new("Windowed", StartupWindowState.Normal),
        new("Minimized (tray)", StartupWindowState.Minimized)
    ];

    public Option<Shader>[] Shaders { get; } =
    [
        new("None (sharp pixels)", Shader.None),
        new("Bilinear (smooth)", Shader.Bilinear),
        new("CRT", Shader.Crt),
        new("Retro LCD", Shader.Retro),
        new("Scanlines", Shader.Scanline)
    ];

    [ObservableProperty] public partial Option<StartupWindowState> SelectedStartupState { get; set; }
    [ObservableProperty] public partial Option<Shader> SelectedShader { get; set; }
    [ObservableProperty] public partial bool StartFullscreen { get; set; }
    [ObservableProperty] public partial bool IntegerScaling { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExecutableError))]
    public partial bool UseCustomExecutable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExecutableError))]
    public partial string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Validation message for the custom executable, empty when valid / unused.</summary>
    public string ExecutableError =>
        !UseCustomExecutable ? string.Empty
        : string.IsNullOrWhiteSpace(ExecutablePath) ? "Select a RetroArch executable."
        : !File.Exists(ExecutablePath.Trim()) ? "File not found."
        : string.Empty;

    [ObservableProperty] public partial string DataInfo { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasData { get; set; }

    public string SettingsPath => AppPaths.SettingsFile;

    public void RefreshDataInfo()
    {
        var versions = AppPaths.GetInstalledDataVersions();
        HasData = versions.Count > 0;
        DataInfo = versions.Count switch
        {
            0 => "Not installed — games can't be launched.",
            1 => $"Version {versions[0]} installed",
            _ => $"Version {versions[0]} installed (+{versions.Count - 1} older)"
        };
    }

    public async Task<bool> SaveAsync()
    {
        if (ExecutableError.Length > 0)
            return false;

        _settings.Main.StartupWindowState = SelectedStartupState.Value;
        _settings.RetroArch.StartFullscreen = StartFullscreen;
        _settings.RetroArch.IntegerScaling = IntegerScaling;
        _settings.RetroArch.Shader = SelectedShader.Value;
        _settings.RetroArch.UseCustomExecutable = UseCustomExecutable;
        _settings.RetroArch.ExecutablePath = ExecutablePath.Trim();

        await _settings.SaveAsync();
        return true;
    }
}
