using System;
using System.Collections.Generic;
using Markdig.Syntax;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Layout;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;

namespace Budiff.Avalonia.Markdown;

// The markdown Preview: the document formatted, as one column. Against a base
// it is a "rich diff" - unchanged blocks plain, a changed block's old version
// tinted red and its new version green; Collapse unchanged folds long runs of
// untouched blocks (keeping one block of context and the nearest heading).
public sealed class MarkdownPreviewer : IDocumentPreviewer
{
    private const int ContextBlocks = 1;
    private const double MaxReadableWidth = 1000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd",
    };

    private readonly ISyntaxHighlighter? _highlighter;

    // The highlighter (optional) colors fenced code blocks by their language.
    public MarkdownPreviewer(ISyntaxHighlighter? highlighter = null)
    {
        _highlighter = highlighter;
    }

    public bool CanPreview(string extension) => Extensions.Contains(extension);

    public Control Render(DocumentPreviewRequest request, DocumentPreviewTheme theme)
    {
        var blocks = MarkdownBlockDiff.Build(request);
        var renderer = new MarkdownRenderer(theme, _highlighter);
        var panel = new StackPanel
        {
            Margin = new Thickness(14, 12, 18, 28),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var visible = Visible(blocks, request.Mode == DocumentPreviewMode.Diff && request.CollapseUnchanged);
        var i = 0;
        while (i < blocks.Count)
        {
            if (visible[i])
            {
                panel.Children.Add(Unit(renderer, theme, blocks, i));
                i++;
                continue;
            }

            var start = i;
            while (i < blocks.Count && !visible[i])
            {
                i++;
            }

            panel.Children.Add(Gap(renderer, theme, blocks, start, i, panel));
        }

        if (blocks.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "(empty document)", Foreground = theme.MutedText });
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = panel,
        };

        // Fill the pane (so change bands span it) up to a readable measure;
        // left-aligned, not centered, in a wide window.
        scroller.SizeChanged += (_, e) =>
            panel.Width = Math.Max(0, Math.Min(MaxReadableWidth, e.NewSize.Width - panel.Margin.Left - panel.Margin.Right));
        return scroller;
    }

    // Which blocks stay visible when collapsing: changed ones, ContextBlocks
    // either side, and the heading a change sits under. A hidden run of one
    // block is not worth a bar, so it stays too.
    internal static bool[] Visible(IReadOnlyList<PreviewBlock> blocks, bool collapse)
    {
        var visible = new bool[blocks.Count];
        var anyChange = false;
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Change == BlockChange.Unchanged)
            {
                continue;
            }

            anyChange = true;
            for (var j = Math.Max(0, i - ContextBlocks); j <= Math.Min(blocks.Count - 1, i + ContextBlocks); j++)
            {
                visible[j] = true;
            }

            for (var j = i - 1; j >= 0; j--)
            {
                if (blocks[j].Block is HeadingBlock)
                {
                    visible[j] = true;
                    break;
                }
            }
        }

        if (!collapse || !anyChange)
        {
            Array.Fill(visible, true);
            return visible;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var lonely = !visible[i]
                && (i == 0 || visible[i - 1])
                && (i == blocks.Count - 1 || visible[i + 1]);
            if (lonely)
            {
                visible[i] = true;
            }
        }

        return visible;
    }

    private static Border Unit(MarkdownRenderer renderer, DocumentPreviewTheme theme, IReadOnlyList<PreviewBlock> blocks, int index)
    {
        var block = blocks[index];
        var (background, marker) = block.Change switch
        {
            BlockChange.Added => (theme.AddedBackground, theme.AddedMarker),
            BlockChange.Removed => (theme.RemovedBackground, theme.RemovedMarker),
            _ => ((IBrush)Brushes.Transparent, (IBrush)Brushes.Transparent),
        };

        return new Border
        {
            Margin = new Thickness(0, index == 0 ? 0 : Spacing(blocks[index - 1], block), 0, 0),
            Padding = new Thickness(12, 3, 8, 3),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = marker,
            Background = background,
            Child = renderer.Render(block),
            Tag = block.Change,
        };
    }

    // Tight between items of a list, roomier before headings.
    private static double Spacing(PreviewBlock previous, PreviewBlock block)
        => block.Block switch
        {
            HeadingBlock { Level: <= 2 } => 20,
            HeadingBlock => 14,
            _ when block.IsListItem && previous.IsListItem => block.List!.IsLoose ? 8 : 2,
            _ => 10,
        };

    private static Border Gap(MarkdownRenderer renderer, DocumentPreviewTheme theme, IReadOnlyList<PreviewBlock> blocks, int start, int end, StackPanel panel)
    {
        var count = end - start;
        var bar = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Height = 22,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)),
            BorderBrush = theme.Border,
            BorderThickness = new Thickness(0, 1),
            Child = new TextBlock
            {
                Text = $"⋯  {count} unchanged blocks  -  click to expand  ⋯",
                FontSize = 11.5,
                Foreground = theme.MutedText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        bar.Tapped += (_, _) =>
        {
            var at = panel.Children.IndexOf(bar);
            if (at < 0)
            {
                return;
            }

            panel.Children.RemoveAt(at);
            for (var i = start; i < end; i++)
            {
                panel.Children.Insert(at++, Unit(renderer, theme, blocks, i));
            }
        };

        return bar;
    }
}
