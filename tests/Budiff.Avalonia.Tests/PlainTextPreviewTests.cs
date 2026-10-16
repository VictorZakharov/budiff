using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Budiff;
using Budiff.Avalonia.Markdown;
using Markdig.Syntax;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The Preview also takes text documents - *.txt and extensionless files such
// as LICENSE - shown as plain text, or as markdown when they read like it;
// so following README -> LICENSE stays in the Preview.
public sealed class PlainTextPreviewTests
{
    private const string Mit = """
        MIT License

        Copyright (c) 2025 Someone

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
        """;

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("notes.txt", true)]
    [InlineData("LICENSE", true)]
    [InlineData("LICENSE-MIT", true)]
    [InlineData("NOTICE", true)]
    [InlineData("CHANGES", true)]
    [InlineData("Makefile", false)]
    [InlineData("Dockerfile", false)]
    [InlineData("app.ts", false)]
    [InlineData(".gitignore", false)]
    public void Preview_TakesDocumentsByName(string fileName, bool expected)
    {
        Assert.Equal(expected, new MarkdownPreviewer().CanPreviewFile(fileName));
        var model = new DiffViewModel { Previewer = new MarkdownPreviewer(), FileName = fileName };
        Assert.Equal(expected, model.CanPreview);
    }

    [Fact]
    public void Sniffing_KeepsLicensesPlain_AndSpotsMarkdown()
    {
        Assert.False(DocumentKind.LooksLikeMarkdown(Mit));
        Assert.False(DocumentKind.LooksLikeMarkdown("   1. Definitions.\n\n      \"License\" shall mean the terms.\n"));
        Assert.False(DocumentKind.LooksLikeMarkdown("#!/bin/sh\n# install\necho hi\n"));
        Assert.True(DocumentKind.LooksLikeMarkdown("# Changes\n\nFixed things.\n"));
        Assert.True(DocumentKind.LooksLikeMarkdown("See [the guide](docs/guide.md).\n"));
        Assert.True(DocumentKind.LooksLikeMarkdown("- one\n- two\n"));

        Assert.True(DocumentKind.IsPlainText("LICENSE", Mit));
        Assert.False(DocumentKind.IsPlainText("CHANGES", "# v2\n\n- faster\n"));
        Assert.False(DocumentKind.IsPlainText("README.md", Mit));
        Assert.False(DocumentKind.IsPlainText(null, Mit));
    }

    // Plain text diffs by paragraph: a changed year marks just that one.
    [Fact]
    public void PlainText_DiffsByParagraph()
    {
        var model = Model(Mit, Mit.Replace("2025", "2026", StringComparison.Ordinal), "LICENSE");
        var blocks = MarkdownBlockDiff.Build(model.CreatePreviewRequest());
        Assert.All(blocks, b => Assert.IsType<PlainTextBlock>(b.Block));
        var labels = blocks.Select(b => $"{b.Change} {((PlainTextBlock)b.Block).Text.Split('\n')[0]}").ToList();
        Assert.Equal(5, labels.Count);
        Assert.Equal("Unchanged MIT License", labels[0]);
        Assert.Equal("Removed Copyright (c) 2025 Someone", labels[1]);
        Assert.Equal("Added Copyright (c) 2026 Someone", labels[2]);
        Assert.StartsWith("Unchanged Permission", labels[3], StringComparison.Ordinal);
        Assert.StartsWith("Unchanged THE SOFTWARE", labels[4], StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void PlainText_RendersAsWritten_MarkdownLookalikesAsMarkdown()
    {
        var (window, host) = Show(Model(Mit, Mit, "LICENSE"));
        var text = host.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text?.StartsWith("Permission", StringComparison.Ordinal) == true);
        Assert.Contains('\n', text.Text);
        Assert.Contains("Consolas", text.FontFamily.ToString(), StringComparison.Ordinal);
        window.Close();

        (window, host) = Show(Model("# Changes\n\n- faster\n- smaller\n", "# Changes\n\n- faster\n- smaller\n", "CHANGES"));
        Assert.Contains(host.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.FontSize > 20);
        Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "•");
        window.Close();
    }

    // README -> LICENSE via a link keeps the Preview on.
    [AvaloniaFact]
    public async Task App_LinkToLicense_StaysInPreview()
    {
        var root = Directory.CreateTempSubdirectory("budiff-license-").FullName;
        File.WriteAllText(Path.Combine(root, "README.md"), "[MIT](LICENSE).\n");
        File.WriteAllText(Path.Combine(root, "LICENSE"), Mit);

        var vm = new global::Budiff.App.MainWindowViewModel(root, gitRoot: false);
        await vm.OpenAnalysisFileAsync(Path.Combine(root, "README.md"), "README.md");
        vm.Diff.ShowPreview = true;
        vm.Diff.PreviewContext!.OpenFile("LICENSE");
        Assert.Equal("LICENSE", vm.Diff.FileName);
        Assert.True(vm.Diff.CanPreview);
        Assert.True(vm.Diff.ShowPreview);
    }

    private static DiffViewModel Model(string oldText, string newText, string fileName)
    {
        var model = new DiffViewModel { Previewer = new MarkdownPreviewer(), FileName = fileName };
        model.SetDiff(LineDiff.Compute(oldText, newText));
        return model;
    }

    private static (Window Window, ContentControl Host) Show(DiffViewModel model)
    {
        model.ShowPreview = true;
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        var host = view.FindControl<ContentControl>("PreviewHost")!;
        Assert.NotNull(host.Content);
        return (window, host);
    }
}
