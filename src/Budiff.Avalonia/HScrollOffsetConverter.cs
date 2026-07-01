using System;
using System.Globalization;
using global::Avalonia;
using global::Avalonia.Data.Converters;

namespace Budiff.Avalonia;

public sealed class HScrollOffsetConverter : IValueConverter
{
    public static readonly HScrollOffsetConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? new Vector(d, 0) : default(Vector);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Vector v ? v.X : 0d;
}
