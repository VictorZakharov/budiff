using System.Collections.Generic;
using System.Linq;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;

namespace Budiff.Avalonia;

// Find in the diff (Ctrl+F): searches ONE side's text - the side the
// pointer is over when Ctrl+F is pressed, else the side of the selection,
// else the new side (a single-pane file: its one side). Every match on that
// side is marked; the current one is selected (so Ctrl+C copies it) and
// scrolled to, a collapsed gap around it opening. Enter / F3 go to the next,
// Shift+Enter / Shift+F3 to the previous; Esc closes. Plain text, any case,
// collapsed lines included.
public partial class DiffView
{
    private readonly List<(int Line, int From, int To)> _found = new();
    private readonly Dictionary<int, List<(int From, int To)>> _foundByLine = new();
    private IReadOnlyList<DiffRow>? _foundRows;
    private bool _findNewSide = true;
    private int _foundCurrent = -1;
    private bool? _pointerSide;
    private bool _researchPosted;
    private TopLevel? _keyRoot;

    public bool IsFindOpen => FindBar.IsVisible;

    // The side being searched: true = new.
    public bool FindNewSide => _findNewSide;

    public int FindMatchCount => _found.Count;

    // Which match is current (0-based); -1 with none.
    public int FindCurrent => _foundCurrent;

    private void InitFind()
    {
        AddHandler(KeyDownEvent, OnFindKeyDown, RoutingStrategies.Tunnel);

        // The keyboard usually stays in the bird's-eye: Ctrl+F anywhere in
        // the window is the diff's while the pointer is over it.
        AttachedToVisualTree += (_, _) =>
        {
            _keyRoot = TopLevel.GetTopLevel(this);
            _keyRoot?.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _keyRoot?.RemoveHandler(KeyDownEvent, OnRootKeyDown);
            _keyRoot = null;
        };
        RowsHost.AddHandler(PointerMovedEvent, (_, e) => _pointerSide = SideUnder(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        RowsHost.PointerExited += (_, _) => _pointerSide = null;
        RowsHost.ContainerPrepared += (_, e) => ApplyMarks(e.Container);
        FindBox.TextChanged += (_, _) => Search();
        FindBox.AddHandler(KeyDownEvent, OnFindBoxKeyDown, RoutingStrategies.Tunnel);
        FindNextButton.Click += (_, _) => FindNext(forward: true);
        FindPreviousButton.Click += (_, _) => FindNext(forward: false);
        FindCloseButton.Click += (_, _) => CloseFind();
    }

    // Opens the find bar on a side (null: as Ctrl+F picks it).
    public void OpenFind(bool? newSide = null)
    {
        if (_model is not { IsPlaceholder: false, IsPreviewActive: false } model)
        {
            return;
        }

        _findNewSide = newSide ?? _pointerSide ?? Selection?.NewSide ?? DefaultSide(model);

        // A one-line selection is what to look for.
        if (Selection is { } selection && selection.Start.Line == selection.End.Line && SelectedText is { Length: > 0 } picked)
        {
            FindBox.Text = picked;
        }

        FindBar.IsVisible = true;
        FindSide.Text = _findNewSide ? "New" : "Old";
        Search();
        FindBox.SelectAll();
        Dispatcher.UIThread.Post(() => FindBox.Focus(), DispatcherPriority.Input);
    }

    public void CloseFind()
    {
        FindBar.IsVisible = false;
        ClearFound();
        ApplyMarks();
        Focus();
    }

    public void FindNext(bool forward)
    {
        if (_found.Count == 0)
        {
            return;
        }

        _foundCurrent = (_foundCurrent + (forward ? 1 : _found.Count - 1)) % _found.Count;
        ShowCurrent();
    }

    // A single-pane file has one side to search; a whole deletion's is old.
    private static bool DefaultSide(DiffViewModel model)
        => !model.IsSinglePane || model.AllRows.Any(r => r.NewNo is not null);

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsFindGesture(e) && IsEffectivelyVisible && (IsPointerOver || IsKeyboardFocusWithin)
            && _model is { IsPlaceholder: false, IsPreviewActive: false })
        {
            OpenFind();
            e.Handled = true;
        }
    }

    private static bool IsFindGesture(KeyEventArgs e)
        => e.Key == Key.F && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta);

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsFindGesture(e))
        {
            OpenFind();
            e.Handled = _model is { IsPlaceholder: false, IsPreviewActive: false };
        }
        else if (e.Key == Key.F3 && IsFindOpen)
        {
            FindNext(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    // Every match of the box's text on the searched side; the current one is
    // the first at or after the top of the view (or the one before, kept).
    private void Search()
    {
        _researchPosted = false;
        var keep = _foundCurrent >= 0 && _foundCurrent < _found.Count ? _found[_foundCurrent] : ((int, int, int)?)null;
        ClearFound();
        var query = FindBox.Text ?? string.Empty;
        if (!FindBar.IsVisible || query.Length == 0 || _model is not { } model)
        {
            ShowCount();
            ApplyMarks();
            return;
        }

        _foundRows = model.AllRows;
        var lines = SideLines(model.AllRows, _findNewSide);
        for (var line = 1; line <= lines.Count; line++)
        {
            var text = lines[line - 1];
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            for (var at = text.IndexOf(query, System.StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, System.StringComparison.OrdinalIgnoreCase))
            {
                _found.Add((line, at, at + query.Length));
                if (!_foundByLine.TryGetValue(line, out var marks))
                {
                    _foundByLine[line] = marks = new List<(int, int)>();
                }

                marks.Add((at, at + query.Length));
            }
        }

        if (_found.Count > 0)
        {
            var from = keep is { } k ? (k.Item1, k.Item2) : (TopLine(), 0);
            _foundCurrent = _found.FindIndex(f => f.Line > from.Item1 || (f.Line == from.Item1 && f.From >= from.Item2));
            if (_foundCurrent < 0)
            {
                _foundCurrent = 0;
            }

            ShowCurrent();
        }

        ShowCount();
        ApplyMarks();
    }

    private void ClearFound()
    {
        _found.Clear();
        _foundByLine.Clear();
        _foundRows = null;
        _foundCurrent = -1;
    }

    private void ShowCount()
        => FindCount.Text = (FindBox.Text ?? string.Empty).Length == 0 ? string.Empty
            : _found.Count == 0 ? "No results"
            : $"{_foundCurrent + 1} of {_found.Count}";

    // Selects the current match and brings its row into view.
    private void ShowCurrent()
    {
        if (_model is not { } model || _foundCurrent < 0)
        {
            return;
        }

        var (line, from, to) = _found[_foundCurrent];
        ShowCount();
        SelectText(_findNewSide, new TextSpot(line, from), new TextSpot(line, to));
        var index = RowIndexOf(model, line);
        if (index is null)
        {
            return;
        }

        if (RowTop(index.Value) is { } top && RowsHost.ContainerFromIndex(index.Value) is { } container)
        {
            var viewport = RowsScroller.Viewport.Height;
            var offset = RowsScroller.Offset.Y;
            if (top < offset + RowHeight || top + container.Bounds.Height > offset + viewport - (2 * RowHeight))
            {
                RowsScroller.Offset = RowsScroller.Offset.WithY(System.Math.Max(0, top - (viewport / 3)));
            }
        }

        ApplyMarks();
    }

    // The shown row of a line of the searched side; a collapsed one first
    // opens its gap.
    private int? RowIndexOf(DiffViewModel model, int line)
    {
        int? Find()
        {
            for (var i = 0; i < model.Rows.Count; i++)
            {
                if (!model.Rows[i].IsGap && LineNo(model.Rows[i], _findNewSide) == line)
                {
                    return i;
                }
            }

            return null;
        }

        if (Find() is { } shown)
        {
            return shown;
        }

        var all = model.AllRows;
        var target = -1;
        for (var i = 0; i < all.Count; i++)
        {
            if ((_findNewSide ? all[i].NewNo : all[i].OldNo) == line)
            {
                target = i;
                break;
            }
        }

        var gap = model.Rows.Where(r => r.IsGap && r.GapRunStart <= target).Select(r => (DiffRowVm?)r).LastOrDefault();
        if (target < 0 || gap is null)
        {
            return null;
        }

        model.ExpandGap(gap.GapRunStart);
        return Find();
    }

    // The first line of the searched side at the top of the view.
    private int TopLine()
    {
        if (TopRow() is { } top && _model is { } model)
        {
            for (var i = model.Rows.IndexOf(top); i >= 0 && i < model.Rows.Count; i++)
            {
                if (LineNo(model.Rows[i], _findNewSide) is { } line)
                {
                    return line;
                }
            }
        }

        return 1;
    }

    private bool? SideUnder(PointerEventArgs e)
        => (e.Source as Visual)?.FindAncestorOfType<CodeClip>(includeSelf: true) is { DataContext: DiffRowVm row } clip ? SideOf(clip, row) : null;

    private void ApplyMarks()
    {
        foreach (var container in RowsHost.GetRealizedContainers())
        {
            ApplyMarks(container);
        }
    }

    private void ApplyMarks(Control container)
    {
        if (container.DataContext is not DiffRowVm row)
        {
            return;
        }

        var current = _model is { } model && ReferenceEquals(_foundRows, model.AllRows);

        // Another file (or a reload) while the bar is open: search it again.
        if (!current && IsFindOpen && !_researchPosted && (FindBox.Text ?? string.Empty).Length > 0)
        {
            _researchPosted = true;
            _foundCurrent = -1;
            Dispatcher.UIThread.Post(Search, DispatcherPriority.Background);
        }

        foreach (var clip in container.GetVisualDescendants().OfType<CodeClip>())
        {
            if (clip.Child is not TextBlock text)
            {
                continue;
            }

            IReadOnlyList<(int From, int To)>? marks = null;
            if (current && SideOf(clip, row) == _findNewSide && LineNo(row, _findNewSide) is { } line)
            {
                marks = _foundByLine.GetValueOrDefault(line);
            }

            DiffInlines.SetMarks(text, marks);
        }
    }
}
