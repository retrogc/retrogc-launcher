using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace RetroGameCard.Views;

/// <summary>
/// Scales fixed-size dialogs up on high resolution screens where the OS scaling doesn't (a 4K display
/// at 100%...): their layout is designed for a 1080 DIP high screen.
/// </summary>
public static class UiScale
{
    private const double ReferenceScreenHeight = 1080;

    /// <summary>The scale for the screen of <paramref name="owner"/> (or the primary screen), at least 1.</summary>
    public static double ForScreen(Window window, Window? owner)
    {
        try
        {
            var screens = (owner ?? window).Screens;
            var screen = (owner is { IsVisible: true } ? screens.ScreenFromWindow(owner) : null) ?? screens.Primary;
            if (screen is null || screen.Scaling <= 0)
                return 1;

            return Math.Max(1, screen.WorkingArea.Height / screen.Scaling / ReferenceScreenHeight);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Scales <paramref name="window"/>'s content and size for its screen. Call before showing it.</summary>
    public static void Apply(Window window, Window? owner)
    {
        var scale = ForScreen(window, owner);
        if (scale < 1.01 || window.Content is not Control content)
            return;

        window.Content = null;
        window.Content = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(scale, scale),
            Child = content,
        };

        window.Width = Scaled(window.Width, scale);
        window.Height = Scaled(window.Height, scale);
        window.MinWidth = Scaled(window.MinWidth, scale);
        window.MinHeight = Scaled(window.MinHeight, scale);
    }

    private static double Scaled(double value, double scale) =>
        double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? value : Math.Round(value * scale);
}
