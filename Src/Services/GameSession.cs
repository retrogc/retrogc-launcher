using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RetroGameCard.Models;
using RetroGameCard.ViewModels;

namespace RetroGameCard.Services;

/// <summary>
/// Glue between the card monitor, RetroArch and the main screen:
/// card inserted → load .rgc → copy BIOS files → launch; card removed → stop.
/// Insert / remove / relaunch requests are serialized so they can never overlap.
/// </summary>
public sealed class GameSession : IAsyncDisposable
{
    private readonly CardMonitor _monitor = new("RETROGC");
    private readonly RetroArchLauncher _launcher = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MainViewModel _view;
    private readonly Func<AppSettings> _settings;

    private Card? _card;

    public GameSession(MainViewModel view, Func<AppSettings> settings)
    {
        _view = view;
        _settings = settings;

        _monitor.Inserted += (_, e) => _ = RunExclusiveAsync(() => OnInsertedAsync(e.Volume));
        _monitor.Removed += (_, e) => _ = RunExclusiveAsync(() => OnRemovedAsync(e.Volume));
        _launcher.Exited += OnRetroArchExited;
    }

    public void Start() => _monitor.Start();

    /// <summary>Launches the current card again (after RetroArch was closed or failed).</summary>
    public void Relaunch()
    {
        if (_card is null || _launcher.IsRunning)
            return;

        var card = _card;
        _ = RunExclusiveAsync(() => LaunchAsync(card));
    }

    private async Task RunExclusiveAsync(Func<Task> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Error("GameSession", exception);
            Ui(() => _view.SetError(exception.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnInsertedAsync(CardInfo volume)
    {
        Log.Info($"Card inserted: {volume.MountPoint}");
        Ui(() =>
        {
            _view.ShowWaiting();
            _view.SetLoading("Reading game card…");
        });

        var definition = await FindDefinitionAsync(volume.MountPoint);
        if (definition is null)
            throw new FileNotFoundException($"No .rgc card definition found at the root of {volume.MountPoint}.");

        var card = await Card.LoadAsync(definition);
        _card = card;

        var env = RetroArchEnvironment.Resolve(card.RetroArchConfig.Version, _settings().RetroArch);
        var cover = await LoadCoverAsync(card.ImagePath);
        Ui(() => _view.ShowCard(card, cover, env.Version));

        await LaunchAsync(card);
    }

    private async Task LaunchAsync(Card card)
    {
        var settings = _settings().RetroArch;
        var env = RetroArchEnvironment.Resolve(card.RetroArchConfig.Version, settings);

        if (!string.Equals(env.Version, card.RetroArchConfig.Version, StringComparison.OrdinalIgnoreCase))
            Log.Info($"RetroArch version mismatch, card: {card.RetroArchConfig.Version}, using: {env.Version}");

        // Every declared ROM must be present (multi-disc games list all of them).
        foreach (var rom in card.RomPaths)
        {
            if (!File.Exists(rom))
                throw new FileNotFoundException($"Missing game file: {Path.GetFileName(rom)}", rom);
        }

        var runtimes = card.RuntimePaths;
        if (runtimes.Length > 0)
        {
            Ui(() => _view.SetLoading("Installing system files…"));
            Directory.CreateDirectory(env.SystemPath);
            foreach (var src in runtimes)
            {
                var dst = Path.Combine(env.SystemPath, Path.GetFileName(src));
                Log.Info($"Copying system file '{src}' -> '{dst}'");
                await CopyIfChangedAsync(src, dst);
            }
        }

        Ui(() => _view.SetLoading("Starting RetroArch…"));
        Log.Notify($"Loading {card.Name}");

        _launcher.Start(new RetroArchLaunchOptions(
            Environment: env,
            RomPath: card.RomPath,
            CorePath: env.GetCorePath(card.RetroArchConfig.Core),
            Fullscreen: settings.StartFullscreen,
            IntegerScaling: settings.IntegerScaling,
            Shader: settings.Shader.GetPresetPath()));

        Ui(() => _view.SetRunning(env.Version));
    }

    private async Task OnRemovedAsync(CardInfo volume)
    {
        Log.Info($"Card removed: {volume.MountPoint}");
        _card = null;
        Ui(_view.ShowWaiting);
        await _launcher.StopAsync(TimeSpan.FromSeconds(3));
    }

    private void OnRetroArchExited(object? sender, RetroArchExitedEventArgs e)
    {
        Log.Info($"RetroArch exited (code {e.ExitCode})");
        Ui(() =>
        {
            // Only meaningful while the card is still inserted.
            if (_card is not null && _view.IsRunning)
                _view.SetStopped();
        });
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Runs a UI update; a failure is shown on screen instead of crashing the launcher.</summary>
    private void Ui(Action action) => Dispatcher.UIThread.Post(() =>
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Log.Error("UI update failed", exception);
            _view.SetError(exception.Message);
        }
    });

    /// <summary>Freshly mounted volumes can take a moment before their files are listed.</summary>
    private static async Task<string?> FindDefinitionAsync(string mountPoint)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var definition = Card.FindDefinition(mountPoint);
                if (definition is not null)
                    return definition;
            }
            catch (IOException)
            {
                // not ready yet
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static async Task<Bitmap?> LoadCoverAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        try
        {
            // Read into memory so the card can be pulled at any time without locking the file.
            var bytes = await File.ReadAllBytesAsync(path);
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            });
        }
        catch (Exception exception)
        {
            Log.Error($"Could not load cover '{path}'", exception);
            return null;
        }
    }

    private static async Task CopyIfChangedAsync(string source, string destination)
    {
        var src = new FileInfo(source);
        if (!src.Exists)
            throw new FileNotFoundException($"Missing system file: {src.Name}", source);

        var dst = new FileInfo(destination);
        if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc)
            return;

        const int bufferSize = 1 << 20;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await input.CopyToAsync(output, bufferSize);
        }

        File.SetLastWriteTimeUtc(destination, src.LastWriteTimeUtc);
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.DisposeAsync();
        await _launcher.DisposeAsync();
        _gate.Dispose();
    }
}
