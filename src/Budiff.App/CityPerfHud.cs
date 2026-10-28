using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Budiff.App;

// The code city's performance panel (after the one in threejs-nanites): the
// average FPS and 1% low over the last 15 s with a history graph; a click
// opens the debug lines - frame cost, the last full redraw split into order
// and draw, cache use, scene counters, the camera and the build.
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
    private readonly Queue<(double At, double Interval, double Cost)> _frames = new();
    private readonly List<double> _avgHistory = new();
    private readonly List<double> _lowHistory = new();
    private double _lastFrame = -1;
    private double _lastSample;

    public bool Expanded { get; set; }

    // Where the panel was last drawn (for the click).
    public Rect Bounds { get; private set; }

    public int Redraws { get; set; }

    public int CachedFrames { get; set; }

    // The last full redraw of the city.
    public double RedrawMs { get; set; }

    public double OrderMs { get; set; }

    public int Faces { get; set; }

    public int Pairs { get; set; }

    public int Labels { get; set; }

    // One drawn frame and what it cost on the UI thread.
    public void Frame(double costMs)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_lastFrame >= 0 && now - _lastFrame < IdleGapMs)
        {
            _frames.Enqueue((now, now - _lastFrame, costMs));
        }

        _lastFrame = now;
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

    public void Draw(DrawingContext context, Rect area, IReadOnlyList<string> details)
    {
        var intervals = _frames.Select(f => f.Interval).ToList();
        var (avg, low) = Rates(intervals);
        var costs = _frames.Select(f => f.Cost).OrderBy(c => c).ToList();
        var costAvg = costs.Count > 0 ? costs.Average() : 0;
        var costP95 = costs.Count > 0 ? costs[Math.Min(costs.Count - 1, (int)(costs.Count * 0.95))] : 0;
        var idle = _lastFrame < 0 || _clock.Elapsed.TotalMilliseconds - _lastFrame > 600;

        var lines = new List<FormattedText>();
        if (Expanded)
        {
            lines.Add(Text($"{F(avg, 1)} AVG / {F(low, 1)} 1% LOW", 10.5, TextBrush));
            lines.Add(Text($"{F(costAvg, 1)} MS FRAME / {F(costP95, 1)} P95", 10.5, TextBrush));
            lines.Add(Text($"REDRAW {F(RedrawMs, 1)} MS (ORDER {F(OrderMs, 1)} + DRAW {F(RedrawMs - OrderMs, 1)})", 10.5, TextBrush));
            lines.Add(Text($"{N(Redraws)} REDRAWS · {N(CachedFrames)} FROM CACHE", 10.5, MutedBrush));
            lines.Add(Text($"{N(Faces)} FACES · {N(Pairs)} PAIRS · {N(Labels)} LABELS", 10.5, MutedBrush));
            foreach (var detail in details)
            {
                lines.Add(Text(detail, 10.5, MutedBrush));
            }

            lines.Add(Text(RuntimeFeature.IsDynamicCodeSupported ? "JIT BUILD" : "NATIVEAOT BUILD", 10.5, MutedBrush));
        }

        var footer = Text(Expanded ? "CLICK TO COLLAPSE" : "CLICK FOR DEBUG INFO", 9.5, MutedBrush);
        var width = Math.Max(Width, lines.Count > 0 ? lines.Max(l => l.Width) + 20 : 0);
        var height = 30 + 16 + 36 + lines.Sum(l => l.Height + 1) + footer.Height + 12;
        var panel = new Rect(area.Right - width - 10, area.Y + 10, width, height);
        Bounds = panel;
        context.DrawRectangle(PanelBrush, PanelPen, panel, 4, 4);

        var big = Text(avg > 0 ? avg.ToString("0", CultureInfo.InvariantCulture) : "--", 22, TextBrush, bold: true);
        context.DrawText(big, new Point(panel.X + 10, panel.Y + 4));
        context.DrawText(Text("FPS AVG", 10.5, MutedBrush), new Point(panel.X + 16 + big.Width, panel.Y + 15));
        var caption = idle ? "IDLE · DRAWS ONLY ON CHANGE" : "LAST 15S · 1% LOW";
        context.DrawText(Text(caption, 9.5, idle ? MutedBrush : LowBrush), new Point(panel.X + 10, panel.Y + 32));

        var graph = new Rect(panel.X + 10, panel.Y + 48, width - 20, 32);
        context.DrawLine(GridPen, graph.TopLeft, graph.TopRight);
        context.DrawLine(GridPen, new Point(graph.X, graph.Center.Y), new Point(graph.Right, graph.Center.Y));
        context.DrawLine(GridPen, graph.BottomLeft, graph.BottomRight);
        var hi = Math.Max(60, _avgHistory.DefaultIfEmpty().Max()) * 1.05;
        DrawSeries(context, _lowHistory, graph, hi, LowPen);
        DrawSeries(context, _avgHistory, graph, hi, AvgPen);
        context.DrawText(Text(hi.ToString("0", CultureInfo.InvariantCulture), 8.5, AvgBrush), new Point(graph.Right - 18, graph.Y + 1));

        var top = graph.Bottom + 6;
        foreach (var line in lines)
        {
            context.DrawText(line, new Point(panel.X + 10, top));
            top += line.Height + 1;
        }

        context.DrawText(footer, new Point(panel.X + 10, top + 2));
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
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? Bold : Font, size, brush);
}
