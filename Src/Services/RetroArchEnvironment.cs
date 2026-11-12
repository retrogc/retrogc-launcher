using System;
using System.IO;
using RetroGameCard.Models;

namespace RetroGameCard.Services;

/// <summary>
/// Resolved RetroArch locations for one launch.
/// </summary>
/// <param name="Version">Data package version in use (may differ from the card's requested version).</param>
/// <param name="ExecutablePath">RetroArch executable.</param>
/// <param name="HomePath">Value for $HOME (Linux AppImage portable home), or null to keep the user's.</param>
/// <param name="RetroPath">RetroArch configuration root (cores, system, shaders...).</param>
public sealed record RetroArchEnvironment(
    string Version,
    string ExecutablePath,
    string? HomePath,
    string RetroPath)
{
    private const string LinuxAppImage = "RetroArch-Linux-x86_64.AppImage";

    public string CoresPath => Path.Combine(RetroPath, "cores");
    public string SystemPath => Path.Combine(RetroPath, "system");

    public string GetCorePath(string coreName) =>
        Path.Combine(CoresPath, coreName + (OperatingSystem.IsWindows() ? "_libretro.dll" : "_libretro.so"));

    /// <summary>
    /// Picks the data package to use: the card's version when installed, otherwise the newest
    /// installed one. A custom executable (settings) replaces the bundled RetroArch binary.
    /// </summary>
    public static RetroArchEnvironment Resolve(string? requestedVersion, RetroArchSettings settings)
    {
        var version = !string.IsNullOrWhiteSpace(requestedVersion)
                      && Directory.Exists(AppPaths.GetDataDirectory(requestedVersion))
            ? requestedVersion
            : AppPaths.GetLatestDataVersion();

        if (version is null)
        {
            // No bundled data at all: only a custom RetroArch can run the game.
            if (settings.UseCustomExecutable && File.Exists(settings.ExecutablePath))
                return ForCustomInstall(settings.ExecutablePath);

            throw new InvalidOperationException(
                "No RetroArch data installed. Press F12 → Data package to install it.");
        }

        var dataPath = AppPaths.GetDataDirectory(version);
        string? homePath;
        string retroPath;
        string exePath;

        if (OperatingSystem.IsWindows())
        {
            homePath = null;
            retroPath = dataPath;
            exePath = Path.Combine(dataPath, "retroarch.exe");
        }
        else
        {
            homePath = Path.Combine(dataPath, LinuxAppImage + ".home");
            retroPath = Path.Combine(homePath, ".config", "retroarch");
            exePath = Path.Combine(dataPath, LinuxAppImage);
        }

        if (settings.UseCustomExecutable && File.Exists(settings.ExecutablePath))
            exePath = settings.ExecutablePath;

        return new RetroArchEnvironment(version, exePath, homePath, retroPath);
    }

    private static RetroArchEnvironment ForCustomInstall(string exePath)
    {
        var retroPath = OperatingSystem.IsWindows()
            ? Path.GetDirectoryName(exePath)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "retroarch");
        return new RetroArchEnvironment("custom", exePath, null, retroPath);
    }
}
