using System;
using System.Linq;
using System.Numerics;
using Budiff.CodeAnalysis;
using Xunit;

namespace Budiff.Core.Tests;

public sealed class CodeCityTests
{
    private const long Now = 1_800_000_000;

    [Fact]
    public void Squarify_tiles_fill_the_bounds_in_proportion_without_overlap()
    {
        var weights = new double[] { 6, 6, 4, 3, 2, 2, 1 };
        var bounds = new TreemapRect(0, 0, 600, 400);

        var tiles = Treemap.Squarify(weights, bounds);

        var total = weights.Sum();
        for (var i = 0; i < tiles.Length; i++)
        {
            Assert.Equal(bounds.Area * weights[i] / total, tiles[i].Area, 6);
            Assert.True(tiles[i].X >= -1e-9 && tiles[i].Y >= -1e-9);
            Assert.True(tiles[i].X + tiles[i].Width <= 600 + 1e-6 && tiles[i].Y + tiles[i].Height <= 400 + 1e-6);
            for (var j = i + 1; j < tiles.Length; j++)
            {
                Assert.False(Overlaps(tiles[i], tiles[j]), $"tiles {i} and {j} overlap");
            }
        }

        // Squarified: no sliver tiles for this mix.
        Assert.All(tiles, t => Assert.True(Math.Max(t.Width / t.Height, t.Height / t.Width) < 3));
    }

    [Fact]
    public void Squarify_gives_zero_weights_an_empty_tile()
    {
        var tiles = Treemap.Squarify(new double[] { 0, 5 }, new TreemapRect(0, 0, 10, 10));

        Assert.Equal(0, tiles[0].Area);
        Assert.Equal(100, tiles[1].Area, 6);
    }

    [Fact]
    public void Buildings_stand_inside_their_district_on_its_plinth()
    {
        var model = CityLayout.Build(SampleTree(), CityMode.Hotspots, Now, hasHistory: true);

        var src = model.Districts.Single(d => d.Name == "src");
        var core = model.Districts.Single(d => d.Name == "core");
        Assert.Equal(0, src.Level);
        Assert.Equal(1, core.Level);
        Assert.True(Contains(src.Footprint, core.Footprint));
        Assert.Equal(src.Top, core.Base, 6);

        foreach (var building in model.Buildings.Where(b => b.RelativePath.StartsWith("src/core/", StringComparison.Ordinal)))
        {
            Assert.True(Contains(core.Footprint, building.Footprint));
            Assert.Equal(core.Top, building.Base, 6);
        }

        Assert.Equal(5, model.Buildings.Count);
    }

    [Fact]
    public void Hotspots_mode_makes_big_busy_files_tallest_and_hottest()
    {
        var model = CityLayout.Build(SampleTree(), CityMode.Hotspots, Now, hasHistory: true);

        var engine = Building(model, "Engine.cs");
        var util = Building(model, "Util.cs");
        var quiet = Building(model, "Quiet.cs");

        Assert.Equal(1, engine.HotspotRank);
        Assert.Equal(1.0, engine.Heat, 6);
        Assert.True(engine.Height > util.Height);
        Assert.True(engine.Heat > util.Heat);

        // No churn in the period: no hotspot, no heat, the shortest height.
        Assert.Equal(0, quiet.HotspotRank);
        Assert.Equal(-1, quiet.Heat);
        Assert.Equal(model.Buildings.Min(b => b.Height), quiet.Height, 6);
        Assert.Equal(4, model.HotspotCount);
    }

    [Fact]
    public void Age_mode_heights_follow_commits_and_colour_follows_recency()
    {
        var model = CityLayout.Build(SampleTree(), CityMode.Age, Now, hasHistory: true);

        var fresh = Building(model, "Util.cs");     // changed an hour ago, 5 commits
        var stale = Building(model, "Legacy.cs");   // changed 300 days ago, 1 commit
        var quiet = Building(model, "Quiet.cs");

        Assert.True(fresh.Heat > 0.9);
        Assert.Equal(0, stale.Heat, 6);
        Assert.Equal(-1, quiet.Heat);
        Assert.True(fresh.Height > stale.Height);
    }

    [Fact]
    public void Without_history_heights_follow_lines_and_nothing_is_coloured()
    {
        var model = CityLayout.Build(SampleTree(), CityMode.Hotspots, Now, hasHistory: false);

        Assert.All(model.Buildings, b => Assert.Equal(-1, b.Heat));
        Assert.True(Building(model, "Engine.cs").Height > Building(model, "Util.cs").Height);
        Assert.Equal(0, model.HotspotCount);
    }

    [Fact]
    public void Footprints_follow_lines()
    {
        var model = CityLayout.Build(SampleTree(), CityMode.Hotspots, Now, hasHistory: true);

        Assert.True(Building(model, "Engine.cs").Footprint.Area > Building(model, "Util.cs").Footprint.Area * 5);
    }

    // The orbit bug: a long flat plinth whose nearest point is closer to the
    // eye than a tall building standing in front of it. Distance order paints
    // the plinth over the building; the separating plane (z between them,
    // eye on the building's side) puts the plinth first.
    [Fact]
    public void DrawOrder_puts_a_near_looking_flat_slab_behind_the_tall_building_in_front()
    {
        var slab = new BoxDrawOrder.Box(new Vector3(0, 0, 0), new Vector3(300, 5, 40));
        var tower = new BoxDrawOrder.Box(new Vector3(0, 0, 60), new Vector3(40, 150, 100));
        var eye = new Vector3(600, 300, 300);
        static float Nearest(BoxDrawOrder.Box b, Vector3 e) => Vector3.Distance(e, Vector3.Clamp(e, b.Min, b.Max));
        var boxes = new[] { slab, tower };

        // Distance alone gets it wrong: the slab looks nearer.
        Assert.True(Nearest(slab, eye) < Nearest(tower, eye));

        var order = BoxDrawOrder.Sort(boxes, eye, new[] { (0, 1) }, i => Nearest(boxes[i], eye));
        Assert.Equal(new[] { 0, 1 }, order);
    }

    [Fact]
    public void DrawOrder_puts_a_plinth_before_what_stands_on_it_and_survives_a_cycle()
    {
        var plinth = new BoxDrawOrder.Box(new Vector3(0, 0, 0), new Vector3(100, 5, 100));
        var house = new BoxDrawOrder.Box(new Vector3(10, 5, 10), new Vector3(20, 50, 20));
        Assert.Equal(-1, BoxDrawOrder.Behind(plinth, house, new Vector3(15, 200, 300)));

        // Three boxes each "behind" the next: every box still comes out once.
        var a = new BoxDrawOrder.Box(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
        var order = BoxDrawOrder.Sort(new[] { a, a, a }, Vector3.Zero, new[] { (0, 1), (1, 2), (2, 0) }, i => i);
        Assert.Equal(new[] { 0, 1, 2 }, order.OrderBy(i => i));
    }

    private static CityBlock Building(CityModel model, string name) => model.Buildings.Single(b => b.Name == name);

    private static AnalysisNode SampleTree()
    {
        var root = new AnalysisNode("", "", AnalysisNodeKind.Root);
        var src = Folder(root, "src", "src");
        var core = Folder(src, "core", "src/core");
        File(core, "Engine.cs", lines: 2000, churn: 900, commits: 12, lastChangedDaysAgo: 2);
        File(core, "Util.cs", lines: 200, churn: 300, commits: 5, lastChangedDaysAgo: 1.0 / 24);
        File(src, "Legacy.cs", lines: 1500, churn: 10, commits: 1, lastChangedDaysAgo: 300);
        File(src, "Quiet.cs", lines: 400, churn: 0, commits: 0, lastChangedDaysAgo: -1);
        File(root, "README.md", lines: 80, churn: 20, commits: 2, lastChangedDaysAgo: 10);
        root.Aggregate();
        return root;
    }

    private static AnalysisNode Folder(AnalysisNode parent, string name, string path)
    {
        var node = new AnalysisNode(name, path, AnalysisNodeKind.Folder);
        parent.Children.Add(node);
        return node;
    }

    private static void File(AnalysisNode parent, string name, int lines, long churn, int commits, double lastChangedDaysAgo)
    {
        var path = parent.RelativePath.Length == 0 ? name : parent.RelativePath + "/" + name;
        var node = new AnalysisNode(name, path, AnalysisNodeKind.File)
        {
            Lines = new LineCounts(lines, 0, 0),
            FileCount = 1,
            Added = churn,
            LastChanged = lastChangedDaysAgo < 0 ? 0 : Now - (long)(lastChangedDaysAgo * 86400),
        };
        for (var i = 0; i < commits; i++)
        {
            node.CommitIds.Add(i);
        }

        parent.Children.Add(node);
    }

    private static bool Overlaps(TreemapRect a, TreemapRect b)
        => a.X < b.X + b.Width - 1e-6 && b.X < a.X + a.Width - 1e-6
           && a.Y < b.Y + b.Height - 1e-6 && b.Y < a.Y + a.Height - 1e-6;

    private static bool Contains(TreemapRect outer, TreemapRect inner)
        => inner.X >= outer.X - 1e-6 && inner.Y >= outer.Y - 1e-6
           && inner.X + inner.Width <= outer.X + outer.Width + 1e-6
           && inner.Y + inner.Height <= outer.Y + outer.Height + 1e-6;
}
