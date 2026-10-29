using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia.Media;
using Budiff.CodeAnalysis;

namespace Budiff.App;

// How a building of the Debt city shows its debt score (CityBlock.Heat in
// CityMode.Debt, 0 = clean, 1 = worst), beyond its colour: a sagging, uneven
// roof once the score passes RoofFrom, rows of windows - lit in a clean
// building, more of them dark, broken or boarded the worse it gets - and a
// crack down a wall of the worst. Plain world-space quads, so the software
// painter and the GPU view draw the same thing; a city-wide budget keeps the
// windows to the biggest buildings in a large repository.
internal sealed class DebtWear
{
    // Which wall a piece is on: -X, +X, -Z, +Z (the painter draws only the
    // walls turned to the eye).
    public enum Wall
    {
        Left,
        Right,
        Front,
        Back,
    }

    public readonly record struct Piece(Wall Wall, Vector3 A, Vector3 B, Vector3 C, Vector3 D, Color Color);

    public const double RoofFrom = 0.45;
    public const int MaxPieces = 40_000;

    private const int MaxFloors = 8;
    private const int MaxColumns = 16;

    private static readonly Color Lit = Color.Parse("#F3E3A2");
    private static readonly Color Dim = Color.Parse("#7C7562");
    private static readonly Color Broken = Color.Parse("#101113");
    private static readonly Color Boarded = Color.Parse("#5A4632");
    private static readonly Color Crack = Color.Parse("#17130F");

    // Roof heights at the footprint's corners (x0z0, x1z0, x1z1, x0z1).
    public float Y00 { get; init; }

    public float Y10 { get; init; }

    public float Y11 { get; init; }

    public float Y01 { get; init; }

    public IReadOnlyList<Piece> Pieces { get; init; } = Array.Empty<Piece>();

    public float RoofAt(int corner) => corner switch { 0 => Y00, 1 => Y10, 2 => Y11, _ => Y01 };

    // The wear of every building in a Debt city (none in the other modes).
    public static Dictionary<CityBlock, DebtWear> For(CityModel model)
    {
        var wear = new Dictionary<CityBlock, DebtWear>();
        if (model.Mode != CityMode.Debt)
        {
            return wear;
        }

        var budget = MaxPieces;
        foreach (var building in model.Buildings.OrderByDescending(b => b.Footprint.Area).ThenBy(b => b.RelativePath, StringComparer.Ordinal))
        {
            var piece = Build(building, model.Size, budget);
            budget -= piece.Pieces.Count;
            wear[building] = piece;
        }

        return wear;
    }

    private static DebtWear Build(CityBlock block, double citySize, int budget)
    {
        var f = block.Footprint;
        var (x0, x1, z0, z1) = ((float)f.X, (float)(f.X + f.Width), (float)f.Y, (float)(f.Y + f.Height));
        var (y0, y1) = ((float)block.Base, (float)block.Top);
        var score = block.Heat;
        var seed = Hash(block.RelativePath);

        // The roof: flat while clean; then one corner sinks, its neighbours
        // half as far - a tilted, broken-backed roof.
        var roof = new float[] { y1, y1, y1, y1 };
        if (score >= RoofFrom)
        {
            var wear01 = (score - RoofFrom) / (1 - RoofFrom);
            var sag = (float)(wear01 * Math.Min(block.Height * 0.3, Math.Min(f.Width, f.Height) * 0.6));
            var low = (int)(seed % 4);
            roof[low] -= sag;
            roof[(low + 1) % 4] -= sag * 0.45f;
            roof[(low + 3) % 4] -= sag * 0.3f;
        }

        var pieces = new List<Piece>();
        if (score >= 0 && budget > 0)
        {
            // Storeys of about a street's width; a tall building gets taller
            // ones so its windows still reach the roof.
            var lowestRoof = roof.Min();
            var wall = lowestRoof - y0;
            var floor = Math.Max((float)(citySize * 0.011), wall / MaxFloors);
            var floors = Math.Min(MaxFloors, (int)((wall - floor * 0.4f) / floor));
            var lift = (float)(citySize * 0.0006);
            AddWall(pieces, Wall.Left, new(x0 - lift, 0, z0), new(x0 - lift, 0, z1), y0, floor, floors, score, seed, ref budget);
            AddWall(pieces, Wall.Right, new(x1 + lift, 0, z1), new(x1 + lift, 0, z0), y0, floor, floors, score, seed + 1, ref budget);
            AddWall(pieces, Wall.Front, new(x1, 0, z0 - lift), new(x0, 0, z0 - lift), y0, floor, floors, score, seed + 2, ref budget);
            AddWall(pieces, Wall.Back, new(x0, 0, z1 + lift), new(x1, 0, z1 + lift), y0, floor, floors, score, seed + 3, ref budget);

            // The worst: a crack from under the roof down the front and left walls.
            if (score >= 0.7 && budget >= 2 && lowestRoof - y0 > floor * 2)
            {
                var top = lowestRoof - floor * 0.2f;
                var bottom = y0 + (lowestRoof - y0) * 0.35f;
                var w = Math.Max(lift * 2, (x1 - x0) * 0.025f);
                var xm = x0 + (x1 - x0) * (0.3f + (seed % 40) / 100f);
                pieces.Add(new Piece(Wall.Front, new(xm, top, z0 - lift * 2), new(xm + w, top, z0 - lift * 2), new(xm - w * 3, bottom, z0 - lift * 2), new(xm - w * 4, bottom, z0 - lift * 2), Crack));
                var zm = z0 + (z1 - z0) * (0.35f + (seed % 30) / 100f);
                pieces.Add(new Piece(Wall.Left, new(x0 - lift * 2, top, zm), new(x0 - lift * 2, top, zm + w), new(x0 - lift * 2, bottom, zm + w * 4), new(x0 - lift * 2, bottom, zm + w * 3), Crack));
            }
        }

        return new DebtWear { Y00 = roof[0], Y10 = roof[1], Y11 = roof[2], Y01 = roof[3], Pieces = pieces };
    }

    // A grid of windows on one wall, from `from` to `to` along the ground
    // (y ignored), floors stacked from y0.
    private static void AddWall(List<Piece> pieces, Wall wall, Vector3 from, Vector3 to, float y0, float floor, int floors, double score, uint seed, ref int budget)
    {
        var length = Vector3.Distance(from, to);
        var columns = Math.Min(MaxColumns, (int)(length / (floor * 1.15f)));
        if (floors < 1 || columns < 1 || budget < floors * columns)
        {
            return;
        }

        var along = (to - from) / columns;

        // Windows no wider than tall-ish, centred in their bay.
        var width = Math.Min(along.Length() * 0.56f, floor * 0.7f);
        var inset = along * ((1 - width / along.Length()) / 2);
        for (var row = 0; row < floors; row++)
        {
            var bottom = y0 + floor * (row + 0.35f);
            var top = bottom + floor * 0.42f;
            for (var col = 0; col < columns; col++)
            {
                var start = from + along * col + inset;
                var end = from + along * (col + 1) - inset;
                var color = WindowColor(score, Hash(seed, row, col));
                pieces.Add(new Piece(
                    wall,
                    start with { Y = bottom },
                    end with { Y = bottom },
                    end with { Y = top },
                    start with { Y = top },
                    color));
                budget--;
            }
        }
    }

    // Lit and warm in a clean building; the worse the score, the more windows
    // are dim, broken (dark) or boarded up.
    internal static Color WindowColor(double score, uint hash)
    {
        var roll = (hash % 1000) / 1000.0;
        if (roll < score * 0.6)
        {
            return Broken;
        }

        if (score > 0.55 && roll < score * 0.8)
        {
            return Boarded;
        }

        var t = Math.Clamp(score * 1.2, 0, 1);
        return Color.FromRgb(
            (byte)(Lit.R + (Dim.R - Lit.R) * t),
            (byte)(Lit.G + (Dim.G - Lit.G) * t),
            (byte)(Lit.B + (Dim.B - Lit.B) * t));
    }

    private static uint Hash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    private static uint Hash(uint seed, int row, int col)
    {
        var hash = seed ^ (uint)(row * 73856093) ^ (uint)(col * 19349663);
        hash ^= hash >> 13;
        hash *= 0x5bd1e995;
        return hash ^ (hash >> 15);
    }
}
