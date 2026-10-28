using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Budiff.App;

// The code city's performance panel (after the one in threejs-nanites): the
// average FPS and 1% low over the last 15 s with a history graph. A click
// copies a plain-text report - frame cost, the last full redraw split into
// order and draw, cache use, scene counters, the camera and the build.
//
// The city draws only when something changes, so FPS counts frames that come
// in a run: a gap over 250 ms is idle time, not a slow frame.
internal sealed class CityPerfHud
{
    private const double WindowMs = 15000;
    private const double IdleGapMs = 250;
    private const double SampleMs = 250;
    private const int History = 60;
    private const double Width = 190;

    private static readonly IBrush PanelBrush = new ImmutableSolidColorBrush(Color.Parse("#E01B1C1F"));
    private static readonly IPen PanelPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#4A4D55")), 1);
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#33FFFFFF")), 1);
    private static readonly IPen AvgPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#62D8F5")), 1.5);
    private static readonly IPen LowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#E0B04F")), 1.2);
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.Parse("#DFE1E5"));
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse("#9DA0A8"));
    private static readonly IBrush AvgBrush = new ImmutableSolidColorBrush(Color.Parse("#62D8F5"));
    private static readonly IBrush LowBrush = new ImmutableSolidColorBrush(Color.Parse("#E0B04F"));
    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Queue<(double At, double Interval, double Cost, long Allocated)> _frames = new();
    private readonly List<double> _avgHistory = new();
    private readonly List<double> _lowHistory = new();
    private double _lastFrame = -1;
    private double _lastSample;

    // The city draws only when something changes, so a pause in the user's
    // input is not a slow frame. An interval counts when the frame was asked
    // for soon after the last one (a drag, a hover) or was already scheduled
    // by the animation loop (flying, WASD, the benchmark).
    private const double PromptMs = 50;
    private double _requestedAt = -1;
    private bool _scheduled;
    private bool _nextScheduled;
    private long _lastAllocated;

    // Where the panel was last drawn (for the click); empty when hidden.
    public Rect Bounds { get; private set; }

    // "Copied" shows in the footer until then.
    public TimeSpan CopiedUntil { get; set; }

    public TimeSpan Now => _clock.Elapsed;

    public void Hide() => Bounds = default;

    // Starts the statistics over (the benchmark, after its warm-up).
    public void Reset()
    {
        _frames.Clear();
        _avgHistory.Clear();
        _lowHistory.Clear();
        _lastFrame = -1;
        _requestedAt = -1;
        _scheduled = _nextScheduled = false;
        Redraws = CachedFrames = 0;
        _gcAtReset = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        _allocatedAtReset = GC.GetTotalAllocatedBytes();
    }

    private long _allocatedAtReset;

    private (int, int, int) _gcAtReset;

    public int Redraws { get; set; }

    public int CachedFrames { get; set; }

    // The last full redraw of the city.
    public double RedrawMs { get; set; }

    public double OrderMs { get; set; }

    public int Faces { get; set; }

    public int Pairs { get; set; }

    public int Labels { get; set; }

    // A redraw was asked for (the first ask since the last frame counts).
    public void Requested()
    {
        if (_requestedAt < 0)
        {
            _requestedAt = _clock.Elapsed.TotalMilliseconds;
        }
    }

    // The animation loop has the next frame booked.
    public void Scheduled() => _scheduled = true;

    // One drawn frame and what it cost on the UI thread.
    public void Frame(double costMs)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes();
        var busy = _nextScheduled
            || (_requestedAt >= 0 && _requestedAt - _lastFrame < PromptMs && now - _lastFrame < IdleGapMs * 4);
        if (_lastFrame >= 0 && busy)
        {
            _frames.Enqueue((now, now - _lastFrame, costMs, allocated - _lastAllocated));
        }

        _lastFrame = now;
        _lastAllocated = allocated;
        _requestedAt = -1;
        _nextScheduled = _scheduled;
        _scheduled = false;
        while (_frames.Count > 0 && _frames.Peek().At < now - WindowMs)
        {
            _frames.Dequeue();
        }

        if (now - _lastSample >= SampleMs && _frames.Count > 0)
        {
            _lastSample = now;
            var (avg, low) = Rates(_frames.Where(f => f.At >= now - 1000).Select(f => f.Interval).ToList());
            if (avg > 0)
            {
                _avgHistory.Add(avg);
                _lowHistory.Add(low);
                if (_avgHistory.Count > History)
                {
                    _avgHistory.RemoveAt(0);
                    _lowHistory.RemoveAt(0);
                }
            }
        }
    }

    // Average FPS and the 1% low (the rate of the slowest 1% of frames).
    private static (double Avg, double Low) Rates(List<double> intervals)
    {
        if (intervals.Count == 0)
        {
            return (0, 0);
        }

        intervals.Sort();
        var slow = Math.Max(1, (int)Math.Ceiling(intervals.Count * 0.01));
        var slowMs = intervals.Skip(intervals.Count - slow).Average();
        return (1000 / intervals.Average(), 1000 / slowMs);
    }

    // The report a click copies.
    public string Report(IReadOnlyList<string> details)
    {
        var (avg, low) = Rates(_frames.Select(f => f.Interval).ToList());
        var (costAvg, costP95) = Costs();
        var lines = new List<string>
        {
            $"budiff code city performance report, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
            $"FPS {F(avg, 1)} avg / {F(low, 1)} 1% low over the last 15 s ({_frames.Count} frames in runs)",
            $"Frame {F(costAvg, 2)} ms avg / {F(costP95, 2)} ms p95 on the UI thread",
            $"Last redraw {F(RedrawMs, 2)} ms (order {F(OrderMs, 2)} + faces/labels {F(RedrawMs - OrderMs, 2)})",
            $"{N(Redraws)} redraws, {N(CachedFrames)} frames from cache",
            $"{N(Faces)} faces, {N(Pairs)} sibling pairs ordered, {N(Labels)} labels",
            $"Longest frame gap {F(_frames.Select(f => f.Interval).DefaultIfEmpty().Max(), 1)} ms, longest frame cost {F(_frames.Select(f => f.Cost).DefaultIfEmpty().Max(), 1)} ms",
            $"GC since the panel opened: gen0 {GC.CollectionCount(0) - _gcAtReset.Item1}, gen1 {GC.CollectionCount(1) - _gcAtReset.Item2}, gen2 {GC.CollectionCount(2) - _gcAtReset.Item3}; {AllocatedPerFrame() / 1024.0:0} KB allocated per frame in the last 15 s",
        };
        lines.AddRange(details);
        lines.Add($"{(IsNativeAot ? "NativeAOT" : "JIT")} build, .NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        return string.Join(Environment.NewLine, lines);
    }

    // What the counted frames allocated, on average.
    private double AllocatedPerFrame() => _frames.Count == 0 ? 0 : _frames.Average(f => (double)f.Allocated);

    private (double Avg, double P95) Costs()
    {
        var costs = _frames.Select(f => f.Cost).OrderBy(c => c).ToList();
        return costs.Count == 0 ? (0, 0) : (costs.Average(), costs[Math.Min(costs.Count - 1, (int)(costs.Count * 0.95))]);
    }

    // NativeAOT runs without the .NET host, which is what sets the deps-file
    // property (the feature switches can't tell: PublishAot turns
    // IsDynamicCodeSupported off in a JIT build too).
    private static bool IsNativeAot => AppContext.GetData("APP_CONTEXT_DEPS_FILES") is null;

    // The numbers shown, refreshed four times a second (so the text repeats
    // between refreshes and its shaping is reused).
    private (double Avg, double Low) _shown;
    private double _shownAt = double.MinValue;

    public void Draw(DrawingContext context, Rect area, string renderer)
    {
        var nowMs = _clock.Elapsed.TotalMilliseconds;
        if (nowMs - _shownAt >= SampleMs)
        {
            _shownAt = nowMs;
            _shown = Rates(_frames.Select(f => f.Interval).ToList());
        }

        var (avg, low) = _shown;
        var idle = _lastFrame < 0 || nowMs - _lastFrame > 600;
        var copied = _clock.Elapsed < CopiedUntil;
        var footer = Text(copied ? "COPIED TO CLIPBOARD" : "CLICK TO COPY DEBUG INFO", 9.5, copied ? AvgBrush : MutedBrush);
        var panel = new Rect(area.Right - Width - 10, area.Y + 10, Width, 30 + 16 + 36 + footer.Height + 10);
        Bounds = panel;
        context.DrawRectangle(PanelBrush, PanelPen, panel, 4, 4);

        var big = Text(avg > 0 ? avg.ToString("0", CultureInfo.InvariantCulture) : "--", 22, TextBrush, bold: true);
        context.DrawText(big, new Point(panel.X + 10, panel.Y + 4));
        context.DrawText(Text("FPS AVG", 10.5, MutedBrush), new Point(panel.X + 16 + big.Width, panel.Y + 15));
        var tag = Text(renderer, 9.5, AvgBrush, bold: true);
        context.DrawText(tag, new Point(panel.Right - tag.Width - 10, panel.Y + 15));
        var caption = idle ? "IDLE \u00B7 DRAWS ONLY ON CHANGE" : $"LAST 15S \u00B7 {F(low, 0)} 1% LOW";
        context.DrawText(Text(caption, 9.5, idle ? MutedBrush : LowBrush), new Point(panel.X + 10, panel.Y + 32));

        var graph = new Rect(panel.X + 10, panel.Y + 48, Width - 20, 32);
        context.DrawLine(GridPen, graph.TopLeft, graph.TopRight);
        context.DrawLine(GridPen, new Point(graph.X, graph.Center.Y), new Point(graph.Right, graph.Center.Y));
        context.DrawLine(GridPen, graph.BottomLeft, graph.BottomRight);
        var hi = Math.Max(60, _avgHistory.DefaultIfEmpty().Max()) * 1.05;
        DrawSeries(context, _lowHistory, graph, hi, LowPen);
        DrawSeries(context, _avgHistory, graph, hi, AvgPen);
        context.DrawText(Text(hi.ToString("0", CultureInfo.InvariantCulture), 8.5, AvgBrush), new Point(graph.Right - 18, graph.Y + 1));
        context.DrawText(footer, new Point(panel.X + 10, graph.Bottom + 6));
    }

    private static void DrawSeries(DrawingContext context, List<double> values, Rect graph, double hi, IPen pen)
    {
        if (values.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var point = new Point(
                    graph.X + graph.Width * i / (History - 1),
                    graph.Bottom - graph.Height * Math.Clamp(values[i] / hi, 0, 1));
                if (i == 0)
                {
                    g.BeginFigure(point, false);
                }
                else
                {
                    g.LineTo(point);
                }
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static string F(double value, int digits)
        => value > 0 ? value.ToString("F" + digits, CultureInfo.InvariantCulture) : "--";

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static FormattedText Text(string text, double size, IBrush brush, bool bold = false)
        => CityText.Get(text, size, brush, bold ? Bold : Font);
}

// Shaped text for the city's overlays, kept for reuse: labels, the legend and
// the panel repeat the same strings every frame, and shaping them anew cost
// hundreds of KB a frame (a GC, and a hitch, every few frames).
internal static class CityText
{
    private static readonly Dictionary<(string, double, IBrush, Typeface), FormattedText> Cache = new();

    public static FormattedText Get(string text, double size, IBrush brush, Typeface typeface)
    {
        var key = (text, size, brush, typeface);
        if (!Cache.TryGetValue(key, out var formatted))
        {
            if (Cache.Count > 4096)
            {
                Cache.Clear();
            }

            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
            Cache[key] = formatted;
        }

        return formatted;
    }
}
