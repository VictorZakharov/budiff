using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Budiff.Avalonia.Markdown;

// Which files the Preview takes, and how it shows them: markdown by its
// extensions; text documents (*.txt, and extensionless files such as LICENSE
// or NOTICE) as plain text - or, best effort, as markdown when the content
// reads like markdown.
internal static class DocumentKind
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text",
    };

    // Extensionless files that are code or config, not documents.
    private static readonly HashSet<string> CodeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Makefile", "GNUmakefile", "Dockerfile", "Containerfile", "Jenkinsfile", "Vagrantfile",
        "Gemfile", "Rakefile", "Podfile", "Brewfile", "Procfile", "Caddyfile", "Justfile",
        "Snakefile", "BUILD", "WORKSPACE", "CODEOWNERS",
    };

    private static readonly Regex Heading = new(@"^#{1,6}[ \t]+\S", RegexOptions.Multiline);
    private static readonly Regex Fence = new(@"^(```|~~~)", RegexOptions.Multiline);
    private static readonly Regex Link = new(@"\[[^\]\n]+\]\([^)\s]+\)");
    private static readonly Regex Bullet = new(@"^[-*+][ \t]+\S", RegexOptions.Multiline);
    private static readonly Regex Bold = new(@"\*\*[^*\n]+\*\*");
    private static readonly Regex TableRule = new(@"^\|?[ \t]*:?-{3,}:?[ \t]*\|", RegexOptions.Multiline);

    public static bool IsMarkdownExtension(string extension) => MarkdownExtensions.Contains(extension);

    public static bool IsTextExtension(string extension) => TextExtensions.Contains(extension);

    public static bool CanPreview(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return MarkdownExtensions.Contains(extension)
            || TextExtensions.Contains(extension)
            || (extension.Length == 0 && fileName.Length > 0 && !CodeNames.Contains(fileName));
    }

    // Plain text unless markdown by extension, or the content reads like it.
    // No file name (a bare request) means markdown.
    public static bool IsPlainText(string? fileName, string content)
        => !string.IsNullOrEmpty(fileName)
           && !MarkdownExtensions.Contains(Path.GetExtension(fileName))
           && !LooksLikeMarkdown(content);

    // A heading, a code fence, a link, a table, or a couple of bullets or
    // bold spans at the margin. (An indented "1. Definitions." in a license
    // is not a list here - as markdown it would turn into a code block.)
    internal static bool LooksLikeMarkdown(string content)
    {
        if (content.StartsWith("#!", StringComparison.Ordinal))
        {
            return false;
        }

        var sample = content.Length > 20000 ? content[..20000] : content;
        return Heading.IsMatch(sample)
            || Fence.IsMatch(sample)
            || Link.IsMatch(sample)
            || TableRule.IsMatch(sample)
            || Bullet.Matches(sample).Count >= 2
            || Bold.Matches(sample).Count >= 2;
    }
}
