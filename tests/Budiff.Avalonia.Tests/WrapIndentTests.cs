using System;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Budiff;
using Budiff.Avalonia.TextMate;
using Xunit;

namespace Budiff.Avalonia.Tests;

// A changed line with a deep leading indent (an HTML attribute aligned under
// its element) used to wrap at the indent: the layout broke after the
// whitespace run, so the first visual line was blank and the code started at
// column 0 on the next one. The indent is now the cell's hanging indent - the
// code starts where its neighbours' does, and wrapped lines continue under it.
public sealed class WrapIndentTests
{
    private const string Indent = "                                        ";

    [AvaloniaFact]
    public void IndentedChangedLine_KeepsItsIndent_WhenItWraps()
    {
        var (window, view, model) = Open();
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        var rows = model.Rows.ToList();
        var changed = rows.FindIndex(r => r.Kind == DiffKind.Modified);
        var below = rows.FindIndex(r => r.NewText?.Contains("[state]", StringComparison.Ordinal) == true);
        Assert.True(changed > 0 && below > changed);
        UiSnapshot.Capture(window, "wrap-indent");

        foreach (var side in new[] { 0, 1 })
        {
            var line = Code(host, changed, window, side);
            var neighbour = Code(host, below, window, side);

            // The first visual line holds code, not just the indent.
            var firstText = FirstLine(line);
            Assert.False(string.IsNullOrWhiteSpace(firstText), $"side {side}: first visual line is blank");

            // The code starts at the neighbour's column.
            Assert.Equal(CodeStartX(neighbour, window), CodeStartX(line, window), 1.0);
        }

        // Every wrapped line of the old side continues under the indent.
        var old = Code(host, changed, window, 0);
        Assert.True(old.TextLayout.TextLines.Count > 1, "the long old line wraps");
        var left = old.TranslatePoint(new Point(old.Padding.Left, 0), window)!.Value.X;
        Assert.True(left >= CodeStartX(Code(host, below, window, 0), window) - 1.0, "continuation is under the indent");
        window.Close();
    }

    [AvaloniaFact]
    public void Indent_IsCappedSoANarrowPaneStillShowsCode()
    {
        var (window, view, model) = Open(width: 520);
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        var changed = model.Rows.ToList().FindIndex(r => r.Kind == DiffKind.Modified);
        var line = Code(host, changed, window, 0);
        var clip = (CodeClip)line.Parent!;
        Assert.True(line.Bounds.Width >= 0.3 * clip.Bounds.Width, $"code gets {line.Bounds.Width} of {clip.Bounds.Width}");
        Assert.False(string.IsNullOrWhiteSpace(FirstLine(line)));
        window.Close();
    }

    // A wrapped line's continuation lines carry a wrap mark in the line-number
    // gutter; a line that fits has none, and wrap off has no continuation lines.
    [AvaloniaFact]
    public void WrappedLines_ShowAWrapMarkInTheGutter()
    {
        var (window, view, model) = Open();
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        var rows = model.Rows.ToList();
        var changed = rows.FindIndex(r => r.Kind == DiffKind.Modified);
        var old = Code(host, changed, window, 0);
        var clip = (CodeClip)old.Parent!;
        Assert.True(clip.VisualLineCount > 1);

        // The gutter strip just left of the code, on the second visual line
        // (wrapped) and on the first (numbered, but right of the number).
        var lineHeight = old.TextLayout.TextLines[0].Height;
        var origin = clip.TranslatePoint(default, window)!.Value;
        Assert.True(InkIn(window, origin.X - 20, origin.Y + lineHeight + 2, 12, lineHeight - 4), "wrap mark on the continuation line");

        var plain = Code(host, rows.FindIndex(r => r.NewText?.Contains("[columns]", StringComparison.Ordinal) == true), window, 0);
        var plainOrigin = ((CodeClip)plain.Parent!).TranslatePoint(default, window)!.Value;
        Assert.Equal(1, ((CodeClip)plain.Parent!).VisualLineCount);
        Assert.False(InkIn(window, plainOrigin.X - 7, plainOrigin.Y + 2, 6, lineHeight - 4), "no mark beside a line that fits");

        model.WrapLines = false;
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        Assert.Equal(1, clip.VisualLineCount);
        window.Close();
    }

    // Whether anything brighter than the editor background is drawn in the
    // rectangle (window coordinates, DIPs).
    private static bool InkIn(Window window, double x, double y, double width, double height)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var bytes = new byte[4];
        for (var py = TestScale.Px(y); py < TestScale.Px(y + height); py++)
        {
            for (var px = TestScale.Px(x); px < TestScale.Px(x + width); px++)
            {
                System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (py * buffer.RowBytes) + (px * 4), bytes, 0, 4);
                if (bytes[0] + bytes[1] + bytes[2] > 3 * 0x60)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static string Fixture(bool changed)
    {
        var text = new StringBuilder();
        text.AppendLine("<section>");
        text.AppendLine("  @if (loadError) {");
        text.AppendLine("    <app-error [error]=\"loadError\"></app-error>");
        text.AppendLine("  }");
        text.AppendLine();
        text.AppendLine("  <app-orders-table [columns]=\"tableColumns\"");
        text.AppendLine(changed
            ? Indent + "[rows]=\"rows\""
            : Indent + "[rows]=\"tableRowsFromTheOrdersStore$ | async\"");
        text.AppendLine(Indent + "[state]=\"tableState\"");
        text.AppendLine(Indent + "[(selectedKey)]=\"selectedOrderKey\"");
        text.AppendLine(Indent + "(init)=\"onTableInitialized($event)\"");
        text.AppendLine(Indent + "(contentReady)=\"onTableContentReady($event)\"");
        text.AppendLine(Indent + "(cellEvent)=\"onTableCellEvent($event)\">");
        text.AppendLine("  </app-orders-table>");
        text.AppendLine("</section>");
        return text.ToString();
    }

    private static (Window Window, DiffView View, DiffViewModel Model) Open(double width = 1000)
    {
        var model = new DiffViewModel { Title = "orders.component.html", Extension = ".html", SyntaxHighlighter = new TextMateSyntaxHighlighter() };
        model.BeginLoad(() => DiffLoaderFor(Fixture(changed: false), Fixture(changed: true)));
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = width, Height = 420, Content = view };
        window.Show();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((model.IsHighlighting || model.Rows.Count == 0) && DateTime.UtcNow < deadline)
        {
            System.Threading.Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
        UiSnapshot.Pump();
        return (window, view, model);
    }

    private static DiffResult DiffLoaderFor(string oldText, string newText) => new(null, LineDiff.Compute(oldText, newText));

    // The code TextBlock of a row's old (0) or new (1) side, in screen order.
    internal static TextBlock Code(ItemsControl host, int index, Window window, int side)
    {
        var container = host.ContainerFromIndex(index) ?? throw new InvalidOperationException($"row {index} not realized");
        return container.GetVisualDescendants().OfType<CodeClip>()
            .Where(c => c.IsEffectivelyVisible)
            .OrderBy(c => c.TranslatePoint(default, window)!.Value.X)
            .Select(c => (TextBlock)c.Child!)
            .ElementAt(side);
    }

    internal static string Text(TextBlock block)
        => string.Concat(block.Inlines!.OfType<global::Avalonia.Controls.Documents.Run>().Select(r => r.Text));

    private static string FirstLine(TextBlock block)
    {
        var text = Text(block);
        return text.Substring(0, Math.Min(text.Length, block.TextLayout.TextLines[0].Length));
    }

    // Where the first non-blank character is drawn, in window coordinates.
    private static double CodeStartX(TextBlock block, Window window)
    {
        var text = Text(block);
        var index = text.Length - text.TrimStart().Length;
        var rect = block.TextLayout.HitTestTextPosition(index);
        return block.TranslatePoint(new Point(rect.X + block.Padding.Left, 0), window)!.Value.X;
    }
}
