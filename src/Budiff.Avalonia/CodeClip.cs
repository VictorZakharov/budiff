using System;
using System.Collections.Generic;
using System.Globalization;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media;

namespace Budiff.Avalonia;

// One code cell of a diff row: its line, shifted left by the diff's shared
// horizontal scroll and clipped to the cell. A wrapping line fits the cell
// (or trims to it) instead and does not scroll. It stands in for a ScrollViewer per cell,
// whose template (presenter, scrollbars) every row paid for - and every wrap
// toggle restyled.
//
// The line's leading whitespace (DiffInlines.Indent) is a hanging indent: the
// text starts that far in and every wrapped line continues there. A fitting
// cell caps it so a deep indent in a narrow pane still leaves room for code.
// Each wrapped (continuation) line gets a wrap mark in the line-number gutter.
public sealed class CodeClip : Decorator
{
    // The wrap mark: drawn right-aligned where a line number would be.
    public const string WrapMark = "↪";

    // Room the cap always leaves for code: a share of the cell, and at least
    // this many characters' worth.
    private const double MinCodeShare = 0.3;
    private const int MinCodeChars = 8;

    // The line number's right padding (DiffView.axaml): the mark lines up with
    // the numbers above it.
    private const double GutterPadding = 8;

    public static readonly StyledProperty<double> ScrollXProperty =
        AvaloniaProperty.Register<CodeClip, double>(nameof(ScrollX));

    public static readonly StyledProperty<IBrush?> WrapMarkBrushProperty =
        AvaloniaProperty.Register<CodeClip, IBrush?>(nameof(WrapMarkBrush), new SolidColorBrush(Color.Parse("#9DA0A8")));

    private static readonly Dictionary<(string Indent, double Size, FontFamily Family), double> IndentWidths = new();

    private double _indent;

    static CodeClip()
    {
        ClipToBoundsProperty.OverrideDefaultValue<CodeClip>(true);
        AffectsArrange<CodeClip>(ScrollXProperty);
        AffectsRender<CodeClip>(WrapMarkBrushProperty);

        // The line's own re-measure would reuse its last (unbounded) width:
        // re-measure the cell, which decides the width to give it.
        TextBlock.TextWrappingProperty.Changed.AddClassHandler<TextBlock>((text, _) => (text.Parent as CodeClip)?.InvalidateMeasure());
        TextBlock.TextTrimmingProperty.Changed.AddClassHandler<TextBlock>((text, _) => (text.Parent as CodeClip)?.InvalidateMeasure());
        DiffInlines.IndentProperty.Changed.AddClassHandler<TextBlock>((text, _) => (text.Parent as CodeClip)?.InvalidateMeasure());
    }

    public double ScrollX
    {
        get => GetValue(ScrollXProperty);
        set => SetValue(ScrollXProperty, value);
    }

    public IBrush? WrapMarkBrush
    {
        get => GetValue(WrapMarkBrushProperty);
        set => SetValue(WrapMarkBrushProperty, value);
    }

    // The hanging indent in use, in pixels (after any cap).
    public double IndentWidth => _indent;

    // Fits the cell: wrapping, or a one-line context row trimmed to it.
    private bool Wraps => Child is TextBlock text
        && (text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var wraps = Wraps;

        // Nothing is wider than a fitting cell: the wrap marks may then draw
        // into the gutter beside it.
        ClipToBounds = !wraps;
        _indent = MeasureIndent(child as TextBlock);
        if (wraps && !double.IsInfinity(availableSize.Width))
        {
            var minCode = Math.Max(MinCodeShare * availableSize.Width, MinCodeChars * CharWidth(child as TextBlock));
            _indent = Math.Max(0, Math.Min(_indent, availableSize.Width - minCode));
        }

        child.Measure(wraps
            ? availableSize.WithWidth(Math.Max(0, availableSize.Width - _indent))
            : availableSize.WithWidth(double.PositiveInfinity));
        var desired = child.DesiredSize;
        return new Size(Math.Min(desired.Width + _indent, availableSize.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            var wraps = Wraps;
            var width = wraps ? Math.Max(0, finalSize.Width - _indent) : Math.Max(finalSize.Width, child.DesiredSize.Width);
            child.Arrange(new Rect(_indent - (wraps ? 0 : ScrollX), 0, width, finalSize.Height));
        }

        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Child is not TextBlock text || text.TextWrapping == TextWrapping.NoWrap || WrapMarkBrush is not { } brush)
        {
            return;
        }

        var lines = text.TextLayout.TextLines;
        if (lines.Count < 2)
        {
            return;
        }

        var mark = new FormattedText(WrapMark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily), text.FontSize * 0.9, brush);
        var y = text.Bounds.Y + text.Padding.Top;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                context.DrawText(mark, new Point(-GutterPadding - mark.WidthIncludingTrailingWhitespace, y + ((lines[i].Height - mark.Height) / 2)));
            }

            y += lines[i].Height;
        }
    }

    // How many visual lines the code wraps to (1 when it does not).
    public int VisualLineCount => Child is TextBlock text && text.TextWrapping != TextWrapping.NoWrap
        ? Math.Max(1, text.TextLayout.TextLines.Count)
        : 1;

    private static double MeasureIndent(TextBlock? text)
    {
        var indent = text is null ? string.Empty : DiffInlines.GetIndent(text);
        return indent.Length == 0 ? 0 : Width(indent, text!);
    }

    private static double CharWidth(TextBlock? text) => text is null ? 0 : Width(" ", text);

    private static double Width(string whitespace, TextBlock text)
    {
        var key = (whitespace, text.FontSize, text.FontFamily);
        if (!IndentWidths.TryGetValue(key, out var width))
        {
            using var layout = new global::Avalonia.Media.TextFormatting.TextLayout(whitespace, new Typeface(text.FontFamily), text.FontSize, null);
            width = layout.WidthIncludingTrailingWhitespace;
            if (IndentWidths.Count > 4096)
            {
                IndentWidths.Clear();
            }

            IndentWidths[key] = width;
        }

        return width;
    }
}
