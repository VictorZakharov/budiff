using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Budiff.Git;

namespace Budiff.App;

// The blame gutter's heat: a soft blue tint, strongest on a file's newest
// lines and fading towards its oldest. The scale is the file's own: the
// distinct ages its labels show ("<1min", "4hr", "3mo"), ranked and spread
// evenly over the shades - so lines that read the same age share a tint and
// every age reads differently. One age for the whole file: no tint at all
// (nothing to tell apart). Uncommitted lines rank newest.
internal static class BlameHeat
{
    // Capped well below opaque: the label stays readable on the newest lines.
    private static readonly Color Tint = Color.FromRgb(0x61, 0xAF, 0xEF);
    private const byte FaintAlpha = 0x08;
    private const byte HotAlpha = 0x3C;
    private const int Steps = 8;

    private static readonly IBrush[] Shades = Enumerable.Range(0, Steps)
        .Select(i => (IBrush)new ImmutableSolidColorBrush(Color.FromArgb(
            (byte)(FaintAlpha + (HotAlpha - FaintAlpha) * i / (Steps - 1)), Tint.R, Tint.G, Tint.B)))
        .ToArray();

    // age: the label's age text for a committed line ("4hr").
    public static Func<BlameLineInfo, IBrush?> For(IEnumerable<BlameLineInfo?> lines, Func<BlameLineInfo, string> age)
    {
        string Key(BlameLineInfo line) => IsUncommitted(line) ? string.Empty : age(line);
        var ranked = lines.OfType<BlameLineInfo>()
            .GroupBy(Key)
            .OrderBy(g => g.Max(Time))
            .Select((g, i) => (g.Key, i))
            .ToDictionary(p => p.Key, p => p.i);
        if (ranked.Count < 2)
        {
            return _ => null;
        }

        return line => Shades[(int)Math.Round((double)ranked[Key(line)] / (ranked.Count - 1) * (Steps - 1))];
    }

    internal static IReadOnlyList<IBrush> Palette => Shades;

    private static long Time(BlameLineInfo line) => IsUncommitted(line) ? long.MaxValue : line.When.ToUnixTimeSeconds();

    private static bool IsUncommitted(BlameLineInfo line) => line.ShortSha.Trim('0').Length == 0;
}
