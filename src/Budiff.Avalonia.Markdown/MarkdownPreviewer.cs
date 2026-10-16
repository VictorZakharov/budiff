using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
// Pictures load from the project (each side from its own revision); links
// open project files in budiff, scroll to "#headings", or open the browser.
public sealed class MarkdownPreviewer : IDocumentPreviewer
{
    private const int ContextBlocks = 1;
    private const double MaxReadableWidth = 1000;

    private readonly ISyntaxHighlighter? _highlighter;
    private readonly MarkdownImages _images = new();

    // The highlighter (optional) colors fenced code blocks by their language.
    public MarkdownPreviewer(ISyntaxHighlighter? highlighter = null)
    {
        _highlighter = highlighter;
    }

    // Opens a web link: the system browser (tests swap it out).
    internal Func<TopLevel, Uri, Task> OpenExternal { get; set; } = (top, uri) => top.Launcher.LaunchUriAsync(uri);

    public bool CanPreview(string extension) => DocumentKind.IsMarkdownExtension(extension) || DocumentKind.IsTextExtension(extension);

    public bool CanPreviewFile(string fileName) => DocumentKind.CanPreview(fileName);

    public Control Render(DocumentPreviewRequest request, DocumentPreviewTheme theme)
        => new Session(this, request, theme).Build();

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

    // GitHub's heading anchors: slug -> block, the document's new version
    // first (a removed heading only answers when nothing else does), and a
    // repeated slug numbered "-1", "-2", ...
    internal static Dictionary<string, int> Anchors(IReadOnlyList<PreviewBlock> blocks)
    {
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var removed in new[] { false, true })
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Block is not HeadingBlock { Inline: { } inline } || (blocks[i].Change == BlockChange.Removed) != removed)
                {
                    continue;
                }

                var slug = MarkdownLinks.Slug(MarkdownRenderer.PlainText(inline));
                var count = seen.GetValueOrDefault(slug);
                seen[slug] = count + 1;
                anchors.TryAdd(count == 0 ? slug : $"{slug}-{count}", i);
            }
        }

        return anchors;
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

    // One rendered preview: knows which blocks are on screen (and which are
    // folded into which bar) so a "#heading" link can unfold and scroll.
    private sealed class Session
    {
        private readonly MarkdownPreviewer _owner;
        private readonly DocumentPreviewRequest _request;
        private readonly DocumentPreviewTheme _theme;
        private readonly IReadOnlyList<PreviewBlock> _blocks;
        private readonly MarkdownRenderer _renderer;
        private readonly Border?[] _units;
        // Folded block -> the bar holding it (and that bar's whole run).
        private readonly Dictionary<int, (Border Bar, int Start, int End)> _gaps = new();
        private readonly StackPanel _panel = new()
        {
            Margin = new Thickness(14, 12, 18, 28),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        private readonly ScrollViewer _scroller = new()
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        private Dictionary<string, int>? _anchors;

        public Session(MarkdownPreviewer owner, DocumentPreviewRequest request, DocumentPreviewTheme theme)
        {
            _owner = owner;
            _request = request;
            _theme = theme;
            _blocks = MarkdownBlockDiff.Build(request);
            _renderer = new MarkdownRenderer(theme, owner._highlighter, request.Context, owner._images, Activate);
            _units = new Border?[_blocks.Count];
        }

        public ScrollViewer Build()
        {
            var visible = Visible(_blocks, _request.Mode == DocumentPreviewMode.Diff && _request.CollapseUnchanged);
            var i = 0;
            while (i < _blocks.Count)
            {
                if (visible[i])
                {
                    _panel.Children.Add(Unit(i));
                    i++;
                    continue;
                }

                var start = i;
                while (i < _blocks.Count && !visible[i])
                {
                    i++;
                }

                _panel.Children.Add(Gap(start, i));
            }

            if (_blocks.Count == 0)
            {
                _panel.Children.Add(new TextBlock { Text = "(empty document)", Foreground = _theme.MutedText });
            }

            _scroller.Content = _panel;

            // Fill the pane (so change bands span it) up to a readable measure;
            // left-aligned, not centered, in a wide window.
            _scroller.SizeChanged += (_, e) =>
                _panel.Width = Math.Max(0, Math.Min(MaxReadableWidth, e.NewSize.Width - _panel.Margin.Left - _panel.Margin.Right));
            return _scroller;
        }

        private Border Unit(int index)
        {
            var block = _blocks[index];
            var (background, marker) = block.Change switch
            {
                BlockChange.Added => (_theme.AddedBackground, _theme.AddedMarker),
                BlockChange.Removed => (_theme.RemovedBackground, _theme.RemovedMarker),
                _ => ((IBrush)Brushes.Transparent, (IBrush)Brushes.Transparent),
            };

            // A removed block - or the whole document when only the old side
            // exists - shows the old revision's pictures.
            var side = block.Change == BlockChange.Removed || _request.Mode == DocumentPreviewMode.OldOnly
                ? DocumentSide.Old
                : DocumentSide.New;
            var unit = new Border
            {
                Margin = new Thickness(0, index == 0 ? 0 : Spacing(_blocks[index - 1], block), 0, 0),
                Padding = new Thickness(12, 3, 8, 3),
                BorderThickness = new Thickness(3, 0, 0, 0),
                BorderBrush = marker,
                Background = background,
                Child = _renderer.Render(block, side),
                Tag = block.Change,
            };
            _units[index] = unit;
            return unit;
        }

        private Border Gap(int start, int end)
        {
            var bar = new Border
            {
                Margin = new Thickness(0, 10, 0, 0),
                Height = 22,
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)),
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(0, 1),
                Child = new TextBlock
                {
                    Text = $"⋯  {end - start} unchanged blocks  -  click to expand  ⋯",
                    FontSize = 11.5,
                    Foreground = _theme.MutedText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            for (var i = start; i < end; i++)
            {
                _gaps[i] = (bar, start, end);
            }

            bar.Tapped += (_, _) => Expand(bar, start, end);
            return bar;
        }

        private void Expand(Border bar, int start, int end)
        {
            var at = _panel.Children.IndexOf(bar);
            if (at < 0)
            {
                return;
            }

            _panel.Children.RemoveAt(at);
            for (var i = start; i < end; i++)
            {
                _gaps.Remove(i);
                _panel.Children.Insert(at++, Unit(i));
            }
        }

        private void Activate(LinkTarget link)
        {
            switch (link.Kind)
            {
                case LinkKind.Anchor:
                    ScrollTo(link.Value);
                    break;
                case LinkKind.File:
                    _request.Context?.OpenFile(link.Value);
                    break;
                case LinkKind.External:
                    if (TopLevel.GetTopLevel(_scroller) is { } top && Uri.TryCreate(link.Value, UriKind.Absolute, out var uri))
                    {
                        _ = OpenSafely(top, uri);
                    }

                    break;
            }
        }

        private async Task OpenSafely(TopLevel top, Uri uri)
        {
            try
            {
                await _owner.OpenExternal(top, uri);
            }
            catch (Exception)
            {
                // No browser / launcher: nothing to do.
            }
        }

        // Unfolds the heading if it is folded away, then puts it at the top.
        internal void ScrollTo(string anchor)
        {
            _anchors ??= Anchors(_blocks);
            if (!_anchors.TryGetValue(anchor.ToLowerInvariant(), out var index))
            {
                return;
            }

            if (_units[index] is null && _gaps.TryGetValue(index, out var gap))
            {
                Expand(gap.Bar, gap.Start, gap.End);
            }

            if (_units[index] is not { } unit)
            {
                return;
            }

            _scroller.UpdateLayout();
            if (unit.TranslatePoint(default, _panel) is { } top)
            {
                _scroller.Offset = new Vector(_scroller.Offset.X, Math.Max(0, top.Y + _panel.Margin.Top - 6));
            }
        }
    }
}
