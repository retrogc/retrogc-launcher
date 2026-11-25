using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Threading;
using SDL3;

namespace RetroGameCard.Services;

/// <summary>What a gamepad button does on the main screen (the keyboard equivalent in brackets).</summary>
public enum GamepadAction
{
    /// <summary>A or Start (Enter): play, start now.</summary>
    Confirm,

    /// <summary>B: stop the launch countdown, keep the game info on screen.</summary>
    Back,

    /// <summary>D-pad up / down: scroll the description.</summary>
    ScrollUp,
    ScrollDown
}

/// <summary>
/// Reads gamepads with SDL3 on a background thread (hotplug included) and raises
/// <see cref="Pressed"/> on the UI thread. Nothing is grabbed: RetroArch sees the pads as usual.
/// Without SDL (library missing, no input access) it logs once and stays silent: the keyboard
/// keeps working.
/// </summary>
public sealed class GamepadInput : IDisposable
{
    private readonly Dictionary<uint, IntPtr> _pads = new();
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>A button was pressed (raised on the UI thread).</summary>
    public event EventHandler<GamepadAction>? Pressed;

    public void Start()
    {
        if (_thread is not null)
            return;

        _thread = new Thread(Run) { IsBackground = true, Name = "Gamepads" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            // There is no SDL window: without this SDL would only report events to a focused one.
            SDL.SetHint(SDL.Hints.JoystickAllowBackgroundEvents, "1");
            if (!SDL.Init(SDL.InitFlags.Gamepad))
            {
                Log.Info($"Gamepads unavailable: {SDL.GetError()}");
                return;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                                              or BadImageFormatException)
        {
            Log.Info($"Gamepads unavailable (SDL3 could not be loaded): {exception.Message}");
            return;
        }

        Log.Info("Gamepads: SDL3 ready");
        try
        {
            while (!_stop)
            {
                if (SDL.WaitEventTimeout(out var e, 100))
                    Handle(ref e);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Gamepad thread stopped", exception);
        }
        finally
        {
            foreach (var pad in _pads.Values)
                SDL.CloseGamepad(pad);
            _pads.Clear();
            SDL.Quit();
        }
    }

    private void Handle(ref SDL.Event e)
    {
        switch ((SDL.EventType)e.Type)
        {
            case SDL.EventType.GamepadAdded:
            {
                var id = e.GDevice.Which;
                if (_pads.ContainsKey(id))
                    break;
                var pad = SDL.OpenGamepad(id);
                if (pad == IntPtr.Zero)
                {
                    Log.Info($"Gamepad {id}: could not open it ({SDL.GetError()})");
                    break;
                }

                _pads[id] = pad;
                Log.Info($"Gamepad connected: {SDL.GetGamepadName(pad) ?? "unknown"}");
                break;
            }
            case SDL.EventType.GamepadRemoved:
            {
                var id = e.GDevice.Which;
                if (_pads.Remove(id, out var pad))
                {
                    Log.Info($"Gamepad disconnected: {SDL.GetGamepadName(pad) ?? "unknown"}");
                    SDL.CloseGamepad(pad);
                }

                break;
            }
            case SDL.EventType.GamepadButtonDown:
            {
                // Positional names: South is A on an Xbox pad (Cross on PlayStation, B on Nintendo).
                GamepadAction? action = (SDL.GamepadButton)e.GButton.Button switch
                {
                    SDL.GamepadButton.South or SDL.GamepadButton.Start => GamepadAction.Confirm,
                    SDL.GamepadButton.East => GamepadAction.Back,
                    SDL.GamepadButton.DPadUp => GamepadAction.ScrollUp,
                    SDL.GamepadButton.DPadDown => GamepadAction.ScrollDown,
                    _ => null
                };

                if (action is { } a)
                    Dispatcher.UIThread.Post(() => Pressed?.Invoke(this, a));
                break;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}
