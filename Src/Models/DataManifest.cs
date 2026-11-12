using System;
using System.Collections.Generic;

namespace RetroGameCard.Models;

/// <summary>
/// The <c>retrogc-data.rgc</c> manifest describing the downloadable RetroArch data packages.
/// </summary>
public sealed class DataManifest
{
    public string Default { get; init; } = string.Empty;
    public List<DataVersion> Versions { get; init; } = [];

    public DataFile? GetDataFile(string platformKey, string name)
    {
        var version = Versions.Find(x => string.Equals(x.Version, Default, StringComparison.OrdinalIgnoreCase));
        if (version is null || !version.Platforms.TryGetValue(platformKey, out var platform))
            return null;

        return platform.Data.Find(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DataVersion
{
    public string Version { get; set; } = string.Empty;
    public Dictionary<string, DataPlatform> Platforms { get; set; } = [];
}

public sealed class DataPlatform
{
    public List<DataFile> Data { get; set; } = [];
}

public sealed class DataFile
{
    public string Name { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
