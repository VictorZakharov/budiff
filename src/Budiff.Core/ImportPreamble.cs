using System.Text.RegularExpressions;

namespace Budiff;

// Where reading a TypeScript file starts: past the header comments and the
// block of imports (and re-exports) at its top, on the first line after them
// that is not blank. A file opened fresh scrolls there instead of showing a
// screen of imports.
public static partial class ImportPreamble
{
    // A statement longer than this is not an import (a stray "import" that
    // never closes would otherwise swallow the file).
    private const int MaxStatementLines = 200;

    private static readonly string[] Extensions = [".ts", ".tsx", ".mts", ".cts"];

    public static bool AppliesTo(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return Array.Exists(Extensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    // 0-based index of the first useful line; 0 when there are no imports to
    // skip (or nothing follows them).
    public static int FirstUsefulLine(IReadOnlyList<string?> lines)
    {
        var lastImport = -1;
        var inComment = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = (lines[i] ?? string.Empty).Trim();
            if (inComment)
            {
                inComment = !line.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                inComment = line.IndexOf("*/", 2, StringComparison.Ordinal) < 0;
                continue;
            }

            // 'use strict'; "use client"
            if (line[0] is '\'' or '"')
            {
                continue;
            }

            var isImport = IsImportStart(line);
            if (!isImport && !IsReExportStart(line))
            {
                break;
            }

            // The whole statement: an import may span lines ("import {" ... "} from './x'").
            var end = i;
            while (!Ends(lines[end] ?? string.Empty) && end + 1 < lines.Count && end - i < MaxStatementLines)
            {
                end++;
            }

            // "export {" is a re-export only when it names a module.
            if (!isImport && !FromModule().IsMatch(Join(lines, i, end)))
            {
                break;
            }

            lastImport = end;
            i = end;
        }

        if (lastImport < 0)
        {
            return 0;
        }

        for (var i = lastImport + 1; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                return i;
            }
        }

        return 0;
    }

    private static bool IsImportStart(string line)
        => line.StartsWith("import", StringComparison.Ordinal)
           && (line.Length == 6 || line[6] is ' ' or '{' or '*' or '\'' or '"' or '\t');

    private static bool IsReExportStart(string line)
        => ExportList().IsMatch(line);

    private static bool Ends(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith(';')
               || FromModule().IsMatch(trimmed)
               || SideEffectImport().IsMatch(trimmed)
               || trimmed.Contains("require(", StringComparison.Ordinal);
    }

    private static string Join(IReadOnlyList<string?> lines, int from, int to)
    {
        var parts = new string?[to - from + 1];
        for (var i = from; i <= to; i++)
        {
            parts[i - from] = lines[i];
        }

        return string.Join("\n", parts);
    }

    [GeneratedRegex(@"\bfrom\s*['""]")]
    private static partial Regex FromModule();

    [GeneratedRegex(@"^\s*import\s*['""]")]
    private static partial Regex SideEffectImport();

    // export { a } / export * / export type { a } - the ones that can re-export.
    [GeneratedRegex(@"^export\s+(type\s+)?(\{|\*)")]
    private static partial Regex ExportList();
}
