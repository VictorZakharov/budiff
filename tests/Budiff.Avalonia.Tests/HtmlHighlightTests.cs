using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Budiff.Avalonia.TextMate;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The HTML grammar scopes every attribute of an <svg> element, and Angular's
// [prop] / (event) / #ref bindings, as invalid - which the theme paints red.
// Valid templates must read like the rest of the markup.
public sealed class HtmlHighlightTests
{
    private const string Template = """
        <section class="panel">
          <svg
            #sphere
            [attr.viewBox]="g.viewBox"
            (focusin)="focusHandler(true)"
          >
            <stop offset="0" stop-color="var(--accent)" />
            <path [attr.d]="arc.path" />
          </svg>
        </section>
        """;

    [Fact]
    public void SvgAndAngularAttributes_AreNotPaintedAsErrors()
    {
        var lines = Highlight(Template);

        // What a plain attribute looks like: class="panel".
        var name = Run(lines[0], "class").Brush;
        var value = Run(lines[0], "panel").Brush;

        Assert.All(lines.SelectMany(l => l), run => Assert.NotEqual(Colors.Red, Hue(run.Brush)));
        Assert.Equal(name, Run(lines[2], "#sphere").Brush);
        Assert.Equal(name, Run(lines[3], "[attr.viewBox]").Brush);
        Assert.Equal(value, Run(lines[3], "\"g.viewBox\"").Brush);
        Assert.Equal(name, Run(lines[4], "(focusin)").Brush);
        Assert.Equal(name, Run(lines[6], "offset").Brush);
        Assert.Equal(name, Run(lines[6], "stop-color").Brush);
        Assert.Equal(value, Run(lines[6], "\"var(--accent)\"").Brush);
        Assert.Equal(name, Run(lines[7], "[attr.d]").Brush);
    }

    private static List<IReadOnlyList<DiffRun>> Highlight(string text)
    {
        var lines = text.Split('\n');
        var session = new TextMateSyntaxHighlighter().CreateSession(".html", lines.Length)!;
        var result = new List<IReadOnlyList<DiffRun>>();
        object? state = null;
        foreach (var line in lines)
        {
            var highlighted = session.HighlightLine(line, state);
            state = highlighted.State;
            result.Add(highlighted.Runs);
        }

        return result;
    }

    private static DiffRun Run(IReadOnlyList<DiffRun> line, string text)
        => line.Single(r => r.Text.Trim() == text);

    // Dark+ paints invalid.illegal as #F44747: "red" here is anything that red.
    private static Color Hue(IBrush brush)
        => brush is ISolidColorBrush { Color: { R: > 0xE0, G: < 0x60, B: < 0x60 } } ? Colors.Red : Colors.Transparent;
}
