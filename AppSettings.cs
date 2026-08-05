using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RetroGameCard;

public sealed class AppSettings
{
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
                StartFullscreen = true,
                UseCustomExecutable = false,
                ExecutablePath = GetDefaultRetroArchPath(),
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
            Shader.None => Path.Combine("shaders_glsl", "nearest.glslp"),
            Shader.Bilinear => Path.Combine("shaders_glsl", "bilinear.glslp"),
            Shader.Crt => Path.Combine("shaders_glsl", "crt", "crt-caligari.glslp"),
            Shader.Retro => Path.Combine("shaders_glsl", "retro", "retro.glslp"),
            Shader.Scanline => Path.Combine("shaders_glsl", "scanlines", "scanline.glslp"),
            _ => Path.Combine("shaders_glsl", "bilinear.glslp")
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
    public bool StartFullscreen { get; set; } = true;

    public bool UseCustomExecutable { get; set; } = false;

    public string ExecutablePath { get; set; } = "";

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