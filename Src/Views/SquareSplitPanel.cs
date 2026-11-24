using System;
using Avalonia;
using Avalonia.Controls;

namespace RetroGameCard.Views;

/// <summary>
/// Two children side by side: the first one is a square (the cover frame) as tall as the panel, capped at
/// <see cref="MaxSquareFraction"/> of its width; the second one takes the rest of the width. The square is centred vertically
/// when the width caps it.
/// A Grid can't size a column from its row height, hence this panel.
/// </summary>
public sealed class SquareSplitPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SquareSplitPanel, double>(nameof(Spacing), 40);

    public static readonly StyledProperty<double> MaxSquareFractionProperty =
        AvaloniaProperty.Register<SquareSplitPanel, double>(nameof(MaxSquareFraction), 0.5);

    static SquareSplitPanel()
    {
        AffectsMeasure<SquareSplitPanel>(SpacingProperty, MaxSquareFractionProperty);
    }

    /// <summary>Space between the square and the second child.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The square is at most this fraction of the panel width (keeps room for the details).</summary>
    public double MaxSquareFraction
    {
        get => GetValue(MaxSquareFractionProperty);
        set => SetValue(MaxSquareFractionProperty, value);
    }

    private double Side(Size size)
    {
        var byWidth = double.IsInfinity(size.Width) ? double.PositiveInfinity : size.Width * MaxSquareFraction;
        var side = Math.Min(size.Height, byWidth);
        return double.IsInfinity(side) ? 0 : Math.Max(0, side);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Side(availableSize);
        var restWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - side - Spacing);

        double height = side;
        double width = side + Spacing;
        if (Children.Count > 0)
            Children[0].Measure(new Size(side, side));
        if (Children.Count > 1)
        {
            Children[1].Measure(new Size(restWidth, availableSize.Height));
            height = Math.Max(height, Children[1].DesiredSize.Height);
            width += Children[1].DesiredSize.Width;
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var side = Side(finalSize);
        if (Children.Count > 0)
            // Centred vertically when the width caps the square (tall screens).
            Children[0].Arrange(new Rect(0, Math.Max(0, (finalSize.Height - side) / 2), side, side));
        if (Children.Count > 1)
        {
            var left = side + Spacing;
            Children[1].Arrange(new Rect(left, 0, Math.Max(0, finalSize.Width - left), finalSize.Height));
        }

        return finalSize;
    }
}
