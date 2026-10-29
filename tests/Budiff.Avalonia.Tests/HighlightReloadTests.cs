using System;
using System.Linq;
using System.Text;
using Avalonia.Headless.XUnit;
using Budiff;
using Budiff.Avalonia.TextMate;
using Xunit;

namespace Budiff.Avalonia.Tests;

// A reload that gives back the same rows (a save, a working-tree refresh)
// while the file is still being highlighted kept the rows - and dropped the
// highlighting still under way, so every line it had not reached yet stayed
// plain: scroll there and the code had no colour.
public sealed class HighlightReloadTests
{
    [AvaloniaFact]
    public void SameRowsReload_MidHighlight_StillColoursTheWholeFile()
    {
        var oldText = Source(changed: false);
        var newText = Source(changed: true);
        var model = new DiffViewModel { Title = "orders.spec.ts", Extension = ".ts", SyntaxHighlighter = new TextMateSyntaxHighlighter() };
        model.BeginLoad(() => new DiffResult(null, LineDiff.Compute(oldText, newText)));
        WaitUntil(() => model.Rows.Count > 0, "rows");

        // Highlighting has only begun: reload the same text.
        Assert.False(Coloured(model.Rows.Last(r => !r.IsGap)), "the end of the file is not highlighted yet");
        model.BeginLoad(() => new DiffResult(null, LineDiff.Compute(oldText, newText)));

        WaitUntil(() => model.Rows.Where(r => !r.IsGap && (r.Source?.NewText ?? string.Empty).Contains("const", StringComparison.Ordinal)).All(Coloured), "every line coloured", seconds: 60);
    }

    private static bool Coloured(DiffRowVm row)
        => (row.NewRuns ?? row.OldRuns) is { } runs && runs.Select(r => r.Brush).Distinct().Count() > 1;

    private static void WaitUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            System.Threading.Thread.Sleep(25);
            UiSnapshot.Pump();
        }
    }

    private static string Source(bool changed)
    {
        var text = new StringBuilder("import { Component } from '@angular/core';\n\n");
        for (var i = 0; i < 700; i++)
        {
            text.Append("describe('orders ").Append(i).Append("', () => {\n");
            text.Append("    it('totals the rows', async () => {\n");
            text.Append("        const total = await service.total(").Append(changed && i % 97 == 0 ? "rows, 2" : "rows").Append(");\n");
            text.Append("        expect(total).toBe(").Append(i).Append(");\n");
            text.Append("    });\n});\n");
        }

        return text.ToString();
    }
}
