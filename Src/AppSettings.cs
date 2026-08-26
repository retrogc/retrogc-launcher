using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RetroGameCard.Controls;

namespace RetroGameCard;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]

// game card
[JsonSerializable(typeof(Card))]
[JsonSerializable(typeof(SystemConfig))]
[JsonSerializable(typeof(RetroArchConfig))]
[JsonSerializable(typeof(MisterFpgaConfig))]

// launcher config
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(MainSettings))]
[JsonSerializable(typeof(RetroArchSettings))]

// data downloader
[JsonSerializable(typeof(RetrogcManifest))]
[JsonSerializable(typeof(RetrogcVersion))]
[JsonSerializable(typeof(RetrogcPlatform))]
[JsonSerializable(typeof(RetrogcDataFile))]
internal partial class AppJsonContext : JsonSerializerContext;

public sealed class AppSettings
{
    [JsonIgnore] public static readonly string RETROARCH_VERSION = "1.22.2";

    public MainSettings Main { get; set; } = new();

    public RetroArchSettings RetroArch { get; set; } = new();

    [JsonIgnore]
    public static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "retrogc");

    [JsonIgnore]
    public static string SettingsPath =>
        Path.Combine(SettingsDirectory, "settings.json");

    public static async Task<AppSettings> LoadAsync()
    {
        try
        {
            Console.WriteLine("AppSettings::LoadAsync: " + SettingsPath);
            if (!File.Exists(SettingsPath))
            {
                var settings = CreateDefault();
                await settings.SaveAsync();
                return settings;
            }

            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(
                stream, AppJsonContext.Default.AppSettings) ?? CreateDefault();
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"Could not load settings '{SettingsPath}': {exception}");

            return CreateDefault();
        }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(SettingsDirectory);

        var temporaryPath = SettingsPath + ".tmp";

        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(
                stream, this, AppJsonContext.Default.AppSettings);
        }

        File.Move(
            temporaryPath,
            SettingsPath,
            overwrite: true);
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            Main = new MainSettings
            {
                StartupWindowState = StartupWindowState.Fullscreen
            },
            RetroArch = new RetroArchSettings
            {
                Version = RETROARCH_VERSION,
                StartFullscreen = true,
                UseCustomExecutable = false,
                ExecutablePath = GetDefaultRetroArchPath(),
                IntegerScaling = false,
                Shader = Shader.Bilinear
            }
        };
    }

    private static string GetDefaultRetroArchPath()
    {
        return OperatingSystem.IsWindows()
            ? @"C:\RetroArch-Win64\retroarch.exe"
            : "/usr/bin/retroarch";
    }

    public static string GetShaderPath(Shader shader)
    {
        return shader switch
        {
            Shader.None => OperatingSystem.IsLinux()
                ? Path.Combine("shaders_glsl", "nearest.glslp")
                : Path.Combine("shaders_slang", "nearest.slangp"),
            Shader.Bilinear => OperatingSystem.IsLinux()
                ? Path.Combine("shaders_glsl", "bilinear.glslp")
                : Path.Combine("shaders_slang", "bilinear.slangp"),
            Shader.Crt => OperatingSystem.IsLinux()
                ? Path.Combine("shaders_glsl", "crt", "crt-caligari.glslp")
                : Path.Combine("shaders_slang", "crt", "crt-caligari.slangp"),
            Shader.Retro => OperatingSystem.IsLinux()
                ? Path.Combine("shaders_glsl", "handheld", "retro-v2.glslp")
                : Path.Combine("shaders_slang", "handheld", "retro-v2.slangp"),
            Shader.Scanline => OperatingSystem.IsLinux()
                ? Path.Combine("shaders_glsl", "scanlines", "scanline.glslp")
                : Path.Combine("shaders_slang", "scanlines", "scanline.slangp"),
            _ => OperatingSystem.IsLinux()
                ? Path.Combine("shaders_glsl", "bilinear.glslp")
                : Path.Combine("shaders_slang", "bilinear.slangp")
        };
    }
}

public sealed class MainSettings
{
    public StartupWindowState StartupWindowState { get; set; } =
        StartupWindowState.Fullscreen;
}

public sealed class RetroArchSettings
{
    public string Version { get; init; } = AppSettings.RETROARCH_VERSION;

    public bool StartFullscreen { get; set; } = true;

    public bool UseCustomExecutable { get; set; }

    public string ExecutablePath { get; set; } = "";

    public bool IntegerScaling { get; set; }

    public Shader Shader { get; set; } =
        Shader.Bilinear;
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