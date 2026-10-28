using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Budiff.App;

// How far a bird's-eye row's name may run to the right: over the cells that
// show nothing for that row (a hidden column, or an empty one such as Files on
// a file or Added with no churn), and on into the first cell that does, up to
// a small gap before its right-aligned number. So a long name uses the blank
// space instead of trimming early, and never covers a value. The Size bar is
// a hard stop.
// Values: the row (CodeAnalysisRow) and the visible-cells mask
// (MainWindowViewModel.VisibleCells, bit i = grid column i).
internal static class NameExtent
{
    // Name, then Lines .. Size.
    internal const int ColumnCount = 12;

    private const int SizeColumn = 11;

    // A number cell's right margin (TextBlock.num) and the gap kept before it.
    private const double NumberRightMargin = 8;
    private const double Gap = 8;

    private static readonly Typeface NumberFont = new("Cascadia Code,Consolas,monospace");
    private const double NumberFontSize = 12;

    // (columns covered, room to leave at the right end for the number).
    public static (int Span, double Reserve) Of(IList<object?> values)
    {
        if (values.Count < 2 || values[0] is not CodeAnalysisRow row || values[1] is not int visible)
        {
            return (1, 0);
        }

        var span = 1;
        for (var column = 1; column < ColumnCount; column++)
        {
            if ((visible & (1 << column)) == 0)
            {
                span++;
                continue;
            }

            if (column == SizeColumn)
            {
                break;
            }

            var text = row.CellText(column);
            if (text.Length == 0)
            {
                span++;
                continue;
            }

            return (span + 1, NumberRightMargin + Width(text) + Gap);
        }

        return (span, 0);
    }

    private static double Width(string text)
    {
        using var layout = new TextLayout(text, NumberFont, NumberFontSize, Brushes.Black);
        return layout.WidthIncludingTrailingWhitespace;
    }
}

// Grid.ColumnSpan of the row's name cell.
public sealed class NameSpanConverter : IMultiValueConverter
{
    public static readonly NameSpanConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => NameExtent.Of(values).Span;
}

// The name cell's Margin: room kept clear for the number it runs up to.
public sealed class NameReserveConverter : IMultiValueConverter
{
    public static readonly NameReserveConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(0, 0, NameExtent.Of(values).Reserve, 0);
}
