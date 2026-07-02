using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Budiff;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;

namespace Budiff.Avalonia;

public sealed class DiffFunnelGutter : Control
{
    private const double GutterWidth = 18;

    // Every diff row (content and gap) renders at this fixed height; geometry is
    // pure index math so it works with a VIRTUALIZED items host, where off-screen
    // rows have no container to measure.
    private const double RowHeight = 18;

    public static readonly StyledProperty<IEnumerable?> RowsProperty =
        AvaloniaProperty.Register<DiffFunnelGutter, IEnumerable?>(nameof(Rows));

    public static readonly StyledProperty<ItemsControl?> ItemsHostProperty =
        AvaloniaProperty.Register<DiffFunnelGutter, ItemsControl?>(nameof(ItemsHost));

    public static readonly StyledProperty<IBrush> AddedBrushProperty =
        AvaloniaProperty.Register<DiffFunnelGutter, IBrush>(nameof(AddedBrush), Brush("#5531C36B"));

    public static readonly StyledProperty<IBrush> RemovedBrushProperty =
        AvaloniaProperty.Register<DiffFunnelGutter, IBrush>(nameof(RemovedBrush), Brush("#55E05252"));

    public static readonly StyledProperty<IBrush> ModifiedBrushProperty =
        AvaloniaProperty.Register<DiffFunnelGutter, IBrush>(nameof(ModifiedBrush), Brush("#55E2C08D"));

    // Width of the blame column left of the code halves (0 when hidden): the
    // funnel sits at the center of the REMAINING width, not the full row.
    public static readonly StyledProperty<double> BlameWidthProperty =
        AvaloniaProperty.Register<DiffFunnelGutter, double>(nameof(BlameWidth));

    public IEnumerable? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public ItemsControl? ItemsHost
    {
        get => GetValue(ItemsHostProperty);
        set => SetValue(ItemsHostProperty, value);
    }

    public IBrush AddedBrush
    {
        get => GetValue(AddedBrushProperty);
        set => SetValue(AddedBrushProperty, value);
    }

    public IBrush RemovedBrush
    {
        get => GetValue(RemovedBrushProperty);
        set => SetValue(RemovedBrushProperty, value);
    }

    public IBrush ModifiedBrush
    {
        get => GetValue(ModifiedBrushProperty);
        set => SetValue(ModifiedBrushProperty, value);
    }

    public double BlameWidth
    {
        get => GetValue(BlameWidthProperty);
        set => SetValue(BlameWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldRows)
            {
                oldRows.CollectionChanged -= OnRowsChanged;
            }

            if (change.NewValue is INotifyCollectionChanged newRows)
            {
                newRows.CollectionChanged += OnRowsChanged;
            }

            InvalidateVisual();
        }
        else if (change.Property == ItemsHostProperty)
        {
            if (change.OldValue is ItemsControl oldHost)
            {
                oldHost.LayoutUpdated -= OnHostLayoutUpdated;
            }

            if (change.NewValue is ItemsControl newHost)
            {
                newHost.LayoutUpdated += OnHostLayoutUpdated;
            }

            InvalidateVisual();
        }
        else if (change.Property == AddedBrushProperty
                 || change.Property == RemovedBrushProperty
                 || change.Property == ModifiedBrushProperty
                 || change.Property == BlameWidthProperty)
        {
            InvalidateVisual();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private void OnHostLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (Rows is null || ItemsHost is not { } host || Bounds.Width <= GutterWidth)
        {
            return;
        }

        var rows = new List<DiffRowVm>();
        foreach (var item in Rows)
        {
            if (item is DiffRowVm row)
            {
                rows.Add(row);
            }
        }

        var gutterLeft = BlameWidth + (Bounds.Width - BlameWidth - GutterWidth) / 2;
        var gutterRight = gutterLeft + GutterWidth;
        var midX = gutterLeft + GutterWidth / 2;
        var hostTop = host.TranslatePoint(new Point(0, 0), this)?.Y ?? 0;

        var i = 0;
        while (i < rows.Count)
        {
            if (rows[i].IsGap || rows[i].Kind == DiffKind.Unchanged)
            {
                i++;
                continue;
            }

            int leftFirst = -1, leftLast = -1, rightFirst = -1, rightLast = -1;
            var added = false;
            var removed = false;
            var modified = false;
            var j = i;
            while (j < rows.Count && !rows[j].IsGap && rows[j].Kind != DiffKind.Unchanged)
            {
                var row = rows[j];
                if (row.OldNo is not null)
                {
                    if (leftFirst < 0)
                    {
                        leftFirst = j;
                    }

                    leftLast = j;
                }

                if (row.NewNo is not null)
                {
                    if (rightFirst < 0)
                    {
                        rightFirst = j;
                    }

                    rightLast = j;
                }

                modified |= row.Kind == DiffKind.Modified;
                added |= row.Kind == DiffKind.Added;
                removed |= row.Kind == DiffKind.Removed;
                j++;
            }

            var blockTop = hostTop + i * RowHeight;
            var leftTop = leftFirst >= 0 ? hostTop + leftFirst * RowHeight : blockTop;
            var leftBottom = leftLast >= 0 ? hostTop + (leftLast + 1) * RowHeight : blockTop;
            var rightTop = rightFirst >= 0 ? hostTop + rightFirst * RowHeight : blockTop;
            var rightBottom = rightLast >= 0 ? hostTop + (rightLast + 1) * RowHeight : blockTop;

            var brush = modified || (added && removed) ? ModifiedBrush : added ? AddedBrush : RemovedBrush;
            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(new Point(gutterLeft, leftTop), true);
                stream.CubicBezierTo(new Point(midX, leftTop), new Point(midX, rightTop), new Point(gutterRight, rightTop));
                stream.LineTo(new Point(gutterRight, rightBottom));
                stream.CubicBezierTo(new Point(midX, rightBottom), new Point(midX, leftBottom), new Point(gutterLeft, leftBottom));
                stream.EndFigure(true);
            }

            context.DrawGeometry(brush, null, geometry);
            i = j;
        }
    }

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}
