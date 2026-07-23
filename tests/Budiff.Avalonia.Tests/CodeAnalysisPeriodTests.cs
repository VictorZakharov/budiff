using System;
using System.Linq;
using System.Threading.Tasks;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// View-model regression test: no Avalonia UI thread needed.
public sealed class CodeAnalysisPeriodTests
{
    [Fact]
    public async Task Custom_period_recalculates_extension_and_author_filters()
    {
        var root = ReadmeFixture.BuildFixtureRepo("period-filters");
        var vm = new CodeAnalysisPanelViewModel((_, _) => Task.CompletedTask);
        await vm.LoadAsync(root);

        var previousCsharp = vm.Extensions.Single(item => item.Extension == ".cs");
        var previousSummary = previousCsharp.Summary;
        previousCsharp.Included = false;
        Assert.Contains(vm.Authors, item => item.Author == "Grace Hopper");

        vm.CustomDaysText = "5";
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.HistoryApplied += () => applied.TrySetResult();
        vm.SelectedPeriod = vm.Periods.Single(period => period.IsCustom);

        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var currentCsharp = vm.Extensions.Single(item => item.Extension == ".cs");
        Assert.NotSame(previousCsharp, currentCsharp);
        Assert.NotEqual(previousSummary, currentCsharp.Summary);
        Assert.Contains("changed lines", currentCsharp.Summary);
        Assert.False(currentCsharp.Included);
        Assert.Equal(new[] { ".cs", ".md" }, vm.Extensions.Select(item => item.Extension).Order());
        Assert.Equal(new[] { "Ada Lovelace" }, vm.Authors.Select(item => item.Author));
    }
}
