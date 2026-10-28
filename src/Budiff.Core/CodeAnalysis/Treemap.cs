using System;
using System.Collections.Generic;
using System.Linq;

namespace Budiff.CodeAnalysis;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    // Shrinks by margin on every side, never below zero size.
    public TreemapRect Inset(double margin)
    {
        var dx = Math.Min(margin, Width / 2);
        var dy = Math.Min(margin, Height / 2);
        return new TreemapRect(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);
    }
}

// Squarified treemap (Bruls, Huizing, van Wijk): lays weights into a rectangle
// as near-square tiles, each with an area proportional to its weight.
public static class Treemap
{
    // Returns one tile per weight, in the order of the weights given.
    // Non-positive weights get an empty tile.
    public static TreemapRect[] Squarify(IReadOnlyList<double> weights, TreemapRect bounds)
    {
        var result = new TreemapRect[weights.Count];
        var order = Enumerable.Range(0, weights.Count)
            .Where(i => weights[i] > 0)
            .OrderByDescending(i => weights[i])
            .ToList();
        var total = order.Sum(i => weights[i]);
        if (order.Count == 0 || total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return result;
        }

        // Areas in the bounds' units.
        var scale = bounds.Area / total;
        var free = bounds;
        var row = new List<int>();
        var next = 0;
        while (next < order.Count)
        {
            var side = Math.Min(free.Width, free.Height);
            var candidate = order[next];
            if (row.Count == 0 || Worst(row, candidate, side, weights, scale) <= Worst(row, -1, side, weights, scale))
            {
                row.Add(candidate);
                next++;
                continue;
            }

            free = LayRow(row, free, weights, scale, result);
            row.Clear();
        }

        if (row.Count > 0)
        {
            LayRow(row, free, weights, scale, result);
        }

        return result;
    }

    // The worst aspect ratio of a row (plus extra, when >= 0) along a side.
    private static double Worst(List<int> row, int extra, double side, IReadOnlyList<double> weights, double scale)
    {
        var sum = 0.0;
        var min = double.MaxValue;
        var max = 0.0;
        foreach (var i in extra >= 0 ? row.Append(extra) : row)
        {
            var area = weights[i] * scale;
            sum += area;
            min = Math.Min(min, area);
            max = Math.Max(max, area);
        }

        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    // Lays a row along the shorter side of free and returns what is left.
    private static TreemapRect LayRow(List<int> row, TreemapRect free, IReadOnlyList<double> weights, double scale, TreemapRect[] result)
    {
        var area = row.Sum(i => weights[i] * scale);
        if (free.Width >= free.Height)
        {
            // A column on the left.
            var width = Math.Min(free.Width, area / free.Height);
            var y = free.Y;
            foreach (var i in row)
            {
                var h = weights[i] * scale / width;
                result[i] = new TreemapRect(free.X, y, width, h);
                y += h;
            }

            return new TreemapRect(free.X + width, free.Y, free.Width - width, free.Height);
        }
        else
        {
            // A row along the top.
            var height = Math.Min(free.Height, area / free.Width);
            var x = free.X;
            foreach (var i in row)
            {
                var w = weights[i] * scale / height;
                result[i] = new TreemapRect(x, free.Y, w, height);
                x += w;
            }

            return new TreemapRect(free.X, free.Y + height, free.Width, free.Height - height);
        }
    }
}
