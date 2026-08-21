using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RetroGameCard;

public sealed class DataManifest
{
    [JsonPropertyName("default")] public string DefaultVersion { get; set; } = string.Empty;
    [JsonPropertyName("versions")] public List<DataManifestVersion> Versions { get; set; } = [];
}

public sealed class DataManifestVersion
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;

    // Keys are runtime identifiers such as "win-x64" and "linux-x64"
    [JsonPropertyName("platforms")] public Dictionary<string, DataManifestPlatform> Platforms { get; set; } = [];
}

public sealed class DataManifestPlatform
{
    [JsonPropertyName("data")] public List<DataManifestFile> Data { get; set; } = [];
}

public sealed class DataManifestFile
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
}