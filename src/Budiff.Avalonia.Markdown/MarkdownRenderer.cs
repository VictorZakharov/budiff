using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Documents;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Layout;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Media.Immutable;
using global::Avalonia.Threading;
using MdInline = Markdig.Syntax.Inlines.Inline;
using ShapePath = global::Avalonia.Controls.Shapes.Path;

namespace Budiff.Avalonia.Markdown;

// Turns markdown blocks into Avalonia controls: selectable wrapped text for
// prose, a tinted panel for code (syntax-highlighted when a highlighter is
// given), grids for tables, pictures for the project's images. Links are
// clickable: another project file opens in budiff, "#heading" scrolls, a web
// address (marked with the usual external-link icon) opens in the browser.
internal sealed class MarkdownRenderer
{
    private const double BodySize = 13.5;
    private const double CodeSize = 12.5;
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");
    private static readonly string[] Bullets = { "•", "◦", "▪" };
    private static readonly Color LinkColor = Color.Parse("#58A6FF");

    // Material Design "open-in-new" (Apache 2.0) - the external-link mark.
    private const string ExternalIconPath = "M14,3V5H17.59L7.76,14.83L9.17,16.24L19,6.41V10H21V3M19,19H5V5H12V3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V12H19V19Z";

    private static readonly Dictionary<string, string> LanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ".cs", ["c#"] = ".cs", ["javascript"] = ".js", ["typescript"] = ".ts",
        ["python"] = ".py", ["bash"] = ".sh", ["shell"] = ".sh", ["zsh"] = ".sh", ["console"] = ".sh",
        ["powershell"] = ".ps1", ["pwsh"] = ".ps1", ["ps"] = ".ps1", ["yml"] = ".yaml",
        ["rust"] = ".rs", ["golang"] = ".go", ["c++"] = ".cpp", ["markdown"] = ".md",
        ["dockerfile"] = ".dockerfile", ["jsonc"] = ".json",
    };

    private static Cursor? _hand;

    private readonly DocumentPreviewTheme _theme;
    private readonly ISyntaxHighlighter? _highlighter;
    private readonly DocumentPreviewContext? _context;
    private readonly MarkdownImages? _images;
    private readonly Action<LinkTarget>? _activate;
    private readonly IBrush _codeBackground;
    private readonly IBrush _rule;
    private readonly IBrush _link = new ImmutableSolidColorBrush(LinkColor);

    // The side of the diff the block being rendered comes from: its images
    // load from that revision.
    private DocumentSide _side = DocumentSide.New;

    public MarkdownRenderer(
        DocumentPreviewTheme theme,
        ISyntaxHighlighter? highlighter,
        DocumentPreviewContext? context = null,
        MarkdownImages? images = null,
        Action<LinkTarget>? activate = null)
    {
        _theme = theme;
        _highlighter = highlighter;
        _context = context;
        _images = images;
        _activate = activate;
        var text = (theme.Text as ISolidColorBrush)?.Color ?? Colors.White;
        _codeBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x1A, text.R, text.G, text.B));
        _rule = new ImmutableSolidColorBrush(Color.FromArgb(0x38, text.R, text.G, text.B));
    }

    public Control Render(PreviewBlock unit, DocumentSide side = DocumentSide.New)
    {
        _side = side;
        return unit is { Block: ListItemBlock item, List: { } list }
            ? ListItem(item, list, unit.Number, _theme.Text, depth: 0)
            : Block(unit.Block, _theme.Text, depth: 0);
    }

    // The pointer over anything that follows a link.
    internal static Cursor HandCursor => _hand ??= new Cursor(StandardCursorType.Hand);

    // The link under a point of a rendered text block, if any.
    internal static LinkTarget? LinkAt(TextBlock block, Point point)
    {
        if (block.Tag is not LinkMap links)
        {
            return null;
        }

        var layout = block.TextLayout;
        var hit = layout.HitTestPoint(point);
        // Not hit.IsInside: it reads false over wrapped lines. The nearest
        // character's own box says whether the point is really on the text.
        var index = hit.CharacterHit.FirstCharacterIndex;
        if (!layout.HitTestTextRange(index, 1).Any(box => box.Inflate(1).Contains(point)))
        {
            return null;
        }

        // Each link's runs carry their own brush instance; find the shaped
        // run under the character and map its brush back to the link.
        foreach (var line in layout.TextLines)
        {
            if (index < line.FirstTextSourceIndex || index >= line.FirstTextSourceIndex + line.Length)
            {
                continue;
            }

            var position = line.FirstTextSourceIndex;
            foreach (var run in line.TextRuns)
            {
                if (index < position + run.Length)
                {
                    return run.Properties?.ForegroundBrush is { } brush && links.TryGetValue(brush, out var target) ? target : null;
                }

                position += run.Length;
            }
        }

        return null;
    }

    private Control Block(Block block, IBrush foreground, int depth) => block switch
    {
        HeadingBlock heading => Heading(heading, foreground),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline, foreground),
        ListBlock list => List(list, foreground, depth),
        AlertBlock alert => Alert(alert, depth),
        QuoteBlock quote => Quote(quote, depth),
        Table table => TableGrid(table, foreground),
        YamlFrontMatterBlock yaml => Code(yaml.Lines.ToString(), language: null, _theme.MutedText),
        FencedCodeBlock fenced => Code(fenced.Lines.ToString(), fenced.Info, foreground),
        CodeBlock code => Code(code.Lines.ToString(), language: null, foreground),
        ThematicBreakBlock => new Border { Height = 1, Background = _rule, Margin = new Thickness(0, 6) },
        HtmlBlock html => Html(html.Lines.ToString()),
        LinkReferenceDefinitionGroup => new Panel(),
        ContainerBlock container => Stack(container, foreground, depth),
        LeafBlock { Inline: { } inline } => Prose(inline, foreground, BodySize),
        LeafBlock leaf => Raw(leaf.Lines.ToString()),
        _ => new Panel(),
    };

    private StackPanel Stack(ContainerBlock container, IBrush foreground, int depth)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in container)
        {
            panel.Children.Add(Block(child, foreground, depth));
        }

        return panel;
    }

    private Control Heading(HeadingBlock heading, IBrush foreground)
    {
        var (size, weight) = heading.Level switch
        {
            1 => (24.0, FontWeight.Bold),
            2 => (20.0, FontWeight.Bold),
            3 => (17.0, FontWeight.SemiBold),
            4 => (15.0, FontWeight.SemiBold),
            _ => (BodySize, FontWeight.SemiBold),
        };
        var text = Prose(heading.Inline, heading.Level >= 6 ? _theme.MutedText : foreground, size);
        text.FontWeight = weight;
        return heading.Level <= 2
            ? new Border { BorderBrush = _rule, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 5), Child = text }
            : text;
    }

    private StackPanel List(ListBlock list, IBrush foreground, int depth)
    {
        var panel = new StackPanel { Spacing = list.IsLoose ? 8 : 3 };
        var number = list.IsOrdered && int.TryParse(list.OrderedStart ?? list.DefaultOrderedStart, out var start) ? start : 1;
        foreach (var child in list)
        {
            if (child is ListItemBlock item)
            {
                panel.Children.Add(ListItem(item, list, number++, foreground, depth));
            }
        }

        return panel;
    }

    private Grid ListItem(ListItemBlock item, ListBlock list, int number, IBrush foreground, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var marker = new TextBlock
        {
            Text = list.IsOrdered ? $"{number}{list.OrderedDelimiter}" : Bullets[depth % Bullets.Length],
            MinWidth = 18,
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = BodySize,
            Foreground = list.IsOrdered ? foreground : _theme.MutedText,
            TextAlignment = TextAlignment.Right,
        };
        grid.Children.Add(marker);

        var content = new StackPanel { Spacing = 6 };
        foreach (var child in item)
        {
            content.Children.Add(Block(child, foreground, depth + 1));
        }

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private Border Quote(QuoteBlock quote, int depth)
        => new()
        {
            BorderBrush = _rule,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Child = Stack(quote, _theme.MutedText, depth),
        };

    // GitHub alerts: > [!NOTE], > [!WARNING], ...
    private Border Alert(AlertBlock alert, int depth)
    {
        var kind = alert.Kind.ToString();
        var color = kind.ToUpperInvariant() switch
        {
            "TIP" => "#3FB950",
            "IMPORTANT" => "#AB7DF8",
            "WARNING" => "#D29922",
            "CAUTION" => "#F85149",
            _ => "#4493F8",
        };
        var brush = new ImmutableSolidColorBrush(Color.Parse(color));
        var body = Stack(alert, _theme.Text, depth);
        body.Children.Insert(0, new TextBlock
        {
            Text = kind.Length == 0 ? "Note" : char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant(),
            FontWeight = FontWeight.SemiBold,
            FontSize = BodySize,
            Foreground = brush,
        });
        return new Border
        {
            BorderBrush = brush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Child = body,
        };
    }

    private Control TableGrid(Table table, IBrush foreground)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };

        // Star weights from each column's longest cell text: narrow columns
        // stay narrow, prose columns (and pictures) get the room, and
        // everything wraps.
        for (var c = 0; c < columns; c++)
        {
            var longest = rows.Max(r => c < r.Count ? CellWeight((TableCell)r[c]) : 0);
            grid.ColumnDefinitions.Add(new ColumnDefinition(Math.Clamp(longest, 4, 40), GridUnitType.Star));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < rows[r].Count && c < columns; c++)
            {
                var content = Stack((TableCell)rows[r][c], foreground, depth: 0);
                var alignment = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                foreach (var text in content.Children.OfType<TextBlock>())
                {
                    text.TextAlignment = alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    };
                }

                foreach (var images in content.Children.OfType<WrapPanel>())
                {
                    images.HorizontalAlignment = alignment switch
                    {
                        TableColumnAlign.Center => HorizontalAlignment.Center,
                        TableColumnAlign.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Left,
                    };
                }

                var cell = new Border
                {
                    BorderBrush = _rule,
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(8, 4),
                    Background = rows[r].IsHeader ? _codeBackground : null,
                    Child = content,
                };
                if (rows[r].IsHeader)
                {
                    TextElement.SetFontWeight(cell, FontWeight.SemiBold);
                }

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private static int CellWeight(TableCell cell)
        => cell.Descendants<LinkInline>().Any(l => l.IsImage) ? 40 : PlainText(cell).Length;

    private Border Code(string text, string? language, IBrush foreground)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var block = new SelectableTextBlock
        {
            FontFamily = Mono,
            FontSize = CodeSize,
            TextWrapping = TextWrapping.Wrap,
            Foreground = foreground,
            Inlines = new InlineCollection(),
        };

        var session = Session(language, lines.Length);
        object? state = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                block.Inlines.Add(new LineBreak());
            }

            if (session is null)
            {
                block.Inlines.Add(new Run(lines[i]));
                continue;
            }

            var result = session.HighlightLine(lines[i], state);
            state = result.State;
            foreach (var run in result.Runs)
            {
                block.Inlines.Add(new Run(run.Text) { Foreground = run.Brush });
            }
        }

        return new Border
        {
            Background = _codeBackground,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = block,
        };
    }

    private ISyntaxHighlightSession? Session(string? language, int lineCount)
    {
        if (_highlighter is null || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var name = language.Trim();
        var extension = LanguageExtensions.TryGetValue(name, out var mapped) ? mapped : "." + name.ToLowerInvariant();
        try
        {
            return _highlighter.CreateSession(extension, lineCount);
        }
        catch
        {
            // An unknown or broken grammar just means plain text.
            return null;
        }
    }

    // Raw HTML shows as muted source - except a block that is only pictures
    // (a centered logo, a row of screenshots), which shows them.
    private Control Html(string html)
    {
        if (!MarkdownLinks.IsOnlyImages(html))
        {
            return Raw(html);
        }

        var row = ImageRow(MarkdownLinks.IsCentered(html) ? HorizontalAlignment.Center : HorizontalAlignment.Left);
        foreach (var image in MarkdownLinks.HtmlImages(html))
        {
            row.Children.Add(Picture(image.Source, image.Alt, Resolve(image.Href), image.Width, image.Height));
        }

        return row;
    }

    private SelectableTextBlock Raw(string text)
        => new()
        {
            Text = text.TrimEnd('\r', '\n'),
            FontFamily = Mono,
            FontSize = CodeSize,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _theme.MutedText,
        };

    // A paragraph of nothing but pictures lays them out as pictures, each
    // scaled down to fit; anything else is text.
    private Control Paragraph(ContainerInline? inline, IBrush foreground)
    {
        if (inline is null || !IsOnlyImages(inline))
        {
            return Prose(inline, foreground, BodySize);
        }

        var row = ImageRow(HorizontalAlignment.Left);
        AddPictures(row, inline, link: null);
        return row;
    }

    private static WrapPanel ImageRow(HorizontalAlignment alignment)
        => new() { Orientation = Orientation.Horizontal, HorizontalAlignment = alignment };

    private static bool IsOnlyImages(ContainerInline container)
    {
        var any = false;
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LinkInline { IsImage: true }:
                    any = true;
                    break;
                case LinkInline link when IsOnlyImages(link):
                    any = true;
                    break;
                case HtmlInline html when MarkdownLinks.HtmlImages(html.Tag).Count > 0:
                    any = true;
                    break;
                case HtmlInline html when MarkdownLinks.TryAnchor(html.Tag, out _):
                case LineBreakInline:
                    break;
                case LiteralInline literal when literal.Content.IsEmptyOrWhitespace():
                    break;
                default:
                    return false;
            }
        }

        return any;
    }

    private void AddPictures(Panel row, ContainerInline container, LinkTarget? link)
    {
        var current = link;
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LinkInline { IsImage: true } image:
                    row.Children.Add(Picture(image.Url, PlainText(image), current, width: null, height: null));
                    break;
                case LinkInline wrapper:
                    AddPictures(row, wrapper, Resolve(wrapper.Url));
                    break;
                case HtmlInline html when MarkdownLinks.TryAnchor(html.Tag, out var href):
                    current = href is null ? link : Resolve(href);
                    break;
                case HtmlInline html:
                    foreach (var image in MarkdownLinks.HtmlImages(html.Tag))
                    {
                        row.Children.Add(Picture(image.Source, image.Alt, current, image.Width, image.Height));
                    }

                    break;
            }
        }
    }

    // A picture from the project, loaded off the UI thread; until then, or
    // when it cannot be shown (a web address, SVG, missing on this side),
    // its alt text stands in. Sized like GitHub: natural size, scaled down to
    // fit, or the width/height the HTML asks for.
    private PictureSlot Picture(string? url, string alt, LinkTarget? link, double? width, double? height)
    {
        var placeholder = new TextBlock
        {
            FontSize = BodySize,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            Foreground = link is null ? _theme.MutedText : _link,
            Inlines = new InlineCollection { new Run("\U0001F5BC " + (alt.Length > 0 ? alt : url ?? "image")) },
        };
        if (link is { Kind: LinkKind.External })
        {
            placeholder.Inlines.Add(ExternalIcon(link, _link));
        }

        var slot = new PictureSlot
        {
            Child = placeholder,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 10, 2),
        };

        if (link is not null)
        {
            Clickable(slot, link);
        }
        else if (alt.Length > 0)
        {
            ToolTip.SetTip(slot, alt);
        }

        if (MarkdownLinks.Resolve(url, _context?.DocumentPath) is { Kind: LinkKind.File } source
            && _context is { } context
            && _images is { } images)
        {
            images.LoadAsync(context, source.Value, _side).ContinueWith(
                loaded => Dispatcher.UIThread.Post(() =>
                {
                    if (loaded.IsCompletedSuccessfully && loaded.Result is { } bitmap)
                    {
                        slot.Child = Fitted(bitmap, width, height);
                    }
                }),
                System.Threading.Tasks.TaskScheduler.Default);
        }

        return slot;
    }

    private static Image Fitted(Bitmap bitmap, double? width, double? height)
    {
        var sized = width is not null || height is not null;
        return new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            StretchDirection = sized ? StretchDirection.Both : StretchDirection.DownOnly,
            MaxWidth = width ?? (sized ? double.PositiveInfinity : bitmap.Size.Width),
            MaxHeight = height ?? (sized ? double.PositiveInfinity : bitmap.Size.Height),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    // Press and release (not Tapped): inside running text the text block
    // would otherwise take the press for a selection and the click with it.
    private void Clickable(Control control, LinkTarget link)
    {
        control.Cursor = HandCursor;
        ToolTip.SetTip(control, link.Display);
        var armed = false;
        control.PointerPressed += (_, e) =>
        {
            armed = e.GetCurrentPoint(control).Properties.IsLeftButtonPressed;
            e.Handled |= armed;
        };
        control.PointerReleased += (_, e) =>
        {
            if (armed && e.InitialPressMouseButton == MouseButton.Left && new Rect(control.Bounds.Size).Contains(e.GetPosition(control)))
            {
                _activate?.Invoke(link);
                e.Handled = true;
            }

            armed = false;
        };
    }

    private InlineUIContainer ExternalIcon(LinkTarget link, IBrush brush)
    {
        var icon = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 0, 1, 0),
            Child = new ShapePath
            {
                Data = StreamGeometry.Parse(ExternalIconPath),
                Fill = brush,
                Stretch = Stretch.Uniform,
                Width = 10,
                Height = 10,
            },
        };
        Clickable(icon, link);
        return new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center };
    }

    private LinkTarget? Resolve(string? url) => MarkdownLinks.Resolve(url, _context?.DocumentPath);

    private SelectableTextBlock Prose(ContainerInline? inline, IBrush foreground, double size)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            Foreground = foreground,
            Inlines = new InlineCollection(),
        };

        var links = new LinkMap();
        if (inline is not null)
        {
            AddChildren(block.Inlines, inline, default, links);
        }

        if (links.Count > 0)
        {
            block.Tag = links;
            FollowLinks(block);
        }

        // Text measures inline pictures without a width limit; cap them to
        // the text's own width so a screenshot mid-paragraph still fits.
        var pictures = block.Inlines.OfType<InlineUIContainer>().Select(i => i.Child).OfType<PictureSlot>().ToList();
        if (pictures.Count > 0)
        {
            block.SizeChanged += (_, e) =>
            {
                foreach (var picture in pictures)
                {
                    picture.MaxWidth = Math.Max(16, e.NewSize.Width - 4);
                }
            };
        }

        return block;
    }

    // Holds one picture (its placeholder until it loads).
    private sealed class PictureSlot : Decorator
    {
    }

    // A click (not a drag-select) on a link's text follows it; hovering one
    // shows the hand and where it goes.
    private void FollowLinks(SelectableTextBlock block)
    {
        LinkTarget? hovered = null;
        LinkTarget? pressed = null;
        var pressedAt = default(Point);

        void Hover(LinkTarget? link)
        {
            if (link == hovered)
            {
                return;
            }

            hovered = link;
            if (link is null)
            {
                block.ClearValue(InputElement.CursorProperty);
                ToolTip.SetTip(block, null);
            }
            else
            {
                block.Cursor = HandCursor;
                ToolTip.SetTip(block, link.Display);
            }
        }

        block.PointerMoved += (_, e) => Hover(LinkAt(block, e.GetPosition(block)));
        block.PointerExited += (_, _) => Hover(null);
        // Tunnel: read the link before the text block handles the press -
        // starting a selection restyles (and re-lays out) its runs.
        block.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(block);
            pressed = point.Properties.IsLeftButtonPressed ? LinkAt(block, point.Position) : null;
            pressedAt = point.Position;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        block.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            var link = pressed;
            pressed = null;
            var at = e.GetPosition(block);
            if (link is not null
                && e.InitialPressMouseButton == MouseButton.Left
                && Math.Abs(at.X - pressedAt.X) < 4 && Math.Abs(at.Y - pressedAt.Y) < 4)
            {
                _activate?.Invoke(link);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // Each link's own brush instance -> where it goes. Reference identity,
    // not color equality: every link is the same blue.
    internal sealed class LinkMap : Dictionary<IBrush, LinkTarget>
    {
        public LinkMap()
            : base(ReferenceEqualityComparer.Instance)
        {
        }
    }

    private readonly record struct Style(bool Bold, bool Italic, bool Strike, bool Muted, IBrush? LinkBrush);

    private void AddChildren(InlineCollection into, ContainerInline container, Style style, LinkMap links)
    {
        // <a href="...">text</a> written as inline HTML links its siblings.
        var current = style;
        LinkTarget? htmlLink = null;
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is HtmlInline html && MarkdownLinks.TryAnchor(html.Tag, out var href))
            {
                if (href is null)
                {
                    if (htmlLink is { Kind: LinkKind.External } && current.LinkBrush is { } brush)
                    {
                        into.Add(ExternalIcon(htmlLink, brush));
                    }

                    current = style;
                    htmlLink = null;
                }
                else
                {
                    htmlLink = Resolve(href);
                    current = Linked(style, htmlLink, links);
                }

                continue;
            }

            Add(into, child, current, links);
        }
    }

    private Style Linked(Style style, LinkTarget? target, LinkMap links)
    {
        var brush = new ImmutableSolidColorBrush(LinkColor);
        if (target is not null)
        {
            links[brush] = target;
        }

        return style with { LinkBrush = brush };
    }

    private void Add(InlineCollection into, MdInline inline, Style style, LinkMap links)
    {
        switch (inline)
        {
            case LiteralInline literal:
                into.Add(MakeRun(literal.Content.ToString(), style));
                break;
            case CodeInline code:
                var run = MakeRun(code.Content, style);
                run.FontFamily = Mono;
                run.FontSize = CodeSize;
                run.Background = _codeBackground;
                into.Add(run);
                break;
            case EmphasisInline emphasis:
                AddChildren(into, emphasis, Emphasize(style, emphasis), links);
                break;
            case LinkInline { IsImage: true } image:
                // A picture inside running text: inline, never wider than the
                // text it sits in.
                var picture = Picture(image.Url, PlainText(image), style.LinkBrush is { } linkBrush && links.TryGetValue(linkBrush, out var around) ? around : null, width: null, height: null);
                picture.Margin = new Thickness(0, 0, 2, 0);
                into.Add(new InlineUIContainer(picture));
                break;
            case LinkInline link:
                AddLink(into, link, Resolve(link.Url), style, links);
                break;
            case AutolinkInline autolink:
                var target = Resolve(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url);
                var linked = Linked(style, target, links);
                into.Add(MakeRun(autolink.Url, linked));
                AddExternalIcon(into, target, linked);
                break;
            case LineBreakInline lineBreak:
                into.Add(lineBreak.IsHard ? new LineBreak() : MakeRun(" ", style));
                break;
            case HtmlEntityInline entity:
                into.Add(MakeRun(entity.Transcoded.ToString(), style));
                break;
            case HtmlInline html:
                // Inline tags are dropped (their text, if any, is a sibling);
                // only <br> and <img> mean something visible.
                if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                {
                    into.Add(new LineBreak());
                }

                foreach (var image in MarkdownLinks.HtmlImages(html.Tag))
                {
                    into.Add(new InlineUIContainer(Picture(image.Source, image.Alt, style.LinkBrush is { } htmlBrush && links.TryGetValue(htmlBrush, out var htmlAround) ? htmlAround : null, image.Width, image.Height)));
                }

                break;
            case TaskList task:
                into.Add(MakeRun(task.Checked ? "☑ " : "☐ ", style));
                break;
            case ContainerInline container:
                AddChildren(into, container, style, links);
                break;
        }
    }

    private void AddLink(InlineCollection into, LinkInline link, LinkTarget? target, Style style, LinkMap links)
    {
        var linked = Linked(style, target, links);
        AddChildren(into, link, linked, links);
        AddExternalIcon(into, target, linked);
    }

    private void AddExternalIcon(InlineCollection into, LinkTarget? target, Style linked)
    {
        if (target is { Kind: LinkKind.External } && linked.LinkBrush is { } brush)
        {
            into.Add(ExternalIcon(target, brush));
        }
    }

    private static Style Emphasize(Style style, EmphasisInline emphasis)
        => emphasis.DelimiterChar == '~'
            ? style with { Strike = true }
            : emphasis.DelimiterCount switch
            {
                1 => style with { Italic = true },
                2 => style with { Bold = true },
                _ => style with { Bold = true, Italic = true },
            };

    private Run MakeRun(string text, Style style)
    {
        var run = new Run(text);
        if (style.Bold)
        {
            run.FontWeight = FontWeight.Bold;
        }

        if (style.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (style.LinkBrush is { } link)
        {
            run.Foreground = link;
        }
        else if (style.Muted)
        {
            run.Foreground = _theme.MutedText;
        }

        if (style.Strike || style.LinkBrush is not null)
        {
            var decorations = new TextDecorationCollection();
            if (style.Strike)
            {
                decorations.AddRange(TextDecorations.Strikethrough);
            }

            if (style.LinkBrush is not null)
            {
                decorations.AddRange(TextDecorations.Underline);
            }

            run.TextDecorations = decorations;
        }

        return run;
    }

    internal static string PlainText(ContainerBlock block)
    {
        var text = new StringBuilder();
        foreach (var leaf in block.Descendants<LeafBlock>())
        {
            if (leaf.Inline is { } inline)
            {
                AppendPlain(text, inline);
            }
        }

        return text.ToString();
    }

    internal static string PlainText(ContainerInline inline)
    {
        var text = new StringBuilder();
        AppendPlain(text, inline);
        return text.ToString();
    }

    private static void AppendPlain(StringBuilder text, ContainerInline container)
    {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case ContainerInline nested:
                    AppendPlain(text, nested);
                    break;
            }
        }
    }
}
