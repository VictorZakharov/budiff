using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

public sealed class DiffViewSnapshotTests
{
    // #48: a wholly-added (new file) diff renders single-pane - only the right
    // side, full width; a mixed diff stays two-pane.
    [AvaloniaFact]
    public void DiffView_NewFile_RendersSinglePane()
    {
        var model = new DiffViewModel { Title = "New.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(string.Empty, "public class New\n{\n    public int Value => 1;\n}\n"));
        Assert.True(model.IsSinglePane);
        // Every row is Added - the missing old side contributes no lines.
        Assert.All(model.Rows, r => Assert.Equal(DiffKind.Added, r.Kind));

        // A wholly new file is not painted green line by line: a solid green
        // gutter bar marks its lines instead.
        var marker = Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(model.Rows.First(r => r.Kind == DiffKind.Added).SingleMarker);
        Assert.Equal(0xFF, marker.Color.A);
        Assert.True(marker.Color.G > marker.Color.R, "green");

        // New-side blame (#51): the single-pane blame cell must carry the
        // commit-details tooltip too (#54 - it used to exist only two-pane).
        var ada = new BlameEntry("Ada Lovelace 10d", "abc1234", "Ada Lovelace", "ada@example.com", "2026-06-22 10:00", "Add New.cs");
        model.SetBlame(new[] { ada, ada, ada, ada }, forNewSide: true);

        var window = new Window
        {
            Width = 920,
            Height = 300,
            Content = new DiffView { DataContext = model },
        };
        var snapshot = UiSnapshot.Capture(window, "diff-view-single");
        Assert.True(new FileInfo(snapshot.Path).Length > 1000);
        AssertBlameCellHasTooltip(window);

        // Mixed diff: back to two panes - tooltip present there as well.
        model.SetDiff(LineDiff.Compute("a\nb\n", "a\nc\n"));
        Assert.False(model.IsSinglePane);
        model.SetBlame(new[] { ada, ada });
        UiSnapshot.Pump();
        AssertBlameCellHasTooltip(window);
        window.Close();
    }

    private static void AssertBlameCellHasTooltip(Window window)
    {
        var cells = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Where(t => t.Classes.Contains("blameCell") && t.IsEffectivelyVisible)
            .OrderBy(t => global::Avalonia.VisualExtensions.TranslatePoint(t, default, window)?.Y)
            .ToList();
        Assert.NotEmpty(cells);
        Assert.All(cells, cell => Assert.NotNull(ToolTip.GetTip(cell)));

        // Open it for real on the top row (rows are not in screen order in the
        // tree): the template content must bind the row's commit details.
        var cell = cells[0];
        ToolTip.SetIsOpen(cell, true);
        UiSnapshot.Pump();
        var tip = Assert.IsAssignableFrom<global::Avalonia.Controls.Control>(ToolTip.GetTip(cell));
        var texts = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tip)
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains("abc1234", texts);
        Assert.Contains("Ada Lovelace", texts);
        Assert.Contains("ada@example.com", texts);
        ToolTip.SetIsOpen(cell, false);
        UiSnapshot.Pump();
    }

    // Regression for the Collapse-unchanged freeze (#45): the rows host is
    // virtualized, so a large diff must realize only the viewport's rows, and
    // toggling collapse must not re-realize one container per line.
    [AvaloniaFact]
    public void DiffView_VirtualizesLargeDiffs()
    {
        var oldText = new StringBuilder();
        var newText = new StringBuilder();
        for (var i = 0; i < 3000; i++)
        {
            oldText.AppendLine($"line {i} value = {i};");
            newText.AppendLine(i == 1500 ? $"line {i} value = {i + 1};" : $"line {i} value = {i};");
        }

        var model = new DiffViewModel { Title = "Big.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(oldText.ToString(), newText.ToString()));
        model.CollapseUnchanged = false;

        var window = new Window
        {
            Width = 920,
            Height = 520,
            Content = new DiffView { DataContext = model },
        };
        window.Show();
        UiSnapshot.Pump();

        Assert.True(model.Rows.Count > 2900);
        var host = ((DiffView)window.Content!).FindControl<ItemsControl>("RowsHost")!;
        var realized = Enumerable.Range(0, model.Rows.Count).Count(i => host.ContainerFromIndex(i) is not null);
        Assert.InRange(realized, 1, 200);

        // The toggle that used to freeze: still only viewport-sized realization.
        model.CollapseUnchanged = true;
        UiSnapshot.Pump();
        Assert.True(model.Rows.Count < 100);

        // Scrolled capture: with LOGICAL scrolling the funnel gutter must
        // anchor its geometry on realized containers, not the host position.
        model.CollapseUnchanged = false;
        UiSnapshot.Pump();
        var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<ScrollViewer>().First();
        scroller.Offset = new global::Avalonia.Vector(0, 1490 * 18);
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "diff-view-scrolled");
        window.Close();
    }

    // Toggling wrap mid-file must not rebuild the rows on screen or move the
    // reader: it re-realized every row (~300ms) and jumped ~15 lines. Long
    // lines also shift together sideways, and wrap for real.
    [AvaloniaFact]
    public void WrapToggle_MidFile_KeepsTheRows()
    {
        var oldText = new StringBuilder();
        var newText = new StringBuilder();
        for (var i = 0; i < 600; i++)
        {
            var line = i % 7 == 0
                ? $"    var value{i} = Compute(alpha, beta, gamma, delta, epsilon, zeta, eta, theta, iota, kappa, lambda) + {i};"
                : $"    var value{i} = {i};";
            oldText.AppendLine(line);
            newText.AppendLine(i % 40 == 0 ? line + " // changed" : line);
        }

        var model = new DiffViewModel { Title = "Wide.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(oldText.ToString(), newText.ToString()));
        model.CollapseUnchanged = false;
        model.WrapLines = false;
        var window = new Window { Width = 1000, Height = 600, Content = new DiffView { DataContext = model } };
        var view = (DiffView)window.Content!;
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        window.Show();
        UiSnapshot.Pump();
        var scroller = view.FindControl<ScrollViewer>("RowsScroller")!;
        scroller.Offset = new global::Avalonia.Vector(0, scroller.Extent.Height / 2);
        UiSnapshot.Pump();
        UiSnapshot.Pump();

        // The row at the top of the viewport: the reader's place.
        int TopRow() => Enumerable.Range(0, model.Rows.Count)
            .Where(i => host.ContainerFromIndex(i) is { } c && global::Avalonia.VisualExtensions.TranslatePoint(c, new global::Avalonia.Point(0, c.Bounds.Height - 1), scroller) is { Y: >= 0 })
            .Min();

        // Each toggle keeps the same line at the top and the rows it has;
        // unwrapping may realize the few more rows that now fit.
        var prepared = 0;
        host.ContainerPrepared += (_, _) => prepared++;
        for (var i = 0; i < 4; i++)
        {
            var top = TopRow();
            prepared = 0;
            model.WrapLines = !model.WrapLines;
            UiSnapshot.Pump();
            UiSnapshot.Pump();
            Assert.Equal(top, TopRow());
            Assert.InRange(prepared, 0, 8);
        }

        // Not wrapping: scrolled sideways, every line moves by the same amount.
        Assert.False(model.WrapLines);
        Assert.True(model.MaxScroll > 0);
        model.HorizontalScroll = Math.Min(200, model.MaxScroll);
        UiSnapshot.Pump();
        var shifts = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<CodeClip>()
            .Where(c => c.IsEffectivelyVisible && c.Child is { } child && child.Bounds.Width > 0)
            .Select(c => Math.Round(c.Child!.Bounds.X - c.IndentWidth))
            .Distinct()
            .ToList();
        Assert.Equal(new[] { -model.HorizontalScroll }, shifts);
        UiSnapshot.Capture(window, "diff-view-hscroll");
        model.WrapLines = true;
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "diff-view-wrapped");

        // Wrapped: the long lines take two lines or more instead of being
        // cut. Counted in laid-out lines, not pixels: the line height depends
        // on the monospace font installed (CI's Linux has another).
        int Lines() => global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<CodeClip>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => (c.Child as TextBlock)?.TextLayout.TextLines.Count ?? 0)
            .Max();
        Assert.True(Lines() >= 2, $"most lines in a wrapped cell: {Lines()}");
        model.WrapLines = false;
        UiSnapshot.Pump();
        Assert.Equal(1, Lines());
        window.Close();
    }

    // A recycled row keeps its controls and only rebinds: building a row
    // costs ~7ms, and reaching the top rebuilt every row on screen (~330ms).
    [AvaloniaFact]
    public void Scrolling_ReusesRowControls()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            text.AppendLine($"line {i} value = {i};");
        }

        var model = new DiffViewModel { Title = "Long.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(text.ToString(), text.ToString().Replace("line 5 ", "line five ", StringComparison.Ordinal)));
        model.CollapseUnchanged = false;
        var window = new Window { Width = 1000, Height = 600, Content = new DiffView { DataContext = model } };
        window.Show();
        UiSnapshot.Pump();
        var view = (DiffView)window.Content!;
        var scroller = view.FindControl<ScrollViewer>("RowsScroller")!;

        var seen = new System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, object>();
        int NewTextBlocks()
        {
            var fresh = 0;
            foreach (var block in global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>())
            {
                if (!seen.TryGetValue(block, out _))
                {
                    seen.Add(block, new object());
                    fresh++;
                }
            }

            return fresh;
        }

        void ScrollTo(double y)
        {
            scroller.Offset = new global::Avalonia.Vector(0, y);
            UiSnapshot.Pump();
            UiSnapshot.Pump();
        }

        // Warm up: down a page and back, so the recycle pool has rows.
        ScrollTo(18 * 60);
        ScrollTo(0);
        NewTextBlocks();

        // Down three rows and back to the top, a page down, then far away:
        // every row shown reuses an existing one.
        foreach (var y in new[] { 54.0, 0, 18 * 60, 0, 18 * 1500, 0 })
        {
            ScrollTo(y);
            Assert.Equal(y, scroller.Offset.Y);
            Assert.Equal(0, NewTextBlocks());
        }

        // A few rows down and back to the very top: only the rows coming into
        // view take a new line. The panel used to treat reaching offset 0 as
        // a jump and rebind every row on screen.
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        foreach (var step in new[] { 54.0, 18, 90 })
        {
            ScrollTo(step);
            var shown = host.ItemsPanelRoot!.Children.Where(c => c.IsVisible).ToDictionary(c => c, c => c.DataContext);
            ScrollTo(0);
            var rebound = shown.Count(pair => pair.Key.IsVisible && !ReferenceEquals(pair.Key.DataContext, pair.Value));
            Assert.InRange(rebound, 0, (int)(step / 18) + 1);
        }

        window.Close();
    }

    // Blame loads alongside the diff and can land first; the "Loading diff..."
    // tick that follows must not drop it (a slow diff left the column blank).
    [AvaloniaFact]
    public void BlameThatLandsBeforeASlowDiff_IsKept()
    {
        var model = new DiffViewModel { Title = "Slow.cs", Extension = ".cs" };
        using var gate = new System.Threading.ManualResetEventSlim();
        try
        {
            model.BeginLoad(() =>
            {
                gate.Wait(5000);
                return DiffResult.FromRows(LineDiff.Compute("a\nb\n", "a\nc\n"));
            });
            var ada = new BlameEntry("Ada Lovelace 10d", "abc1234", "Ada Lovelace", "ada@example.com", "2026-06-22 10:00", "msg");
            model.SetBlame(new[] { ada, ada });

            // The loading indicator's tick (headless runs no timers).
            model.ShowLoadingIndicator();
            Assert.Equal("Loading diff...", model.Placeholder);
        }
        finally
        {
            gate.Set();
        }

        var until = System.Diagnostics.Stopwatch.StartNew();
        while (model.IsLoading && until.ElapsedMilliseconds < 5000)
        {
            System.Threading.Thread.Sleep(10);
            UiSnapshot.Pump();
        }

        Assert.False(model.IsLoading);
        Assert.Equal("Ada Lovelace 10d", model.Rows.First(r => r.OldNo == "1").Blame?.Label);
    }

    [AvaloniaFact]
    public void DiffView_RendersSideBySideRows()
    {
        var model = new DiffViewModel
        {
            Title = "Sample.cs",
            Subtitle = "HEAD vs working tree",
            Extension = ".cs",
        };
        model.SetDiff(LineDiff.Compute(
            "public class Sample\n{\n    public int Count => 1;\n}\n",
            "public class Sample\n{\n    public int Count => 2;\n    public string Name => \"budiff\";\n}\n"));

        Assert.Equal(6, model.Rows.Count);
        Assert.Contains(model.Rows, row => row.Kind == DiffKind.Modified);
        Assert.Contains(model.Rows, row => row.Kind == DiffKind.Added);

        // Blame visible in this capture: the funnel must sit between the code
        // halves (offset by the blame column), not at the row's center.
        var ada = new BlameEntry("Ada Lovelace 10d", "abc1234", "Ada Lovelace", "ada@example.com", "2026-06-22 10:00", "Scaffold the sample");
        model.SetBlame(new[] { ada, ada, ada, ada });

        var window = new Window
        {
            Width = 920,
            Height = 520,
            Content = new DiffView { DataContext = model },
        };

        var snapshot = UiSnapshot.Capture(window, "diff-view");

        Assert.Equal(TestScale.Px(920), snapshot.Width);
        Assert.Equal(TestScale.Px(520), snapshot.Height);
        Assert.True(new FileInfo(snapshot.Path).Length > 1000);
    }
}
