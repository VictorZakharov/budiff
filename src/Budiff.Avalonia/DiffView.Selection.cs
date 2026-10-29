using System.Collections.Generic;
using System.Linq;
using System.Text;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using global::Avalonia.VisualTree;

namespace Budiff.Avalonia;

// Text selection in the line diff: drag over the code of one side (old or
// new) to select it line to line - the other side never joins in - then
// Ctrl+C copies it. A double-click selects a word, a triple-click the line,
// Shift+click extends, Ctrl+A selects the whole side last clicked, Esc clears.
// The selection is kept in line numbers of its side, so it survives
// recycled rows, scrolling and gap expansion; the copy takes the lines from
// the whole diff, collapsed ones included.
public partial class DiffView
{
    // A place in one side's text: line number (1-based) and character.
    public readonly record struct TextSpot(int Line, int Char) : System.IComparable<TextSpot>
    {
        public int CompareTo(TextSpot other) => Line != other.Line ? Line.CompareTo(other.Line) : Char.CompareTo(other.Char);
    }

    private IReadOnlyList<DiffRow>? _selRows;
    private bool _selNewSide;
    private TextSpot _selAnchor;
    private TextSpot _selCaret;
    private bool _selecting;

    // The selection: its side and its ends in order; null when nothing is selected.
    public (bool NewSide, TextSpot Start, TextSpot End)? Selection
        => _model is { } model && ReferenceEquals(_selRows, model.AllRows) && _selAnchor != _selCaret
            ? (_selNewSide, Min(_selAnchor, _selCaret), Max(_selAnchor, _selCaret))
            : null;

    private void InitSelection()
    {
        RowsHost.AddHandler(PointerPressedEvent, OnCodePointerPressed, RoutingStrategies.Tunnel);
        RowsHost.AddHandler(PointerMovedEvent, OnCodePointerMoved, RoutingStrategies.Tunnel);
        RowsHost.AddHandler(PointerReleasedEvent, OnCodePointerReleased, RoutingStrategies.Tunnel);
        RowsHost.AddHandler(PointerCaptureLostEvent, (_, _) => _selecting = false);
        RowsHost.ContainerPrepared += (_, e) => ApplySelection(e.Container);
        AddHandler(KeyDownEvent, OnSelectionKeyDown, RoutingStrategies.Tunnel);
    }

    // The selected text, lines joined with newlines; empty with no selection.
    public string SelectedText
    {
        get
        {
            if (Selection is not { } selection || _model is null)
            {
                return string.Empty;
            }

            var lines = SideLines(_model.AllRows, selection.NewSide);
            var text = new StringBuilder();
            for (var line = selection.Start.Line; line <= selection.End.Line && line <= lines.Count; line++)
            {
                var full = lines[line - 1] ?? string.Empty;
                var from = line == selection.Start.Line ? System.Math.Min(selection.Start.Char, full.Length) : 0;
                var to = line == selection.End.Line ? System.Math.Min(selection.End.Char, full.Length) : full.Length;
                if (line > selection.Start.Line)
                {
                    text.Append('\n');
                }

                text.Append(full, from, System.Math.Max(0, to - from));
            }

            return text.ToString();
        }
    }

    public void SelectText(bool newSide, TextSpot anchor, TextSpot caret)
    {
        _selRows = _model?.AllRows;
        _selNewSide = newSide;
        _selAnchor = anchor;
        _selCaret = caret;
        ApplySelection();
    }

    public void ClearSelection()
    {
        _selRows = null;
        ApplySelection();
    }

    public bool CopySelection()
    {
        var text = SelectedText;
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        _ = clipboard.SetTextAsync(text);
        return true;
    }

    private void OnSelectionKeyDown(object? sender, KeyEventArgs e)
    {
        // The find box's own keys (copy, select all, Esc) are its own.
        if (e.Source is Visual source && FindBar.IsVisualAncestorOf(source))
        {
            return;
        }

        var command = e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta;
        if (command && e.Key is Key.C or Key.Insert && Selection is not null)
        {
            e.Handled = CopySelection();
        }
        else if (command && e.Key == Key.A && _selRows is not null && _model is { } model)
        {
            var lines = SideLines(model.AllRows, _selNewSide);
            if (lines.Count > 0)
            {
                SelectText(_selNewSide, new TextSpot(1, 0), new TextSpot(lines.Count, (lines[^1] ?? string.Empty).Length));
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && Selection is not null)
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    private void OnCodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(RowsHost);
        if (!point.Properties.IsLeftButtonPressed || _model is not { } model
            || (e.Source as Visual)?.FindAncestorOfType<CodeClip>(includeSelf: true) is not { } clip
            || SpotAt(clip, e) is not { } hit)
        {
            return;
        }

        var lineText = LineText(model, hit.NewSide, hit.Spot.Line);
        if (e.ClickCount >= 3)
        {
            SelectText(hit.NewSide, new TextSpot(hit.Spot.Line, 0), new TextSpot(hit.Spot.Line, lineText.Length));
        }
        else if (e.ClickCount == 2)
        {
            var (from, to) = WordAt(lineText, hit.Spot.Char);
            SelectText(hit.NewSide, new TextSpot(hit.Spot.Line, from), new TextSpot(hit.Spot.Line, to));
        }
        else
        {
            var extend = e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selRows is not null
                && ReferenceEquals(_selRows, model.AllRows) && _selNewSide == hit.NewSide;
            SelectText(hit.NewSide, extend ? _selAnchor : hit.Spot, hit.Spot);
            _selecting = true;
            e.Pointer.Capture(RowsHost);
        }

        // Ctrl+C needs the keyboard here.
        Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    private void OnCodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_selecting || _model is null)
        {
            return;
        }

        // Past the top or bottom: scroll along.
        var inScroller = e.GetPosition(RowsScroller);
        var viewport = RowsScroller.Viewport.Height;
        if (inScroller.Y < 0 || inScroller.Y > viewport)
        {
            var dy = inScroller.Y < 0 ? inScroller.Y : inScroller.Y - viewport;
            RowsScroller.Offset = RowsScroller.Offset.WithY(System.Math.Max(0, RowsScroller.Offset.Y + dy));
        }

        if (ClipOnSideAt(e.GetPosition(RowsHost).Y) is { } clip && SpotAt(clip, e) is { } hit)
        {
            _selCaret = hit.Spot;
            ApplySelection();
        }

        e.Handled = true;
    }

    private void OnCodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // The selected side's code cell on the row at this height (clamped to
    // the realized rows, so dragging past the last line selects to it).
    private CodeClip? ClipOnSideAt(double y)
    {
        CodeClip? best = null;
        var bestDistance = double.MaxValue;
        foreach (var container in RowsHost.GetRealizedContainers())
        {
            if (!container.IsVisible || container.DataContext is not DiffRowVm { IsGap: false } row
                || container.TranslatePoint(default, RowsHost) is not { } at)
            {
                continue;
            }

            var distance = y < at.Y ? at.Y - y : y > at.Y + container.Bounds.Height ? y - at.Y - container.Bounds.Height : 0;
            if (distance >= bestDistance || LineNo(row, _selNewSide) is null)
            {
                continue;
            }

            if (container.GetVisualDescendants().OfType<CodeClip>().FirstOrDefault(c => c.IsEffectivelyVisible && SideOf(c, row) == _selNewSide) is { } clip)
            {
                best = clip;
                bestDistance = distance;
            }
        }

        return best;
    }

    // The side and place the pointer is over in a code cell.
    private (bool NewSide, TextSpot Spot)? SpotAt(CodeClip clip, PointerEventArgs e)
    {
        if (clip.DataContext is not DiffRowVm row || clip.Child is not TextBlock text || SideOf(clip, row) is not { } newSide
            || LineNo(row, newSide) is not { } line)
        {
            return null;
        }

        var indent = DiffInlines.GetIndent(text).Length;
        var p = e.GetPosition(text);
        var inText = new Point(p.X - text.Padding.Left, System.Math.Max(0, p.Y - text.Padding.Top));
        var column = inText.X < 0
            ? (clip.IndentWidth > 0 && e.GetPosition(clip).X >= clip.IndentWidth / 2 ? indent : 0)
            : indent + text.TextLayout.HitTestPoint(inText).TextPosition;
        if (inText.Y > text.TextLayout.Height)
        {
            column = indent + TextLength(text);
        }

        return (newSide, new TextSpot(line, column));
    }

    // Which side a code cell shows for its row: true = new.
    private static bool? SideOf(CodeClip clip, DiffRowVm row)
        => clip.Classes.Contains("old") ? false
            : clip.Classes.Contains("new") ? true
            : clip.Classes.Contains("single") ? row.NewNo is not null
            : null;

    private static int? LineNo(DiffRowVm row, bool newSide) => newSide ? row.Source?.NewNo : row.Source?.OldNo;

    private void ApplySelection()
    {
        foreach (var container in RowsHost.GetRealizedContainers())
        {
            ApplySelection(container);
        }
    }

    private void ApplySelection(Control container)
    {
        if (container.DataContext is not DiffRowVm row)
        {
            return;
        }

        var selection = Selection;
        foreach (var clip in container.GetVisualDescendants().OfType<CodeClip>())
        {
            if (clip.Child is not SelectableTextBlock text)
            {
                continue;
            }

            (int, int) range = default;
            if (selection is { } s && SideOf(clip, row) == s.NewSide && LineNo(row, s.NewSide) is { } line
                && line >= s.Start.Line && line <= s.End.Line)
            {
                var from = line == s.Start.Line ? s.Start.Char : 0;
                var to = line == s.End.Line ? s.End.Char : int.MaxValue;
                range = (from, to);
            }

            DiffInlines.SetSelection(text, range);
        }
    }

    private static int TextLength(TextBlock text)
        => text.Inlines?.OfType<global::Avalonia.Controls.Documents.Run>().Sum(r => r.Text?.Length ?? 0) ?? 0;

    private static string LineText(DiffViewModel model, bool newSide, int line)
    {
        var lines = SideLines(model.AllRows, newSide);
        return line >= 1 && line <= lines.Count ? lines[line - 1] ?? string.Empty : string.Empty;
    }

    private static List<string?> SideLines(IReadOnlyList<DiffRow> rows, bool newSide)
    {
        var lines = new List<string?>();
        foreach (var row in rows)
        {
            if ((newSide ? row.NewNo : row.OldNo) is { } no)
            {
                while (lines.Count < no)
                {
                    lines.Add(null);
                }

                lines[no - 1] = newSide ? row.NewText : row.OldText;
            }
        }

        return lines;
    }

    // The word (letters, digits, _) or the run of other non-blank characters
    // around a character.
    internal static (int From, int To) WordAt(string text, int index)
    {
        if (text.Length == 0)
        {
            return (0, 0);
        }

        index = System.Math.Clamp(index, 0, text.Length - 1);
        static int Kind(char c) => char.IsLetterOrDigit(c) || c == '_' ? 0 : char.IsWhiteSpace(c) ? 1 : 2;
        var kind = Kind(text[index]);
        var from = index;
        var to = index + 1;
        while (from > 0 && Kind(text[from - 1]) == kind)
        {
            from--;
        }

        while (to < text.Length && Kind(text[to]) == kind)
        {
            to++;
        }

        return (from, to);
    }

    private static TextSpot Min(TextSpot a, TextSpot b) => a.CompareTo(b) <= 0 ? a : b;

    private static TextSpot Max(TextSpot a, TextSpot b) => a.CompareTo(b) >= 0 ? a : b;
}
