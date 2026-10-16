using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Budiff;
using Budiff.Avalonia.Markdown;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Back after following a link returns to where the reader was: the view
// keeps DiffViewModel.ScrollSpot current, the host hands it back as
// RestoreScroll, and the view scrolls there - holding the spot while
// pictures above it are still loading in.
public sealed class ScrollMemoryTests
{
    [AvaloniaFact]
    public void Preview_ReturnsToTheRememberedSpot()
    {
        var doc = Paragraphs(60);
        var (window, view, model) = Show(doc, ".md", context: null);
        var scroller = (ScrollViewer)Host(view).Content!;
        scroller.Offset = new Vector(0, 900);
        UiSnapshot.Pump();
        Assert.Equal(new DiffScrollSpot(true, 900), model.ScrollSpot);

        // Away (the spot now tracks the other document) and back.
        var spot = model.ScrollSpot;
        model.SetDiff(LineDiff.Compute("elsewhere\n", "elsewhere\n"));
        UiSnapshot.Pump();
        Assert.Equal(0, model.ScrollSpot!.Offset);
        model.RestoreScroll = spot;
        model.SetDiff(LineDiff.Compute(doc, doc));
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        var again = (ScrollViewer)Host(view).Content!;
        Assert.NotSame(scroller, again);
        Assert.Equal(900, again.Offset.Y, 0.5);
        Assert.Null(model.RestoreScroll);
        window.Close();
    }

    // A tall picture at the top loads after the first layout and pushes the
    // text down; the spot is re-applied so the same text stays in view.
    [AvaloniaFact]
    public void Preview_HoldsTheSpot_WhilePicturesLoad()
    {
        var png = MarkdownLinksTests.Png(300, 900, Colors.SlateGray);
        var context = new DocumentPreviewContext("README.md", (_, _) =>
        {
            Thread.Sleep(250);
            return png;
        }, _ => { });
        var doc = "![tall](tall.png)\n\n" + Paragraphs(60);
        var (window, view, model) = Show(doc, ".md", context);
        PumpUntil(() => Host(view).GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));
        var scroller = (ScrollViewer)Host(view).Content!;
        scroller.Offset = new Vector(0, 1500);
        UiSnapshot.Pump();
        var spot = model.ScrollSpot!;

        model.SetDiff(LineDiff.Compute("elsewhere\n", "elsewhere\n"));
        UiSnapshot.Pump();
        model.RestoreScroll = spot;
        model.SetDiff(LineDiff.Compute(doc, doc));
        UiSnapshot.Pump();
        var again = (ScrollViewer)Host(view).Content!;
        Assert.DoesNotContain(Host(view).GetVisualDescendants().OfType<Image>(), i => i.Source is not null);
        PumpUntil(() => Host(view).GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null));
        UiSnapshot.Pump();
        Assert.Equal(1500, again.Offset.Y, 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public void LineDiff_ReturnsToTheRememberedSpot()
    {
        var text = string.Concat(Enumerable.Range(1, 300).Select(i => $"line {i}\n"));
        var (window, view, model) = Show(text, ".txt", context: null, preview: false);
        var rows = view.FindControl<ScrollViewer>("RowsScroller")!;
        rows.Offset = new Vector(0, 1800);
        UiSnapshot.Pump();
        Assert.Equal(new DiffScrollSpot(false, 1800), model.ScrollSpot);

        rows.Offset = default;
        model.SetDiff(LineDiff.Compute("elsewhere\n", "elsewhere\n"));
        UiSnapshot.Pump();
        model.RestoreScroll = new DiffScrollSpot(false, 1800);
        model.SetDiff(LineDiff.Compute(text, text));
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        Assert.Equal(1800, rows.Offset.Y, 0.5);
        window.Close();
    }

    // The App: README (scrolled) -> link -> Back hands README its spot.
    [AvaloniaFact]
    public async Task App_BackRestoresWhereTheReaderWas()
    {
        var root = Directory.CreateTempSubdirectory("budiff-scroll-").FullName;
        File.WriteAllText(Path.Combine(root, "README.md"), "[MIT](LICENSE)\n");
        File.WriteAllText(Path.Combine(root, "LICENSE"), "MIT License\n");
        var vm = new global::Budiff.App.MainWindowViewModel(root, gitRoot: false);
        await vm.OpenAnalysisFileAsync(Path.Combine(root, "README.md"), "README.md");
        vm.Diff.ScrollSpot = new DiffScrollSpot(true, 750);

        vm.Diff.PreviewContext!.OpenFile("LICENSE");
        Assert.Null(vm.Diff.RestoreScroll);

        vm.Diff.GoBackCommand.Execute(null);
        Assert.Equal("README.md", vm.Diff.Title);
        Assert.Equal(new DiffScrollSpot(true, 750), vm.Diff.RestoreScroll);
    }

    private static string Paragraphs(int count)
    {
        var text = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            text.Append("Paragraph ").Append(i).Append(" with enough words to take a line or two of the preview.\n\n");
        }

        return text.ToString();
    }

    private static (Window Window, DiffView View, DiffViewModel Model) Show(string text, string extension, DocumentPreviewContext? context, bool preview = true)
    {
        var model = new DiffViewModel { Extension = extension, Previewer = new MarkdownPreviewer(), PreviewContext = context };
        model.SetDiff(LineDiff.Compute(text, text));
        model.ShowPreview = preview;
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        return (window, view, model);
    }

    private static ContentControl Host(DiffView view) => view.FindControl<ContentControl>("PreviewHost")!;

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < 5000)
        {
            UiSnapshot.Pump();
            Thread.Sleep(10);
        }

        UiSnapshot.Pump();
        Assert.True(done(), "timed out");
    }
}
