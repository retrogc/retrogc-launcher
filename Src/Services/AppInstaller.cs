using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace RetroGameCard.Services;

/// <summary>
/// Installs the launcher executable into <see cref="AppPaths.InstallDirectory"/> and creates
/// the application menu shortcut (Linux .desktop entry, Windows Start Menu .lnk).
/// </summary>
public static class AppInstaller
{
    private const string AppName = "Retro Game Card";
    private const string IconAsset = "avares://retrogc-launcher/Assets/Icons/retrogc.png";
    private const string OldSuffix = ".old";

    // Held for the life of the process, see TryAcquireInstanceLock.
    private static Mutex? _instanceLock;

    /// <summary>Version of the running launcher (set at publish time with -p:Version=X.Y.Z).</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    public static string IconPath => Path.Combine(AppPaths.InstallDirectory, "retrogc.png");

    /// <summary>
    /// Version of the installed launcher. The executable itself can't tell (release builds carry no
    /// version resource), so it is recorded next to it at install time and by the installed launcher.
    /// </summary>
    public static string VersionFilePath => Path.Combine(AppPaths.InstallDirectory, "version");

    /// <summary>"1.0.3" and "1.0.3.0" must compare equal.</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static Version ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null)
        {
            // "1.0.4+commit" / "1.0.4-beta"
            var end = informational.IndexOfAny(['+', '-']);
            if (Version.TryParse(end >= 0 ? informational[..end] : informational, out var parsed))
                return Normalize(parsed);
        }

        return Normalize(assembly?.GetName().Version ?? new Version(0, 0, 0));
    }

    // ------------------------------------------------------------ instance lock

    /// <summary>
    /// Marks this process as "the" running launcher. Returns false when another launcher already
    /// holds the lock (used to avoid starting a second launcher after an installation).
    /// </summary>
    public static bool TryAcquireInstanceLock()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, "retrogc-launcher-instance");
            if (mutex.WaitOne(0))
            {
                _instanceLock = mutex;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it: we own it now.
            _instanceLock = mutex;
            return true;
        }
        catch (Exception exception)
        {
            Log.Error("Instance lock", exception);
        }

        mutex?.Dispose();
        return false;
    }

    public static bool OwnsInstanceLock => _instanceLock is not null;

    /// <summary>Releases the instance lock (before starting a replacement launcher).</summary>
    public static void ReleaseInstanceLock()
    {
        try
        {
            _instanceLock?.ReleaseMutex();
            _instanceLock?.Dispose();
        }
        catch (Exception exception)
        {
            Log.Error("Instance lock release", exception);
        }

        _instanceLock = null;
    }

    // ------------------------------------------------------------ install

    /// <summary>
    /// Copies <paramref name="sourceExecutable"/> to the install directory, then creates the
    /// application shortcut. The installed launcher may be running: it is renamed, never overwritten.
    /// <paramref name="version"/> is recorded for later updates (null: unknown).
    /// </summary>
    public static async Task InstallAsync(string sourceExecutable, Version? version, CancellationToken ct)
    {
        var destination = AppPaths.InstalledExecutable;
        Directory.CreateDirectory(AppPaths.InstallDirectory);

        if (!AppPaths.PathsEqual(sourceExecutable, destination))
        {
            var temporary = destination + ".new";
            const int bufferSize = 1 << 20;
            await using (var input = new FileStream(sourceExecutable, FileMode.Open, FileAccess.Read, FileShare.Read,
                             bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, bufferSize, ct);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Windows can rename a running executable but not overwrite it.
            if (File.Exists(destination))
                MoveAside(destination);

            File.Move(temporary, destination);
            Log.Info($"Launcher installed: {sourceExecutable} -> {destination}");
            WriteInstalledVersion(version);
        }

        await WriteIconAsync();
        CreateShortcut();
    }

    // ------------------------------------------------------------ installed version

    /// <summary>What is installed in <see cref="AppPaths.InstallDirectory"/>.</summary>
    /// <param name="IsInstalled">An installed executable exists.</param>
    /// <param name="Version">Its version, null when unknown (installed before versions were recorded).</param>
    public readonly record struct InstalledLauncher(bool IsInstalled, Version? Version);

    public static InstalledLauncher ReadInstalledLauncher()
    {
        if (!File.Exists(AppPaths.InstalledExecutable))
            return new InstalledLauncher(false, null);

        try
        {
            if (File.Exists(VersionFilePath) && Version.TryParse(File.ReadAllText(VersionFilePath).Trim(), out var version))
                return new InstalledLauncher(true, Normalize(version));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not read the installed launcher version", exception);
        }

        return new InstalledLauncher(true, null);
    }

    /// <summary>
    /// True when this launcher, started from elsewhere (a download...), should offer to install itself:
    /// nothing installed, installed version unknown, or older than this one.
    /// </summary>
    public static bool ShouldOfferInstall(InstalledLauncher installed, Version current) =>
        !installed.IsInstalled || installed.Version is null || Normalize(current) > installed.Version;

    /// <summary>
    /// The running launcher isn't the installed one and should offer to install itself. Unversioned
    /// development builds (0.0.0) never do.
    /// </summary>
    public static bool NeedsInstallOffer(out InstalledLauncher installed)
    {
        installed = default;
        if (AppPaths.IsRunningInstalled || AppPaths.CurrentExecutable is null || CurrentVersion == new Version(0, 0, 0))
            return false;

        installed = ReadInstalledLauncher();
        return ShouldOfferInstall(installed, CurrentVersion);
    }

    /// <summary>Records <paramref name="version"/> as the installed one (null: forget it). Never throws.</summary>
    public static void WriteInstalledVersion(Version? version)
    {
        try
        {
            if (version is null)
            {
                File.Delete(VersionFilePath);
                return;
            }

            var text = Normalize(version).ToString(3);
            if (File.Exists(VersionFilePath) && File.ReadAllText(VersionFilePath).Trim() == text)
                return;

            Directory.CreateDirectory(AppPaths.InstallDirectory);
            File.WriteAllText(VersionFilePath, text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not record the installed launcher version", exception);
        }
    }

    /// <summary>
    /// Stops the installed launcher if it is running (and the RetroArch it started), before it is
    /// replaced by this one. Returns how many were stopped.
    /// </summary>
    public static int StopInstalledInstances()
    {
        var stopped = 0;
        var currentId = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppPaths.ExecutableName)))
        {
            using (process)
            {
                try
                {
                    if (process.Id == currentId)
                        continue;

                    var path = process.MainModule?.FileName;
                    if (path is null || !AppPaths.PathsEqual(path, AppPaths.InstalledExecutable))
                        continue;

                    Log.Info($"Stopping the installed launcher (PID {process.Id})");
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
                        Log.Error($"The installed launcher (PID {process.Id}) is still running");
                    stopped++;
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                      System.ComponentModel.Win32Exception or
                                                      NotSupportedException)
                {
                    // exited in between, or not ours to inspect
                }
            }
        }

        return stopped;
    }

    /// <summary>Deletes executables left behind by a previous update (best effort).</summary>
    public static void CleanupOldExecutables()
    {
        try
        {
            if (!Directory.Exists(AppPaths.InstallDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(AppPaths.InstallDirectory, "*" + OldSuffix))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Still running (Windows): removed on a later start.
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error("Cleanup of old launcher executables", exception);
        }
    }

    /// <summary>Starts the installed launcher.</summary>
    public static void LaunchInstalled()
    {
        Log.Info($"Starting installed launcher: {AppPaths.InstalledExecutable}");
        using var _ = Process.Start(new ProcessStartInfo(AppPaths.InstalledExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = AppPaths.InstallDirectory
        });
    }

    private static void MoveAside(string path)
    {
        try
        {
            File.Move(path, path + OldSuffix, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A previous ".old" is still running (Windows): use a unique name.
            File.Move(path, $"{path}.{DateTime.UtcNow.Ticks}{OldSuffix}");
        }
    }

    private static async Task WriteIconAsync()
    {
        try
        {
            await using var source = AssetLoader.Open(new Uri(IconAsset));
            await using var target = File.Create(IconPath);
            await source.CopyToAsync(target);
        }
        catch (Exception exception)
        {
            Log.Error("Could not write the launcher icon", exception);
        }
    }

    // ------------------------------------------------------------ shortcuts

    /// <summary>Creates the application menu shortcut. A failure is logged, never thrown.</summary>
    public static void CreateShortcut()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                CreateLinuxDesktopEntry();
            else if (OperatingSystem.IsWindows())
                CreateWindowsShortcut();
        }
        catch (Exception exception)
        {
            Log.Error("Could not create the application shortcut", exception);
        }
    }

    private static void CreateLinuxDesktopEntry()
    {
        // ~/.local/share/applications (XDG_DATA_HOME/applications)
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications");
        Directory.CreateDirectory(directory);

        var entry = new StringBuilder()
            .AppendLine("[Desktop Entry]")
            .AppendLine("Type=Application")
            .AppendLine($"Name={AppName}")
            .AppendLine("Comment=Plug in a card. Play the game.")
            .AppendLine($"Exec={QuoteDesktopExec(AppPaths.InstalledExecutable)}")
            .AppendLine($"Path={AppPaths.InstallDirectory}")
            .AppendLine($"Icon={IconPath}")
            .AppendLine("Terminal=false")
            .AppendLine("Categories=Game;Emulator;")
            .AppendLine("StartupWMClass=retrogc-launcher")
            .ToString();

        var path = Path.Combine(directory, "retrogc-launcher.desktop");
        File.WriteAllText(path, entry);
        Log.Info($"Desktop entry created: {path}");
    }

    /// <summary>Quotes an Exec argument (Desktop Entry spec: string escapes apply before quoting).</summary>
    private static string QuoteDesktopExec(string argument)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in argument)
        {
            builder.Append(c switch
            {
                '\\' => @"\\\\",
                '"' or '`' or '$' => @"\\" + c,
                _ => c.ToString()
            });
        }

        return builder.Append('"').ToString();
    }

    private static void CreateWindowsShortcut()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(directory);
        var shortcut = Path.Combine(directory, AppName + ".lnk");
        var target = AppPaths.InstalledExecutable;

        // WScript.Shell through PowerShell: no COM interop in a trimmed build.
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script =
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({Quote(shortcut)}); " +
            $"$s.TargetPath = {Quote(target)}; " +
            $"$s.WorkingDirectory = {Quote(AppPaths.InstallDirectory)}; " +
            $"$s.IconLocation = {Quote(target + ",0")}; " +
            $"$s.Description = {Quote(AppName)}; " +
            "$s.Save()";

        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }
        }) ?? throw new InvalidOperationException("Could not start PowerShell.");

        if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("PowerShell did not create the shortcut in time.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell exited with code {process.ExitCode}.");

        Log.Info($"Start Menu shortcut created: {shortcut}");
    }
}
