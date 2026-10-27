using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Budiff;
using Budiff.Avalonia.Markdown;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Back after following a link returns to where the reader was: the view
// keeps DiffViewModel.ScrollSpot current, the host hands it back as
// RestoreScroll. A recently shown preview comes back as the very same
// control (already there, sections still unfolded); one rendered anew is
// scrolled there out of sight, holding the spot while pictures load in.
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
        Assert.Same(scroller, Host(view).Content);
        Assert.Equal(900, scroller.Offset.Y, 0.5);
        Assert.Equal(1, scroller.Opacity);
        Assert.Null(model.RestoreScroll);
        window.Close();
    }

    // Sections the reader unfolded stay unfolded when they come back.
    [AvaloniaFact]
    public void Preview_KeepsUnfoldedSections_OnBack()
    {
        var oldDoc = "Intro.\n\n" + Paragraphs(40);
        var newDoc = "Intro changed.\n\n" + Paragraphs(40);
        var model = new DiffViewModel { Extension = ".md", Previewer = new MarkdownPreviewer(), CollapseUnchanged = true };
        model.SetDiff(LineDiff.Compute(oldDoc, newDoc));
        model.ShowPreview = true;
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 700, Height = 500, Content = view };
        window.Show();
        UiSnapshot.Pump();
        Assert.False(Shows(view, "Paragraph 30 "));

        var bar = Host(view).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains("unchanged blocks", StringComparison.Ordinal) == true);
        var at = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiSnapshot.Pump();
        Assert.True(Shows(view, "Paragraph 30 "));
        var scroller = (ScrollViewer)Host(view).Content!;
        scroller.Offset = new Vector(0, 600);
        UiSnapshot.Pump();
        var spot = model.ScrollSpot;
        Assert.Equal(new DiffScrollSpot(true, 600), spot);

        model.SetDiff(LineDiff.Compute("elsewhere\n", "elsewhere\n"));
        UiSnapshot.Pump();
        model.RestoreScroll = spot;
        model.SetDiff(LineDiff.Compute(oldDoc, newDoc));
        UiSnapshot.Pump();
        Assert.Same(scroller, Host(view).Content);
        Assert.True(Shows(view, "Paragraph 30 "));
        Assert.Equal(600, scroller.Offset.Y, 0.5);
        window.Close();
    }

    // Following a link to a document seen before starts at its top; only
    // Back returns to a spot.
    [AvaloniaFact]
    public void Preview_ForwardToASeenDocument_StartsAtTheTop()
    {
        var doc = Paragraphs(60);
        var (window, view, model) = Show(doc, ".md", context: null);
        model.FileName = "a.md";
        UiSnapshot.Pump();
        var scroller = (ScrollViewer)Host(view).Content!;
        scroller.Offset = new Vector(0, 900);
        UiSnapshot.Pump();

        Open(model, "b.md", "elsewhere\n");
        Open(model, "a.md", doc);
        Assert.Same(scroller, Host(view).Content);
        Assert.Equal(0, scroller.Offset.Y, 0.5);
        Assert.Equal(new DiffScrollSpot(true, 0), model.ScrollSpot);
        window.Close();
    }

    // Not cached any more (the file changed meanwhile): rendered anew, the
    // view stays hidden until it reaches the spot - here only once a tall
    // picture at the top loads in and makes the document long enough.
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
        PumpUntil(() => Host(view).GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), () => PreviewPictures.Describe(Host(view)));
        var scroller = (ScrollViewer)Host(view).Content!;
        var bottom = scroller.Extent.Height - scroller.Viewport.Height;
        scroller.Offset = new Vector(0, bottom);
        UiSnapshot.Pump();
        var spot = model.ScrollSpot!;
        Assert.Equal(bottom, spot.Offset, 0.5);

        model.SetDiff(LineDiff.Compute("elsewhere\n", "elsewhere\n"));
        UiSnapshot.Pump();
        model.RestoreScroll = spot;
        var edited = doc + "One more paragraph.\n";
        model.SetDiff(LineDiff.Compute(edited, edited));
        UiSnapshot.Pump();
        var again = (ScrollViewer)Host(view).Content!;
        Assert.NotSame(scroller, again);
        Assert.DoesNotContain(Host(view).GetVisualDescendants().OfType<Image>(), i => i.Source is not null);
        Assert.Equal(0, again.Opacity);
        PumpUntil(() => Host(view).GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null), () => PreviewPictures.Describe(Host(view)));
        UiSnapshot.Pump();
        Assert.Equal(bottom, again.Offset.Y, 0.5);
        Assert.Equal(1, again.Opacity);
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
        Assert.Equal(1, rows.Opacity);
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
        Assert.Equal(DiffScrollSpot.Start, vm.Diff.RestoreScroll);

        vm.Diff.GoBackCommand.Execute(null);
        Assert.Equal("README.md", vm.Diff.Title);
        Assert.Equal(new DiffScrollSpot(true, 750), vm.Diff.RestoreScroll);
        Assert.True(vm.Diff.IsLoading);
        Assert.Equal("README.md", vm.Diff.FileName);
    }

    // The real window: README (scrolled) -> CONTRIBUTE.md -> Back -> LICENSE
    // -> Back. The second Back must land where the first one did: the README
    // that came back already in place still has to count as scrolled there.
    [AvaloniaFact]
    public async Task App_SecondBackStillReturnsToTheSpot()
    {
        var root = Directory.CreateTempSubdirectory("budiff-twolinks-").FullName;
        File.WriteAllText(Path.Combine(root, "README.md"), "See [CONTRIBUTE.md](CONTRIBUTE.md) and [MIT](LICENSE).\n\n" + Paragraphs(80));
        File.WriteAllText(Path.Combine(root, "CONTRIBUTE.md"), "# Contribute\n\n" + Paragraphs(5));
        File.WriteAllText(Path.Combine(root, "LICENSE"), "MIT License\n\nPermission is hereby granted.\n");
        var window = new global::Budiff.App.MainWindow(root, gitRoot: false) { Width = 1200, Height = 700 };
        window.Show();
        var vm = window.ViewModel;
        vm.Diff.ShowPreview = true;
        await vm.OpenAnalysisFileAsync(Path.Combine(root, "README.md"), "README.md");
        PumpUntil(() => !vm.Diff.IsLoading && Preview(window) is not null);
        var readme = Preview(window)!;
        readme.Offset = new Vector(0, 900);
        UiSnapshot.Pump();

        foreach (var link in new[] { "CONTRIBUTE.md", "LICENSE" })
        {
            vm.Diff.PreviewContext!.OpenFile(link);
            PumpUntil(() => !vm.Diff.IsLoading && vm.Diff.Title == link && Preview(window) is { } shown && shown != readme);
            vm.Diff.GoBackCommand.Execute(null);
            PumpUntil(() => !vm.Diff.IsLoading && Preview(window) == readme);
            Assert.Equal(900, readme.Offset.Y, 0.5);
            Assert.Equal(new DiffScrollSpot(true, 900), vm.Diff.ScrollSpot);
        }

        window.Close();
    }

    private static ScrollViewer? Preview(Window window)
        => window.GetVisualDescendants().OfType<DiffView>().Where(v => v.IsEffectivelyVisible)
            .Select(v => Host(v).Content).OfType<ScrollViewer>().FirstOrDefault();

    // A view on screen keeps the current preview while the next file loads -
    // the rows are still the old file's - and renders the new one on arrival.
    [AvaloniaFact]
    public void Preview_WaitsForTheNextFile()
    {
        var doc = Paragraphs(10);
        var (window, view, model) = Show(doc, ".md", context: null);
        model.FileName = "README.md";
        UiSnapshot.Pump();
        var shown = Host(view).Content;

        // Checked straight away: a slow load shows "Loading diff..." soon.
        using var gate = new ManualResetEventSlim();
        try
        {
            model.BeginLoad(() =>
            {
                gate.Wait(5000);
                return DiffResult.FromRows(LineDiff.Compute("MIT License\n", "MIT License\n"));
            });
            model.FileName = "LICENSE";
            Assert.Same(shown, Host(view).Content);
        }
        finally
        {
            gate.Set();
        }

        PumpUntil(() => !model.IsLoading);
        Assert.NotSame(shown, Host(view).Content);
        Assert.True(Shows(view, "MIT License"));
        window.Close();
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

    // The way the App opens a file: the load starts, then the name changes,
    // then the rows land.
    private static void Open(DiffViewModel model, string fileName, string text)
    {
        model.BeginLoad(() => DiffResult.FromRows(LineDiff.Compute(text, text)));
        model.FileName = fileName;
        PumpUntil(() => !model.IsLoading);
    }

    private static bool Shows(DiffView view, string start)
        => Host(view).GetVisualDescendants().OfType<TextBlock>()
            .Any(t => (t.Inlines?.Count > 0 ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) : t.Text ?? string.Empty).StartsWith(start, StringComparison.Ordinal));

    private static void PumpUntil(Func<bool> done, Func<string>? describe = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < 5000)
        {
            UiSnapshot.Pump();
            Thread.Sleep(10);
        }

        UiSnapshot.Pump();
        Assert.True(done(), describe is null ? "timed out" : "timed out: " + describe());
    }
}
