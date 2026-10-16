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
using global::Avalonia.Layout;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace Budiff.Avalonia.Markdown;

// Turns markdown blocks into Avalonia controls: selectable wrapped text for
// prose, a tinted panel for code (syntax-highlighted when a highlighter is
// given), grids for tables. Read-only - links show as links but do not open.
internal sealed class MarkdownRenderer
{
    private const double BodySize = 13.5;
    private const double CodeSize = 12.5;
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");
    private static readonly string[] Bullets = { "•", "◦", "▪" };

    private static readonly Dictionary<string, string> LanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ".cs", ["c#"] = ".cs", ["javascript"] = ".js", ["typescript"] = ".ts",
        ["python"] = ".py", ["bash"] = ".sh", ["shell"] = ".sh", ["zsh"] = ".sh", ["console"] = ".sh",
        ["powershell"] = ".ps1", ["pwsh"] = ".ps1", ["ps"] = ".ps1", ["yml"] = ".yaml",
        ["rust"] = ".rs", ["golang"] = ".go", ["c++"] = ".cpp", ["markdown"] = ".md",
        ["dockerfile"] = ".dockerfile", ["jsonc"] = ".json",
    };

    private readonly DocumentPreviewTheme _theme;
    private readonly ISyntaxHighlighter? _highlighter;
    private readonly IBrush _codeBackground;
    private readonly IBrush _rule;
    private readonly IBrush _link = new ImmutableSolidColorBrush(Color.Parse("#58A6FF"));

    public MarkdownRenderer(DocumentPreviewTheme theme, ISyntaxHighlighter? highlighter)
    {
        _theme = theme;
        _highlighter = highlighter;
        var text = (theme.Text as ISolidColorBrush)?.Color ?? Colors.White;
        _codeBackground = new ImmutableSolidColorBrush(Color.FromArgb(0x1A, text.R, text.G, text.B));
        _rule = new ImmutableSolidColorBrush(Color.FromArgb(0x38, text.R, text.G, text.B));
    }

    public Control Render(PreviewBlock unit)
        => unit is { Block: ListItemBlock item, List: { } list }
            ? ListItem(item, list, unit.Number, _theme.Text, depth: 0)
            : Block(unit.Block, _theme.Text, depth: 0);

    private Control Block(Block block, IBrush foreground, int depth) => block switch
    {
        HeadingBlock heading => Heading(heading, foreground),
        ParagraphBlock paragraph => Prose(paragraph.Inline, foreground, BodySize),
        ListBlock list => List(list, foreground, depth),
        AlertBlock alert => Alert(alert, depth),
        QuoteBlock quote => Quote(quote, depth),
        Table table => TableGrid(table, foreground),
        YamlFrontMatterBlock yaml => Code(yaml.Lines.ToString(), language: null, _theme.MutedText),
        FencedCodeBlock fenced => Code(fenced.Lines.ToString(), fenced.Info, foreground),
        CodeBlock code => Code(code.Lines.ToString(), language: null, foreground),
        ThematicBreakBlock => new Border { Height = 1, Background = _rule, Margin = new Thickness(0, 6) },
        HtmlBlock html => Raw(html.Lines.ToString()),
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
        // stay narrow, prose columns get the room, and everything wraps.
        for (var c = 0; c < columns; c++)
        {
            var longest = rows.Max(r => c < r.Count ? PlainText((TableCell)r[c]).Length : 0);
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

    private SelectableTextBlock Raw(string text)
        => new()
        {
            Text = text.TrimEnd('\r', '\n'),
            FontFamily = Mono,
            FontSize = CodeSize,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _theme.MutedText,
        };

    private SelectableTextBlock Prose(ContainerInline? inline, IBrush foreground, double size)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            Foreground = foreground,
            Inlines = new InlineCollection(),
        };

        if (inline is not null)
        {
            AddChildren(block.Inlines, inline, default);
        }

        return block;
    }

    private readonly record struct Style(bool Bold, bool Italic, bool Strike, bool Link, bool Muted);

    private void AddChildren(InlineCollection into, ContainerInline container, Style style)
    {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            Add(into, child, style);
        }
    }

    private void Add(InlineCollection into, MdInline inline, Style style)
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
                AddChildren(into, emphasis, Emphasize(style, emphasis));
                break;
            case LinkInline { IsImage: true } image:
                var alt = PlainText(image);
                into.Add(MakeRun("\U0001F5BC " + (alt.Length > 0 ? alt : image.Url ?? "image"), style with { Italic = true, Muted = true }));
                break;
            case LinkInline link:
                AddChildren(into, link, style with { Link = true });
                break;
            case AutolinkInline autolink:
                into.Add(MakeRun(autolink.Url, style with { Link = true }));
                break;
            case LineBreakInline lineBreak:
                into.Add(lineBreak.IsHard ? new LineBreak() : MakeRun(" ", style));
                break;
            case HtmlEntityInline entity:
                into.Add(MakeRun(entity.Transcoded.ToString(), style));
                break;
            case HtmlInline html:
                // Inline tags are dropped (their text, if any, is a sibling);
                // only <br> means something visible.
                if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                {
                    into.Add(new LineBreak());
                }

                break;
            case TaskList task:
                into.Add(MakeRun(task.Checked ? "☑ " : "☐ ", style));
                break;
            case ContainerInline container:
                AddChildren(into, container, style);
                break;
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

        if (style.Link)
        {
            run.Foreground = _link;
        }
        else if (style.Muted)
        {
            run.Foreground = _theme.MutedText;
        }

        if (style.Strike || style.Link)
        {
            var decorations = new TextDecorationCollection();
            if (style.Strike)
            {
                decorations.AddRange(TextDecorations.Strikethrough);
            }

            if (style.Link)
            {
                decorations.AddRange(TextDecorations.Underline);
            }

            run.TextDecorations = decorations;
        }

        return run;
    }

    private static string PlainText(ContainerBlock block)
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

    private static string PlainText(ContainerInline inline)
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
