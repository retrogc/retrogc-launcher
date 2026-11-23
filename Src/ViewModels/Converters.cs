using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace RetroGameCard.ViewModels;

/// <summary>
/// Small value converters shared by the views.
/// </summary>
public static class Converters
{
    /// <summary>true → ConverterParameter (or 1), false → 0. Used for fades.</summary>
    public static readonly IValueConverter BoolToOpacity = new BoolConverter((value, parameter) =>
        value ? ParseDouble(parameter, 1d) : 0d);

    /// <summary>ConverterParameter "whenTrue|whenFalse".</summary>
    public static readonly IValueConverter BoolToText = new BoolConverter((value, parameter) =>
    {
        var parts = (parameter as string ?? string.Empty).Split('|');
        return value ? parts[0] : parts.Length > 1 ? parts[1] : string.Empty;
    });

    /// <summary>true → OK cyan, false → warning yellow.</summary>
    public static readonly IValueConverter BoolToBrush = new BoolConverter((value, _) =>
        value ? OkBrush : WarnBrush);

    private static readonly IBrush OkBrush = Brush("#2EE6D6");
    private static readonly IBrush WarnBrush = Brush("#FFD23F");

    // Arcade accents (Theme.axaml) and their dark tints, picked by a stable hash of a text so that a
    // platform / tag always gets the same colour.
    private static readonly (IBrush Accent, IBrush Soft)[] Accents =
    [
        (Brush("#FF3E8A"), Brush("#3A1534")), // magenta
        (Brush("#2EE6D6"), Brush("#113A45")), // cyan
        (Brush("#FFD23F"), Brush("#3A3022")), // yellow
        (Brush("#FF8C42"), Brush("#3B2228")), // orange
        (Brush("#9B6CFF"), Brush("#2A1F55")), // violet
        (Brush("#6BE675"), Brush("#18352E")), // green
    ];

    /// <summary>A text → one of the accent colours (always the same one for the same text).</summary>
    public static readonly IValueConverter TextToAccentBrush =
        new TextConverter(text => Accents[AccentIndex(text)].Accent);

    /// <summary>A text → the dark tint of its accent colour (see <see cref="TextToAccentBrush"/>).</summary>
    public static readonly IValueConverter TextToSoftBrush =
        new TextConverter(text => Accents[AccentIndex(text)].Soft);

    private static int AccentIndex(string? text)
    {
        // FNV-1a: string.GetHashCode() changes between runs.
        var hash = 2166136261u;
        foreach (var c in (text ?? string.Empty).Trim().ToUpperInvariant())
            hash = (hash ^ c) * 16777619u;
        return (int)(hash % (uint)Accents.Length);
    }

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

    private static double ParseDouble(object? parameter, double fallback) =>
        parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => fallback
        };

    private sealed class TextConverter(Func<string?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            convert(value?.ToString());

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class BoolConverter(Func<bool, object?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            convert(value is true, parameter);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
