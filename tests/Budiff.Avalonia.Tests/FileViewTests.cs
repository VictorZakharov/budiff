using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Outside git there is nothing to compare against: a drill-in shows the file
// as-is - one full-width pane, every line visible, no blame column.
public sealed class FileViewTests
{
    private const string Text = "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10\n";

    [Fact]
    public void FileView_ShowsEveryLineInOnePane()
    {
        var rows = DiffLoader.BuildView(new BlobInfo(Text, Text.Length, IsBinary: false)).Rows!;

        // A comparison of an unchanged file folds into one gap...
        var diff = new DiffViewModel();
        diff.SetDiff(rows);
        Assert.False(diff.IsSinglePane);
        Assert.Single(diff.Rows, r => r.IsGap);

        // ...while the file view shows it whole.
        var view = new DiffViewModel { IsFileView = true };
        view.SetDiff(rows);
        Assert.True(view.IsSinglePane);
        Assert.DoesNotContain(view.Rows, r => r.IsGap);
        Assert.Equal(Enumerable.Range(1, 10).Select(n => n.ToString()), view.Rows.Select(r => r.SingleNo));
        Assert.False(view.IsBlameVisible);
        Assert.Equal(0, view.BlameGutterWidth);
        Assert.False(view.ShowDiffOptions);
    }

    [AvaloniaFact]
    public void NonGitFolder_OpeningAFileShowsItsCurrentContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "budiff-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cs"), Text);

            var window = new MainWindow(root, gitRoot: false) { Width = 1200, Height = 520 };
            window.Show();
            var vm = window.ViewModel;
            Assert.Equal("folder - no git", vm.ScopeTitle);
            Assert.StartsWith("Folder analysis only: ", vm.StatusText);
            WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "folder scan");

            // The same call the bird's-eye row double-tap makes.
            var row = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == "a.cs");
            _ = vm.Analysis.OpenAsync(row);
            WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder, "file view load");
            UiSnapshot.Capture(window, "nogit-file-view");

            Assert.True(vm.Diff.IsFileView);
            Assert.True(vm.Diff.IsSinglePane);
            Assert.Equal("a.cs", vm.Diff.Title);
            Assert.Equal(10, vm.Diff.Rows.Count);
            Assert.Equal("line 1", vm.Diff.Rows[0].NewText);
            window.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
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
