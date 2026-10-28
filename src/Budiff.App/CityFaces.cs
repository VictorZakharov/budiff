using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Budiff.App;

// The city's projected faces in draw order: four screen points, a colour and
// flags per face. Built on the UI thread, then only read.
internal sealed class FaceBatch
{
    public const byte Edge = 1;
    public const byte Match = 2;

    private float[] _points;
    private uint[] _colors;
    private byte[] _flags;

    public FaceBatch(int capacity)
    {
        capacity = Math.Max(16, capacity);
        _points = new float[capacity * 8];
        _colors = new uint[capacity];
        _flags = new byte[capacity];
    }

    public int Count { get; private set; }

    public void Add(Point a, Point b, Point c, Point d, Color color, byte flags)
    {
        if (Count == _colors.Length)
        {
            Array.Resize(ref _points, _points.Length * 2);
            Array.Resize(ref _colors, _colors.Length * 2);
            Array.Resize(ref _flags, _flags.Length * 2);
        }

        var p = Count * 8;
        (_points[p], _points[p + 1], _points[p + 2], _points[p + 3]) = ((float)a.X, (float)a.Y, (float)b.X, (float)b.Y);
        (_points[p + 4], _points[p + 5], _points[p + 6], _points[p + 7]) = ((float)c.X, (float)c.Y, (float)d.X, (float)d.Y);
        _colors[Count] = color.ToUInt32();
        _flags[Count] = flags;
        Count++;
    }

    // Paints every face in order on a Skia canvas: one reused path, one fill
    // paint, and the roof edges / search outlines right after their face.
    public void Paint(SKCanvas canvas)
    {
        using var path = new SKPath();
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0, 0, 0, 0x38) };
        using var match = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0xFF, 0xE0, 0x82) };
        for (var i = 0; i < Count; i++)
        {
            var p = i * 8;
            path.Rewind();
            path.MoveTo(_points[p], _points[p + 1]);
            path.LineTo(_points[p + 2], _points[p + 3]);
            path.LineTo(_points[p + 4], _points[p + 5]);
            path.LineTo(_points[p + 6], _points[p + 7]);
            path.Close();
            fill.Color = new SKColor(_colors[i]);
            canvas.DrawPath(path, fill);
            switch (_flags[i])
            {
                case Edge:
                    canvas.DrawPath(path, edge);
                    break;
                case Match:
                    canvas.DrawPath(path, match);
                    break;
            }
        }
    }
}

// Hands a FaceBatch to the renderer's own Skia canvas (on the render thread,
// GPU-backed in the app, with the window's transform and scaling applied).
internal sealed class FacesDrawOperation : ICustomDrawOperation
{
    private readonly FaceBatch _faces;

    public FacesDrawOperation(Rect bounds, FaceBatch faces)
    {
        Bounds = bounds;
        _faces = faces;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia)
        {
            return;
        }

        using var lease = skia.Lease();
        _faces.Paint(lease.SkCanvas);
    }
}
