using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Budiff.Avalonia.Markdown;

internal enum LinkKind
{
    // A web address, opened in the browser.
    External,

    // Another file of the project ('/'-separated, from the project root).
    File,

    // A heading of this document, by its GitHub-style slug.
    Anchor,
}

internal sealed record LinkTarget(LinkKind Kind, string Value)
{
    public string Display => Kind == LinkKind.Anchor ? "#" + Value : Value;
}

// An <img> found in raw HTML, with the link it sits in (if any).
internal sealed record HtmlImage(string Source, string Alt, double? Width, double? Height, string? Href);

// Resolves where links and image sources point, the way GitHub does for a
// file in a repository: relative to the document's folder, "/..." from the
// project root, "#..." a heading of the document itself.
internal static partial class MarkdownLinks
{
    private static readonly HashSet<string> WebSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "ftp",
    };

    // Null when the url is empty, uses another scheme (javascript:, file:,
    // data:, ...), or climbs out of the project; relative paths also need
    // the document's own path.
    public static LinkTarget? Resolve(string? url, string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        if (url.StartsWith('#'))
        {
            return url.Length > 1 ? new LinkTarget(LinkKind.Anchor, Unescape(url[1..])) : null;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        var colon = url.IndexOf(':');
        var stop = url.IndexOfAny(['/', '?', '#']);
        if (colon > 0 && (stop < 0 || colon < stop))
        {
            return WebSchemes.Contains(url[..colon]) && Uri.TryCreate(url, UriKind.Absolute, out _)
                ? new LinkTarget(LinkKind.External, url)
                : null;
        }

        if (documentPath is null)
        {
            return null;
        }

        var hash = url.IndexOf('#');
        var fragment = hash >= 0 && hash < url.Length - 1 ? Unescape(url[(hash + 1)..]) : null;
        var end = url.IndexOfAny(['?', '#']);
        var path = Unescape(end < 0 ? url : url[..end]).Replace('\\', '/');
        if (path.Length == 0)
        {
            return fragment is null ? null : new LinkTarget(LinkKind.Anchor, fragment);
        }

        var resolved = Normalize(path.StartsWith('/') ? string.Empty : Folder(documentPath), path);
        if (resolved is null || resolved.Length == 0)
        {
            return null;
        }

        // README.md#usage from README.md itself is just an anchor.
        return fragment is not null && string.Equals(resolved, Normalize(string.Empty, documentPath), StringComparison.Ordinal)
            ? new LinkTarget(LinkKind.Anchor, fragment)
            : new LinkTarget(LinkKind.File, resolved);
    }

    // GitHub's heading anchor: lower case, spaces to '-', punctuation dropped.
    public static string Slug(string heading)
    {
        var slug = new StringBuilder(heading.Length);
        foreach (var c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    // The <img> tags of an HTML fragment, each with the <a href> around it.
    public static IReadOnlyList<HtmlImage> HtmlImages(string html)
    {
        var images = new List<HtmlImage>();
        string? href = null;
        foreach (Match tag in TagPattern().Matches(html))
        {
            var name = tag.Groups[2].Value.ToLowerInvariant();
            var closing = tag.Groups[1].Length > 0;
            if (name == "a")
            {
                href = closing ? null : Attribute(tag.Value, "href");
            }
            else if (name == "img" && !closing && Attribute(tag.Value, "src") is { Length: > 0 } src)
            {
                images.Add(new HtmlImage(src, Attribute(tag.Value, "alt") ?? string.Empty, Pixels(Attribute(tag.Value, "width")), Pixels(Attribute(tag.Value, "height")), href));
            }
        }

        return images;
    }

    // True when the fragment shows nothing but images: every tag stripped,
    // no text is left.
    public static bool IsOnlyImages(string html)
        => HtmlImages(html).Count > 0
           && WebUtility.HtmlDecode(TagPattern().Replace(CommentPattern().Replace(html, string.Empty), string.Empty)).Trim().Length == 0;

    public static bool IsCentered(string html) => CenterPattern().IsMatch(html);

    // An <a> tag: true, with its href - or null for </a> and for an <a>
    // without one (a named anchor), which ends any link.
    public static bool TryAnchor(string tag, out string? href)
    {
        href = null;
        var match = TagPattern().Match(tag);
        if (!match.Success || match.Index != 0 || !string.Equals(match.Groups[2].Value, "a", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (match.Groups[1].Length == 0)
        {
            href = Attribute(tag, "href");
        }

        return true;
    }

    private static string Folder(string documentPath)
    {
        var slash = documentPath.Replace('\\', '/').LastIndexOf('/');
        return slash < 0 ? string.Empty : documentPath[..slash];
    }

    private static string? Normalize(string folder, string path)
    {
        var parts = new List<string>();
        foreach (var part in (folder + "/" + path).Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private static string? Attribute(string tag, string name)
    {
        foreach (Match attribute in AttributePattern().Matches(tag))
        {
            if (string.Equals(attribute.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                var value = attribute.Groups[2].Success ? attribute.Groups[2].Value
                    : attribute.Groups[3].Success ? attribute.Groups[3].Value
                    : attribute.Groups[4].Value;
                return WebUtility.HtmlDecode(value);
            }
        }

        return null;
    }

    // "300" or "300px"; percentages and junk are ignored.
    private static double? Pixels(string? value)
    {
        var text = value?.Trim();
        if (text is null)
        {
            return null;
        }

        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2];
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels > 0 ? pixels : null;
    }

    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"align\s*=\s*[""']?center", RegexOptions.IgnoreCase)]
    private static partial Regex CenterPattern();
}
