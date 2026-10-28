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
        var card = CodeCityView.Describe(hovered, hasHistory: true, CityMode.Hotspots).ToList();
        Assert.Contains(card, line => line.Any(r => r.Text.Contains("commit", StringComparison.Ordinal)));
        Assert.Contains(card, line => line.Any(r => r.Tone == CodeCityView.CardTone.Added && r.Text.StartsWith('+')));
        Assert.Contains(card, line => line.Any(r => r.Tone == CodeCityView.CardTone.Heat && r.Text.StartsWith("Hotspot #", StringComparison.Ordinal)));
        UiSnapshot.Capture(window, "city-hover");

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        WaitUntil(() => vm.IsDiffOpen && vm.ShowDiffPane, "diff open");
        Assert.Equal(hovered.Name, vm.Diff.FileName);
        WaitUntil(() => !vm.Diff.IsPlaceholder || vm.Diff.Rows.Count > 0, "diff load");
        UiSnapshot.Capture(window, "city-docked-diff");
    }

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
