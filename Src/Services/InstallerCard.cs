using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RetroGameCard.Services;

/// <summary>
/// What an installer card offers compared to what is installed on this computer.
/// </summary>
public sealed record InstallerCardInfo(
    string ManifestPath,
    string DataVersion,
    bool DataInstalled,
    string? LauncherPath,
    Version? LauncherVersion)
{
    public bool HasLauncher => LauncherPath is not null;

    /// <summary>The card launcher is known to be newer than the running one.</summary>
    public bool LauncherIsNewer => LauncherVersion is not null && LauncherVersion > AppInstaller.CurrentVersion;

    public bool HasUpdate => !DataInstalled || LauncherIsNewer;
}

/// <summary>
/// An installer card is a volume with a <c>retrogc-data.rgc</c> manifest at its root, the data
/// archives it references and, optionally, the launcher executables:
/// <code>
/// retrogc-data.rgc
/// retrogc-data-1.22.2-windows.zip
/// retrogc-data-1.22.2-linux.zip
/// retrogc-launcher.exe   (Windows)
/// retrogc-launcher       (Linux)
/// </code>
/// The launcher can also be declared in the manifest, which gives its version (needed to offer
/// an update to an already installed launcher):
/// <code>{ "name": "retrogc-launcher", "version": "1.0.4", "file": "retrogc-launcher.exe", "url": "retrogc-launcher.exe" }</code>
/// </summary>
public static class InstallerCard
{
    public const string ManifestFileName = "retrogc-data.rgc";
    public const string LauncherPackageName = "retrogc-launcher";

    /// <summary>Path of the installer manifest at the root of <paramref name="directory"/>, if any.</summary>
    public static string? FindManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The installer manifest next to the running executable, when the launcher was started
    /// from an installer card (and not from its installed copy).
    /// </summary>
    public static string? FindLaunchManifest()
    {
        if (AppPaths.CurrentExecutable is null || AppPaths.IsRunningInstalled)
            return null;

        var directory = Path.GetDirectoryName(AppPaths.CurrentExecutable);
        return directory is null ? null : FindManifest(directory);
    }

    public static async Task<InstallerCardInfo> LoadAsync(string manifestPath, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException($"Invalid manifest path: {manifestPath}");
        var manifest = await DataInstaller.LoadManifestAsync(fullPath, ct);

        var entry = DataInstaller.PlatformKey is { } platform
            ? manifest.GetDataFile(platform, LauncherPackageName)
            : null;

        // Declared launcher first, then the conventional executable name at the card root.
        var relative = entry is null
            ? AppPaths.ExecutableName
            : string.IsNullOrWhiteSpace(entry.Url) ? entry.File : entry.Url;

        string? launcherPath = null;
        if (!string.IsNullOrWhiteSpace(relative) && !Uri.TryCreate(relative, UriKind.Absolute, out _))
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, relative));
            if (File.Exists(candidate))
                launcherPath = candidate;
        }

        var launcherVersion = entry is not null && Version.TryParse(entry.Version, out var parsed)
            ? AppInstaller.Normalize(parsed)
            : null;

        return new InstallerCardInfo(
            ManifestPath: fullPath,
            DataVersion: manifest.Default,
            DataInstalled: IsDataInstalled(manifest.Default),
            LauncherPath: launcherPath,
            LauncherVersion: launcherVersion);
    }

    private static bool IsDataInstalled(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return Directory.Exists(AppPaths.GetDataDirectory(version));
    }
}
