using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// A wholly deleted or added file shows single-pane: every line numbered on
// its own side and marked by the red / green gutter bar - including a blank
// line after the imports, which once paired with the missing side's "empty
// line" into an Unchanged row (numbered 1, no bar).
public sealed class WholeFileRowsTests
{
    private const int LineCount = 20;

    [AvaloniaFact]
    public void DeletedAndAddedFiles_NumberAndMarkEveryLine()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-whole-file-rows");
        using (var repo = new Repository(root))
        {
            var ada = ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 2);
            ReadmeFixture.Commit(repo, ada, "Add the old service", new() { ["src/OldService.cs"] = Source("OldService") });

            System.IO.File.Delete(System.IO.Path.Combine(root, "src", "OldService.cs"));
            Commands.Stage(repo, "src/OldService.cs");
            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 1), "Replace the old service", new() { ["src/NewService.cs"] = Source("NewService") });
        }

        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        var window = new MainWindow(root, gitRoot: true, settings: null, commit, "src/OldService.cs") { Width = 1100, Height = 800 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.FileName == "OldService.cs", "OldService.cs");
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "whole-file-deleted");
        AssertEveryLine(window, DiffKind.Removed);

        var added = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == "NewService.cs");
        _ = vm.Analysis.OpenAsync(added);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.FileName == "NewService.cs", "NewService.cs");
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "whole-file-added");
        AssertEveryLine(window, DiffKind.Added);

        window.Close();
    }

    // Rows 1..20 in order, all one kind, each shown with its own number and a
    // solid bar of the kind's colour - checked on the rendered gutter too.
    private static void AssertEveryLine(MainWindow window, DiffKind kind)
    {
        var diff = window.ViewModel.Diff;
        Assert.True(diff.IsSinglePane);
        Assert.Equal(LineCount, diff.Rows.Count);

        var host = window.GetVisualDescendants().OfType<ItemsControl>().Single(h => h.Name == "RowsHost" && h.IsEffectivelyVisible);
        for (var i = 0; i < LineCount; i++)
        {
            var row = diff.Rows[i];
            Assert.Equal(kind, row.Kind);
            Assert.Equal((i + 1).ToString(), row.SingleNo);
            var marker = Assert.IsAssignableFrom<ISolidColorBrush>(row.SingleMarker);
            Assert.Equal(0xFF, marker.Color.A);
            Assert.True(kind == DiffKind.Added ? marker.Color.G > marker.Color.R : marker.Color.R > marker.Color.G, $"line {i + 1} bar colour");

            var container = host.ContainerFromIndex(i);
            Assert.NotNull(container);
            var bar = container!.GetVisualDescendants().OfType<Border>().Single(b => b.Width == 3 && b.IsEffectivelyVisible);
            var number = ((Grid)bar.Parent!).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 0);
            Assert.Equal((i + 1).ToString(), number.Text);
            Assert.Equal(marker.Color, Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color);
        }

        // The blank line after the imports is line 4 like any other.
        Assert.Equal(string.Empty, kind == DiffKind.Added ? diff.Rows[3].NewText : diff.Rows[3].OldText);
    }

    // 20 lines: three imports, a blank line, then a small class with blanks.
    private static string Source(string name)
    {
        var lines = new List<string>
        {
            "using System;",
            "using System.Collections.Generic;",
            "using System.Linq;",
            string.Empty,
            "namespace Budiff.Sample;",
            string.Empty,
            $"public sealed class {name}",
            "{",
            "    private readonly List<int> _values = new();",
            string.Empty,
            "    public void Add(int value) => _values.Add(value);",
            string.Empty,
            "    public int Total()",
            "    {",
            "        return _values.Sum();",
            "    }",
            string.Empty,
            $"    public override string ToString() => \"{name}\";",
            "    public int Count => _values.Count;",
            "}",
        };
        Assert.Equal(LineCount, lines.Count);
        return string.Join("\n", lines) + "\n";
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
