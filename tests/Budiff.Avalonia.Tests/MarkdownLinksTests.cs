using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Budiff;
using Budiff.Avalonia.Markdown;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The markdown Preview reaches into the project: pictures load from it (each
// side of the diff from its own revision), relative links open its files,
// "#heading" links scroll, and web links - marked with the external-link
// icon - open in the browser.
public sealed class MarkdownLinksTests
{
    [Theory]
    [InlineData("CONTRIBUTE.md", "File", "docs/CONTRIBUTE.md")]
    [InlineData("./guide.md#setup", "File", "docs/guide.md")]
    [InlineData("../LICENSE", "File", "LICENSE")]
    [InlineData("/src/app.cs", "File", "src/app.cs")]
    [InlineData("img/My%20Shot.png", "File", "docs/img/My Shot.png")]
    [InlineData("#Usage", "Anchor", "Usage")]
    [InlineData("README.md#install", "Anchor", "install")]
    [InlineData("https://github.com/x/y/issues", "External", "https://github.com/x/y/issues")]
    [InlineData("mailto:me@example.test", "External", "mailto:me@example.test")]
    [InlineData("//cdn.example.test/a.png", "External", "https://cdn.example.test/a.png")]
    public void Resolve_FollowsGitHubRules(string url, string kind, string value)
    {
        var target = MarkdownLinks.Resolve(url, "docs/README.md");
        Assert.NotNull(target);
        Assert.Equal(kind, target.Kind.ToString());
        Assert.Equal(value, target.Value);
    }

    [Theory]
    [InlineData("../../outside.md")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/x.md")]
    [InlineData("C:/x.md")]
    [InlineData("")]
    [InlineData("#")]
    public void Resolve_RefusesWhatItCannotOpen(string url)
        => Assert.Null(MarkdownLinks.Resolve(url, "docs/README.md"));

    [Fact]
    public void Anchors_UseGitHubSlugs_NumberingRepeats()
    {
        Assert.Equal("getting-started-v2", MarkdownLinks.Slug("Getting Started: v2!"));
        Assert.Equal("c-api--notes", MarkdownLinks.Slug("C# API / notes"));

        var blocks = MarkdownBlockDiff.Build(Model("# Intro\n\n## Usage\n\ntext\n\n## Usage\n", "# Intro\n\n## Usage\n\ntext\n\n## Usage\n").CreatePreviewRequest());
        var anchors = MarkdownPreviewer.Anchors(blocks);
        Assert.Equal(0, anchors["intro"]);
        Assert.Equal(1, anchors["usage"]);
        Assert.Equal(3, anchors["usage-1"]);
    }

    [Fact]
    public void HtmlImages_ReadSizesAndLinks()
    {
        const string html = """<p align="center"><a href="https://x.test"><img src="logo.png" alt="Logo" width="120"></a> <img src='b.png' height=40px></p>""";
        var images = MarkdownLinks.HtmlImages(html);
        Assert.Collection(images,
            a => Assert.Equal(new HtmlImage("logo.png", "Logo", 120, null, "https://x.test"), a),
            b => Assert.Equal(new HtmlImage("b.png", string.Empty, null, 40, null), b));
        Assert.True(MarkdownLinks.IsOnlyImages(html));
        Assert.True(MarkdownLinks.IsCentered(html));
        Assert.False(MarkdownLinks.IsOnlyImages("<div>text <img src=\"a.png\"></div>"));
    }

    // A changed picture: the removed block shows the base revision's file,
    // the added block the new one; a wide picture is scaled to the pane.
    [AvaloniaFact]
    public void Pictures_LoadFromTheirSideOfTheDiff_AndFitThePane()
    {
        var oldPng = Png(300, 100, Colors.IndianRed);
        var newPng = Png(1600, 400, Colors.SeaGreen);
        var reads = new ConcurrentBag<(string Path, DocumentSide Side)>();
        var context = Context("docs/README.md", (path, side) =>
        {
            reads.Add((path, side));
            return path == "docs/img/shot.png" ? (side == DocumentSide.Old ? oldPng : newPng) : null;
        });

        var (window, host) = Show("# Shots\n\n![before](img/shot.png)\n\n![web](https://example.test/x.png)\n", "# Shots\n\n![after](img/shot.png)\n\n![web](https://example.test/x.png)\n", context, width: 700);
        PumpUntil(() => Images(host).Count == 2, () => PreviewPictures.Describe(host));

        var removed = Images(host).Single(i => Unit(i)?.Tag is BlockChange.Removed);
        var added = Images(host).Single(i => Unit(i)?.Tag is BlockChange.Added);
        Assert.Equal(300, removed.Bounds.Width, 0.5);
        Assert.True(added.Bounds.Width < 700, $"picture is {added.Bounds.Width}px wide");
        Assert.Equal(added.Bounds.Width / 4, added.Bounds.Height, 1);
        Assert.Contains(("docs/img/shot.png", DocumentSide.Old), reads);
        Assert.Contains(("docs/img/shot.png", DocumentSide.New), reads);
        Assert.DoesNotContain(reads, r => r.Path.Contains("example.test", StringComparison.Ordinal));

        // A web picture is not fetched: its alt text stands in.
        Assert.Contains(Texts(host), t => t.Contains("🖼 web", StringComparison.Ordinal));
        UiSnapshot.Capture(window, "markdown-preview-pictures");
        window.Close();
    }

    [AvaloniaFact]
    public void HtmlPictures_HonorTheirWidth_AndTableCellsShowPictures()
    {
        var png = Png(400, 200, Colors.SteelBlue);
        var context = Context("README.md", (path, _) => path is "logo.png" or "docs/a.png" ? png : null);
        const string doc = """
            <p align="center"><img src="logo.png" alt="Logo" width="120"></p>

            | Lobby | Wave |
            |---|---|
            | ![a](docs/a.png) | ![b](docs/missing.png) |
            """;
        var (window, host) = Show(doc, doc, context, width: 800);
        PumpUntil(() => Images(host).Count == 2, () => PreviewPictures.Describe(host));

        var logo = Images(host).Single(i => i.MaxWidth == 120);
        Assert.Equal(120, logo.Bounds.Width, 0.5);
        var cell = Images(host).Single(i => i != logo);
        Assert.True(cell.Bounds.Width > 100 && cell.Bounds.Width <= 400);
        Assert.Contains(Texts(host), t => t.Contains("🖼 b", StringComparison.Ordinal));
        window.Close();
    }

    // A file that does not decode keeps its alt text like a missing one; the
    // slot keeps why, for a test that waits on it.
    [AvaloniaFact]
    public void APictureThatDoesNotDecode_KeepsItsAltText()
    {
        var context = Context("README.md", (path, _) => path == "broken.png" ? System.Text.Encoding.ASCII.GetBytes("not a picture") : null);
        const string doc = "![broken](broken.png)\n\n![gone](gone.png)\n";
        var (window, host) = Show(doc, doc, context, width: 700);
        var slots = host.GetVisualDescendants().OfType<MarkdownRenderer.PictureSlot>().ToList();
        PumpUntil(() => slots.All(s => s.LoadState != "loading"), () => PreviewPictures.Describe(host));

        Assert.StartsWith("failed: ", slots.Single(s => s.Source == "broken.png").LoadState, StringComparison.Ordinal);
        Assert.Equal("no picture", slots.Single(s => s.Source == "gone.png").LoadState);
        Assert.Empty(Images(host));
        Assert.Contains(Texts(host), t => t.Contains("🖼 broken", StringComparison.Ordinal));
        Assert.Contains(Texts(host), t => t.Contains("🖼 gone", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingALink_OpensTheProjectFile()
    {
        var opened = new List<string>();
        var context = Context("docs/README.md", (_, _) => null, opened.Add);
        const string doc = "To contribute, see [the guide](CONTRIBUTE.md) or [setup](../SETUP.md#prereqs).\n";
        var (window, host) = Show(doc, doc, context, width: 800);

        Click(window, host, "the guide");
        Click(window, host, "setup");
        Assert.Equal(new[] { "docs/CONTRIBUTE.md", "SETUP.md" }, opened);

        // A drag across the link selects text instead of following it.
        var (block, point) = FindLink(host, "the guide");
        var start = block.TranslatePoint(point, window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(40, 0));
        window.MouseUp(start + new Point(40, 0), MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Equal(2, opened.Count);
        window.Close();
    }

    // A link on a wrapped (second) line: hovering shows the hand, a click
    // follows it. (Avalonia's own "inside" flag reads false there.)
    [AvaloniaFact]
    public void LinksOnWrappedLines_ShowTheHand_AndFollow()
    {
        var opened = new List<string>();
        const string doc = "This is an early build — feedback, bug reports and ideas are very welcome in [Issues](https://github.com/x/y/issues). To contribute code, see [CONTRIBUTE.md](CONTRIBUTE.md).\n";
        var (window, host) = Show(doc, doc, Context("README.md", (_, _) => null, opened.Add), width: 560);

        var (block, point) = FindLink(host, "CONTRIBUTE.md");
        Assert.True(point.Y > block.Bounds.Height / 2, "the link should sit on the wrapped line");
        var at = block.TranslatePoint(point, window)!.Value;
        window.MouseMove(at);
        UiSnapshot.Pump();
        Assert.Same(MarkdownRenderer.HandCursor, block.Cursor);
        Assert.Equal("CONTRIBUTE.md", ToolTip.GetTip(block));

        window.MouseMove(block.TranslatePoint(new Point(2, 2), window)!.Value);
        UiSnapshot.Pump();
        Assert.NotSame(MarkdownRenderer.HandCursor, block.Cursor);

        Click(window, host, "CONTRIBUTE.md");
        Assert.Equal(new[] { "CONTRIBUTE.md" }, opened);
        window.Close();
    }

    [AvaloniaFact]
    public void ExternalLinks_CarryTheIcon_AndOpenInTheBrowser()
    {
        var previewer = new MarkdownPreviewer();
        var launched = new List<Uri>();
        previewer.OpenExternal = (_, uri) =>
        {
            launched.Add(uri);
            return Task.FromResult(true);
        };

        const string doc = "Ideas welcome in [Issues](https://github.com/x/y/issues), license is [MIT](LICENSE).\n";
        var opened = new List<string>();
        var (window, host) = Show(doc, doc, Context("README.md", (_, _) => null, opened.Add), width: 800, previewer);

        var text = host.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Tag is not null);
        var icons = text.Inlines!.OfType<InlineUIContainer>().ToList();
        Assert.Single(icons);
        var issuesEnd = text.Inlines!.ToList().FindIndex(i => i is Run { Text: "Issues" });
        Assert.IsType<InlineUIContainer>(text.Inlines![issuesEnd + 1]);

        Click(window, host, "Issues");
        var icon = icons[0].Child!;
        var center = icon.TranslatePoint(new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Equal(2, launched.Count);
        Assert.All(launched, uri => Assert.Equal("https://github.com/x/y/issues", uri.OriginalString));

        // A link after the icon (an embedded control in the text) still hits.
        Click(window, host, "MIT");
        Assert.Equal(new[] { "LICENSE" }, opened);
        UiSnapshot.Capture(window, "markdown-preview-links");
        window.Close();
    }

    // A table-of-contents link unfolds a collapsed run and scrolls to it.
    [AvaloniaFact]
    public void AnchorLinks_UnfoldAndScrollToTheHeading()
    {
        var filler = string.Concat(Enumerable.Range(1, 30).Select(i => $"Paragraph {i} of filler text.\n\n"));
        var oldDoc = "# Title\n\nSee [Usage](#usage).\n\nIntro.\n\n" + filler + "## Usage\n\nRun it.\n\n" + filler;
        var newDoc = "# Title\n\nSee [Usage](#usage).\n\nIntro changed.\n\n" + filler + "## Usage\n\nRun it.\n\n" + filler;
        var (window, host) = Show(oldDoc, newDoc, Context("README.md", (_, _) => null), width: 700, height: 500);
        var scroller = (ScrollViewer)host.Content!;
        Assert.DoesNotContain(Texts(host), t => t == "Run it.");

        Click(window, host, "Usage");
        UiSnapshot.Pump();
        Assert.Contains(Texts(host), t => t == "Run it.");
        Assert.True(scroller.Offset.Y > 100, $"offset {scroller.Offset.Y}");
        var heading = host.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => Text(t) == "Usage");
        var top = heading.TranslatePoint(default, scroller)!.Value.Y;
        Assert.InRange(top, 0, 60);
        window.Close();
    }

    // The App: a relative link opens that file in the diff, Back returns.
    [AvaloniaFact]
    public async Task App_FollowsLinks_AndGoesBack()
    {
        var root = Directory.CreateTempSubdirectory("budiff-links-").FullName;
        File.WriteAllText(Path.Combine(root, "README.md"), "See [guide](docs/guide.md).\n");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "guide.md"), "# Guide\n");
        File.WriteAllBytes(Path.Combine(root, "docs", "shot.png"), Png(10, 10, Colors.Orange));

        var vm = new global::Budiff.App.MainWindowViewModel(root, gitRoot: false);
        await vm.OpenAnalysisFileAsync(Path.Combine(root, "README.md"), "README.md");
        var context = vm.Diff.PreviewContext;
        Assert.NotNull(context);
        Assert.Equal("README.md", context.DocumentPath);
        Assert.NotNull(context.ReadFile("docs/shot.png", DocumentSide.New));
        Assert.Null(context.ReadFile("../outside.png", DocumentSide.New));
        Assert.False(vm.Diff.CanGoBack);

        context.OpenFile("docs/guide.md");
        Assert.Equal("docs/guide.md", vm.Diff.Title);
        Assert.True(vm.Diff.CanGoBack);
        Assert.Equal("docs/guide.md", vm.Diff.PreviewContext!.DocumentPath);

        vm.Diff.PreviewContext.OpenFile("missing.md");
        Assert.Equal("docs/guide.md", vm.Diff.Title);
        Assert.Contains("missing.md", vm.StatusText, StringComparison.Ordinal);

        vm.Diff.GoBackCommand.Execute(null);
        Assert.Equal("README.md", vm.Diff.Title);
        Assert.False(vm.Diff.CanGoBack);
    }

    private static (Window Window, ContentControl Host) Show(string oldDoc, string newDoc, DocumentPreviewContext context, double width, MarkdownPreviewer? previewer = null)
        => Show(oldDoc, newDoc, context, width, 700, previewer);

    private static (Window Window, ContentControl Host) Show(string oldDoc, string newDoc, DocumentPreviewContext context, double width, double height, MarkdownPreviewer? previewer = null)
    {
        var model = Model(oldDoc, newDoc);
        if (previewer is not null)
        {
            model.Previewer = previewer;
        }

        model.PreviewContext = context;
        model.ShowPreview = true;
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        var host = view.FindControl<ContentControl>("PreviewHost")!;
        Assert.NotNull(host.Content);
        return (window, host);
    }

    private static DiffViewModel Model(string oldDoc, string newDoc)
    {
        var model = new DiffViewModel { Extension = ".md", Previewer = new MarkdownPreviewer() };
        model.SetDiff(LineDiff.Compute(oldDoc, newDoc));
        return model;
    }

    private static DocumentPreviewContext Context(string documentPath, Func<string, DocumentSide, byte[]?> read, Action<string>? open = null)
        => new(documentPath, read, open ?? (_ => { }));

    private static List<Image> Images(ContentControl host)
        => host.GetVisualDescendants().OfType<Image>().Where(i => i.Source is not null).ToList();

    private static Border? Unit(Visual visual)
        => visual.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Tag is BlockChange);

    private static List<string> Texts(ContentControl host)
        => host.GetVisualDescendants().OfType<TextBlock>().Select(Text).ToList();

    private static string Text(TextBlock block)
        => block.Inlines?.Count > 0 ? string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)) : block.Text ?? string.Empty;

    // A point over a link's text, found the way a user would: by hovering.
    private static (SelectableTextBlock Block, Point Point) FindLink(ContentControl host, string linkText)
    {
        foreach (var block in host.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.Tag is not null))
        {
            var runs = block.Inlines!.ToList();
            var offset = 0;
            foreach (var inline in runs)
            {
                if (inline is Run { Text: { } text } && text == linkText)
                {
                    var rect = block.TextLayout.HitTestTextRange(offset, text.Length).First();
                    return (block, rect.Center);
                }

                offset += inline switch
                {
                    Run run => run.Text?.Length ?? 0,
                    _ => 1,
                };
            }
        }

        throw new InvalidOperationException($"link '{linkText}' not found");
    }

    private static void Click(Window window, ContentControl host, string linkText)
    {
        var (block, point) = FindLink(host, linkText);
        Assert.NotNull(MarkdownRenderer.LinkAt(block, point));
        var at = block.TranslatePoint(point, window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiSnapshot.Pump();
    }

    private static void PumpUntil(Func<bool> done, Func<string>? describe = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < 5000)
        {
            UiSnapshot.Pump();
            Thread.Sleep(10);
        }

        UiSnapshot.Pump();
        Assert.True(done(), "timed out waiting for the preview" + (describe is null ? string.Empty : ": " + describe()));
    }

    internal static byte[] Png(int width, int height, Color color)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var row = new int[width];
            Array.Fill(row, unchecked((int)color.ToUInt32()));
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(row, 0, buffer.Address + (y * buffer.RowBytes), width);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }
}
