using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// File-type icons in the bird's-eye, and the column chooser whose choices
// survive a restart.
public sealed class BirdsEyeColumnTests
{
    // No test may read or write the user's own settings: every window that is
    // not handed settings of its own reads an empty scratch folder.
    [ModuleInitializer]
    internal static void IsolateSettings()
        => Environment.SetEnvironmentVariable(AppSettings.DirectoryVariable, Directory.CreateTempSubdirectory("budiff-settings-").FullName);

    [Theory]
    [InlineData("Program.cs", AppIconKind.LanguageCsharp, true)]
    [InlineData("App.TSX", AppIconKind.React, true)]
    [InlineData("models.ts", AppIconKind.LanguageTypescript, true)]
    [InlineData("app.module.ts", AppIconKind.Puzzle, true)]
    [InlineData("unit-ranges.spec.ts", AppIconKind.TestTube, true)]
    [InlineData("Button.test.TSX", AppIconKind.TestTube, true)]
    [InlineData("login.e2e-spec.ts", AppIconKind.TestTube, true)]
    [InlineData(".spec.ts", AppIconKind.LanguageTypescript, true)]
    [InlineData("README.md", AppIconKind.LanguageMarkdown, true)]
    [InlineData("Budiff.App.csproj", AppIconKind.Xml, true)]
    [InlineData("Dockerfile", AppIconKind.Docker, true)]
    [InlineData("Dockerfile.dev", AppIconKind.Docker, true)]
    [InlineData(".gitignore", AppIconKind.Git, true)]
    [InlineData("yarn.lock", AppIconKind.Lock, true)]
    [InlineData("notes.txt", AppIconKind.FileDocumentOutline, false)]
    [InlineData("data.bin", AppIconKind.FileOutline, false)]
    [InlineData("Makefile.", AppIconKind.FileOutline, false)]
    public void FileIcons_PickTheTypesIcon(string name, AppIconKind kind, bool tinted)
    {
        var icon = FileIcons.For(name);
        Assert.Equal(kind, icon.Kind);
        Assert.Equal(tinted, icon.Tint is not null);
    }

    // A column turned off takes no room: the next one in the keep order
    // (Lines, Code, Commits, Authors, ...) shows in its place.
    [AvaloniaFact]
    public void FitColumns_SkipsColumnsTurnedOff()
    {
        var vm = new MainWindowViewModel(TempDir("budiff-fit-off-"), gitRoot: false, AppSettings.Load(TempSettings())) { IsDiffOpen = true };
        Option(vm, "Lines").IsOn = false;

        // Room for two optional columns: Code and Commits, not Lines.
        vm.FitColumns(MainWindowViewModel.NameMinWidth + MainWindowViewModel.AddedRemovedWidth + 72 + 64);
        Assert.False(vm.ShowLinesColumn);
        Assert.True(vm.ShowCodeColumn && vm.ShowCommitsColumn);
        Assert.False(vm.ShowAuthorsColumn);
        Assert.Equal(2, vm.FittingColumns);

        // Back on: it takes its place again, without waiting for a resize -
        // and now only Lines fits.
        Option(vm, "Lines").IsOn = true;
        Assert.True(vm.ShowLinesColumn);
        Assert.False(vm.ShowCodeColumn || vm.ShowCommitsColumn);
    }

    [AvaloniaFact]
    public void ColumnChoices_SurviveARestart()
    {
        var path = TempSettings();
        var dir = TempDir("budiff-persist-");
        var first = new MainWindowViewModel(dir, gitRoot: false, AppSettings.Load(path));
        Option(first, "Comment").IsOn = false;
        Option(first, "Size").IsOn = false;
        Assert.False(first.ShowCommentColumn || first.ShowSizeColumn);
        Assert.Contains("Comment", File.ReadAllText(path));

        var second = new MainWindowViewModel(dir, gitRoot: false, AppSettings.Load(path));
        Assert.False(Option(second, "Comment").IsOn || Option(second, "Size").IsOn);
        Assert.True(Option(second, "Lines").IsOn);
        Assert.False(second.ShowCommentColumn || second.ShowSizeColumn);
        Assert.True(second.ShowBlankColumn && second.ShowFilesColumn);

        Option(second, "Comment").IsOn = true;
        var third = new MainWindowViewModel(dir, gitRoot: false, AppSettings.Load(path));
        Assert.True(Option(third, "Comment").IsOn);
        Assert.False(Option(third, "Size").IsOn);
    }

    // A damaged or missing file is just the defaults.
    [Fact]
    public void Settings_ToleratesABadFile()
    {
        var path = TempSettings();
        Assert.Empty(AppSettings.Load(path).HiddenColumns);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(AppSettings.Load(path).HiddenColumns);
        File.WriteAllText(path, "{\"hiddenColumns\": [\"Blank\", 3, null, \"\"]}");
        Assert.Equal(new[] { "Blank" }, AppSettings.Load(path).HiddenColumns);
    }

    // The real window: file rows carry their type's icon, a column turned off
    // in the chooser leaves the header and the rows, and the chooser opens
    // from the header's corner button.
    [AvaloniaFact]
    public void Window_ShowsTypeIcons_AndHidesColumnsTurnedOff()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-icons-columns");
        var settings = AppSettings.Load(TempSettings());
        settings.HiddenColumns.Add("Blank");
        var window = new MainWindow(root, gitRoot: true, settings) { Width = 1440, Height = 640 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        foreach (var row in vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsFile))
        {
            row.IsExpanded = true;
        }

        UiSnapshot.Pump();
        UiSnapshot.Pump();

        var icons = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<AppIcon>().Where(i => i.IsEffectivelyVisible).ToList();
        Assert.Contains(icons, i => i.Kind == AppIconKind.LanguageCsharp && i.Tint is not null);
        Assert.Contains(icons, i => i.Kind == AppIconKind.LanguageMarkdown && i.Tint is not null);

        Assert.False(vm.ShowBlankColumn);
        Assert.True(vm.ShowCommentColumn && vm.ShowSizeColumn);
        var blankHeader = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>()
            .Single(b => b.Classes.Contains("colHeader") && Equals(b.CommandParameter, global::Budiff.CodeAnalysis.AnalysisSortKey.Blank));
        Assert.False(blankHeader.IsEffectivelyVisible);
        UiSnapshot.Capture(window, "birdseye-file-icons");

        var button = window.FindControl<Button>("ColumnsButton")!;
        Assert.True(button.IsEffectivelyVisible);
        var menu = (MenuFlyout)button.Flyout!;
        menu.ShowAt(button);
        UiSnapshot.Pump();
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(
            new[] { "All columns", "Lines", "Code", "Blank", "Comment", "Files", "Added", "Removed", "Commits", "Authors", "Size", "Reset to default" },
            items.Select(i => i.Header as string));
        MenuItem Item(string header) => items.Single(i => (string?)i.Header == header);
        Assert.False(Item("Blank").IsChecked);
        Assert.True(Item("Code").IsChecked);
        Assert.False(Item("All columns").IsChecked);
        Assert.True(Item("Reset to default").IsEffectivelyEnabled);

        // The checks keep the menu open to pick several columns; Reset closes it.
        Assert.All(items.Where(i => i.ToggleType == MenuItemToggleType.CheckBox), i => Assert.True(i.StaysOpenOnClick, (string?)i.Header));
        Assert.False(Item("Reset to default").StaysOpenOnClick);

        // Ticking it in the menu brings it back; now all are on.
        Item("Blank").IsChecked = true;
        UiSnapshot.Pump();
        Assert.True(vm.ShowBlankColumn);
        Assert.True(blankHeader.IsEffectivelyVisible);
        Assert.True(Item("All columns").IsChecked);
        Assert.False(Item("Reset to default").IsEffectivelyEnabled);
        menu.Hide();

        // The header's right-click opens the same menu.
        var header = (Border)blankHeader.Parent!.Parent!;
        Assert.Same(menu, header.ContextFlyout);

        // All off leaves only Name: Added / Removed go too, header and rows.
        menu.ShowAt(button);
        UiSnapshot.Pump();
        Item("All columns").IsChecked = false;
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        Assert.False(vm.ShowAddedColumn || vm.ShowRemovedColumn);
        var visibleHeaders = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>()
            .Where(b => b.Classes.Contains("colHeader") && b.IsEffectivelyVisible).ToList();
        Assert.Equal(global::Budiff.CodeAnalysis.AnalysisSortKey.Name, Assert.Single(visibleHeaders).CommandParameter);
        Assert.DoesNotContain(
            global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>(),
            b => b.Classes.Contains("num") && b.IsEffectivelyVisible);
        UiSnapshot.Capture(window, "birdseye-no-columns");
        Item("Reset to default").Command!.Execute(null);
        UiSnapshot.Pump();
        Assert.True(vm.ShowAddedColumn && vm.ShowRemovedColumn && vm.ShowLinesColumn);
        Assert.True(Item("Added").IsChecked && Item("All columns").IsChecked);
        menu.Hide();
        window.Close();
    }

    // The top check hides every optional column, then shows them all again;
    // Reset goes back to the defaults and forgets the saved choices.
    [AvaloniaFact]
    public void AllColumns_And_Reset()
    {
        var path = TempSettings();
        var vm = new MainWindowViewModel(TempDir("budiff-all-"), gitRoot: false, AppSettings.Load(path));
        Assert.True(vm.AllColumnsOn);
        Assert.False(vm.ResetColumnsCommand.CanExecute(null));

        vm.AllColumnsOn = false;
        Assert.True(vm.ColumnOptions.All(o => !o.IsOn));
        Assert.Equal(0, vm.FittingColumns);
        Assert.Equal(10, AppSettings.Load(path).HiddenColumns.Count);
        Assert.False(vm.ShowAddedColumn || vm.ShowRemovedColumn);
        Assert.True(vm.ResetColumnsCommand.CanExecute(null));

        vm.AllColumnsOn = true;
        Assert.Equal(8, vm.FittingColumns);
        Assert.Empty(AppSettings.Load(path).HiddenColumns);

        Option(vm, "Files").IsOn = false;
        Option(vm, "Size").IsOn = false;
        Assert.False(vm.AllColumnsOn);
        vm.ResetColumnsCommand.Execute(null);
        Assert.True(vm.AllColumnsOn);
        Assert.Equal(8, vm.FittingColumns);
        Assert.Empty(AppSettings.Load(path).HiddenColumns);
        Assert.False(vm.ResetColumnsCommand.CanExecute(null));
    }

    // A name cut short by the ellipsis gets its full text as a tooltip; one
    // that fits gets none.
    [AvaloniaFact]
    public void NameTooltip_OnlyWhenTrimmed()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-name-tips");
        var window = new MainWindow(root, gitRoot: true, AppSettings.Load(TempSettings())) { MinWidth = 0, Width = 1440, Height = 640 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        foreach (var row in vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsFile))
        {
            row.IsExpanded = true;
        }

        UiSnapshot.Pump();
        TextBlock NameOf(string name) => global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>()
            .Single(b => b.TextTrimming == global::Avalonia.Media.TextTrimming.CharacterEllipsis && (b.DataContext as CodeAnalysisRow)?.Name == name);
        bool TipOpens(TextBlock block)
        {
            var e = new global::Avalonia.Interactivity.CancelRoutedEventArgs(ToolTip.ToolTipOpeningEvent);
            block.RaiseEvent(e);
            return !e.Cancel;
        }

        Assert.Equal("AnsiRenderer.cs", ToolTip.GetTip(NameOf("AnsiRenderer.cs")));
        Assert.False(TipOpens(NameOf("AnsiRenderer.cs")));

        // Squeeze the Name column until the long name trims.
        window.Width = 480;
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        Assert.True(TipOpens(NameOf("AnsiRenderer.cs")));
        window.Close();
    }

    // Next to the docked diff the bird's-eye goes as narrow as what it still
    // shows: Name plus the Added / Removed columns that are on, with the period
    // controls and the filter wrapping to rows of their own - and nothing
    // in the toolbar cut off.
    [AvaloniaFact]
    public void DockedBirdsEye_NarrowsToItsShownColumns()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-pane-min");
        var window = new MainWindow(root, gitRoot: true, AppSettings.Load(TempSettings())) { Width = 1400, Height = 700 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        var file = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.IsFile && r.Name == "Program.cs");
        _ = vm.Analysis.OpenAsync(file);
        WaitUntil(() => vm.IsDiffOpen && vm.ShowDiffPane, "diff open");

        // Drag the diff as wide as it goes: the bird's-eye sits at its minimum.
        var cols = window.FindControl<Grid>("BodyGrid")!.ColumnDefinitions;
        var pane = window.FindControl<DockPanel>("BirdsEyePanel")!;
        double Narrowest()
        {
            cols[2].Width = new GridLength(5000);
            UiSnapshot.Pump();
            UiSnapshot.Pump();
            UiSnapshot.Pump();
            return pane.Bounds.Width;
        }

        Assert.Equal(330, Narrowest(), 0.5);
        AssertToolbarFits(window, pane);

        Option(vm, "Removed").IsOn = false;
        Assert.Equal(252, Narrowest(), 0.5);
        AssertToolbarFits(window, pane);
        UiSnapshot.Capture(window, "birdseye-docked-narrow");

        // Without Added either, the wrapped toolbar's widest row sets it.
        Option(vm, "Added").IsOn = false;
        var bare = Narrowest();
        Assert.InRange(bare, 180, 230);
        AssertToolbarFits(window, pane);

        // Back on: the pane widens to fit them again.
        vm.ResetColumnsCommand.Execute(null);
        Assert.Equal(330, Narrowest(), 0.5);
        window.Close();
    }

    private static void AssertToolbarFits(MainWindow window, Control pane)
    {
        var period = window.FindControl<Control>("PeriodBar")!;
        var filter = window.FindControl<Control>("FilterRow")!;
        Assert.Equal(1, Grid.GetRow(period));
        Assert.Equal(2, Grid.GetRow(filter));
        foreach (var name in new[] { "GroupingSwitch", "PeriodBar", "ResetButton" })
        {
            var control = window.FindControl<Control>(name)!;
            var right = global::Avalonia.VisualExtensions.TranslatePoint(control, new global::Avalonia.Point(control.Bounds.Width, 0), pane)!.Value.X;
            Assert.True(right <= pane.Bounds.Width + 0.5, $"{name} ends at {right}, pane is {pane.Bounds.Width}");
        }
    }

    private static BirdsEyeColumnOption Option(MainWindowViewModel vm, string name) => vm.ColumnOptions.Single(o => o.Name == name);

    private static string TempDir(string prefix) => Directory.CreateTempSubdirectory(prefix).FullName;

    private static string TempSettings() => Path.Combine(TempDir("budiff-settings-test-"), "settings.json");

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }
    }
}
