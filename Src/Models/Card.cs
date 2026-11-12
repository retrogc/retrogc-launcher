using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard.Models;

// Note: the source-generated JSON reader assigns null to init-only properties missing from the
// file (overriding their initializers), hence the "?? string.Empty" in the setters below.

/// <summary>
/// A Retro Game Card definition (the <c>.rgc</c> JSON file at the root of a card).
/// Every field added after the original format is optional, so older cards keep loading
/// and older launchers simply ignore the new fields.
/// </summary>
public sealed record Card
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("description")] public string Description { get; init => field = value ?? string.Empty; } = string.Empty;

    [JsonPropertyName("platform")] public string Platform { get; init => field = value ?? string.Empty; } = string.Empty;

    [JsonPropertyName("year")] public string Year { get; init => field = value ?? string.Empty; } = string.Empty;

    [JsonPropertyName("image")] public string Image { get; init => field = value ?? string.Empty; } = string.Empty;

    // ----- optional metadata (format extension, display only) -----

    /// <summary>Developer / studio, e.g. <c>Capcom</c>.</summary>
    [JsonPropertyName("developer")] public string Developer { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Genre, e.g. <c>Beat 'em up</c>.</summary>
    [JsonPropertyName("genre")] public string Genre { get; init => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Number of players, e.g. <c>1-2</c>.</summary>
    [JsonPropertyName("players")] public string Players { get; init => field = value ?? string.Empty; } = string.Empty;

    // ----- launch targets -----

    [JsonPropertyName("retroarch")] public required RetroArchConfig RetroArchConfig { get; init; }

    [JsonPropertyName("misterfpga")] public MisterFpgaConfig? MisterFpgaConfig { get; init; }

    /// <summary>
    /// Absolute path to the .rgc file. Runtime-only, never serialized.
    /// </summary>
    [JsonIgnore]
    public string DefinitionPath { get; private init; } = string.Empty;

    [JsonIgnore]
    public string BaseDirectory =>
        Path.GetDirectoryName(DefinitionPath)
        ?? throw new InvalidOperationException("DefinitionPath has no parent directory.");

    /// <summary>Absolute path of the ROM RetroArch is launched with (first <c>roms</c> entry).</summary>
    [JsonIgnore]
    public string RomPath => ResolvePath(RetroArchConfig.Roms[0]);

    /// <summary>Absolute paths of every ROM file the card declares.</summary>
    [JsonIgnore]
    public string[] RomPaths => RetroArchConfig.Roms.Select(ResolvePath).ToArray();

    /// <summary>Absolute paths of the runtime (BIOS) files to copy into RetroArch's system folder.</summary>
    [JsonIgnore]
    public string[] RuntimePaths => (RetroArchConfig.Runtimes ?? []).Select(ResolvePath).ToArray();

    /// <summary>Absolute path to the optional cover image, or empty.</summary>
    [JsonIgnore]
    public string ImagePath => string.IsNullOrWhiteSpace(Image) ? string.Empty : ResolvePath(Image);

    [JsonIgnore]
    public bool SupportsMisterFpga => !string.IsNullOrWhiteSpace(MisterFpgaConfig?.Core);

    public static async Task<Card> LoadAsync(string definitionPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        var fullDefinitionPath = Path.GetFullPath(definitionPath);
        if (!File.Exists(fullDefinitionPath))
            throw new FileNotFoundException($"Card definition not found: {fullDefinitionPath}", fullDefinitionPath);

        await using var stream = File.OpenRead(fullDefinitionPath);
        var card = await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.Card, cancellationToken)
                   ?? throw new JsonException($"Card definition is empty: {fullDefinitionPath}");

        card = card with { DefinitionPath = fullDefinitionPath };
        card.Validate();
        return card;
    }

    /// <summary>
    /// Returns the first <c>.rgc</c> file found at the root of <paramref name="directory"/>, if any.
    /// </summary>
    public static string? FindDefinition(string directory) =>
        Directory.EnumerateFiles(directory, "*.rgc", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException($"An empty path is defined in '{Name}'.");

        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"Paths in a card must be relative: '{relativePath}'.");

        var baseDirectory = BaseDirectory;
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        var baseWithSeparator = Path.EndsInDirectorySeparator(baseDirectory)
            ? baseDirectory
            : baseDirectory + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(baseWithSeparator, comparison))
            throw new InvalidOperationException($"Path '{relativePath}' escapes the card directory.");

        return fullPath;
    }

    private void Validate()
    {
        var file = Path.GetFileName(DefinitionPath);

        if (string.IsNullOrWhiteSpace(Name))
            throw new JsonException($"Missing 'name' in {file}.");

        if (RetroArchConfig is null)
            throw new JsonException($"Missing 'retroarch' in {file}.");

        if (string.IsNullOrWhiteSpace(RetroArchConfig.Core))
            throw new JsonException($"Missing 'retroarch.core' in {file}.");

        if (RetroArchConfig.Roms is null || RetroArchConfig.Roms.Length == 0 || string.IsNullOrWhiteSpace(RetroArchConfig.Roms[0]))
            throw new JsonException($"Missing 'retroarch.roms' in {file}.");
    }
}

public record SystemConfig
{
    [JsonPropertyName("core")] public required string Core { get; init; }
    [JsonPropertyName("roms")] public required string[] Roms { get; init; }
    [JsonPropertyName("runtimes")] public string[]? Runtimes { get; init => field = value ?? []; } = [];
}

public sealed record RetroArchConfig : SystemConfig
{
    [JsonPropertyName("version")] public required string Version { get; init => field = value ?? string.Empty; } = string.Empty;
}

public sealed record MisterFpgaConfig : SystemConfig
{
    // Only used by the MiSTer script; declared so the launcher round-trips the full format.
    [JsonPropertyName("delay")] public string? Delay { get; init; }
    [JsonPropertyName("index")] public string? Index { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}
