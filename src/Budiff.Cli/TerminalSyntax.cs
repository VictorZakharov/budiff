using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TextMateSharp.Grammars;
using TextMateSharp.Themes;
using TmRegistry = TextMateSharp.Registry.Registry;

// Color gating + Windows VT enablement for the terminal renderer.
internal static class AnsiConsole
{
    // BUDIFF_FORCE_COLOR=1 keeps color when piping (e.g. `| less -R`) and in tests.
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("BUDIFF_FORCE_COLOR") == "1" || !Console.IsOutputRedirected;

    // Classic conhost needs virtual-terminal processing switched on explicitly,
    // or escape sequences render as garbage. Windows Terminal has it on already.
    public static void TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            const int StdOutputHandle = -11;
            const uint EnableVirtualTerminalProcessing = 0x0004;
            var handle = GetStdHandle(StdOutputHandle);
            if (GetConsoleMode(handle, out var mode))
            {
                SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            }
        }
        catch
        {
            // Best effort - a console that rejects VT just shows plain text.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}

// One syntax-colored piece of a line; Fg is a ready ANSI foreground prefix (or null
// for the theme default).
internal readonly record struct SyntaxChunk(string Text, string? Fg);

// TextMate-backed syntax colors for the terminal diff - the same DarkPlus grammar
// pipeline the GUI uses (see Budiff.Avalonia.TextMate), mapping theme colors to
// 24-bit ANSI foregrounds instead of brushes. TextMateSharp is UI-free, so the CLI
// stays Avalonia-free.
internal static class TerminalSyntax
{
    private const int MaxHighlightLines = 6000;
    private static readonly string Esc = ((char)27).ToString();

    internal static readonly object Gate = new();

    private static RegistryOptions? _options;
    private static TmRegistry? _registry;
    private static Theme? _theme;
    private static readonly Dictionary<string, IGrammar?> Grammars = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string?> FgCache = new();

    public static TerminalSyntaxSession? CreateSession(string extension, int rowCount)
    {
        if (string.IsNullOrWhiteSpace(extension) || rowCount > MaxHighlightLines)
        {
            return null;
        }

        lock (Gate)
        {
            try
            {
                _options ??= new RegistryOptions(ThemeName.DarkPlus);
                _registry ??= new TmRegistry(_options);
                _theme ??= _registry.GetTheme();
                if (!Grammars.TryGetValue(extension, out var grammar))
                {
                    var scope = _options.GetScopeByExtension(extension);
                    grammar = string.IsNullOrEmpty(scope) ? null : _registry.LoadGrammar(scope);
                    Grammars[extension] = grammar;
                }

                return grammar is null ? null : new TerminalSyntaxSession(grammar);
            }
            catch
            {
                return null;
            }
        }
    }

    // Assumes Gate is held (called from the tokenize loop).
    internal static string? Foreground(List<string> scopes)
    {
        if (_theme is null)
        {
            return null;
        }

        var fg = -1;
        foreach (var rule in _theme.Match(scopes))
        {
            if (fg == -1 && rule.foreground > 0)
            {
                fg = rule.foreground;
            }
        }

        if (fg <= 0)
        {
            return null;
        }

        if (FgCache.TryGetValue(fg, out var cached))
        {
            return cached;
        }

        string? ansi = null;
        try
        {
            var hex = _theme.GetColor(fg);
            if (hex is { Length: >= 7 } && hex[0] == '#')
            {
                var r = Convert.ToInt32(hex.Substring(1, 2), 16);
                var g = Convert.ToInt32(hex.Substring(3, 2), 16);
                var b = Convert.ToInt32(hex.Substring(5, 2), 16);
                ansi = Esc + $"[38;2;{r};{g};{b}m";
            }
        }
        catch
        {
            ansi = null;
        }

        FgCache[fg] = ansi;
        return ansi;
    }
}

// Per-file tokenization session threading the grammar state line-by-line for each
// diff side independently (old and new are separate line sequences).
internal sealed class TerminalSyntaxSession(IGrammar grammar)
{
    private IStateStack? _oldStack;
    private IStateStack? _newStack;

    public List<SyntaxChunk> Highlight(string text, bool oldSide)
    {
        var stack = oldSide ? _oldStack : _newStack;
        var chunks = new List<SyntaxChunk>();
        try
        {
            lock (TerminalSyntax.Gate)
            {
                var tokenized = grammar.TokenizeLine(text, stack, TimeSpan.FromMilliseconds(200));
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
                        chunks.Add(new SyntaxChunk(text[cursor..start], null));
                    }

                    chunks.Add(new SyntaxChunk(text[start..end], TerminalSyntax.Foreground(token.Scopes)));
                    cursor = end;
                }

                if (cursor < text.Length)
                {
                    chunks.Add(new SyntaxChunk(text[cursor..], null));
                }
            }
        }
        catch
        {
            chunks.Clear();
            chunks.Add(new SyntaxChunk(text, null));
        }

        if (chunks.Count == 0)
        {
            chunks.Add(new SyntaxChunk(text, null));
        }

        if (oldSide)
        {
            _oldStack = stack;
        }
        else
        {
            _newStack = stack;
        }

        return chunks;
    }
}
