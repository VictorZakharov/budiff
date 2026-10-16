using global::Avalonia;
using global::Avalonia.Controls;

namespace Budiff.Avalonia;

// One two-pane diff row: blame | old | funnel gutter | new. The code halves
// are weighted star columns, so SplitRatio moves the divider without anyone
// needing the row's pixel width - and every row (plus DiffFunnelGutter, which
// uses the same formula) lands the gutter at the same x.
public sealed class DiffSplitGrid : Grid
{
    public static readonly StyledProperty<double> SplitRatioProperty =
        AvaloniaProperty.Register<DiffSplitGrid, double>(nameof(SplitRatio), 0.5);

    public DiffSplitGrid()
    {
        ColumnDefinitions = new ColumnDefinitions
        {
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(0.5, GridUnitType.Star),
            new ColumnDefinition(DiffFunnelGutter.GutterWidth, GridUnitType.Pixel),
            new ColumnDefinition(0.5, GridUnitType.Star),
        };
    }

    public double SplitRatio
    {
        get => GetValue(SplitRatioProperty);
        set => SetValue(SplitRatioProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SplitRatioProperty)
        {
            var ratio = Math.Clamp(SplitRatio, DiffViewModel.MinSplitRatio, DiffViewModel.MaxSplitRatio);
            ColumnDefinitions[1].Width = new GridLength(ratio, GridUnitType.Star);
            ColumnDefinitions[3].Width = new GridLength(1 - ratio, GridUnitType.Star);
        }
    }
}
