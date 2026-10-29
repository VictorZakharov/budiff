using System;
using System.Collections.Generic;
using System.Linq;

namespace Budiff.CodeAnalysis;

// What the city's buildings show.
// Hotspots: height = churn (+/-), colour = churn x size (big files that keep changing).
// Age: height = commits, colour = recency (fresh bright, stale faded).
// Debt: height = code lines, colour and wear = the file's technical-debt
// score (CodeDebt): clean files stand bright and whole, the worst ones dark,
// sagging and with broken windows.
public enum CityMode
{
    Hotspots,
    Age,
    Debt,
}

// One box of the city: a folder's plinth (a district) or a file's building.
// The footprint is on the ground plane (X/Y of the rect = world X/Z).
public sealed class CityBlock
{
    public required AnalysisNode Node { get; init; }

    public string RelativePath => Node.RelativePath;

    public string Name => Node.Name;

    public bool IsFile => Node.IsFile;

    // Nesting depth: 0 = a top-level folder or file.
    public int Level { get; init; }

    public TreemapRect Footprint { get; init; }

    // Where the box starts (the plinth it stands on) and how tall it is.
    public double Base { get; init; }

    public double Height { get; init; }

    public double Top => Base + Height;

    // 0..1 along the mode's colour ramp; -1 = no data (no change in the period;
    // in the Debt city: a binary file). In the Debt city it is the debt score.
    public double Heat { get; init; } = -1;

    // 1 = the hottest file by churn x size; 0 = not a hotspot (no churn).
    public int HotspotRank { get; set; }
}

public sealed class CityModel
{
    public required CityMode Mode { get; init; }

    // Ground side length in world units (the city is square).
    public required double Size { get; init; }

    // Plinths, parents before children.
    public required IReadOnlyList<CityBlock> Districts { get; init; }

    public required IReadOnlyList<CityBlock> Buildings { get; init; }

    // Whether git history fed the heights and colours (else heights are lines).
    public required bool HasHistory { get; init; }

    public int HotspotCount { get; init; }

    public static CityModel Empty(CityMode mode) => new()
    {
        Mode = mode,
        Size = CityLayout.DefaultSize,
        Districts = Array.Empty<CityBlock>(),
        Buildings = Array.Empty<CityBlock>(),
        HasHistory = false,
    };
}

public static class CityLayout
{
    public const double DefaultSize = 1000;

    // Plinth height per folder level, and the building height range, as a
    // share of the city side.
    private const double PlinthShare = 0.006;
    private const double MinHeightShare = 0.004;
    private const double MaxHeightShare = 0.22;

    public static CityModel Build(AnalysisNode root, CityMode mode, long nowUnix, bool hasHistory, double size = DefaultSize)
    {
        var districts = new List<CityBlock>();
        var files = new List<(AnalysisNode Node, int Level, TreemapRect Footprint, double Base)>();
        Lay(root.Children, new TreemapRect(0, 0, size, size), level: 0, ground: 0, size, districts, files);

        var leaves = files.Select(f => f.Node).ToList();
        var maxLines = Math.Max(1, leaves.Select(n => (double)n.Lines.Total).DefaultIfEmpty().Max());
        var heightOf = HeightMetric(mode, hasHistory);
        var maxMetric = leaves.Select(heightOf).DefaultIfEmpty().Max();
        var useLines = maxMetric <= 0;

        var minHeight = size * MinHeightShare;
        var maxHeight = size * MaxHeightShare;
        var heat = Heat(leaves, mode, nowUnix, hasHistory);
        var buildings = new List<CityBlock>(files.Count);
        foreach (var (node, level, footprint, ground) in files)
        {
            // Square roots so one giant does not flatten everything else. A
            // little of the size shows even without churn, so a quiet file still
            // reads as a building, not a paving slab.
            var bulk = Math.Sqrt(node.Lines.Total / maxLines);
            var fraction = useLines
                ? 0.3 * bulk
                : 0.08 * bulk + 0.92 * Math.Sqrt(heightOf(node) / maxMetric);
            buildings.Add(new CityBlock
            {
                Node = node,
                Level = level,
                Footprint = footprint,
                Base = ground,
                Height = minHeight + (maxHeight - minHeight) * fraction,
                Heat = heat.TryGetValue(node, out var h) ? h : -1,
            });
        }

        // Hotspot ranks (churn x size) are shown in both modes.
        var rank = 0;
        foreach (var building in buildings.Where(b => hasHistory && b.Node.Churn > 0).OrderByDescending(b => HotspotScore(b.Node)))
        {
            building.HotspotRank = ++rank;
        }

        return new CityModel
        {
            Mode = mode,
            Size = size,
            Districts = districts,
            Buildings = buildings,
            HasHistory = hasHistory,
            HotspotCount = rank,
        };
    }

    public static double HotspotScore(AnalysisNode node) => (double)node.Churn * Math.Max(1, node.Lines.Total);

    private static Func<AnalysisNode, double> HeightMetric(CityMode mode, bool hasHistory)
        => mode == CityMode.Debt ? n => n.Lines.Code
            : !hasHistory ? _ => 0
            : mode == CityMode.Hotspots ? n => n.Churn
            : n => n.CommitCount;

    private static void Lay(
        IReadOnlyList<AnalysisNode> nodes,
        TreemapRect bounds,
        int level,
        double ground,
        double size,
        List<CityBlock> districts,
        List<(AnalysisNode, int, TreemapRect, double)> files)
    {
        // Binary and empty files still get a small lot.
        var weights = nodes.Select(n => (double)Math.Max(1, n.Lines.Total)).ToArray();
        var tiles = Treemap.Squarify(weights, bounds);
        var plinth = size * PlinthShare;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var tile = tiles[i];
            var side = Math.Min(tile.Width, tile.Height);
            if (node.IsFile)
            {
                files.Add((node, level, tile.Inset(side * 0.12), ground));
                continue;
            }

            if (node.Children.Count == 0)
            {
                continue;
            }

            // A street around each district, and a rim inside it so the
            // plinth shows around its buildings.
            var lot = tile.Inset(Math.Min(size * 0.004, side * 0.04));
            districts.Add(new CityBlock
            {
                Node = node,
                Level = level,
                Footprint = lot,
                Base = ground,
                Height = plinth,
            });
            var inner = lot.Inset(Math.Min(size * 0.004, Math.Min(lot.Width, lot.Height) * 0.04));
            Lay(node.Children, inner, level + 1, ground + plinth, size, districts, files);
        }
    }

    private static Dictionary<AnalysisNode, double> Heat(List<AnalysisNode> files, CityMode mode, long nowUnix, bool hasHistory)
    {
        var heat = new Dictionary<AnalysisNode, double>();
        if (mode == CityMode.Debt)
        {
            foreach (var file in files)
            {
                if (CodeDebt.Score(file, hasHistory) is var score and >= 0)
                {
                    heat[file] = score;
                }
            }

            return heat;
        }

        if (!hasHistory)
        {
            return heat;
        }

        if (mode == CityMode.Hotspots)
        {
            // Mostly rank, squared so only the top few files run hot (in a young
            // repo every file churns), plus some log scale so a runaway hotspot
            // still stands out from the merely busy.
            var hot = files.Where(f => f.Churn > 0).OrderBy(HotspotScore).ToList();
            var maxLog = hot.Count > 0 ? Math.Log(1 + HotspotScore(hot[^1])) : 1;
            for (var i = 0; i < hot.Count; i++)
            {
                var rankFraction = hot.Count == 1 ? 1 : (double)i / (hot.Count - 1);
                var logFraction = maxLog > 0 ? Math.Log(1 + HotspotScore(hot[i])) / maxLog : 1;
                heat[hot[i]] = 0.75 * rankFraction * rankFraction + 0.25 * logFraction * logFraction;
            }

            return heat;
        }

        // Age: log of the age in days, the newest change 1, the oldest 0.
        var touched = files.Where(f => f.LastChanged > 0).ToList();
        double Days(AnalysisNode n) => Math.Max(0, nowUnix - n.LastChanged) / 86400.0;
        var maxLogAge = Math.Log(1 + touched.Select(Days).DefaultIfEmpty().Max());
        foreach (var file in touched)
        {
            heat[file] = maxLogAge > 0 ? 1 - Math.Log(1 + Days(file)) / maxLogAge : 1;
        }

        return heat;
    }
}
