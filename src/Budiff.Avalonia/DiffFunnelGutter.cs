using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Budiff;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Data;
using global::Avalonia.Input;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;
using global::Avalonia.Rendering;

namespace Budiff.Avalonia;

// Draws the change "funnels" between the code halves AND is the split handle:
// the gutter band is hit-testable (nothing else is, so the rows underneath keep
// their clicks), dragging it moves SplitRatio, double-click resets it.
public sealed class DiffFunnelGutter : Control, ICustomHitTest
{
    public const double GutterWidth = 18;

    // Extra grab slop either side of the 18px band.
    private const double GrabSlop = 2;

    // A single-line row's height. Wrapped rows are taller, so geometry comes
    // from the realized (on-screen) containers; rows outside them - which a
    // VIRTUALIZED host has no container for - extrapolate at the realized
    // rows' average pitch (only off-screen funnel ends land there).
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

    // The old side's share of the code width - the same weight DiffSplitGrid
    // gives its star columns, so the funnel and the rows' divider agree.
    public static readonly StyledProperty<double> SplitRatioProperty =
        AvaloniaProperty.Register<DiffFunnelGutter, double>(nameof(SplitRatio), 0.5, defaultBindingMode: BindingMode.TwoWay);

    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    // Row geometry from the last Render (sorted by index), for the funnel
    // ends and for hit testing between frames.
    private readonly List<RowExtent> _realized = new();
    private double _pitch = RowHeight;
    private double _hostTop;
    private readonly List<DiffRowVm> _rows = new();
    private bool _dragging;
    private double _grabOffset;

    public DiffFunnelGutter()
    {
        Cursor = ResizeCursor;
    }

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

    public double SplitRatio
    {
        get => GetValue(SplitRatioProperty);
        set => SetValue(SplitRatioProperty, value);
    }

    private double CodeWidth => Math.Max(0, Bounds.Width - BlameWidth - GutterWidth);

    private double GutterLeft => BlameWidth + (Math.Clamp(SplitRatio, DiffViewModel.MinSplitRatio, DiffViewModel.MaxSplitRatio) * CodeWidth);

    // Only the gutter band of a content row: a gap row's "N unchanged lines"
    // bar spans the gutter and must stay clickable.
    public bool HitTest(Point point)
    {
        if (!IsEffectivelyVisible || Bounds.Width <= GutterWidth)
        {
            return false;
        }

        if (_dragging)
        {
            return true;
        }

        var left = GutterLeft;
        if (point.X < left - GrabSlop || point.X > left + GutterWidth + GrabSlop)
        {
            return false;
        }

        if (_blocksDirty)
        {
            RebuildBlocks();
        }

        foreach (var row in _realized)
        {
            if (point.Y >= row.Top && point.Y < row.Bottom)
            {
                return row.Index < _rows.Count && !_rows[row.Index].IsGap;
            }
        }

        return false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            SetCurrentValue(SplitRatioProperty, 0.5);
            return;
        }

        // Keep the grab point under the pointer instead of snapping the
        // gutter's edge to it.
        _grabOffset = e.GetPosition(this).X - GutterLeft;
        _dragging = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || CodeWidth <= 0)
        {
            return;
        }

        var left = e.GetPosition(this).X - _grabOffset;
        var ratio = Math.Clamp((left - BlameWidth) / CodeWidth, DiffViewModel.MinSplitRatio, DiffViewModel.MaxSplitRatio);
        SetCurrentValue(SplitRatioProperty, ratio);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
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
                 || change.Property == BlameWidthProperty
                 || change.Property == SplitRatioProperty)
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

        var gutterLeft = GutterLeft;
        var gutterRight = gutterLeft + GutterWidth;
        var midX = gutterLeft + GutterWidth / 2;

        MeasureRealized(host);

        foreach (var block in _blocks)
        {
            var blockTop = RowTop(block.Start);
            var leftTop = block.LeftFirst >= 0 ? RowTop(block.LeftFirst) : blockTop;
            var leftBottom = block.LeftLast >= 0 ? RowBottom(block.LeftLast) : blockTop;
            var rightTop = block.RightFirst >= 0 ? RowTop(block.RightFirst) : blockTop;
            var rightBottom = block.RightLast >= 0 ? RowBottom(block.RightLast) : blockTop;

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

    private readonly record struct RowExtent(int Index, double Top, double Bottom);

    // The virtualized panel scrolls LOGICALLY (the host itself never moves),
    // so every row position is read off the realized containers.
    private void MeasureRealized(ItemsControl host)
    {
        _realized.Clear();
        foreach (var container in host.GetRealizedContainers())
        {
            var index = host.IndexFromContainer(container);
            if (index < 0 || !container.IsVisible || container.TranslatePoint(new Point(0, 0), this) is not { } top)
            {
                continue;
            }

            _realized.Add(new RowExtent(index, top.Y, top.Y + container.Bounds.Height));
        }

        _realized.Sort((a, b) => a.Index.CompareTo(b.Index));
        _hostTop = host.TranslatePoint(new Point(0, 0), this)?.Y ?? 0;
        _pitch = RowHeight;
        if (_realized.Count > 0)
        {
            var first = _realized[0];
            var last = _realized[^1];
            var pitch = (last.Bottom - first.Top) / (last.Index - first.Index + 1);
            _pitch = pitch > 0 ? pitch : RowHeight;
        }
    }

    private double RowTop(int index)
    {
        if (_realized.Count == 0)
        {
            return _hostTop + (index * _pitch);
        }

        var first = _realized[0];
        var last = _realized[^1];
        if (index <= first.Index)
        {
            return first.Top - ((first.Index - index) * _pitch);
        }

        if (index > last.Index)
        {
            return last.Bottom + ((index - last.Index - 1) * _pitch);
        }

        // Inside the realized range: exact, or just past the nearest realized
        // row above (a gap in realization is rare - e.g. a focused container).
        var at = FindRealized(index);
        if (at >= 0)
        {
            return _realized[at].Top;
        }

        var below = ~at - 1;
        return _realized[below].Bottom + ((index - _realized[below].Index - 1) * _pitch);
    }

    private double RowBottom(int index)
    {
        var at = _realized.Count == 0 ? -1 : FindRealized(index);
        return at >= 0 ? _realized[at].Bottom : RowTop(index + 1);
    }

    private int FindRealized(int index)
    {
        int lo = 0, hi = _realized.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var value = _realized[mid].Index;
            if (value == index)
            {
                return mid;
            }

            if (value < index)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ~lo;
    }

    private void RebuildBlocks()
    {
        _blocks.Clear();
        _rows.Clear();
        _blocksDirty = false;
        if (Rows is null)
        {
            return;
        }

        var rows = _rows;
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
