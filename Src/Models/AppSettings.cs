using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RetroGameCard.Services;

namespace RetroGameCard.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
// game card
[JsonSerializable(typeof(Card))]
// launcher settings
[JsonSerializable(typeof(AppSettings))]
// data package manifest
[JsonSerializable(typeof(DataManifest))]
internal partial class AppJsonContext : JsonSerializerContext;

public sealed class AppSettings
{
    public const string DefaultRetroArchVersion = "1.22.2";

    public MainSettings Main { get; set; } = new();

    public RetroArchSettings RetroArch { get; set; } = new();

    public static async Task<AppSettings> LoadAsync()
    {
        var path = AppPaths.SettingsFile;
        try
        {
            Log.Info($"Loading settings: {path}");
            if (!File.Exists(path))
            {
                var settings = new AppSettings();
                await settings.SaveAsync();
                return settings;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.AppSettings)
                   ?? new AppSettings();
        }
        catch (Exception exception)
        {
            Log.Error($"Could not load settings '{path}'", exception);
            return new AppSettings();
        }
    }

    /// <summary>Atomically writes the settings file.</summary>
    public async Task SaveAsync()
    {
        Directory.CreateDirectory(AppPaths.SettingsDirectory);

        var temporaryPath = AppPaths.SettingsFile + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, this, AppJsonContext.Default.AppSettings);
        }

        File.Move(temporaryPath, AppPaths.SettingsFile, overwrite: true);
    }
}

public sealed class MainSettings
{
    public StartupWindowState StartupWindowState { get; set; } = StartupWindowState.Fullscreen;
}

public sealed class RetroArchSettings
{
    public string Version { get; set; } = AppSettings.DefaultRetroArchVersion;

    public bool StartFullscreen { get; set; } = true;

    public bool UseCustomExecutable { get; set; }

    public string ExecutablePath { get; set; } = string.Empty;

    public bool IntegerScaling { get; set; }

    public Shader Shader { get; set; } = Shader.Bilinear;
}

public enum StartupWindowState
{
    Normal,
    Fullscreen,
    Minimized
}

public enum Shader
{
    None,
    Bilinear,
    Crt,
    Retro,
    Scanline
}

public static class ShaderExtensions
{
    /// <summary>
    /// Shader preset path, relative to RetroArch's shader directory.
    /// Linux builds use GLSL presets, Windows builds use Slang presets.
    /// </summary>
    public static string GetPresetPath(this Shader shader)
    {
        var glsl = OperatingSystem.IsLinux();
        var (dir, ext) = glsl ? ("shaders_glsl", ".glslp") : ("shaders_slang", ".slangp");

        var relative = shader switch
        {
            Shader.None => "nearest",
            Shader.Crt => Path.Combine("crt", "crt-caligari"),
            Shader.Retro => Path.Combine("handheld", "retro-v2"),
            Shader.Scanline => Path.Combine("scanlines", "scanline"),
            _ => "bilinear"
        };

        return Path.Combine(dir, relative + ext);
    }
}
