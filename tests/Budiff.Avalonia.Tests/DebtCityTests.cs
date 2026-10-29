using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Budiff.App;
using Budiff.CodeAnalysis;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The Debt view: a third segment beside Tree / City, the city with each
// building worn by its file's debt score - a big, deeply nested, busy file
// dark with a sagging roof and broken windows, a small flat one clean and lit.
// Hover, card and click work as in the City.
public sealed class DebtCityTests
{
    [AvaloniaFact]
    public void Debt_ShowsDilapidatedAndCleanBuildings_AndTheCityStillPicks()
    {
        var root = BuildRepo("budiff-debt-city");
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0 && vm.Analysis.GitAvailable, "bird's-eye scan");

        var debt = window.FindControl<Button>("DebtButton")!;
        Assert.True(debt.IsEffectivelyVisible);
        debt.Command!.Execute(null);
        WaitUntil(() => vm.Analysis.City.Buildings.Count > 0 && vm.Analysis.City.Mode == CityMode.Debt, "debt city");
        UiSnapshot.Pump();
        UiSnapshot.Pump();

        Assert.True(vm.Analysis.IsDebtView);
        Assert.False(vm.Analysis.IsHistoryCityView);
        Assert.True(debt.Classes.Contains("active"));
        Assert.False(window.FindControl<Button>("CityButton")!.Classes.Contains("active"));
        Assert.False(window.FindControl<Border>("CityModeSwitch")!.IsVisible, "Hotspots / Age belong to the City");

        var city = window.FindControl<CodeCityView>("CodeCity")!;
        var monolith = vm.Analysis.City.Buildings.Single(b => b.Name == "Monolith.cs");
        var small = vm.Analysis.City.Buildings.Single(b => b.Name == "Small.cs");
        Assert.InRange(monolith.Heat, 0.8, 1);
        Assert.InRange(small.Heat, 0, 0.2);
        Assert.True(monolith.Height > small.Height);

        // Worn: a sagging roof and mostly dark, broken or boarded windows;
        // clean: a level roof and lit windows.
        var worn = city.WearOf(monolith)!;
        var clean = city.WearOf(small)!;
        Assert.True(Enumerable.Range(0, 4).Min(worn.RoofAt) < monolith.Top - 1, "the roof sags");
        Assert.All(Enumerable.Range(0, 4), c => Assert.Equal(small.Top, clean.RoofAt(c), 3));
        Assert.True(worn.Pieces.Count > 8);
        Assert.True(clean.Pieces.Count > 0);
        Assert.True(DarkShare(worn) > 0.45, $"dark windows {DarkShare(worn):P0}");
        Assert.True(DarkShare(clean) < 0.15, $"dark windows {DarkShare(clean):P0}");

        UiSnapshot.Capture(window, "city-debt");

        // The card explains the score.
        var card = string.Join("\n", CodeCityView.Describe(monolith, vm.Analysis.City).Select(l => string.Concat(l.Select(r => r.Text))));
        Assert.Contains("dilapidated", card, StringComparison.Ordinal);
        Assert.Contains("levels deep", card, StringComparison.Ordinal);

        // Picking is the City's: hover the worn building and its card shows.
        var f = monolith.Footprint;
        var top = city.Project(new System.Numerics.Vector3((float)(f.X + f.Width / 2), (float)(monolith.Base + monolith.Height * 0.6), (float)(f.Y + f.Height / 2)));
        Assert.NotNull(top);
        window.MouseMove(global::Avalonia.VisualExtensions.TranslatePoint(city, top!.Value, window)!.Value);
        UiSnapshot.Pump();
        Assert.Same(monolith, city.Hovered);
        UiSnapshot.Capture(window, "city-debt-card");

        // City goes back to the history colouring it had.
        window.FindControl<Button>("CityButton")!.Command!.Execute(null);
        WaitUntil(() => vm.Analysis.City.Mode == CityMode.Hotspots, "history city");
        Assert.True(vm.Analysis.IsHistoryCityView);
        Assert.Null(city.WearOf(vm.Analysis.City.Buildings.First()));
        window.Close();
    }

    private static double DarkShare(DebtWear wear)
    {
        var windows = wear.Pieces.Where(p => p.Color.R + p.Color.G + p.Color.B > 0x60 || p.Color == Color.Parse("#101113")).ToList();
        return windows.Count(p => p.Color.R + p.Color.G + p.Color.B < 3 * 0x70) / (double)Math.Max(1, windows.Count);
    }

    // A small clean file, some ordinary ones, and one big, deeply nested,
    // wide and busy one.
    private static string BuildRepo(string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        ReadmeFixture.ForceDelete(root);
        Directory.CreateDirectory(root);
        Repository.Init(root);
        using var repo = new Repository(root);
        var files = new Dictionary<string, string>
        {
            ["src/Clean/Small.cs"] = Flat("Small", 60),
            ["src/Clean/Tidy.cs"] = Flat("Tidy", 140),
            ["src/Core/Service.cs"] = Flat("Service", 420),
            ["src/Core/Model.cs"] = Flat("Model", 260),
            ["src/Legacy/Monolith.cs"] = Deep(0),
            ["README.md"] = "# Fixture\n\nA project for the debt city.\n",
        };
        ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 25), "Scaffold", files);
        for (var i = 1; i <= 12; i++)
        {
            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Grace Hopper", "grace@example.com", daysAgo: 24 - i), $"Patch the monolith {i}", new()
            {
                ["src/Legacy/Monolith.cs"] = Deep(i),
            });
        }

        return root;
    }

    private static string Flat(string name, int lines)
    {
        var text = new StringBuilder($"public sealed class {name}\n{{\n");
        for (var i = 0; i < lines; i++)
        {
            text.Append("    public int Value").Append(i).Append(" => ").Append(i).Append(";\n");
        }

        return text.Append("}\n").ToString();
    }

    private static string Deep(int revision)
    {
        var text = new StringBuilder("public sealed class Monolith\n{\n");
        for (var block = 0; block < 260; block++)
        {
            text.Append("    void M").Append(block).Append("()\n    {\n");
            for (var depth = 2; depth <= 10; depth++)
            {
                text.Append(new string(' ', depth * 4)).Append("if (x").Append(depth).Append(" > ").Append(revision).Append(") {\n");
            }

            text.Append(new string(' ', 44)).Append("Call(").Append(string.Join(", ", Enumerable.Range(0, 24).Select(n => "argument" + n))).Append(");\n");
            for (var depth = 10; depth >= 2; depth--)
            {
                text.Append(new string(' ', depth * 4)).Append("}\n");
            }

            text.Append("    }\n");
        }

        return text.Append("}\n").ToString();
    }

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
