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

            _blocksDirty = true;
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

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _blocksDirty = true;
        InvalidateVisual();
    }

    private void OnHostLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    // One contiguous run of changed rows: everything Render needs that depends
    // only on the rows themselves, so scrolling repaints don't re-scan them.
    private readonly record struct ChangeBlock(
        int Start,
        int LeftFirst,
        int LeftLast,
        int RightFirst,
        int RightLast,
        DiffKind Kind);

    private readonly List<ChangeBlock> _blocks = new();
    private bool _blocksDirty = true;

    public override void Render(DrawingContext context)
    {
        if (Rows is null || ItemsHost is not { } host || Bounds.Width <= GutterWidth)
        {
            return;
        }

        if (_blocksDirty)
        {
            RebuildBlocks();
        }

        var gutterLeft = BlameWidth + (Bounds.Width - BlameWidth - GutterWidth) / 2;
        var gutterRight = gutterLeft + GutterWidth;
        var midX = gutterLeft + GutterWidth / 2;

        // Self-calibrating geometry: the virtualized panel scrolls LOGICALLY
        // (the host itself never moves), so derive row 0's y AND the actual
        // row pitch from realized containers instead of assuming either.
        var hostTop = host.TranslatePoint(new Point(0, 0), this)?.Y ?? 0;
        var pitch = RowHeight;
        (int Index, double Top)? first = null;
        (int Index, double Top)? last = null;
        foreach (var container in host.GetRealizedContainers())
        {
            var index = host.IndexFromContainer(container);
            if (index < 0 || container.TranslatePoint(new Point(0, 0), this) is not { } top)
            {
                continue;
            }

            if (first is null || index < first.Value.Index)
            {
                first = (index, top.Y);
            }

            if (last is null || index > last.Value.Index)
            {
                last = (index, top.Y);
            }
        }

        if (first is { } f)
        {
            if (last is { } l && l.Index > f.Index)
            {
                pitch = (l.Top - f.Top) / (l.Index - f.Index);
            }

            hostTop = f.Top - f.Index * pitch;
        }

        foreach (var block in _blocks)
        {
            var blockTop = hostTop + block.Start * pitch;
            var leftTop = block.LeftFirst >= 0 ? hostTop + block.LeftFirst * pitch : blockTop;
            var leftBottom = block.LeftLast >= 0 ? hostTop + (block.LeftLast + 1) * pitch : blockTop;
            var rightTop = block.RightFirst >= 0 ? hostTop + block.RightFirst * pitch : blockTop;
            var rightBottom = block.RightLast >= 0 ? hostTop + (block.RightLast + 1) * pitch : blockTop;

            var brush = block.Kind switch
            {
                DiffKind.Modified => ModifiedBrush,
                DiffKind.Added => AddedBrush,
                _ => RemovedBrush,
            };
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
        }
    }

    private void RebuildBlocks()
    {
        _blocks.Clear();
        _blocksDirty = false;
        if (Rows is null)
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

            var kind = modified || (added && removed) ? DiffKind.Modified : added ? DiffKind.Added : DiffKind.Removed;
            _blocks.Add(new ChangeBlock(i, leftFirst, leftLast, rightFirst, rightLast, kind));
            i = j;
        }
    }

    private static IBrush Brush(string hex) => HexBrush.Parse(hex);
}
