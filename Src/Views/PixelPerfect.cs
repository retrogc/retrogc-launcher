using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace RetroGameCard.Views;

/// <summary>
/// Keeps the pixel font and the CRT scanlines aligned on screen pixels, whatever the window / OS scaling.
/// <para>
/// The pixel font (Press Start 2P) is drawn on an 8 px grid: it is only sharp when each of its pixels covers
/// a whole number of screen pixels. Text using it sets <see cref="FontSizeProperty"/> (its size at scale 1)
/// instead of FontSize; the actual size is snapped to a multiple of 8 screen pixels and drawn aliased.
/// </para>
/// <para>
/// Each window publishes its total scale (screen pixels per layout unit: OS scaling × UI scale) through the
/// inherited <see cref="ScaleProperty"/> (<see cref="Track"/>), and gets a <c>ScanlinesBrush</c> resource
/// whose lines are whole screen pixels.
/// </para>
/// </summary>
public static class PixelPerfect
{
    private const double FontGrid = 8;

    /// <summary>Screen pixels per layout unit for this subtree (inherited).</summary>
    public static readonly AttachedProperty<double> ScaleProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Scale", typeof(PixelPerfect), 1d, inherits: true);

    /// <summary>Size of pixel-font text at scale 1 (0: not pixel text).</summary>
    public static readonly AttachedProperty<double> FontSizeProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("FontSize", typeof(PixelPerfect), 0d);

    private static readonly ConditionalWeakTable<TopLevel, Tracker> Trackers = new();

    static PixelPerfect()
    {
        ScaleProperty.Changed.AddClassHandler<Control>((control, _) => UpdateFont(control));
        FontSizeProperty.Changed.AddClassHandler<Control>((control, _) => UpdateFont(control));
    }

    public static double GetScale(Control control) => control.GetValue(ScaleProperty);
    public static void SetScale(Control control, double value) => control.SetValue(ScaleProperty, value);
    public static double GetFontSize(Control control) => control.GetValue(FontSizeProperty);
    public static void SetFontSize(Control control, double value) => control.SetValue(FontSizeProperty, value);

    /// <summary>
    /// Follows the OS scaling of <paramref name="window"/>, combined with its own UI scale
    /// (the LayoutTransform applied to its content). Call again when the UI scale changes.
    /// </summary>
    public static void Track(TopLevel window, double uiScale)
    {
        if (!Trackers.TryGetValue(window, out var tracker))
        {
            tracker = new Tracker(window);
            Trackers.Add(window, tracker);
        }

        tracker.UiScale = uiScale;
        tracker.Update();
    }

    private static void UpdateFont(Control control)
    {
        var size = GetFontSize(control);
        if (size <= 0)
            return;

        var scale = Math.Max(0.1, GetScale(control));
        var cells = Math.Max(1, Math.Round(size * scale / FontGrid, MidpointRounding.AwayFromZero));
        var device = cells * FontGrid;

        control.SetValue(TextElement.FontSizeProperty, device / scale);
        if (control is TextBlock text)
            text.LineHeight = Math.Round(device * 1.5) / scale;

        // Whole font pixels: no antialiasing needed, and none of its blur.
        TextOptions.SetTextRenderingMode(control, TextRenderingMode.Alias);
    }

    /// <summary>One dark line of <c>k</c> screen pixels every <c>3k</c>, in layout units of the given scale.</summary>
    private static IBrush CreateScanlines(double scale)
    {
        var k = Math.Max(1, Math.Round(scale));
        var line = k / scale;
        var period = 3 * k / scale;
        var width = 8 / scale;

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing
        {
            Brush = Brushes.Transparent,
            Geometry = new RectangleGeometry(new Rect(0, 0, width, period)),
        });
        group.Children.Add(new GeometryDrawing
        {
            Brush = Brushes.Black,
            Geometry = new RectangleGeometry(new Rect(0, period - line, width, line)),
        });

        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, width, period, RelativeUnit.Absolute),
        };
    }

    private sealed class Tracker
    {
        private readonly TopLevel _window;

        public double UiScale { get; set; } = 1;

        public Tracker(TopLevel window)
        {
            _window = window;
            window.ScalingChanged += (_, _) => Update();
            if (window is Window w)
                w.Opened += (_, _) => Update();
        }

        public void Update()
        {
            var scale = _window.RenderScaling * UiScale;
            if (Math.Abs(GetScale(_window) - scale) < 0.001 && _window.Resources.ContainsKey("ScanlinesBrush"))
                return;

            SetScale(_window, scale);
            _window.Resources["ScanlinesBrush"] = CreateScanlines(scale);
        }
    }
}
