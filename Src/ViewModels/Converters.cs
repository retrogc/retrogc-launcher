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

    /// <summary>true → accent green, false → warning amber.</summary>
    public static readonly IValueConverter BoolToBrush = new BoolConverter((value, _) =>
        value ? OkBrush : WarnBrush);

    private static readonly IBrush OkBrush = new ImmutableSolidColorBrush(Color.Parse("#5CE1B7"));
    private static readonly IBrush WarnBrush = new ImmutableSolidColorBrush(Color.Parse("#F2BF5E"));

    private static double ParseDouble(object? parameter, double fallback) =>
        parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => fallback
        };

    private sealed class BoolConverter(Func<bool, object?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            convert(value is true, parameter);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
