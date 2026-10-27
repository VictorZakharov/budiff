using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        button.Flyout!.ShowAt(button);
        UiSnapshot.Pump();
        var items = OpenMenuItems(button.Flyout);
        Assert.Equal(new[] { "Lines", "Code", "Blank", "Comment", "Files", "Commits", "Authors", "Size" }, items.Select(i => i.Header as string));
        Assert.False(items.Single(i => (string?)i.Header == "Blank").IsChecked);
        Assert.True(items.Single(i => (string?)i.Header == "Code").IsChecked);

        // Ticking it in the menu brings it back.
        items.Single(i => (string?)i.Header == "Blank").IsChecked = true;
        UiSnapshot.Pump();
        Assert.True(vm.ShowBlankColumn);
        Assert.True(blankHeader.IsEffectivelyVisible);
        button.Flyout.Hide();
        window.Close();
    }

    // The open flyout's item containers. Only the flyout knows its popup
    // (a protected property), hence the reflection.
    private static System.Collections.Generic.List<MenuItem> OpenMenuItems(FlyoutBase flyout)
    {
        var popup = (Popup?)typeof(PopupFlyoutBase)
            .GetProperties(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Single(p => p.Name == "Popup" && p.PropertyType == typeof(Popup))
            .GetValue(flyout);
        return popup?.Child is ItemsControl presenter ? presenter.GetRealizedContainers().OfType<MenuItem>().ToList() : [];
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
