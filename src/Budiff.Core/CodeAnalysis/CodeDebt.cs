using System;
using System.Collections.Generic;
using System.IO;

namespace Budiff.CodeAnalysis;

// How deeply a file's code nests and how wide it runs, read from its
// indentation and line lengths: one cheap pass over the text the line
// counter reads anyway, the same for every language.
//   MaxDepth  - the deepest indentation level of a non-blank line;
//   DeepLines - non-blank lines at DeepFrom levels or deeper;
//   LongLines - lines longer than LongFrom characters.
// A level is the file's own indent step: its smallest indentation (tabs
// count 4 columns), kept between 2 and 8 columns.
public readonly record struct CodeShape(int MaxDepth, int DeepLines, int LongLines)
{
    public const int DeepFrom = 5;
    public const int LongFrom = 120;

    private const int TabColumns = 4;

    // Indents past this many columns count as this many.
    private const int MaxColumns = 255;

    public static CodeShape Measure(string text)
    {
        // One pass over the text (it runs for every scanned file): a count
        // per indentation width, then the levels from the file's step.
        Span<int> byIndent = stackalloc int[MaxColumns + 1];
        byIndent.Clear();
        var longLines = 0;
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var columns = 0;
            for (; i < text.Length && text[i] is ' ' or '\t'; i++)
            {
                columns += text[i] == '\t' ? TabColumns : 1;
            }

            // Blank lines and block-comment continuations (" * ...") say
            // nothing about nesting.
            var content = i < text.Length && text[i] is not ('\n' or '\r' or '*');
            while (i < text.Length && text[i] != '\n')
            {
                i++;
            }

            var length = i - start - (i > start && text[i - 1] == '\r' ? 1 : 0);
            if (length > LongFrom)
            {
                longLines++;
            }

            if (content)
            {
                byIndent[Math.Min(columns, MaxColumns)]++;
            }

            i++;
        }

        var step = 0;
        for (var c = 1; c <= MaxColumns && step == 0; c++)
        {
            if (byIndent[c] > 0)
            {
                step = c;
            }
        }

        step = step == 0 ? TabColumns : Math.Clamp(step, 2, 8);
        int maxDepth = 0, deep = 0;
        for (var c = 0; c <= MaxColumns; c++)
        {
            if (byIndent[c] == 0)
            {
                continue;
            }

            var depth = c / step;
            maxDepth = Math.Max(maxDepth, depth);
            if (depth >= DeepFrom)
            {
                deep += byIndent[c];
            }
        }

        return new CodeShape(maxDepth, deep, longLines);
    }
}

// A file's technical-debt score for the Debt city: 0 = clean, 1 = as bad as
// it gets, from the cheap metrics the bird's-eye already has.
//   size     (weight 0.35) - code lines: none up to 300, full at 3,000 (log scale);
//   nesting  (weight 0.40) - the worse of the deepest level (none up to 4,
//                            full at 9) and the share of deeply nested lines
//                            (full at a quarter of the code);
//   width    (weight 0.10) - the share of lines over 120 characters (full at 20%);
//   churn    (weight 0.15) - commits in the period: none at 0, full at 20 or
//                            more (log scale); without history the other three
//                            share its weight.
// Binary files, data, config and prose (JSON, YAML, CSV, Markdown, lock
// files, minified bundles...) have no score (-1): they hold no code to owe
// debt on, and no city gives them a lot.
public static class CodeDebt
{
    private static readonly HashSet<string> NotCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc", ".json5", ".jsonl", ".ndjson", ".geojson", ".har",
        ".csv", ".tsv", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env", ".properties",
        ".xml", ".svg", ".resx", ".plist", ".xsd", ".lock", ".sum", ".map", ".snap",
        ".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc", ".log",
    };

    // Whether a file holds code the score applies to.
    public static bool IsCode(AnalysisNode file)
        => file.IsFile && !file.IsBinary && IsCodeName(file.Name);

    public static bool IsCodeName(string name)
        => !NotCode.Contains(Path.GetExtension(name))
            && !name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase);

    public const double SizeWeight = 0.35;
    public const double NestingWeight = 0.40;
    public const double WidthWeight = 0.10;
    public const double ChurnWeight = 0.15;

    public static double Score(AnalysisNode file, bool hasHistory)
    {
        if (!IsCode(file))
        {
            return -1;
        }

        var code = Math.Max(1, file.Lines.Code);
        var lines = Math.Max(1, file.Lines.Total);
        var size = Clamp(Math.Log10(code / 300.0));
        var nesting = Math.Max(Clamp((file.Shape.MaxDepth - 4) / 5.0), Clamp(file.Shape.DeepLines / (double)code / 0.25));
        var width = Clamp(file.Shape.LongLines / (double)lines / 0.2);
        var structural = (SizeWeight * size) + (NestingWeight * nesting) + (WidthWeight * width);
        if (!hasHistory)
        {
            return structural / (1 - ChurnWeight);
        }

        var churn = Clamp(Math.Log(1 + file.CommitCount) / Math.Log(1 + 20));
        return structural + (ChurnWeight * churn);
    }

    // Plain words for the score, as the city's card says it.
    public static string Describe(double score) => score switch
    {
        < 0 => "no score",
        < 0.2 => "clean",
        < 0.4 => "fair",
        < 0.6 => "worn",
        < 0.8 => "run-down",
        _ => "dilapidated",
    };

    private static double Clamp(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
