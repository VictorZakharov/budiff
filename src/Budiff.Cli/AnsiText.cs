// Pure ANSI-text measurement/manipulation primitives with direct unit tests.
internal static class AnsiText
{
    public static string ExpandTabs(string text) => text.Replace("\t", "    ", StringComparison.Ordinal);

    // Width of the text once tabs are expanded, without allocating the copy.
    public static int ExpandedLength(string text)
    {
        var length = text.Length;
        foreach (var ch in text)
        {
            if (ch == '\t')
            {
                length += 3;
            }
        }

        return length;
    }

    public static string Truncate(string text, int width)
    {
        if (text.Length <= width)
        {
            return text.PadRight(width);
        }

        return width <= 1 ? text[..width] : text[..(width - 1)] + ">";
    }

    public static string TruncatePlain(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        return width <= 1 ? text[..width] : text[..(width - 1)] + ">";
    }

    public static string PadAnsi(string text, int width)
    {
        var visible = VisibleLength(text);
        return visible >= width ? text : text + new string(' ', width - visible);
    }

    public static int VisibleLength(string text)
    {
        var count = 0;
        var inEscape = false;
        foreach (var ch in text)
        {
            if (ch == '\x1b')
            {
                inEscape = true;
                continue;
            }

            if (inEscape)
            {
                if (char.IsLetter(ch))
                {
                    inEscape = false;
                }

                continue;
            }

            count++;
        }

        return count;
    }
}
