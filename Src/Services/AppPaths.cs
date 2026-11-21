using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RetroGameCard.Services;

/// <summary>
/// Well known launcher locations.
/// </summary>
public static class AppPaths
{
    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "retrogc");

    public static string SettingsFile { get; } = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Root of the installed RetroArch data packages (one sub folder per version).</summary>
    public static string DataRoot { get; } = Path.Combine(SettingsDirectory, "data");

    /// <summary>Where data packages are downloaded before extraction (same volume as <see cref="DataRoot"/>).</summary>
    public static string DownloadDirectory { get; } = Path.Combine(SettingsDirectory, "downloads");

    /// <summary>Where the launcher installs itself (from an installer card).</summary>
    public static string InstallDirectory { get; } = Path.Combine(SettingsDirectory, "app");

    public static string ExecutableName { get; } =
        OperatingSystem.IsWindows() ? "retrogc-launcher.exe" : "retrogc-launcher";

    public static string InstalledExecutable { get; } = Path.Combine(InstallDirectory, ExecutableName);

    /// <summary>Full path of the running launcher executable.</summary>
    public static string? CurrentExecutable { get; } = Environment.ProcessPath;

    /// <summary>True when the running launcher is the installed copy.</summary>
    public static bool IsRunningInstalled =>
        CurrentExecutable is not null && PathsEqual(CurrentExecutable, InstalledExecutable);

    public static string GetDataDirectory(string version) => Path.Combine(DataRoot, version);

    public static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Installed data package versions, newest first. Hidden / temporary folders are ignored.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledDataVersions()
    {
        try
        {
            if (!Directory.Exists(DataRoot))
                return [];

            return Directory.EnumerateDirectories(DataRoot)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.Length > 0 && name[0] != '.')
                .OrderByDescending(ParseVersion)
                .ThenByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception)
        {
            Log.Error("Could not list installed data versions", exception);
            return [];
        }
    }

    public static string? GetLatestDataVersion() => GetInstalledDataVersions().FirstOrDefault();

    // "1.22.2" sorts after "1.9.0" (plain string ordering would not).
    private static Version ParseVersion(string name) =>
        Version.TryParse(name, out var version) ? version : new Version(0, 0);
}
