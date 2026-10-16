using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Budiff;
using Budiff.Avalonia.Markdown;
using Budiff.Avalonia.TextMate;
using Markdig.Syntax;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The markdown Preview: a formatted, block-level "rich diff" in place of the
// line diff - unchanged blocks once, a changed block's old version (red) then
// its new version (green), long unchanged runs folded behind a bar.
public sealed class MarkdownPreviewTests
{
    [Fact]
    public void BlockDiff_MarksChangedParagraphs_AndSingleListItems()
    {
        var blocks = Blocks(
            "# Title\n\nIntro para.\n\n- one\n- two\n- three\n\nOutro.\n",
            "# Title\n\nIntro para changed.\n\n- one\n- two\n- two and a half\n- three\n\nOutro.\n");

        Assert.Equal(
            new[]
            {
                "Unchanged Title",
                "Removed Intro para.",
                "Added Intro para changed.",
                "Unchanged - one",
                "Unchanged - two",
                "Added - two and a half",
                "Unchanged - three",
                "Unchanged Outro.",
            },
            blocks);
    }

    [Fact]
    public void BlockDiff_ShowsARemovedItemWhereItWas_WithItsOldNumber()
    {
        var model = Model("1. a\n2. b\n3. c\n", "1. a\n2. c\n");
        var units = MarkdownBlockDiff.Build(model.CreatePreviewRequest());

        // Matched by content, not line: "c" survived (renumbered 3 -> 2).
        Assert.Equal(new[] { BlockChange.Unchanged, BlockChange.Removed, BlockChange.Unchanged }, units.Select(u => u.Change));
        Assert.Equal(new[] { 1, 2, 2 }, units.Select(u => u.Number));
    }

    [Fact]
    public void BlockDiff_WithNothingToCompare_ShowsOneSidePlain()
    {
        const string doc = "# Notes\n\nSome text.\n";

        var same = MarkdownBlockDiff.Build(Model(doc, doc).CreatePreviewRequest());
        Assert.Equal(2, same.Count);
        Assert.All(same, u => Assert.Equal(BlockChange.Unchanged, u.Change));

        var added = Model(string.Empty, doc).CreatePreviewRequest();
        Assert.Equal(DocumentPreviewMode.NewOnly, added.Mode);
        Assert.All(MarkdownBlockDiff.Build(added), u => Assert.Equal(BlockChange.Unchanged, u.Change));

        var deleted = Model(doc, string.Empty).CreatePreviewRequest();
        Assert.Equal(DocumentPreviewMode.OldOnly, deleted.Mode);
        Assert.Equal(2, MarkdownBlockDiff.Build(deleted).Count);
    }

    [Fact]
    public void Collapse_KeepsChangesContextAndTheirHeading()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Paragraph {i}."));
        var oldDoc = "# Top\n\n" + paragraphs + "\n";
        var newDoc = "# Top\n\n" + paragraphs.Replace("Paragraph 8.", "Paragraph eight.", StringComparison.Ordinal) + "\n";
        var units = MarkdownBlockDiff.Build(Model(oldDoc, newDoc).CreatePreviewRequest());

        // # Top, P0..P7, P8 old, P8 new, P9..P11
        var visible = MarkdownPreviewer.Visible(units, collapse: true);
        var shown = units.Where((_, i) => visible[i]).Select(Label).ToList();
        Assert.Equal(new[] { "Unchanged Top", "Unchanged Paragraph 7.", "Removed Paragraph 8.", "Added Paragraph eight.", "Unchanged Paragraph 9." }, shown);

        Assert.All(MarkdownPreviewer.Visible(units, collapse: false), Assert.True);
    }

    [Fact]
    public void Preview_IsOfferedOnlyForMarkdown()
    {
        var model = new DiffViewModel { Previewer = new MarkdownPreviewer(), Extension = ".cs", ShowPreview = true };
        model.SetDiff(LineDiff.Compute("a\n", "b\n"));
        Assert.False(model.CanPreview);
        Assert.False(model.IsPreviewActive);
        Assert.True(model.ShowBlameOption);

        model.Extension = ".MD";
        Assert.True(model.CanPreview);
        Assert.True(model.IsPreviewActive);
        Assert.False(model.ShowBlameOption);
        Assert.False(model.ShowWrapOption);

        model.SetPlaceholder("Loading diff...");
        Assert.False(model.IsPreviewActive);
    }

    [AvaloniaFact]
    public void DiffView_RendersThePreview_InPlaceOfTheLineDiff()
    {
        var paragraphs = Enumerable.Range(0, 10).Select(i => $"Paragraph {i} with `code` and a [link](https://example.com).").ToList();
        var oldDoc = "# Heroes\n\n" + string.Join("\n\n", paragraphs) + "\n\n- **Steel Tempest** pace\n- The healing draught\n";
        paragraphs[6] = "Paragraph 6 *rewritten* with **bold** text.";
        var newDoc = "# Heroes\n\n" + string.Join("\n\n", paragraphs) + "\n\n- **Steel Tempest** pace\n- The healing draught\n- **Through the eyes** the arms come up\n";

        var model = Model(oldDoc, newDoc);
        model.Title = "docs/heroes.md";
        model.Previewer = new MarkdownPreviewer(new TextMateSyntaxHighlighter());
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        UiSnapshot.Pump();

        var previewToggle = Toggle(view, "Preview");
        Assert.True(previewToggle.IsEffectivelyVisible);
        Assert.True(Toggle(view, "Blame").IsEffectivelyVisible);

        model.ShowPreview = true;
        UiSnapshot.Pump();
        UiSnapshot.Pump();

        var host = view.FindControl<ContentControl>("PreviewHost")!;
        Assert.True(host.IsEffectivelyVisible);
        Assert.IsType<ScrollViewer>(host.Content);
        Assert.False(view.FindControl<ItemsControl>("RowsHost")!.IsEffectivelyVisible);
        Assert.False(Toggle(view, "Blame").IsEffectivelyVisible);
        Assert.False(Toggle(view, "Wrap lines").IsEffectivelyVisible);

        Assert.Equal(1, Marked(host, BlockChange.Removed));
        Assert.Equal(2, Marked(host, BlockChange.Added));
        UiSnapshot.Capture(window, "markdown-preview");

        // The fold bar expands in place.
        var bar = host.GetVisualDescendants().OfType<Border>()
            .First(b => b.Child is TextBlock { Text: { } text } && text.Contains("unchanged blocks", StringComparison.Ordinal));
        var before = Units(host);
        var center = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        UiSnapshot.Pump();
        Assert.True(Units(host) > before);

        // Collapse off re-renders everything; Preview off brings the line diff back.
        model.CollapseUnchanged = false;
        UiSnapshot.Pump();
        Assert.DoesNotContain(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("unchanged blocks", StringComparison.Ordinal) == true);

        model.ShowPreview = false;
        UiSnapshot.Pump();
        Assert.Null(host.Content);
        Assert.True(view.FindControl<ItemsControl>("RowsHost")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void App_OffersPreviewForMarkdownFiles()
    {
        var vm = new global::Budiff.App.MainWindowViewModel(System.IO.Directory.CreateTempSubdirectory("budiff-md-").FullName, gitRoot: false);
        Assert.IsType<MarkdownPreviewer>(vm.Diff.Previewer);
        vm.Diff.Extension = ".md";
        Assert.True(vm.Diff.CanPreview);
        Assert.False(vm.Diff.ShowPreview);
        Assert.True(vm.Diff.WrapLines);
    }

    // Smoke + eyeball capture of the formatting itself.
    [AvaloniaFact]
    public void RichDocument_RendersEveryBlockKind()
    {
        const string doc = """
            ---
            title: Sample
            ---
            # Budiff notes

            Some *italic*, **bold**, ~~struck~~, `inline code`, a [link](https://x.y) and <https://auto.link>.<br>A forced break.

            ## Lists

            1. First
            2. Second
               - nested bullet
               - [x] done task
               - [ ] open task
            3. Third

            > A quote with **emphasis**.

            > [!WARNING]
            > Careful here.

            ```csharp
            public sealed class Sample { public int Value => 42; }
            ```

            | Column | Align right | Notes |
            |:-------|------------:|-------|
            | a      | 1           | short |
            | b      | 22          | a much longer cell that should wrap nicely |

            ---

            ![diagram](docs/diagram.png)

            <div align="center">raw html block</div>
            """;

        var model = Model(doc, doc);
        model.Previewer = new MarkdownPreviewer(new TextMateSyntaxHighlighter());
        model.ShowPreview = true;
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 820, Height = 900, Content = view };
        UiSnapshot.Capture(window, "markdown-preview-rich");

        var host = view.FindControl<ContentControl>("PreviewHost")!;
        var texts = host.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Count > 0 ? string.Concat(t.Inlines.OfType<global::Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text).ToList();
        Assert.Contains(texts, t => t?.Contains("Budiff notes", StringComparison.Ordinal) == true);
        Assert.Contains(texts, t => t?.Contains("public sealed class Sample", StringComparison.Ordinal) == true);
        Assert.Contains(texts, t => t?.Contains("a much longer cell", StringComparison.Ordinal) == true);
        Assert.Contains(texts, t => t == "Warning");
        Assert.Contains(texts, t => t?.Contains("☑", StringComparison.Ordinal) == true);
        Assert.Equal(0, Marked(host, BlockChange.Added) + Marked(host, BlockChange.Removed));
        window.Close();
    }

    private static List<string> Blocks(string oldDoc, string newDoc)
        => MarkdownBlockDiff.Build(Model(oldDoc, newDoc).CreatePreviewRequest()).Select(Label).ToList();

    private static string Label(PreviewBlock block) => $"{block.Change} {FirstLine(block)}";

    private static string FirstLine(PreviewBlock block)
    {
        var source = block.Block is ListItemBlock ? "- " : string.Empty;
        return source + PlainText(block.Block);
    }

    private static string PlainText(Block block) => block switch
    {
        LeafBlock { Inline: { } inline } => string.Concat(inline.Descendants<Markdig.Syntax.Inlines.LiteralInline>().Select(l => l.Content.ToString())),
        ContainerBlock container => container.Count > 0 ? PlainText(container[0]) : string.Empty,
        _ => string.Empty,
    };

    private static DiffViewModel Model(string oldDoc, string newDoc)
    {
        var model = new DiffViewModel { Extension = ".md", Previewer = new MarkdownPreviewer() };
        model.SetDiff(LineDiff.Compute(oldDoc, newDoc));
        return model;
    }

    private static CheckBox Toggle(DiffView view, string label)
        => view.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, label));

    private static int Marked(ContentControl host, BlockChange change)
        => host.GetVisualDescendants().OfType<Border>().Count(b => b.Tag is BlockChange c && c == change);

    private static int Units(ContentControl host)
        => host.GetVisualDescendants().OfType<Border>().Count(b => b.Tag is BlockChange);
}
