using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Budiff.App;
using Budiff.CodeAnalysis;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The code city: the bird's-eye's Tree/City switch, both colourings, the
// hover card, click-to-open, and search with fly-to - on the real window.
public sealed class CodeCityTests
{
    [AvaloniaFact]
    public void City_ReplacesTheTree_AndSwitchesBetweenHotspotsAndAge()
    {
        var (window, vm) = Open("budiff-city-modes");
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        var tree = window.FindControl<DockPanel>("TreeArea")!;
        Assert.False(city.IsVisible);
        Assert.True(tree.IsVisible);

        ShowCity(window, vm);
        Assert.True(city.IsVisible);
        Assert.False(tree.IsVisible);
        Assert.Equal(CityMode.Hotspots, vm.Analysis.City.Mode);
        Assert.True(vm.Analysis.City.HasHistory);
        Assert.Equal(vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Count(r => r.IsFile), vm.Analysis.City.Buildings.Count);

        // The big busy engine file is the top hotspot.
        var hottest = vm.Analysis.City.Buildings.Single(b => b.HotspotRank == 1);
        Assert.Equal("Renderer.cs", hottest.Name);
        UiSnapshot.Capture(window, "city-hotspots");

        // The switches hug their buttons: stretched to the taller Show FPS
        // box, they left a gap above and below the highlighted segment.
        foreach (var name in new[] { "CityModeSwitch", "ViewSwitch" })
        {
            var box = window.FindControl<Border>(name)!;
            var segment = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(box).OfType<Button>().First();
            Assert.Equal(box.Bounds.Height - 2, segment.Bounds.Height, 0.5);
        }

        vm.Analysis.ShowAgeCommand.Execute(null);
        UiSnapshot.Pump();
        Assert.Equal(CityMode.Age, vm.Analysis.City.Mode);
        Assert.Same(vm.Analysis.City, city.Model);

        // Untouched in the period: no colour in either mode.
        Assert.Equal(-1, vm.Analysis.City.Buildings.Single(b => b.Name == "Legacy.cs").Heat);
        UiSnapshot.Capture(window, "city-age");

        vm.Analysis.ShowTreeCommand.Execute(null);
        UiSnapshot.Pump();
        Assert.False(city.IsVisible);
        Assert.True(tree.IsVisible);
    }

    [AvaloniaFact]
    public void Hover_ShowsTheCard_AndClickOpensTheDiff()
    {
        var (window, vm) = Open("budiff-city-click");
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;

        var tallest = vm.Analysis.City.Buildings.OrderByDescending(b => b.Top).First();
        var at = TopCenter(city, window, tallest);
        window.MouseMove(at);
        UiSnapshot.Pump();

        var hovered = city.Hovered;
        Assert.NotNull(hovered);
        Assert.True(hovered!.IsFile);
        var card = CodeCityView.Describe(hovered, vm.Analysis.City).ToList();
        Assert.Contains(card, line => line.Any(r => r.Text.Contains("commit", StringComparison.Ordinal)));
        Assert.Contains(card, line => line.Any(r => r.Tone == CodeCityView.CardTone.Added && r.Text.StartsWith('+')));

        // The card says why the building has its shape: what sets each
        // dimension, and how it ranks in the city.
        string Says(string label) => string.Concat(card.Single(line => line[0].Text == label + " ").Select(r => r.Text));
        Assert.Matches(@"^Height [\d,]+ changed lines?, (taller than \d+% of files|among the (tallest|shortest))$", Says("Height"));
        Assert.Matches(@"^Footprint [\d,]+ lines?, ", Says("Footprint"));
        Assert.Contains(card, line => line.Any(r => r.Tone == CodeCityView.CardTone.Heat && r.Text.StartsWith("hotspot #", StringComparison.Ordinal)));
        var district = CodeCityView.Describe(vm.Analysis.City.Districts[0], vm.Analysis.City).ToList();
        Assert.Contains(district, line => line[0].Text == "Area ");
        var renderer = vm.Analysis.City.Buildings.Single(b => b.Name == "Renderer.cs");
        Assert.EndsWith("among the tallest", string.Concat(CodeCityView.Describe(renderer, vm.Analysis.City).Single(l => l[0].Text == "Height ").Select(r => r.Text)), StringComparison.Ordinal);
        UiSnapshot.Capture(window, "city-hover");

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        WaitUntil(() => vm.IsDiffOpen && vm.ShowDiffPane, "diff open");
        Assert.Equal(hovered.Name, vm.Diff.FileName);
        WaitUntil(() => !vm.Diff.IsPlaceholder || vm.Diff.Rows.Count > 0, "diff load");
        UiSnapshot.Capture(window, "city-docked-diff");
    }

    // Right-click a building (without dragging): Show in Explorer, on that
    // file. Right-drag still pans, with no menu.
    [AvaloniaFact]
    public void RightClick_ABuilding_ShowsItInExplorer()
    {
        var (window, vm) = Open("budiff-city-reveal");
        var shown = new List<string>();
        vm.Analysis.RevealInFileManager = shown.Add;
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        var tallest = vm.Analysis.City.Buildings.OrderByDescending(b => b.Top).First();
        var at = TopCenter(city, window, tallest);

        window.MouseDown(at, MouseButton.Right);
        window.MouseMove(at + new global::Avalonia.Point(120, 30), RawInputModifiers.RightMouseButton);
        window.MouseUp(at + new global::Avalonia.Point(120, 30), MouseButton.Right);
        UiSnapshot.Pump();
        Assert.Null(city.BlockMenu);

        at = TopCenter(city, window, tallest);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        UiSnapshot.Pump();
        var menu = city.BlockMenu!;
        Assert.True(menu.IsOpen);

        // The pointer leaving for the menu keeps the building highlighted.
        window.MouseMove(new global::Avalonia.Point(1, 1));
        UiSnapshot.Pump();
        Assert.Null(city.Hovered);
        Assert.Equal(tallest.RelativePath, city.Highlighted?.RelativePath);
        var item = (MenuItem)menu.Items[0]!;
        Assert.Equal(FileManager.RevealLabel, item.Header);
        item.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        var expected = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.RelativePath == tallest.RelativePath).FullPath!;
        Assert.Equal(new[] { expected }, shown);
        Assert.False(vm.IsDiffOpen);
        menu.Close();
        UiSnapshot.Pump();
        Assert.Null(city.Highlighted);
    }

    // Right-click a tree row: the row is selected and its menu shows it in
    // Explorer - a file, or a folder.
    [AvaloniaFact]
    public void RightClick_ATreeRow_ShowsItInExplorer()
    {
        var (window, vm) = Open("budiff-tree-reveal");
        var shown = new List<string>();
        vm.Analysis.RevealInFileManager = shown.Add;
        var folder = vm.Analysis.Roots.First(r => !r.IsFile && r.Children.Any(c => c.IsFile));
        folder.IsExpanded = true;
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        var file = folder.Children.First(r => r.IsFile);
        var tree = window.FindControl<TreeView>("AnalysisTree")!;

        foreach (var row in new[] { file, folder })
        {
            var item = (Control)tree.TreeContainerFromItem(row)!;
            var at = global::Avalonia.VisualExtensions.TranslatePoint(item, new global::Avalonia.Point(80, 12), window)!.Value;
            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            UiSnapshot.Pump();
            Assert.Same(row, tree.SelectedItem);
            var menu = window.RowMenu!;
            Assert.True(menu.IsOpen);
            ItemNamed(menu, FileManager.RevealLabel).RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
        }

        Assert.Equal(new[] { file.FullPath!, Path.GetDirectoryName(file.FullPath)! }, shown);
        Assert.False(vm.IsDiffOpen);
    }

    // A folder's menu expands or collapses that folder and everything under
    // it; other folders stay as they were. A file's menu has no such items.
    [AvaloniaFact]
    public void RightClick_AFolder_ExpandsAndCollapsesItsSubtree()
    {
        var (window, vm) = Open("budiff-tree-subtree");
        var roots = vm.Analysis.Roots;
        var folder = roots.First(r => !r.IsFile && r.Children.Any(c => !c.IsFile));
        var other = roots.First(r => !r.IsFile && r != folder);
        other.IsExpanded = true;
        folder.IsExpanded = false;
        UiSnapshot.Pump();
        var tree = window.FindControl<TreeView>("AnalysisTree")!;
        var under = folder.SelfAndDescendants().Where(r => !r.IsFile).ToList();
        Assert.True(under.Count > 1);

        OpenRowMenu(window, tree, folder);
        ItemNamed(window.RowMenu!, FileManager.ExpandAllLabel).RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        window.RowMenu!.Close();
        UiSnapshot.Pump();
        Assert.All(under, r => Assert.True(r.IsExpanded, r.RelativePath));
        Assert.True(other.IsExpanded);
        Assert.All(other.SelfAndDescendants().Where(r => !r.IsFile).Skip(1), r => Assert.False(r.IsExpanded, r.RelativePath));

        OpenRowMenu(window, tree, folder);
        ItemNamed(window.RowMenu!, FileManager.CollapseAllLabel).RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        window.RowMenu!.Close();
        UiSnapshot.Pump();
        Assert.All(under, r => Assert.False(r.IsExpanded, r.RelativePath));
        Assert.True(other.IsExpanded);

        // Collapsed stays collapsed through a tree rebuild (a new sort).
        vm.Analysis.SortCommand.Execute(global::Budiff.CodeAnalysis.AnalysisSortKey.Name);
        UiSnapshot.Pump();
        Assert.All(vm.Analysis.Roots.Single(r => r.RelativePath == folder.RelativePath).SelfAndDescendants().Where(r => !r.IsFile), r => Assert.False(r.IsExpanded, r.RelativePath));

        var file = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.IsFile);
        vm.Analysis.Reveal(file.RelativePath);
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        OpenRowMenu(window, tree, file);
        Assert.Equal(new[] { FileManager.RevealLabel }, window.RowMenu!.Items.OfType<MenuItem>().Select(i => i.Header));
        window.RowMenu!.Close();
    }

    private static void OpenRowMenu(MainWindow window, TreeView tree, CodeAnalysisRow row)
    {
        var item = (Control)tree.TreeContainerFromItem(row)!;
        var at = global::Avalonia.VisualExtensions.TranslatePoint(item, new global::Avalonia.Point(80, 12), window)!.Value;
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        UiSnapshot.Pump();
        Assert.True(window.RowMenu!.IsOpen);
    }

    private static MenuItem ItemNamed(ContextMenu menu, string header)
        => menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, header));

    [AvaloniaFact]
    public void Drag_OrbitsWithoutOpening()
    {
        var (window, vm) = Open("budiff-city-drag");
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        var at = TopCenter(city, window, vm.Analysis.City.Buildings.OrderByDescending(b => b.Top).First());

        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new global::Avalonia.Point(160, -40), RawInputModifiers.LeftMouseButton);
        window.MouseUp(at + new global::Avalonia.Point(160, -40), MouseButton.Left);
        UiSnapshot.Pump();

        Assert.False(vm.IsDiffOpen);
        UiSnapshot.Capture(window, "city-orbited");
    }

    [AvaloniaFact]
    public void Search_GhostsTheRest_AndEnterFliesToTheMatch()
    {
        var (window, vm) = Open("budiff-city-search");
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        var before = city.CameraDistance;

        vm.Analysis.SearchText = "Settings";
        UiSnapshot.Pump();
        var match = Assert.Single(city.Matches!);
        Assert.Equal("Settings.cs", match.Name);

        var box = window.FindControl<TextBox>("FilterBox")!;
        box.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal((float)(match.Footprint.X + match.Footprint.Width / 2), city.FlyDestination.X, 0.5f);
        Assert.Equal((float)(match.Footprint.Y + match.Footprint.Height / 2), city.FlyDestination.Z, 0.5f);
        city.FinishFly();
        Assert.Equal(city.FlyDestination, city.CameraTarget);
        Assert.True(city.CameraDistance < before);
        UiSnapshot.Capture(window, "city-search");
    }

    [AvaloniaFact]
    public void Wasd_FliesTheCamera_SpaceUp_CtrlDown()
    {
        var (window, vm) = Open("budiff-city-wasd");
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        city.Focus();
        UiSnapshot.Pump();
        var eye = city.Eye;
        var start = city.CameraTarget;

        // W: along the view on the ground, away from the eye.
        window.KeyPress(Key.W, RawInputModifiers.None, PhysicalKey.W, "w");
        Assert.Contains(Key.W, city.HeldKeys);
        city.Move(0.1);
        var moved = city.CameraTarget - start;
        Assert.Equal(0, moved.Y, 3);
        Assert.True(System.Numerics.Vector3.Dot(moved, new System.Numerics.Vector3(start.X - eye.X, 0, start.Z - eye.Z)) > 0);
        window.KeyRelease(Key.W, RawInputModifiers.None, PhysicalKey.W, "w");
        Assert.Empty(city.HeldKeys);

        // A goes left, D goes right (across the view).
        var forward = new System.Numerics.Vector3(start.X - eye.X, 0, start.Z - eye.Z);
        var right = System.Numerics.Vector3.Cross(forward, System.Numerics.Vector3.UnitY);
        var before = city.CameraTarget;
        window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        city.Move(0.05);
        window.KeyRelease(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        Assert.True(System.Numerics.Vector3.Dot(city.CameraTarget - before, right) < 0);
        before = city.CameraTarget;
        window.KeyPress(Key.D, RawInputModifiers.None, PhysicalKey.D, "d");
        city.Move(0.05);
        window.KeyRelease(Key.D, RawInputModifiers.None, PhysicalKey.D, "d");
        Assert.True(System.Numerics.Vector3.Dot(city.CameraTarget - before, right) > 0);

        // Space rises, Ctrl comes down (never under the ground).
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        city.Move(0.3);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        var up = city.CameraTarget.Y;
        Assert.True(up > 10);
        window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        city.Move(5);
        window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Assert.Equal(0, city.CameraTarget.Y, 3);
        Assert.False(vm.IsDiffOpen);
        UiSnapshot.Capture(window, "city-wasd");
    }

    [AvaloniaFact]
    public void PointingAtABuildingsSide_HoversIt()
    {
        var (window, vm) = Open("budiff-city-side");
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        var eye = city.Eye;
        var tallest = vm.Analysis.City.Buildings.OrderByDescending(b => b.Top).First();
        var f = tallest.Footprint;
        var y = (float)(tallest.Base + tallest.Height * 0.3);

        // The middle of a side the eye can see, low down.
        var side = eye.X < f.X
            ? new System.Numerics.Vector3((float)f.X, y, (float)(f.Y + f.Height / 2))
            : new System.Numerics.Vector3((float)(f.X + f.Width), y, (float)(f.Y + f.Height / 2));
        var at = global::Avalonia.VisualExtensions.TranslatePoint(city, city.Project(side)!.Value, window)!.Value;
        window.MouseMove(at);
        UiSnapshot.Pump();
        Assert.Same(tallest, city.Hovered);
        UiSnapshot.Capture(window, "city-hover-side");
    }

    [AvaloniaFact]
    public void ShowFps_IsOffByDefault_AndAClickCopiesTheReport()
    {
        var (window, vm) = Open("budiff-city-hud");
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        var box = window.FindControl<CheckBox>("ShowFpsBox")!;
        Assert.True(box.IsEffectivelyVisible);
        Assert.False(box.IsChecked);
        Assert.Equal(default, city.Hud.Bounds);

        box.IsChecked = true;
        UiSnapshot.Pump();
        Assert.True(vm.Analysis.ShowCityFps);

        // A few frames in a run (an orbit drag).
        var mid = global::Avalonia.VisualExtensions.TranslatePoint(city, new global::Avalonia.Point(city.Bounds.Width / 2, city.Bounds.Height / 2), window)!.Value;
        window.MouseDown(mid, MouseButton.Left);
        for (var i = 1; i <= 8; i++)
        {
            window.MouseMove(mid + new global::Avalonia.Point(i * 6, 0), RawInputModifiers.LeftMouseButton);
            UiSnapshot.Pump();
        }

        window.MouseUp(mid + new global::Avalonia.Point(48, 0), MouseButton.Left);
        UiSnapshot.Pump();
        Assert.True(city.Hud.Redraws > 0);
        Assert.True(city.Hud.Faces > 0);
        Assert.True(city.Hud.Bounds.Width > 0);

        // A click copies the report; it opens nothing.
        var hud = global::Avalonia.VisualExtensions.TranslatePoint(city, city.Hud.Bounds.Center, window)!.Value;
        window.MouseDown(hud, MouseButton.Left);
        window.MouseUp(hud, MouseButton.Left);
        UiSnapshot.Pump();
        Assert.False(vm.IsDiffOpen);
        Assert.NotNull(city.LastReport);
        Assert.Contains("FPS", city.LastReport, StringComparison.Ordinal);
        Assert.Contains("23 buildings", city.LastReport, StringComparison.Ordinal);
        Assert.Contains("faces", city.LastReport, StringComparison.Ordinal);
        UiSnapshot.Capture(window, "city-fps-copied");
    }

    // What you see is what you pick: wherever the pointer is over a coloured
    // (building) pixel, the pick is a building, never the district under it.
    [AvaloniaFact]
    public void Picking_MatchesThePixelsDrawn()
    {
        var (window, vm) = Open("budiff-city-pixels");
        ShowCity(window, vm);
        var city = window.FindControl<CodeCityView>("CodeCity")!;
        using var frame = window.CaptureRenderedFrame()!;
        using var pixels = frame.Lock();
        var origin = global::Avalonia.VisualExtensions.TranslatePoint(city, new global::Avalonia.Point(0, 0), window)!.Value;
        var checkedPoints = 0;
        var wrong = new List<string>();
        for (var y = 20; y < city.Bounds.Height - 120; y += 7)
        {
            for (var x = 20; x < city.Bounds.Width - 20; x += 7)
            {
                var hit = city.PickNear(new global::Avalonia.Point(x, y));
                if (hit is null || hit.IsFile || city.LabelBoxes.Any(l => l.Inflate(2).Contains(new global::Avalonia.Point(x, y))))
                {
                    continue;
                }

                var at = pixels.Address + (int)((origin.Y + y) * TestScale.Value) * pixels.RowBytes + (int)((origin.X + x) * TestScale.Value) * 4;
                var r = System.Runtime.InteropServices.Marshal.ReadByte(at, 0);
                var g = System.Runtime.InteropServices.Marshal.ReadByte(at, 1);
                var b = System.Runtime.InteropServices.Marshal.ReadByte(at, 2);
                checkedPoints++;
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 40)
                {
                    wrong.Add($"({x},{y}) {hit.Name} rgb {r},{g},{b}");
                }
            }
        }

        Assert.True(checkedPoints > 50);
        Assert.Empty(wrong);
    }

    // A district's label sits just inside its nearest (lowest) edge, not at
    // its centre, where it seemed to name a sub-folder.
    [Fact]
    public void DistrictLabel_SitsInsideTheNearestVisibleEdge()
    {
        // A plinth seen from above and in front: the c2-c3 edge is lowest.
        var (c0, c1, c2, c3) = (new global::Avalonia.Point(300, 100), new global::Avalonia.Point(700, 100), new global::Avalonia.Point(800, 500), new global::Avalonia.Point(200, 500));
        var center = new global::Avalonia.Point(500, 300);
        var at = CodeCityView.EdgeAnchor(c0, c1, c2, c3, center, new global::Avalonia.Rect(0, 0, 1000, 800), inset: 12)!.Value;
        Assert.Equal(500, at.X, 1);
        Assert.Equal(488, at.Y, 1);

        // The front edge runs off the right of the view: the label follows
        // its visible part.
        at = CodeCityView.EdgeAnchor(c0, c1, c2, c3, center, new global::Avalonia.Rect(0, 0, 600, 800), inset: 12)!.Value;
        Assert.InRange(at.X, 380, 420);
        Assert.InRange(at.Y, 480, 495);

        // The front edge is below the view: the next edge that shows wins.
        at = CodeCityView.EdgeAnchor(c0, c1, c2, c3, center, new global::Avalonia.Rect(0, 0, 1000, 450), inset: 12)!.Value;
        Assert.True(at.Y < 450 && (at.X < 300 || at.X > 700), $"{at}");
        Assert.Null(CodeCityView.EdgeAnchor(c0, c1, c2, c3, center, new global::Avalonia.Rect(2000, 0, 100, 100), inset: 12));
    }

    // Opt-in: the city of a real repository (BUDIFF_CITY_REPO), for review.
    [AvaloniaFact]
    public void RealRepository_Snapshots()
    {
        var repo = Environment.GetEnvironmentVariable("BUDIFF_CITY_REPO");
        if (string.IsNullOrWhiteSpace(repo))
        {
            return;
        }

        var window = new MainWindow(repo, gitRoot: true) { Width = 1600, Height = 900 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan", seconds: 120);
        ShowCity(window, vm);
        UiSnapshot.Capture(window, "city-real-hotspots");
        vm.Analysis.ShowAgeCommand.Execute(null);
        UiSnapshot.Capture(window, "city-real-age");
    }

    private static (MainWindow Window, MainWindowViewModel Vm) Open(string name)
    {
        var root = BuildCityRepo(name);
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0 && vm.Analysis.GitAvailable, "bird's-eye scan");
        return (window, vm);
    }

    private static void ShowCity(MainWindow window, MainWindowViewModel vm)
    {
        var button = window.FindControl<Button>("CityButton")!;
        Assert.True(button.IsEffectivelyVisible);
        button.Command!.Execute(null);
        WaitUntil(() => vm.Analysis.City.Buildings.Count > 0, "city layout");
        UiSnapshot.Pump();
        UiSnapshot.Pump();
    }

    private static global::Avalonia.Point TopCenter(CodeCityView city, Window window, CityBlock block)
    {
        var f = block.Footprint;
        var top = city.Project(new System.Numerics.Vector3((float)(f.X + f.Width / 2), (float)block.Top, (float)(f.Y + f.Height / 2)));
        Assert.NotNull(top);
        return global::Avalonia.VisualExtensions.TranslatePoint(city, top!.Value, window)!.Value;
    }

    // A small project with a history: an engine with one big busy file, a UI,
    // data, tests and docs, four authors, and a legacy file nobody touched in
    // the period.
    private static string BuildCityRepo(string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        Repository.Init(root);
        using var repo = new Repository(root);

        var sizes = new Dictionary<string, int>
        {
            ["src/Engine/Renderer.cs"] = 900,
            ["src/Engine/Scene.cs"] = 320,
            ["src/Engine/Camera.cs"] = 140,
            ["src/Engine/Mesh.cs"] = 260,
            ["src/Engine/Shader.cs"] = 180,
            ["src/Engine/Legacy.cs"] = 600,
            ["src/Ui/MainView.cs"] = 420,
            ["src/Ui/Toolbar.cs"] = 160,
            ["src/Ui/Settings.cs"] = 220,
            ["src/Ui/Theme.cs"] = 90,
            ["src/Ui/Controls/Button.cs"] = 70,
            ["src/Ui/Controls/Slider.cs"] = 110,
            ["src/Ui/Controls/List.cs"] = 240,
            ["src/Data/Store.cs"] = 380,
            ["src/Data/Query.cs"] = 210,
            ["src/Data/Cache.cs"] = 130,
            ["tests/RendererTests.cs"] = 300,
            ["tests/SceneTests.cs"] = 140,
            ["tests/StoreTests.cs"] = 160,
            ["docs/guide.md"] = 120,
            ["docs/design.md"] = 80,
            ["README.md"] = 60,
            ["build.ps1"] = 25,
        };

        ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 60), "Scaffold",
            sizes.ToDictionary(p => p.Key, p => Body(p.Key, p.Value, 0)));

        // Recent work: the renderer churns constantly, the rest less so.
        var authors = new[] { ("Ada Lovelace", "ada@example.com"), ("Grace Hopper", "grace@example.com"), ("Alan Turing", "alan@example.com"), ("Edsger Dijkstra", "edsger@example.com") };
        var busy = new[]
        {
            new[] { "src/Engine/Renderer.cs", "src/Engine/Scene.cs" },
            new[] { "src/Engine/Renderer.cs", "tests/RendererTests.cs" },
            new[] { "src/Ui/MainView.cs", "src/Ui/Toolbar.cs" },
            new[] { "src/Engine/Renderer.cs", "src/Engine/Shader.cs" },
            new[] { "src/Data/Store.cs", "tests/StoreTests.cs" },
            new[] { "src/Engine/Renderer.cs" },
            new[] { "src/Ui/Controls/List.cs", "src/Ui/MainView.cs" },
            new[] { "src/Engine/Renderer.cs", "src/Engine/Mesh.cs" },
            new[] { "docs/guide.md", "README.md" },
            new[] { "src/Engine/Renderer.cs", "src/Engine/Scene.cs", "src/Data/Query.cs" },
        };
        var revision = new Dictionary<string, int>();
        for (var i = 0; i < busy.Length; i++)
        {
            var (author, email) = authors[i % authors.Length];
            var files = new Dictionary<string, string>();
            foreach (var path in busy[i])
            {
                revision[path] = revision.GetValueOrDefault(path) + 1;
                files[path] = Body(path, sizes[path] + revision[path] * 15, revision[path]);
            }

            ReadmeFixture.Commit(repo, ReadmeFixture.Sig(author, email, daysAgo: 28 - i * 3), $"Change {i}", files);
        }

        return root;
    }

    // n lines of C#-ish text; a revision rewrites every fourth line.
    private static string Body(string path, int lines, int revision)
        => string.Join("\n", Enumerable.Range(0, lines).Select(i =>
            i % 4 == 0 && revision > 0 ? $"    var v{i} = Rev{revision}({i}); // {Path.GetFileName(path)}" : $"    var v{i} = Step({i});")) + "\n";

    private static void WaitUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
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
