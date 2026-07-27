using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroGameCard;

public sealed record Game
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("filename")] public required string Filename { get; init; }

    [JsonPropertyName("core")] public required string Core { get; init; }

    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

    [JsonPropertyName("image")] public string Image { get; init; } = string.Empty;

    [JsonPropertyName("background")] public string Background { get; init; } = string.Empty;

    /// <summary>
    /// Full path to the JSON file from which this definition was loaded.
    /// Never serialized into the card's JSON metadata.
    /// </summary>
    [JsonIgnore]
    public string DefinitionPath { get; private init; } = string.Empty;

    /// <summary>
    /// Directory containing DefinitionPath.
    /// </summary>
    [JsonIgnore]
    public string BaseDirectory =>
        Path.GetDirectoryName(DefinitionPath)
        ?? throw new InvalidOperationException("DefinitionPath has no directory.");

    [JsonIgnore] public string RomPath => ResolvePath(Filename);

    [JsonIgnore] public string CorePath => ResolvePath(Core);

    [JsonIgnore] public string ImagePath => ResolvePath(Image);

    [JsonIgnore]
    public string? BackgroundPath =>
        string.IsNullOrWhiteSpace(Background) ? null : ResolvePath(Background);

    public static async Task<Game> LoadAsync(string definitionPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);

        var fullDefinitionPath = Path.GetFullPath(definitionPath);

        if (!File.Exists(fullDefinitionPath))
        {
            throw new FileNotFoundException(
                $"Game definition file not found: {fullDefinitionPath}",
                fullDefinitionPath);
        }

        await using FileStream stream = File.OpenRead(fullDefinitionPath);

        Game? game =
            await JsonSerializer.DeserializeAsync<Game>(
                stream,
                JsonOptions,
                cancellationToken);

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

        // Prevent ../ paths in removable-media JSON from escaping BaseDirectory.
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
            throw new JsonException($"Missing 'name' in {definitionPath}.");

        if (string.IsNullOrWhiteSpace(Filename))
            throw new JsonException($"Missing 'filename' in {definitionPath}.");

        if (string.IsNullOrWhiteSpace(Core))
            throw new JsonException($"Missing 'core' in {definitionPath}.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}