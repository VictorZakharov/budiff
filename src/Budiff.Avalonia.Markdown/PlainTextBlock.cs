using Markdig.Syntax;

namespace Budiff.Avalonia.Markdown;

// One paragraph of a plain-text document (LICENSE, NOTICE, *.txt): shown as
// written - monospaced, line breaks kept - and diffed like a markdown block.
internal sealed class PlainTextBlock : LeafBlock
{
    public PlainTextBlock(string text, int start, int end, int line)
        : base(null)
    {
        Text = text;
        Span = new SourceSpan(start, end);
        Line = line;
    }

    public string Text { get; }
}
