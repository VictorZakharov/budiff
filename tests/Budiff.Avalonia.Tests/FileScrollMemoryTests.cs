using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Budiff;
using Budiff.App;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Each file opens where the reader left it; one never opened before starts at
// the top - or, for TypeScript, past its imports - not at the line the file
// shown before was scrolled to.
public sealed class FileScrollMemoryTests
{
    private const double RowHeight = 18;

    [AvaloniaFact]
    public void FilesRememberTheirOwnSpot_NewOnesStartAtTheTop()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-file-scroll");
        using (var repo = new Repository(root))
        {
            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 2), "Add long files", new()
            {
                ["src/First.cs"] = Lines("first", 300),
                ["src/Second.cs"] = Lines("second", 300),
                ["web/app.component.spec.ts"] = TypeScript(),
            });
        }

        var window = new MainWindow(root, gitRoot: true) { Width = 1600, Height = 900 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        Open(window, "First.cs");
        var scroller = RowsScroller(window);
        scroller.Offset = new Vector(0, 900);
        UiSnapshot.Pump();
        Assert.Equal(900, vm.Diff.ScrollSpot!.Offset, 0.5);

        // Never opened: the top, not First.cs's spot.
        Open(window, "Second.cs");
        Assert.Equal(0, RowsScroller(window).Offset.Y, 0.5);
        RowsScroller(window).Offset = new Vector(0, 360);
        UiSnapshot.Pump();

        // TypeScript: past the header and the imports, at the doc comment.
        Open(window, "app.component.spec.ts");
        Assert.Equal(13 * RowHeight, RowsScroller(window).Offset.Y, 0.5);
        Assert.Equal(1, RowsScroller(window).Opacity);

        // Back to the earlier ones: each where it was left.
        Open(window, "First.cs");
        Assert.Equal(900, RowsScroller(window).Offset.Y, 0.5);
        Open(window, "Second.cs");
        Assert.Equal(360, RowsScroller(window).Offset.Y, 0.5);

        // Closed and opened again: still remembered.
        vm.CloseDiffCommand.Execute(null);
        UiSnapshot.Pump();
        Open(window, "First.cs");
        Assert.Equal(900, RowsScroller(window).Offset.Y, 0.5);
        window.Close();
    }

    // In a two-sided diff the start never skips past a change, even one
    // among the imports.
    [AvaloniaFact]
    public void TypeScriptStart_StopsAtAChangedImport()
    {
        var before = TypeScript();
        var after = before.Replace("import { By } from '@angular/platform-browser';", "import { By, DomSanitizer } from '@angular/platform-browser';", StringComparison.Ordinal);
        Assert.NotEqual(before, after);

        var model = new DiffViewModel { FileName = "app.component.spec.ts", CollapseUnchanged = false };
        model.SetDiff(LineDiff.Compute(before, after));
        Assert.Equal(8, model.StartRow());

        // Wholly added: all change, the imports are still skipped.
        model.SetDiff(LineDiff.Compute(string.Empty, after));
        Assert.Equal(13, model.StartRow());

        // Not TypeScript: the top.
        model.FileName = "app.component.spec.js";
        Assert.Equal(0, model.StartRow());
    }

    // 13 lines of header and imports, then the code (0-based line 13).
    private static string TypeScript()
    {
        var text = new StringBuilder("""
            /**
             * The app component's tests.
             */
            import { ComponentFixture, TestBed } from '@angular/core/testing';
            import {
              HttpClientTestingModule,
              HttpTestingController,
            } from '@angular/common/http/testing';
            import { By } from '@angular/platform-browser';
            import { AppComponent } from './app.component';
            import { UserService } from './user.service';
            import './test-setup';

            /** Renders the shell. */
            describe('AppComponent', () => {

            """);
        for (var i = 0; i < 150; i++)
        {
            text.Append("  it('works ").Append(i).Append("', () => expect(true).toBe(true));\n");
        }

        return text.Append("});\n").ToString();
    }

    private static string Lines(string name, int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append("// ").Append(name).Append(" line ").Append(i).Append('\n');
        }

        return text.ToString();
    }

    private static void Open(MainWindow window, string name)
    {
        var vm = window.ViewModel;
        var row = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == name);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.FileName == name, name + " to load");
        UiSnapshot.Pump();
        UiSnapshot.Pump();
    }

    private static ScrollViewer RowsScroller(Window window)
        => window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "RowsScroller" && s.IsEffectivelyVisible);

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }
}
