using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Budiff.App;

// How many bird's-eye grid columns a row's name may cover: its own, plus the
// cells to its right that show nothing for that row (a hidden column, or an
// empty one such as Files on a file or Added with no churn), up to the first
// cell with something in it. So a long name runs on over blank space instead
// of trimming early, and never over a number.
// Values: the row (CodeAnalysisRow) and the visible-cells mask
// (MainWindowViewModel.VisibleCells, bit i = grid column i).
public sealed class NameSpanConverter : IMultiValueConverter
{
    public static readonly NameSpanConverter Instance = new();

    // Name, then Lines .. Size.
    internal const int ColumnCount = 11;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not CodeAnalysisRow row || values[1] is not int visible)
        {
            return 1;
        }

        var span = 1;
        for (var column = 1; column < ColumnCount; column++)
        {
            if ((visible & (1 << column)) != 0 && !row.IsCellEmpty(column))
            {
                break;
            }

            span++;
        }

        return span;
    }
}
