using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Budiff.CodeAnalysis;

namespace Budiff.App;

// The code city: folders as plinths (districts), files as buildings, drawn by
// a small software 3D renderer - a perspective camera projects each box's
// visible faces to 2D polygons, painted back to front. No GPU, no package:
// it costs nothing in the AOT exe and renders in headless snapshots.
//
// Left-drag orbits, right/middle-drag pans, the wheel zooms toward the
// pointer, Home resets. Hover shows a card; clicking a building opens its
// diff, clicking a district flies to it.
internal sealed class CodeCityView : Control
{
    public static readonly StyledProperty<CityModel?> ModelProperty =
        AvaloniaProperty.Register<CodeCityView, CityModel?>(nameof(Model));

    public static readonly StyledProperty<string?> SearchTextProperty =
        AvaloniaProperty.Register<CodeCityView, string?>(nameof(SearchText));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<CodeCityView, ICommand?>(nameof(OpenCommand));

    private const float FieldOfView = 0.70f;
    private const float MinPitch = 0.12f;
    private const float MaxPitch = 1.50f;
    private const float HomeYaw = -0.62f;
    private const float HomePitch = 0.80f;

    private static readonly Color Background = Color.Parse("#1B1C1F");
    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Background);
    private static readonly IBrush GroundBrush = new ImmutableSolidColorBrush(Color.Parse("#232529"));
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.Parse("#DFE1E5"));
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse("#9DA0A8"));
    private static readonly IBrush AddedBrush = new ImmutableSolidColorBrush(Color.Parse("#6BB36B"));
    private static readonly IBrush RemovedBrush = new ImmutableSolidColorBrush(Color.Parse("#D16969"));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#C8DFE1E5"));
    private static readonly IBrush LabelPillBrush = new ImmutableSolidColorBrush(Color.Parse("#C8181A1D"));
    private static readonly IPen LabelPillPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#5A5E66")), 1);
    private static readonly IBrush CardBrush = new ImmutableSolidColorBrush(Color.Parse("#F02B2D31"));
    private static readonly IPen CardPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#4A4D55")), 1);
    private static readonly IPen EdgePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#38000000")), 1);
    private static readonly IPen HoverPen = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 2);
    private static readonly IPen MatchPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFE082")), 1.5);
    private static readonly Typeface UiFont = new(FontFamily.Default);
    private static readonly Typeface UiBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    // Ramps: Hotspots calm blue -> amber -> red; Age faded slate -> bright cyan.
    private static readonly Color[] HotspotRamp = { Color.Parse("#4F6E91"), Color.Parse("#E0B04F"), Color.Parse("#E5484D") };
    private static readonly Color[] AgeRamp = { Color.Parse("#4D5766"), Color.Parse("#3E8FB0"), Color.Parse("#62D8F5") };
    private static readonly Color HotspotNone = Color.Parse("#596171");
    private static readonly Color AgeNone = Color.Parse("#3A3E45");

    private readonly Dictionary<uint, IBrush> _brushes = new();
    private readonly Dictionary<CityBlock, Color> _colors = new();

    // The model as a tree: top-level blocks, and each district's contents.
    private readonly List<CityBlock> _topLevel = new();
    private readonly Dictionary<CityBlock, List<CityBlock>> _contents = new();
    private HashSet<CityBlock>? _matches;

    // Camera: orbits target at distance; yaw around Y, pitch above the ground.
    private Vector3 _target;
    private float _distance;
    private float _yaw = HomeYaw;
    private float _pitch = HomePitch;
    private bool _hasCamera;

    // Home waits for the first render with a size to fit the city to.
    private bool _fitPending;

    // Per-render projection state.
    private Vector3 _eye;
    private Vector3 _forward;
    private Vector3 _right;
    private Vector3 _up;
    private float _focal;
    private Point _center;

    private Point _pressPoint;
    private Point _lastPoint;
    private bool _pressed;
    private bool _dragged;
    private bool _panning;
    private Point? _pointer;
    private CityBlock? _hover;

    private DispatcherTimer? _flyTimer;
    private (Vector3 Target, float Distance, float Yaw, float Pitch) _flyFrom;
    private (Vector3 Target, float Distance, float Yaw, float Pitch) _flyTo;
    private DateTime _flyStart;

    static CodeCityView()
    {
        AffectsRender<CodeCityView>(ModelProperty, SearchTextProperty);
        FocusableProperty.OverrideDefaultValue<CodeCityView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CodeCityView>(true);
    }

    public CityModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public string? SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    // The building or district under the pointer (for tests and the card).
    internal CityBlock? Hovered => _hover;

    internal IReadOnlyCollection<CityBlock>? Matches => _matches;

    internal Vector3 CameraTarget => _target;

    internal float CameraDistance => _distance;

    // Where the camera is flying to (its target when it is not flying).
    internal Vector3 FlyDestination => _flyTimer?.IsEnabled == true ? _flyTo.Target : _target;

    // Lands a fly at once (headless tests have no running timers).
    internal void FinishFly()
    {
        if (_flyTimer?.IsEnabled == true)
        {
            _flyStart = DateTime.MinValue;
            OnFlyTick(null, EventArgs.Empty);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            var old = change.GetOldValue<CityModel?>();
            OnModelChanged(resetCamera: old is null || old.Buildings.Count == 0 || old.Size != Model?.Size);
        }
        else if (change.Property == SearchTextProperty)
        {
            UpdateMatches();
        }
    }

    private void OnModelChanged(bool resetCamera)
    {
        _colors.Clear();
        _topLevel.Clear();
        _contents.Clear();
        _hover = null;
        if (Model is { } model)
        {
            var byNode = model.Districts.Concat(model.Buildings).ToDictionary(b => b.Node);
            foreach (var block in byNode.Values)
            {
                if (block.Level == 0)
                {
                    _topLevel.Add(block);
                }
            }

            foreach (var district in model.Districts)
            {
                _contents[district] = district.Node.Children.Select(n => byNode.GetValueOrDefault(n)).OfType<CityBlock>().ToList();
            }

            foreach (var building in model.Buildings)
            {
                _colors[building] = BuildingColor(model.Mode, building.Heat);
            }

            foreach (var district in model.Districts)
            {
                _colors[district] = DistrictColor(district.Level);
            }
        }

        UpdateMatches();
        if (resetCamera || !_hasCamera)
        {
            ResetCamera(animate: false);
        }
    }

    // Matches the search text against file paths; null = no search.
    private void UpdateMatches()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        _matches = query.Length == 0 || Model is null
            ? null
            : Model.Buildings.Where(b => b.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToHashSet();
        InvalidateVisual();
    }

    // Flies to the best search match: an exact file name, else the biggest
    // match. Returns it, or null when nothing matches.
    internal CityBlock? FlyToBestMatch(bool animate = true)
    {
        if (_matches is not { Count: > 0 } matches)
        {
            return null;
        }

        var query = SearchText!.Trim();
        var best = matches
            .OrderByDescending(b => string.Equals(b.Name, query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(b => b.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(b => b.Footprint.Area)
            .ThenBy(b => b.RelativePath, StringComparer.Ordinal)
            .First();
        FlyTo(best, animate);
        return best;
    }

    internal void FlyTo(CityBlock block, bool animate = true)
    {
        var f = block.Footprint;
        var span = (float)Math.Max(Math.Max(f.Width, f.Height), block.Top * 0.8);
        var size = (float)(Model?.Size ?? CityLayout.DefaultSize);
        // Far enough to see the neighbourhood, steep enough to see into the street.
        var distance = Math.Clamp(span * 4f, size * 0.3f, size * 2.2f);
        var target = new Vector3((float)(f.X + f.Width / 2), (float)(block.Top * 0.4), (float)(f.Y + f.Height / 2));
        StartFly(target, distance, _yaw, Math.Clamp(_pitch, 0.95f, 1.2f), animate);
    }

    internal void ResetCamera(bool animate = true)
    {
        var size = (float)(Model?.Size ?? CityLayout.DefaultSize);
        var target = new Vector3(size / 2, 0, size / 2);
        if (Bounds.Width < 2 || Bounds.Height < 2)
        {
            _fitPending = true;
            StartFly(target, size * 1.6f, HomeYaw, HomePitch, animate: false);
            return;
        }

        StartFly(target, FitDistance(target, HomeYaw, HomePitch), HomeYaw, HomePitch, animate);
    }

    // The distance at which the whole city (with its tallest building) fills
    // most of the view: perspective size is about inverse to distance, so a
    // few rescaling passes converge.
    private float FitDistance(Vector3 target, float yaw, float pitch)
    {
        var model = Model;
        var size = (float)(model?.Size ?? CityLayout.DefaultSize);
        var top = model is { Buildings.Count: > 0 } ? (float)model.Buildings.Max(b => b.Top) : 0f;
        var saved = (_target, _distance, _yaw, _pitch);
        var distance = size * 1.6f;
        for (var pass = 0; pass < 4; pass++)
        {
            (_target, _distance, _yaw, _pitch) = (target, distance, yaw, pitch);
            UpdateCamera();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var x in new[] { 0f, size })
            {
                foreach (var y in new[] { 0f, top })
                {
                    foreach (var z in new[] { 0f, size })
                    {
                        if (Project(new Vector3(x, y, z)) is { } p)
                        {
                            (minX, maxX, minY, maxY) = (Math.Min(minX, p.X), Math.Max(maxX, p.X), Math.Min(minY, p.Y), Math.Max(maxY, p.Y));
                        }
                    }
                }
            }

            var need = Math.Max((maxX - minX) / (Bounds.Width * 0.92), (maxY - minY) / (Bounds.Height * 0.80));
            if (!double.IsFinite(need) || need <= 0)
            {
                break;
            }

            distance = Math.Clamp(distance * (float)need, size * 0.2f, size * 4f);
        }

        (_target, _distance, _yaw, _pitch) = saved;
        return distance;
    }

    private void StartFly(Vector3 target, float distance, float yaw, float pitch, bool animate)
    {
        _flyTimer?.Stop();
        if (!animate || !_hasCamera)
        {
            (_target, _distance, _yaw, _pitch) = (target, distance, yaw, pitch);
            _hasCamera = true;
            InvalidateVisual();
            return;
        }

        _flyFrom = (_target, _distance, _yaw, _pitch);
        _flyTo = (target, distance, yaw, pitch);
        _flyStart = DateTime.UtcNow;
        _flyTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, OnFlyTick);
        _flyTimer.Start();
    }

    private void OnFlyTick(object? sender, EventArgs e)
    {
        var t = (float)Math.Min(1, (DateTime.UtcNow - _flyStart).TotalMilliseconds / 450);
        var ease = 1 - MathF.Pow(1 - t, 3);
        _target = Vector3.Lerp(_flyFrom.Target, _flyTo.Target, ease);
        _distance = _flyFrom.Distance + (_flyTo.Distance - _flyFrom.Distance) * ease;
        _yaw = _flyFrom.Yaw + (_flyTo.Yaw - _flyFrom.Yaw) * ease;
        _pitch = _flyFrom.Pitch + (_flyTo.Pitch - _flyFrom.Pitch) * ease;
        if (t >= 1)
        {
            _flyTimer!.Stop();
        }

        InvalidateVisual();
    }

    // ---- input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        _pressed = true;
        _dragged = false;
        _panning = !point.Properties.IsLeftButtonPressed;
        _pressPoint = _lastPoint = point.Position;
        _flyTimer?.Stop();
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;
        if (_pressed)
        {
            var delta = position - _lastPoint;
            _lastPoint = position;
            if (!_dragged && Distance(position, _pressPoint) < 4)
            {
                return;
            }

            _dragged = true;
            _hover = null;
            if (_panning)
            {
                Pan(delta);
            }
            else
            {
                _yaw -= (float)delta.X * 0.008f;
                _pitch = Math.Clamp(_pitch + (float)delta.Y * 0.006f, MinPitch, MaxPitch);
            }

            InvalidateVisual();
            return;
        }

        UpdateHover();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        e.Pointer.Capture(null);
        if (!_dragged && !_panning && Pick(e.GetPosition(this)) is { } hit)
        {
            if (hit.IsFile)
            {
                if (OpenCommand is { } open && open.CanExecute(hit))
                {
                    open.Execute(hit);
                }
            }
            else
            {
                FlyTo(hit);
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _flyTimer?.Stop();
        var size = (float)(Model?.Size ?? CityLayout.DefaultSize);
        var old = _distance;
        _distance = Math.Clamp(_distance * MathF.Pow(0.85f, (float)e.Delta.Y), size * 0.04f, size * 3f);

        // Zoom toward the ground point under the pointer.
        UpdateCamera();
        if (GroundPoint(e.GetPosition(this)) is { } ground)
        {
            _target += (ground - _target) * (1 - _distance / old);
            _target.Y = 0;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Home)
        {
            ResetCamera();
            e.Handled = true;
        }
    }

    private void Pan(Point delta)
    {
        UpdateCamera();
        var scale = _distance / _focal;
        var flatForward = Vector3.Normalize(new Vector3(_forward.X, 0, _forward.Z));
        var flatRight = Vector3.Normalize(new Vector3(_right.X, 0, _right.Z));
        _target += -flatRight * (float)delta.X * scale + flatForward * (float)delta.Y * scale;
        var size = (float)(Model?.Size ?? CityLayout.DefaultSize);
        _target.X = Math.Clamp(_target.X, -size * 0.25f, size * 1.25f);
        _target.Z = Math.Clamp(_target.Z, -size * 0.25f, size * 1.25f);
    }

    private void UpdateHover()
    {
        var hit = _pointer is { } p ? Pick(p) : null;
        if (!ReferenceEquals(hit, _hover) || hit is not null)
        {
            _hover = hit;
            InvalidateVisual();
        }
    }

    // ---- camera and picking ----

    private void UpdateCamera()
    {
        var bounds = Bounds.Size;
        _center = new Point(bounds.Width / 2, bounds.Height / 2);
        _focal = (float)(Math.Max(1, bounds.Height) / 2 / Math.Tan(FieldOfView / 2));
        var offset = new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
        _eye = _target + offset * _distance;
        _forward = Vector3.Normalize(_target - _eye);
        _right = Vector3.Normalize(Vector3.Cross(_forward, Vector3.UnitY));
        _up = Vector3.Cross(_right, _forward);
    }

    private Vector3 Ray(Point screen)
        => Vector3.Normalize(_forward
            + _right * (float)((screen.X - _center.X) / _focal)
            + _up * (float)((_center.Y - screen.Y) / _focal));

    private Vector3? GroundPoint(Point screen)
    {
        var dir = Ray(screen);
        if (dir.Y >= -1e-4f)
        {
            return null;
        }

        var t = -_eye.Y / dir.Y;
        return _eye + dir * t;
    }

    // The nearest building or district under a screen point.
    internal CityBlock? Pick(Point screen)
    {
        if (Model is not { } model)
        {
            return null;
        }

        UpdateCamera();
        var dir = Ray(screen);
        CityBlock? best = null;
        var bestT = float.MaxValue;
        foreach (var block in model.Buildings.Concat(model.Districts))
        {
            if (Intersect(block, dir) is { } t && t < bestT - 1e-3f)
            {
                bestT = t;
                best = block;
            }
        }

        return best;
    }

    private float? Intersect(CityBlock block, Vector3 dir)
    {
        var f = block.Footprint;
        var min = new Vector3((float)f.X, (float)block.Base, (float)f.Y);
        var max = new Vector3((float)(f.X + f.Width), (float)block.Top, (float)(f.Y + f.Height));
        var tMin = 0f;
        var tMax = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? _eye.X : axis == 1 ? _eye.Y : _eye.Z;
            var d = axis == 0 ? dir.X : axis == 1 ? dir.Y : dir.Z;
            var lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            var hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-7f)
            {
                if (o < lo || o > hi)
                {
                    return null;
                }

                continue;
            }

            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            tMin = Math.Max(tMin, Math.Min(t1, t2));
            tMax = Math.Min(tMax, Math.Max(t1, t2));
            if (tMin > tMax)
            {
                return null;
            }
        }

        return tMin;
    }

    // Screen point of a world point; null when behind the camera.
    internal Point? Project(Vector3 world)
    {
        var d = world - _eye;
        var z = Vector3.Dot(d, _forward);
        if (z < 0.5f)
        {
            return null;
        }

        return new Point(
            _center.X + _focal * Vector3.Dot(d, _right) / z,
            _center.Y - _focal * Vector3.Dot(d, _up) / z);
    }

    // ---- rendering ----

    // The drawn city is kept as a bitmap and redrawn only when what it shows
    // changes (camera, size, model, search); hovering only draws the outline
    // and the card over it.
    private RenderTargetBitmap? _scene;
    private (Vector3, float, float, float, Size, double, CityModel?, HashSet<CityBlock>?) _sceneKey;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Model is not { } model || bounds.Width < 2 || bounds.Height < 2)
        {
            context.FillRectangle(BackgroundBrush, bounds);
            return;
        }

        if (_fitPending)
        {
            _fitPending = false;
            var home = new Vector3((float)model.Size / 2, 0, (float)model.Size / 2);
            _distance = FitDistance(home, _yaw, _pitch);
        }

        UpdateCamera();
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var key = (_target, _distance, _yaw, _pitch, bounds.Size, scale, Model, _matches);
        if (_scene is null || key != _sceneKey)
        {
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)));
            if (_scene is null || _scene.PixelSize != pixels)
            {
                _scene?.Dispose();
                _scene = new RenderTargetBitmap(pixels, new global::Avalonia.Vector(96 * scale, 96 * scale));
            }

            using (var scene = _scene.CreateDrawingContext())
            {
                RenderScene(scene, model, bounds);
            }

            _sceneKey = key;
        }

        context.DrawImage(_scene, new Rect(_scene.Size), bounds);
        if (_hover is { } hover)
        {
            DrawTopOutline(context, hover, HoverPen);
            if (_pointer is { } pointer)
            {
                DrawCard(context, hover, pointer, bounds);
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _scene?.Dispose();
        _scene = null;
    }

    private void RenderScene(DrawingContext context, CityModel model, Rect bounds)
    {
        context.FillRectangle(BackgroundBrush, bounds);

        var size = (float)model.Size;
        DrawQuad(context, GroundBrush, null,
            new Vector3(0, 0, 0), new Vector3(size, 0, 0), new Vector3(size, 0, size), new Vector3(0, 0, size));

        foreach (var block in DrawOrder(model))
        {
            var ghost = _matches is not null && (!block.IsFile || !_matches.Contains(block));
            DrawBox(context, block, _colors[block], ghost);
            if (block.IsFile && _matches is not null && !ghost)
            {
                DrawTopOutline(context, block, MatchPen);
            }
        }

        DrawLabels(context, model);
        DrawLegend(context, model, bounds);
    }

    // Every plinth and building, back to front. The treemap nests: all of a
    // district's contents stand inside its footprint, on top of it. So a
    // district goes before its contents, and two siblings' whole subtrees
    // follow the order of the siblings themselves - which BoxDrawOrder sets
    // exactly from their separating plane. Only siblings are ever compared.
    internal List<CityBlock> DrawOrder(CityModel model)
    {
        var order = new List<CityBlock>(model.Districts.Count + model.Buildings.Count);
        Emit(_topLevel);
        return order;

        void Emit(List<CityBlock> siblings)
        {
            var boxes = new BoxDrawOrder.Box[siblings.Count];
            var extents = new Rect[siblings.Count];
            for (var i = 0; i < siblings.Count; i++)
            {
                boxes[i] = BoxOf(siblings[i]);
                extents[i] = ScreenExtent(boxes[i]);
            }

            foreach (var i in BoxDrawOrder.Sort(boxes, _eye, OverlappingPairs(extents), i => FarDistance(siblings[i])))
            {
                var block = siblings[i];
                order.Add(block);
                if (_contents.TryGetValue(block, out var inside))
                {
                    Emit(inside);
                }
            }
        }
    }

    // Pairs whose screen extents overlap: a sweep along x, so a folder of
    // hundreds of small files costs about one pass, not every pair.
    private static List<(int, int)> OverlappingPairs(Rect[] extents)
    {
        var pairs = new List<(int, int)>();
        var byLeft = Enumerable.Range(0, extents.Length).OrderBy(i => extents[i].X).ToArray();
        for (var a = 0; a < byLeft.Length; a++)
        {
            var ea = extents[byLeft[a]];
            for (var b = a + 1; b < byLeft.Length && extents[byLeft[b]].X <= ea.Right; b++)
            {
                var eb = extents[byLeft[b]];
                if (eb.Y <= ea.Bottom && ea.Y <= eb.Bottom)
                {
                    pairs.Add((byLeft[a], byLeft[b]));
                }
            }
        }

        return pairs;
    }

    // A box's screen bounding rectangle; a huge one when part of it is behind
    // the camera (so it is ordered against everything).
    private Rect ScreenExtent(BoxDrawOrder.Box box)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var corner = 0; corner < 8; corner++)
        {
            var world = new Vector3(
                (corner & 1) == 0 ? box.Min.X : box.Max.X,
                (corner & 2) == 0 ? box.Min.Y : box.Max.Y,
                (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
            if (Project(world) is not { } p)
            {
                return new Rect(-1e6, -1e6, 2e6, 2e6);
            }

            (minX, maxX, minY, maxY) = (Math.Min(minX, p.X), Math.Max(maxX, p.X), Math.Min(minY, p.Y), Math.Max(maxY, p.Y));
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static BoxDrawOrder.Box BoxOf(CityBlock block)
    {
        var f = block.Footprint;
        return new BoxDrawOrder.Box(
            new Vector3((float)f.X, (float)block.Base, (float)f.Y),
            new Vector3((float)(f.X + f.Width), (float)block.Top, (float)(f.Y + f.Height)));
    }

    // Distance from the eye to the nearest point of the box: the fallback
    // order between boxes that do not overlap on screen.
    private float FarDistance(CityBlock block)
    {
        var f = block.Footprint;
        var x = Math.Clamp(_eye.X, (float)f.X, (float)(f.X + f.Width));
        var z = Math.Clamp(_eye.Z, (float)f.Y, (float)(f.Y + f.Height));
        var y = Math.Clamp(_eye.Y, (float)block.Base, (float)block.Top);
        return Vector3.DistanceSquared(_eye, new Vector3(x, y, z));
    }

    private void DrawBox(DrawingContext context, CityBlock block, Color color, bool ghost)
    {
        var f = block.Footprint;
        if (f.Width <= 0 || f.Height <= 0)
        {
            return;
        }

        if (ghost)
        {
            color = Blend(color, Background, block.IsFile ? 0.78 : 0.55);
        }

        var x0 = (float)f.X;
        var x1 = (float)(f.X + f.Width);
        var z0 = (float)f.Y;
        var z1 = (float)(f.Y + f.Height);
        var y0 = (float)block.Base;
        var y1 = (float)block.Top;

        // Only the faces turned toward the eye.
        if (_eye.X < x0)
        {
            DrawQuad(context, Shade(color, 0.70), null, new(x0, y0, z0), new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1));
        }
        else if (_eye.X > x1)
        {
            DrawQuad(context, Shade(color, 0.70), null, new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1));
        }

        if (_eye.Z < z0)
        {
            DrawQuad(context, Shade(color, 0.52), null, new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0));
        }
        else if (_eye.Z > z1)
        {
            DrawQuad(context, Shade(color, 0.52), null, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
        }

        if (_eye.Y > y1)
        {
            DrawQuad(context, Shade(color, 1.0), EdgePen, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1));
        }
    }

    private void DrawTopOutline(DrawingContext context, CityBlock block, IPen pen)
    {
        var f = block.Footprint;
        var y = (float)block.Top;
        DrawQuad(context, null, pen,
            new((float)f.X, y, (float)f.Y),
            new((float)(f.X + f.Width), y, (float)f.Y),
            new((float)(f.X + f.Width), y, (float)(f.Y + f.Height)),
            new((float)f.X, y, (float)(f.Y + f.Height)));
    }

    private void DrawQuad(DrawingContext context, IBrush? fill, IPen? pen, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        if (Project(a) is not { } pa || Project(b) is not { } pb || Project(c) is not { } pc || Project(d) is not { } pd)
        {
            return;
        }

        // Edges only where the face is big enough for them to help.
        var extent = Math.Max(Math.Max(Math.Abs(pa.X - pc.X), Math.Abs(pb.X - pd.X)), Math.Max(Math.Abs(pa.Y - pc.Y), Math.Abs(pb.Y - pd.Y)));
        if (extent < 0.6 && pen != HoverPen && pen != MatchPen)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(pa, fill is not null);
            g.LineTo(pb);
            g.LineTo(pc);
            g.LineTo(pd);
            g.EndFigure(true);
        }

        context.DrawGeometry(fill, fill is null || extent >= 8 ? pen : null, geometry);
    }

    // District names on their plinths, the top level always, deeper ones when
    // there is room.
    private void DrawLabels(DrawingContext context, CityModel model)
    {
        var taken = new List<Rect>();
        bool Place(Rect rect)
        {
            if (taken.Any(r => r.Intersects(rect.Inflate(2))))
            {
                return false;
            }

            taken.Add(rect);
            return true;
        }

        // Search matches first: a name tag over each (when they are few).
        if (_matches is { Count: > 0 and <= 25 } matches)
        {
            foreach (var match in matches.OrderBy(m => m.HotspotRank == 0 ? int.MaxValue : m.HotspotRank))
            {
                var f = match.Footprint;
                if (Project(new Vector3((float)(f.X + f.Width / 2), (float)match.Top, (float)(f.Y + f.Height / 2))) is not { } top)
                {
                    continue;
                }

                var tag = Text(match.Name, 12, TextBrush, bold: true);
                var box = new Rect(top.X - tag.Width / 2 - 6, top.Y - tag.Height - 12, tag.Width + 12, tag.Height + 4);
                if (Place(box))
                {
                    context.DrawRectangle(CardBrush, MatchPen, box, 3, 3);
                    context.DrawText(tag, box.TopLeft + new Point(6, 2));
                }
            }
        }

        // Map-style labels: on top of everything in a dark pill at the
        // district's centre, so orbiting never hides them behind a building.
        // Top-level folders always get one; deeper ones when their plinth is
        // big enough on screen. Bigger districts win a clash, so the choice
        // stays steady while the camera moves.
        foreach (var district in model.Districts.Where(d => d.Level <= 2).OrderBy(d => d.Level).ThenByDescending(d => d.Footprint.Area))
        {
            var f = district.Footprint;
            var y = (float)district.Top;
            if (Project(new Vector3((float)(f.X + f.Width / 2), y, (float)(f.Y + f.Height / 2))) is not { } center)
            {
                continue;
            }

            if (district.Level > 0)
            {
                var corner = Project(new Vector3((float)f.X, y, (float)f.Y));
                var opposite = Project(new Vector3((float)(f.X + f.Width), y, (float)(f.Y + f.Height)));
                if (corner is null || opposite is null || Distance(corner.Value, opposite.Value) < 120 * district.Level)
                {
                    continue;
                }
            }

            var top = district.Level == 0;
            var text = Text(district.Name, top ? 12.5 : 11, top ? TextBrush : LabelBrush, top);
            var pill = new Rect(center.X - text.Width / 2 - 6, center.Y - text.Height / 2 - 2, text.Width + 12, text.Height + 4);
            if (!new Rect(Bounds.Size).Contains(pill.Center) || !Place(pill))
            {
                continue;
            }

            context.DrawRectangle(LabelPillBrush, top ? LabelPillPen : null, pill, 4, 4);
            context.DrawText(text, pill.TopLeft + new Point(6, 2));
        }
    }

    private static void DrawLegend(DrawingContext context, CityModel model, Rect bounds)
    {
        var (title, low, high, ramp, none) = !model.HasHistory
            ? ("Height = lines (no git history)", string.Empty, string.Empty, (Color[]?)null, HotspotNone)
            : model.Mode == CityMode.Hotspots
                ? ("Height = churn · colour = churn × size", "calm", "hotspot", HotspotRamp, HotspotNone)
                : ("Height = commits · colour = last change", "old", "fresh", AgeRamp, AgeNone);

        var titleText = Text(title, 12, TextBrush);
        var panelWidth = Math.Max(titleText.Width, 220) + 20;
        var panelHeight = ramp is null ? 30 : 64;
        var panel = new Rect(10, bounds.Height - panelHeight - 10, panelWidth, panelHeight);
        context.DrawRectangle(CardBrush, CardPen, panel, 4, 4);
        context.DrawText(titleText, panel.TopLeft + new Point(10, 7));

        if (ramp is not null)
        {
            var bar = new Rect(panel.X + 10, panel.Y + 28, 120, 10);
            var stops = new GradientStops();
            for (var i = 0; i < ramp.Length; i++)
            {
                stops.Add(new GradientStop(ramp[i], (double)i / (ramp.Length - 1)));
            }

            context.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = stops,
            }, null, bar, 2, 2);
            context.DrawText(Text(low, 10.5, MutedBrush), new Point(bar.X, bar.Bottom + 2));
            var highText = Text(high, 10.5, MutedBrush);
            context.DrawText(highText, new Point(bar.Right - highText.Width, bar.Bottom + 2));

            var swatch = new Rect(bar.Right + 18, bar.Y, 10, 10);
            context.DrawRectangle(new ImmutableSolidColorBrush(none), null, swatch, 2, 2);
            context.DrawText(Text("no change", 10.5, MutedBrush), new Point(swatch.Right + 5, swatch.Y - 2));
        }

        var hint = Text("Drag: orbit · Right-drag: pan · Wheel: zoom · Click: open · Home: reset", 11, MutedBrush);
        if (hint.Width + panel.Right + 30 < bounds.Width)
        {
            context.DrawText(hint, new Point(bounds.Width - hint.Width - 12, bounds.Height - hint.Height - 10));
        }
    }

    private void DrawCard(DrawingContext context, CityBlock block, Point pointer, Rect bounds)
    {
        var heat = Shade(_colors.TryGetValue(block, out var c) ? Blend(c, Colors.White, 0.2) : Colors.White, 1.0);
        IBrush BrushOf(CardTone tone) => tone switch
        {
            CardTone.Muted => MutedBrush,
            CardTone.Added => AddedBrush,
            CardTone.Removed => RemovedBrush,
            CardTone.Heat => heat,
            _ => TextBrush,
        };

        // Each line is a row of coloured runs.
        var lines = new List<List<FormattedText>>
        {
            new() { Text(block.Name, 13, TextBrush, bold: true) },
            new() { Text(block.RelativePath, 11, MutedBrush) },
        };
        foreach (var detail in Describe(block, Model!.HasHistory, Model.Mode))
        {
            lines.Add(detail.Select(run => Text(run.Text, 12, BrushOf(run.Tone))).ToList());
        }

        const double Swatch = 10;
        double LineWidth(List<FormattedText> line) => line.Sum(t => t.WidthIncludingTrailingWhitespace);
        double LineHeight(List<FormattedText> line) => line.Max(t => t.Height);
        var width = Math.Max(LineWidth(lines[0]) + Swatch + 6, lines.Skip(1).Max(LineWidth)) + 20;
        var height = lines.Sum(l => LineHeight(l) + 2) + 14;
        var x = pointer.X + 16;
        var y = pointer.Y + 18;
        if (x + width > bounds.Width - 4)
        {
            x = Math.Max(4, pointer.X - width - 8);
        }

        if (y + height > bounds.Height - 4)
        {
            y = Math.Max(4, pointer.Y - height - 8);
        }

        context.DrawRectangle(CardBrush, CardPen, new Rect(x, y, width, height), 4, 4);
        var top = y + 7;
        for (var i = 0; i < lines.Count; i++)
        {
            var left = x + 10;
            if (i == 0)
            {
                // The building's own colour beside its name.
                var mid = top + LineHeight(lines[0]) / 2;
                context.DrawRectangle(Shade(c, 1.0), null, new Rect(left, mid - Swatch / 2, Swatch, Swatch), 2, 2);
                left += Swatch + 6;
            }

            foreach (var run in lines[i])
            {
                context.DrawText(run, new Point(left, top));
                left += run.WidthIncludingTrailingWhitespace;
            }

            top += LineHeight(lines[i]) + 2;
        }
    }

    internal enum CardTone
    {
        Text,
        Muted,
        Added,
        Removed,
        Heat,
    }

    internal readonly record struct CardRun(string Text, CardTone Tone);

    // The card's detail lines (each a row of coloured runs) for a building or
    // district. The mode's own measure takes the building's colour: the
    // hotspot rank in Hotspots, the last change in Age.
    internal static IEnumerable<CardRun[]> Describe(CityBlock block, bool hasHistory, CityMode mode)
    {
        var node = block.Node;
        var size = node.IsBinary ? "binary" : Count(node.Lines.Total, "line");
        yield return block.IsFile
            ? new[] { new CardRun(size, CardTone.Text) }
            : new[] { new CardRun(Count(node.FileCount, "file"), CardTone.Text), new CardRun(" · ", CardTone.Muted), new CardRun(size, CardTone.Text) };
        if (!hasHistory)
        {
            yield break;
        }

        if (node.Churn > 0)
        {
            yield return new[]
            {
                new CardRun($"+{node.Added:N0}", CardTone.Added),
                new CardRun(" ", CardTone.Text),
                new CardRun($"−{node.Removed:N0}", CardTone.Removed),
                new CardRun(" churn", CardTone.Muted),
            };
        }

        var git = new List<CardRun>();
        void Add(string text, CardTone tone)
        {
            if (git.Count > 0)
            {
                git.Add(new CardRun(" · ", CardTone.Muted));
            }

            git.Add(new CardRun(text, tone));
        }

        if (node.CommitCount > 0)
        {
            Add(Count(node.CommitCount, "commit"), CardTone.Text);
        }

        if (node.AuthorCount > 0)
        {
            Add(Count(node.AuthorCount, "author"), CardTone.Text);
        }

        var age = CodeAnalysisPanelViewModel.Age(node.LastChanged, DateTimeOffset.Now);
        Add(age.Length > 0 ? "changed " + age + " ago" : "no change in period",
            age.Length == 0 ? CardTone.Muted : block.IsFile && mode == CityMode.Age ? CardTone.Heat : CardTone.Text);
        yield return git.ToArray();
        if (block.HotspotRank > 0)
        {
            yield return new[]
            {
                new CardRun($"Hotspot #{block.HotspotRank}", mode == CityMode.Hotspots ? CardTone.Heat : CardTone.Text),
            };
        }
    }

    private static string Count(long value, string noun)
        => $"{value.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(value == 1 ? string.Empty : "s")}";

    private static FormattedText Text(string text, double size, IBrush brush, bool bold = false)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? UiBold : UiFont, size, brush);

    // ---- colours ----

    internal static Color BuildingColor(CityMode mode, double heat)
    {
        if (heat < 0)
        {
            return mode == CityMode.Hotspots ? HotspotNone : AgeNone;
        }

        var ramp = mode == CityMode.Hotspots ? HotspotRamp : AgeRamp;
        var scaled = Math.Clamp(heat, 0, 1) * (ramp.Length - 1);
        var i = Math.Min((int)scaled, ramp.Length - 2);
        return Blend(ramp[i], ramp[i + 1], scaled - i);
    }

    private static Color DistrictColor(int level)
    {
        var v = (byte)Math.Min(0x50, 0x34 + level * 7);
        return Color.FromRgb(v, (byte)(v + 2), (byte)(v + 6));
    }

    private IBrush Shade(Color color, double factor)
    {
        var shaded = Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));
        if (!_brushes.TryGetValue(shaded.ToUInt32(), out var brush))
        {
            brush = new ImmutableSolidColorBrush(shaded);
            _brushes[shaded.ToUInt32()] = brush;
        }

        return brush;
    }

    private static Color Blend(Color from, Color to, double t)
        => Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
