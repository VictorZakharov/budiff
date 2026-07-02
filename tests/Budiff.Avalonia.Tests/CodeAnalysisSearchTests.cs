using System.Linq;
using System.Threading.Tasks;
using Budiff.App;
using Budiff.CodeAnalysis;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Pure view-model tests: no Avalonia UI thread needed.
public sealed class CodeAnalysisSearchTests
{
    private static CodeAnalysisPanelViewModel MakeLoadedViewModel()
    {
        var files = Enumerable.Range(0, 80)
            .Select(i => new FileStat
            {
                RelativePath = $"pack/item{i:00}.cs",
                Extension = ".cs",
                Lines = new LineCounts(10, 1, 1),
            })
            .Append(new FileStat { RelativePath = "solo/unique.cs", Extension = ".cs", Lines = new LineCounts(5, 0, 0) })
            .ToArray();
        var scan = new ScanResult { Root = "X:\\fake", HasGit = false, Files = files };

        var vm = new CodeAnalysisPanelViewModel((_, _) => Task.CompletedTask);
        vm.LoadFrom(scan, null);
        return vm;
    }

    private static CodeAnalysisRow Folder(CodeAnalysisPanelViewModel vm, string name)
        => vm.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => !r.IsFile && r.Name == name);

    [Fact]
    public void BroadQuery_HighlightsOnly_NoExpandNoFilter()
    {
        var vm = MakeLoadedViewModel();

        // 80 matches > threshold: highlight in place, but neither expand the tree
        // nor hide anything - both would force a layout pass over every realized
        // row on the very first keystroke.
        vm.SearchText = "item";

        Assert.True(vm.Roots.SelectMany(r => r.SelfAndDescendants()).All(r => r.IsSearchVisible));
        Assert.False(Folder(vm, "pack").IsExpanded);

        var match = vm.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.Name == "item00.cs");
        Assert.Contains(match.NameRuns!, r => r.IsMatch);
    }

    [Fact]
    public void NarrowQuery_ExpandsMatchPathsAndClearRestores()
    {
        var vm = MakeLoadedViewModel();

        vm.SearchText = "item";
        vm.SearchText = "item7";

        Assert.True(Folder(vm, "pack").IsExpanded);
        Assert.False(Folder(vm, "solo").IsExpanded);
        Assert.False(Folder(vm, "solo").IsSearchVisible);

        vm.SearchText = string.Empty;

        Assert.False(Folder(vm, "pack").IsExpanded);
        Assert.True(Folder(vm, "solo").IsSearchVisible);
    }

    [Fact]
    public void NarrowingBelowThreshold_ThenBroadening_RestoresCapturedExpansion()
    {
        var vm = MakeLoadedViewModel();
        Folder(vm, "solo").IsExpanded = true;

        vm.SearchText = "item7";
        Assert.True(Folder(vm, "pack").IsExpanded);
        Assert.False(Folder(vm, "solo").IsExpanded);

        // Back above the threshold: the user's captured layout comes back.
        vm.SearchText = "item";
        Assert.False(Folder(vm, "pack").IsExpanded);
        Assert.True(Folder(vm, "solo").IsExpanded);
    }

    [Fact]
    public void MatchedRow_GetsHighlightRuns_AndUnmatchedKeepsPlainRuns()
    {
        var vm = MakeLoadedViewModel();

        vm.SearchText = "item7";

        var match = vm.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.Name == "item70.cs");
        Assert.Equal(new[] { true, false }, match.NameRuns!.Select(r => r.IsMatch));
        Assert.Equal("item7", match.NameRuns![0].Text);

        var plain = vm.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.Name == "unique.cs");
        Assert.Single(plain.NameRuns!);
        Assert.False(plain.NameRuns![0].IsMatch);
    }
}
