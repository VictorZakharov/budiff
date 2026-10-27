using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Budiff.Avalonia;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;
using TextMateSharp.Grammars;
using TextMateSharp.Themes;
using TmRegistry = TextMateSharp.Registry.Registry;

namespace Budiff.Avalonia.TextMate;

public sealed class TextMateSyntaxHighlighter : ISyntaxHighlighter
{
    private const int MaxHighlightLines = 6000;

    public ISyntaxHighlightSession? CreateSession(string extension, int lineCount)
    {
        if (lineCount > MaxHighlightLines)
        {
            return null;
        }

        var grammar = TextMatePalette.Grammar(extension);
        return grammar is null ? null : new TextMateSyntaxHighlightSession(grammar);
    }
}

internal sealed partial class TextMateSyntaxHighlightSession(IGrammar grammar) : ISyntaxHighlightSession
{
    private readonly bool _html = grammar.GetScopeName()?.StartsWith("text.html", StringComparison.Ordinal) == true;

    public SyntaxHighlightResult HighlightLine(string text, object? state)
    {
        text ??= string.Empty;
        IStateStack? stack = state as IStateStack;
        var runs = new List<DiffRun>();

        try
        {
            lock (TextMatePalette.Gate)
            {
                var tokenized = grammar.TokenizeLine(text, stack, TimeSpan.FromMilliseconds(500));
                stack = tokenized.RuleStack;

                var cursor = 0;
                foreach (var token in tokenized.Tokens)
                {
                    var start = Math.Max(token.StartIndex, cursor);
                    var end = Math.Min(token.EndIndex, text.Length);
                    if (end <= cursor)
                    {
                        continue;
                    }

                    if (start > cursor)
                    {
                        runs.Add(new DiffRun(text[cursor..start], TextMatePalette.DefaultBrush));
                    }

                    if (token.Scopes.Count > 0 && token.Scopes[^1].StartsWith("invalid.", StringComparison.Ordinal))
                    {
                        AddInvalid(runs, text[start..end], token.Scopes);
                    }
                    else
                    {
                        runs.Add(new DiffRun(text[start..end], TextMatePalette.Foreground(token.Scopes)));
                    }

                    cursor = end;
                }

                if (cursor < text.Length)
                {
                    runs.Add(new DiffRun(text[cursor..], TextMatePalette.DefaultBrush));
                }
            }
        }
        catch
        {
            runs.Clear();
            runs.Add(new DiffRun(text, TextMatePalette.DefaultBrush));
        }

        if (runs.Count == 0)
        {
            runs.Add(new DiffRun(text, TextMatePalette.DefaultBrush));
        }

        return new SyntaxHighlightResult(runs, stack);
    }

    // A grammar's "invalid" is not ours to judge in a diff - and the HTML one
    // calls every attribute of an <svg> element, or Angular's [prop] / (event)
    // / #ref bindings, invalid, painting valid templates red. Such a run gets
    // the colours of what it holds: name="value" attributes in HTML, else the
    // text around it.
    private void AddInvalid(List<DiffRun> runs, string text, List<string> scopes)
    {
        var outer = scopes.FindAll(s => !s.StartsWith("invalid.", StringComparison.Ordinal));
        var plain = TextMatePalette.Foreground(outer);
        if (!_html)
        {
            runs.Add(new DiffRun(text, plain));
            return;
        }

        var cursor = 0;
        foreach (Match attribute in HtmlAttribute().Matches(text))
        {
            if (attribute.Index > cursor)
            {
                runs.Add(new DiffRun(text[cursor..attribute.Index], plain));
            }

            Add(runs, attribute.Groups["name"], outer, "entity.other.attribute-name.html");
            Add(runs, attribute.Groups["eq"], outer, "punctuation.separator.key-value.html");
            Add(runs, attribute.Groups["value"], outer, "string.quoted.double.html");
            cursor = attribute.Index + attribute.Length;
        }

        if (cursor < text.Length)
        {
            runs.Add(new DiffRun(text[cursor..], plain));
        }
    }

    private static void Add(List<DiffRun> runs, Group part, List<string> outer, string scope)
    {
        if (part.Success && part.Length > 0)
        {
            runs.Add(new DiffRun(part.Value, TextMatePalette.Foreground([.. outer, scope])));
        }
    }

    [GeneratedRegex(@"(?<name>[^\s=""'/>]+)(?:(?<eq>\s*=\s*)(?<value>""[^""]*""?|'[^']*'?|[^\s>]+))?")]
    private static partial Regex HtmlAttribute();
}

internal static class TextMatePalette
{
    private static readonly RegistryOptions Options = new(ThemeName.DarkPlus);
    private static readonly TmRegistry Registry = new(Options);
    private static readonly Theme SyntaxTheme = Registry.GetTheme();
    private static readonly Dictionary<string, IGrammar?> Grammars = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, IBrush> BrushCache = new();

    public static readonly IBrush DefaultBrush = new ImmutableSolidColorBrush(Color.Parse("#D4D4D4"));

    public static object Gate { get; } = new();

    public static IGrammar? Grammar(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        lock (Gate)
        {
            if (Grammars.TryGetValue(extension, out var cached))
            {
                return cached;
            }

            IGrammar? grammar = null;
            try
            {
                var scope = Options.GetScopeByExtension(extension);
                if (!string.IsNullOrEmpty(scope))
                {
                    grammar = Registry.LoadGrammar(scope);
                }
            }
            catch
            {
                grammar = null;
            }

            Grammars[extension] = grammar;
            return grammar;
        }
    }

    public static IBrush Foreground(List<string> scopes)
    {
        lock (Gate)
        {
            var fg = -1;
            foreach (var rule in SyntaxTheme.Match(scopes))
            {
                if (fg == -1 && rule.foreground > 0)
                {
                    fg = rule.foreground;
                }
            }

            if (fg <= 0)
            {
                return DefaultBrush;
            }

            if (BrushCache.TryGetValue(fg, out var cached))
            {
                return cached;
            }

            IBrush brush;
            try
            {
                brush = new ImmutableSolidColorBrush(Color.Parse(SyntaxTheme.GetColor(fg)));
            }
            catch
            {
                brush = DefaultBrush;
            }

            BrushCache[fg] = brush;
            return brush;
        }
    }
}
