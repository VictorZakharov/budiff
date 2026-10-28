using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// A wholly deleted (or added) TypeScript file shows in one pane, and its
// start skips the imports. When the file fits the pane there is nothing to
// scroll: line 1 must sit at the top. Opened as the diff pane itself opened
// (the pane not laid out yet), the start row used to be brought into view
// at no width, and line 1 then sat below a blank band.
public sealed class WholeFileStartTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void ShortDeletedAndAddedFiles_StartAtLineOne(double scale)
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-whole-file-start-" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using (var repo = new Repository(root))
        {
            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 2), "Add the widget modules", new()
            {
                ["web/widget/widget.module.ts"] = Module("Widget"),
                ["web/sprocket/sprocket.module.ts"] = Module("Sprocket"),
            });

            // One commit drops both, adds another and a long file.
            foreach (var gone in new[] { "web/widget/widget.module.ts", "web/sprocket/sprocket.module.ts" })
            {
                File.Delete(Path.Combine(root, gone.Replace('/', Path.DirectorySeparatorChar)));
                Commands.Remove(repo, gone);
            }

            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Grace Hopper", "grace@example.com", daysAgo: 1), "Replace the widget modules", new()
            {
                ["web/gadget/gadget.module.ts"] = Module("Gadget"),
                ["web/list/list.ts"] = string.Concat(Enumerable.Range(0, 400).Select(i => $"export const item{i} = {i};\n")),
            });
        }

        // A pane a little taller than the file: the start row (past the
        // imports) can't reach the top.
        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        var window = new MainWindow(root, gitRoot: true, settings: null, commit, "web/widget/widget.module.ts") { Width = 1800, Height = 400 };
        window.Show();
        global::Avalonia.Headless.HeadlessWindowExtensions.SetRenderScaling(window, scale);
        var vm = window.ViewModel;
        var suffix = "-" + (scale * 100).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The window's first file, opening the pane.
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.FileName == "widget.module.ts", "the deleted file");
        Settle();
        Assert.True(vm.Diff.IsSinglePane);
        Assert.DoesNotContain(vm.Diff.Rows, r => r.Kind == DiffKind.Added);
        Assert.True(vm.Diff.StartRow() > 0);
        UiSnapshot.Capture(window, "whole-file-deleted-first" + suffix);
        AssertLineOneOnTop(window);

        // The pane closed, then opened again on the added file.
        vm.CloseDiffCommand.Execute(null);
        Settle();
        Open(window, "gadget.module.ts");
        Assert.DoesNotContain(vm.Diff.Rows, r => r.Kind == DiffKind.Removed);
        UiSnapshot.Capture(window, "whole-file-added" + suffix);
        AssertLineOneOnTop(window);

        // In the open pane, after a long file scrolled well down.
        Open(window, "list.ts");
        Scroller(window).Offset = new global::Avalonia.Vector(0, 3000);
        Settle();
        Open(window, "sprocket.module.ts");
        Assert.DoesNotContain(vm.Diff.Rows, r => r.Kind == DiffKind.Added);
        UiSnapshot.Capture(window, "whole-file-deleted" + suffix);
        AssertLineOneOnTop(window);
        window.Close();
    }

    private static void Open(MainWindow window, string name)
    {
        var vm = window.ViewModel;
        var row = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == name);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.FileName == name, name + " to load");
        Settle();
        Assert.True(vm.Diff.IsSinglePane);
    }

    private static ScrollViewer Scroller(MainWindow window)
        => window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "RowsScroller" && s.IsEffectivelyVisible);

    // About 20 lines: a block of imports, then the module.
    private static string Module(string name) => $$"""
        import { CommonModule } from '@angular/common';
        import { NgModule } from '@angular/core';
        import { ReactiveFormsModule } from '@angular/forms';

        import {
          ButtonModule,
          CheckboxModule,
          InputModule,
          SelectModule,
        } from '@example/ui-kit';

        import { {{name}}Component } from './{{name.ToLowerInvariant()}}.component';
        import { {{name}}Service } from './{{name.ToLowerInvariant()}}.service';

        @NgModule({
          declarations: [{{name}}Component],
          imports: [CommonModule, ReactiveFormsModule, ButtonModule, CheckboxModule, InputModule, SelectModule],
          providers: [{{name}}Service],
          exports: [{{name}}Component],
        })
        export class {{name}}Module {}

        """;

    // Line 1 (row 0) at the top of the pane, unscrolled and shown.
    private static void AssertLineOneOnTop(MainWindow window)
    {
        var scroller = Scroller(window);
        var host = window.GetVisualDescendants().OfType<ItemsControl>().Single(h => h.Name == "RowsHost" && h.IsEffectivelyVisible);
        Assert.True(scroller.Extent.Height <= scroller.Viewport.Height, $"the file fits the pane (extent {scroller.Extent.Height}, viewport {scroller.Viewport.Height})");
        Assert.Equal(1, scroller.Opacity);
        Assert.Equal(0, scroller.Offset.Y, 0.5);
        var row = host.ContainerFromIndex(0);
        Assert.NotNull(row);
        Assert.InRange(global::Avalonia.VisualExtensions.TranslatePoint(row!, default, scroller)!.Value.Y, -1, 1);
    }

    private static void Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(25);
            UiSnapshot.Pump();
        }
    }

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
