using System;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media;

namespace Budiff.Avalonia;

// One code cell of a diff row: its line, shifted left by the diff's shared
// horizontal scroll and clipped to the cell. A wrapping line fits the cell
// (or trims to it) instead and does not scroll. It stands in for a ScrollViewer per cell,
// whose template (presenter, scrollbars) every row paid for - and every wrap
// toggle restyled.
public sealed class CodeClip : Decorator
{
    public static readonly StyledProperty<double> ScrollXProperty =
        AvaloniaProperty.Register<CodeClip, double>(nameof(ScrollX));

    static CodeClip()
    {
        ClipToBoundsProperty.OverrideDefaultValue<CodeClip>(true);
        AffectsArrange<CodeClip>(ScrollXProperty);

        // The line's own re-measure would reuse its last (unbounded) width:
        // re-measure the cell, which decides the width to give it.
        TextBlock.TextWrappingProperty.Changed.AddClassHandler<TextBlock>((text, _) => (text.Parent as CodeClip)?.InvalidateMeasure());
        TextBlock.TextTrimmingProperty.Changed.AddClassHandler<TextBlock>((text, _) => (text.Parent as CodeClip)?.InvalidateMeasure());
    }

    public double ScrollX
    {
        get => GetValue(ScrollXProperty);
        set => SetValue(ScrollXProperty, value);
    }

    // Fits the cell: wrapping, or a one-line context row trimmed to it.
    private bool Wraps => Child is TextBlock text
        && (text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        child.Measure(Wraps ? availableSize : availableSize.WithWidth(double.PositiveInfinity));
        var desired = child.DesiredSize;
        return new Size(Math.Min(desired.Width, availableSize.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            var wraps = Wraps;
            var width = wraps ? finalSize.Width : Math.Max(finalSize.Width, child.DesiredSize.Width);
            child.Arrange(new Rect(wraps ? 0 : -ScrollX, 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
