using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard;

public sealed record Card
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("platform")] public string Platform { get; init; } = "WAITING";

    [JsonPropertyName("year")] public string Year { get; init; } = "WAITING";

    [JsonPropertyName("image")] public string Image { get; init; } = string.Empty;

    [JsonPropertyName("retroarch")] public required RetroArchConfig RetroArchConfig { get; init; }

    [JsonPropertyName("misterfpga")] public MisterFpgaConfig? MisterFpgaConfig { get; init; }

    /// <summary>
    /// Absolute path to the game JSON file. This is runtime-only metadata,
    /// and is not written back to the SD card JSON.
    /// </summary>
    [JsonIgnore]
    public string DefinitionPath { get; private init; } = string.Empty;

    [JsonIgnore]
    public string BaseDirectory =>
        Path.GetDirectoryName(DefinitionPath)
        ?? throw new InvalidOperationException(
            "DefinitionPath has no parent directory.");

    /// <summary>
    /// Absolute path to the ROM on the mounted RETROGC card.
    /// </summary>
    [JsonIgnore]
    public string? RomPath =>
        string.IsNullOrWhiteSpace(RetroArchConfig.Roms[0])
            ? null
            : ResolvePath(RetroArchConfig.Roms[0]);

    [JsonIgnore]
    public string Core =>
        string.IsNullOrWhiteSpace(RetroArchConfig.Core)
            ? "WAITING"
            : RetroArchConfig.Core;

    /// <summary>
    /// Absolute path to the optional game image on the SD card.
    /// </summary>
    [JsonIgnore]
    public string ImagePath =>
        string.IsNullOrWhiteSpace(Image)
            ? string.Empty
            : ResolvePath(Image);

    /// <summary>
    /// The installed RetroArch core filename, e.g. fbneo_libretro.so.
    /// It is intentionally not resolved relative to the SD card.
    /// </summary>
    [JsonIgnore]
    public string RetroArchCore => RetroArchConfig.Core;

    /// <summary>
    /// MiSTer MRA path relative to the MiSTer SD-card root.
    /// </summary>
    [JsonIgnore]
    public string? MisterFpgaCore => MisterFpgaConfig?.Core;

    public bool SupportsRetroArch =>
        !string.IsNullOrWhiteSpace(RetroArchConfig.Core);

    public bool SupportsMisterFpga =>
        !string.IsNullOrWhiteSpace(MisterFpgaConfig?.Core);

    public static async Task<Card> LoadAsync(
        string definitionPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        var fullDefinitionPath = Path.GetFullPath(definitionPath);
        if (!File.Exists(fullDefinitionPath))
        {
            throw new FileNotFoundException(
                $"Game definition file not found: {fullDefinitionPath}",
                fullDefinitionPath);
        }

        await using var stream = File.OpenRead(fullDefinitionPath);
        var game = await JsonSerializer.DeserializeAsync(
            stream, AppJsonContext.Default.Card, cancellationToken);

        if (game is null)
        {
            throw new JsonException(
                $"Game definition is empty: {fullDefinitionPath}");
        }

        game.Validate(fullDefinitionPath);

        return game with { DefinitionPath = fullDefinitionPath };
    }

    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException(
                $"No path is defined for '{Name}'.");
        }

        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException(
                $"Paths in game metadata must be relative: '{relativePath}'.");
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(BaseDirectory, relativePath));

        var baseDirectoryWithSeparator =
            Path.EndsInDirectorySeparator(BaseDirectory)
                ? BaseDirectory
                : BaseDirectory + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(baseDirectoryWithSeparator, comparison))
        {
            throw new InvalidOperationException(
                $"Path '{relativePath}' escapes the game definition directory.");
        }

        return fullPath;
    }

    private void Validate(string definitionPath)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new JsonException(
                $"Missing 'name' in {definitionPath}.");
        }

        // A game must target RetroArch.
        if (!SupportsRetroArch)
        {
            throw new JsonException(
                $"Missing both 'retroarch.core' and " +
                $"'misterfpga_config.core' in {definitionPath}.");
        }
    }
}

public record SystemConfig
{
    [JsonPropertyName("core")] public required string Core { get; init; }
    [JsonPropertyName("roms")] public required string[] Roms { get; init; }
    [JsonPropertyName("runtimes")] public string[]? Runtimes { get; init; } = [];
}

public sealed record RetroArchConfig : SystemConfig
{
    [JsonPropertyName("version")] public required string Version { get; init; } = string.Empty;
}

public sealed record MisterFpgaConfig : SystemConfig;