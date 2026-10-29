using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
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
// pointer, WASD flies (Space up, Ctrl down, Shift faster), Home resets.
// Hover shows a card; clicking a building opens its diff, clicking a
// district flies to it.
internal sealed class CodeCityView : Control, global::Avalonia.Rendering.ICustomHitTest
{
    public static readonly StyledProperty<CityModel?> ModelProperty =
        AvaloniaProperty.Register<CodeCityView, CityModel?>(nameof(Model));

    public static readonly StyledProperty<string?> SearchTextProperty =
        AvaloniaProperty.Register<CodeCityView, string?>(nameof(SearchText));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<CodeCityView, ICommand?>(nameof(OpenCommand));

    // Right-click (without dragging) on a building or a district.
    public static readonly StyledProperty<ICommand?> RevealCommandProperty =
        AvaloniaProperty.Register<CodeCityView, ICommand?>(nameof(RevealCommand));

    public static readonly StyledProperty<bool> ShowFpsProperty =
        AvaloniaProperty.Register<CodeCityView, bool>(nameof(ShowFps));

    private const float FieldOfView = 0.70f;
    private const float MinPitch = 0.12f;
    private const float MaxPitch = 1.50f;
    private const float HomeYaw = -0.62f;
    private const float HomePitch = 0.80f;

    private static readonly Color Background = Color.Parse("#1B1C1F");
    private static readonly IBrush BackgroundBrush = new ImmutableSolidColorBrush(Background);
    private static readonly Color GroundColor = Color.Parse("#232529");
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.Parse("#DFE1E5"));
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse("#9DA0A8"));
    private static readonly IBrush AddedBrush = new ImmutableSolidColorBrush(Color.Parse("#6BB36B"));
    private static readonly IBrush RemovedBrush = new ImmutableSolidColorBrush(Color.Parse("#D16969"));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#C8DFE1E5"));
    private static readonly IBrush LabelPillBrush = new ImmutableSolidColorBrush(Color.Parse("#C8181A1D"));
    private static readonly IPen LabelPillPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#5A5E66")), 1);
    private static readonly IBrush CardBrush = new ImmutableSolidColorBrush(Color.Parse("#F02B2D31"));
    private static readonly IPen CardPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#4A4D55")), 1);
    private static readonly IPen HoverPen = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 2);
    private static readonly IPen HighlightPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#D0FFFFFF")), 1.5);
    private static readonly IBrush HighlightBrush = new ImmutableSolidColorBrush(Color.Parse("#40FFFFFF"));
    private static readonly IPen MatchPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFE082")), 1.5);
    private static readonly Typeface UiFont = new(FontFamily.Default);
    private static readonly Typeface UiBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    // Ramps: Hotspots calm blue -> amber -> red; Age faded slate -> bright cyan;
    // Debt fresh mint -> sand -> worn brown -> weathered grey (duller = worse;
    // light enough that its dark, broken windows still show).
    private static readonly Color[] HotspotRamp = { Color.Parse("#4F6E91"), Color.Parse("#E0B04F"), Color.Parse("#E5484D") };
    private static readonly Color[] AgeRamp = { Color.Parse("#4D5766"), Color.Parse("#3E8FB0"), Color.Parse("#62D8F5") };
    private static readonly Color[] DebtRamp = { Color.Parse("#9AD6BE"), Color.Parse("#CFC08C"), Color.Parse("#9A7E62"), Color.Parse("#77685A") };
    private static readonly Color HotspotNone = Color.Parse("#596171");
    private static readonly Color AgeNone = Color.Parse("#3A3E45");
    private static readonly Color DebtNone = Color.Parse("#4A4E55");

    private readonly Dictionary<uint, IBrush> _brushes = new();
    private readonly Dictionary<CityBlock, Color> _colors = new();

    // The Debt city's roofs, windows and cracks, per building (empty in the
    // other modes).
    private Dictionary<CityBlock, DebtWear> _wear = new();

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
    private CityBlock? _menuBlock;

    private bool _flying;
    private bool _frameRequested;
    private TimeSpan? _lastFrameTime;
    private readonly HashSet<Key> _held = new();
    private (Vector3 Target, float Distance, float Yaw, float Pitch) _flyFrom;
    private (Vector3 Target, float Distance, float Yaw, float Pitch) _flyTo;
    private DateTime _flyStart;

    static CodeCityView()
    {
        AffectsRender<CodeCityView>(ModelProperty, SearchTextProperty, ShowFpsProperty);
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

    public ICommand? RevealCommand
    {
        get => GetValue(RevealCommandProperty);
        set => SetValue(RevealCommandProperty, value);
    }

    // The last building menu opened (tests).
    internal ContextMenu? BlockMenu { get; private set; }

    // The performance panel (the "Show FPS" box).
    public bool ShowFps
    {
        get => GetValue(ShowFpsProperty);
        set => SetValue(ShowFpsProperty, value);
    }

    // The GPU view under this one (set by the window); when it is ready this
    // control draws only the overlays and leaves the city to it.
    public CityGlView? Gpu
    {
        get => _gpu;
        set
        {
            _gpu = value;
            if (value is not null)
            {
                value.Scene = this;
            }

            OnGpuChanged();
        }
    }

    private CityGlView? _gpu;
    private bool _gpuGeometryFresh;

    internal bool UsesGpu => _gpu is { IsReady: true };

    // The whole area takes the pointer, transparent or not.
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    internal void OnGpuChanged()
    {
        _gpuGeometryFresh = false;
        _faces = null;
        InvalidateVisual();
    }

    // The camera for the GPU: the same eye, target and field of view the
    // software projection (and so picking and the overlays) uses.
    internal Matrix4x4 ViewProjection(float aspect)
    {
        UpdateCamera();
        var near = Math.Max(0.5f, _distance * 0.01f);
        var far = _distance * 4 + (float)(Model?.Size ?? CityLayout.DefaultSize) * 3;
        return Matrix4x4.CreateLookAt(_eye, _target, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, near, far);
    }

    // World-space triangles for every plinth and building (and the ground):
    // five faces a box, each with its shade and a 0..1 uv so the shader can
    // draw its edges. Colours go in a second buffer so a search only resends
    // those.
    private void PushGpuGeometry(CityModel model)
    {
        var blocks = model.Districts.Concat(model.Buildings).ToList();
        var faces = blocks.Count * 5 + 1 + _wear.Values.Sum(w => w.Pieces.Count);
        var vertices = new float[faces * 6 * CityGlView.Stride];
        var count = 0;
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float shade, float edge)
        {
            Span<(Vector3 P, float U, float V)> quad = stackalloc (Vector3, float, float)[] { (a, 0, 0), (b, 1, 0), (c, 1, 1), (d, 0, 1) };
            foreach (var i in TriangleCorners)
            {
                var o = count++ * CityGlView.Stride;
                var (p, u, v) = quad[i];
                (vertices[o], vertices[o + 1], vertices[o + 2], vertices[o + 3]) = (p.X, p.Y, p.Z, shade);
                (vertices[o + 4], vertices[o + 5], vertices[o + 6]) = (u, v, edge);
            }
        }

        var size = (float)model.Size;
        Face(new(0, 0, 0), new(size, 0, 0), new(size, 0, size), new(0, 0, size), 1, 0);
        foreach (var block in blocks)
        {
            var f = block.Footprint;
            var (x0, x1, z0, z1) = ((float)f.X, (float)(f.X + f.Width), (float)f.Y, (float)(f.Y + f.Height));
            var (y0, y1) = ((float)block.Base, (float)block.Top);
            var edge = block.IsFile ? 1f : 0.6f;
            var wear = _wear.GetValueOrDefault(block);
            var (r00, r10, r11, r01) = wear is null ? (y1, y1, y1, y1) : (wear.Y00, wear.Y10, wear.Y11, wear.Y01);
            Face(new(x0, r00, z0), new(x1, r10, z0), new(x1, r11, z1), new(x0, r01, z1), 1f, edge);
            Face(new(x0, y0, z0), new(x0, r00, z0), new(x0, r01, z1), new(x0, y0, z1), 0.70f, edge);
            Face(new(x1, y0, z0), new(x1, r10, z0), new(x1, r11, z1), new(x1, y0, z1), 0.70f, edge);
            Face(new(x0, y0, z0), new(x1, y0, z0), new(x1, r10, z0), new(x0, r00, z0), 0.52f, edge);
            Face(new(x0, y0, z1), new(x1, y0, z1), new(x1, r11, z1), new(x0, r01, z1), 0.52f, edge);
            foreach (var piece in wear?.Pieces ?? Array.Empty<DebtWear.Piece>())
            {
                Face(piece.A, piece.B, piece.C, piece.D, 1f, 0);
            }
        }

        _gpuBlocks = blocks;
        _gpu!.SetGeometry(vertices, GpuColors(model), count);
        _gpuGeometryFresh = true;
    }

    private static readonly int[] TriangleCorners = { 0, 1, 2, 0, 2, 3 };

    private List<CityBlock> _gpuBlocks = new();

    // RGBA per vertex, in the order PushGpuGeometry wrote the faces.
    private byte[] GpuColors(CityModel model)
    {
        var colors = new byte[(_gpuBlocks.Count * 5 + 1 + _wear.Values.Sum(w => w.Pieces.Count)) * 6 * 4];
        var at = 0;
        void Put(Color color, int vertices)
        {
            for (var i = 0; i < vertices; i++)
            {
                (colors[at], colors[at + 1], colors[at + 2], colors[at + 3]) = (color.R, color.G, color.B, 255);
                at += 4;
            }
        }

        Put(GroundColor, 6);
        foreach (var block in _gpuBlocks)
        {
            var color = _colors[block];
            var ghost = _matches is not null && (!block.IsFile || !_matches.Contains(block));
            if (ghost)
            {
                color = Blend(color, Background, block.IsFile ? 0.78 : 0.55);
            }

            Put(color, 30);
            foreach (var piece in _wear.GetValueOrDefault(block)?.Pieces ?? Array.Empty<DebtWear.Piece>())
            {
                Put(ghost ? Blend(piece.Color, Background, 0.78) : piece.Color, 6);
            }
        }

        return colors;
    }

    // The building or district under the pointer (for tests and the card).
    internal CityBlock? Hovered => _hover;

    // What is drawn highlighted: the right-clicked block while its menu is
    // open (the pointer has left for the menu), else the hovered one.
    internal CityBlock? Highlighted => _menuBlock ?? _hover;

    internal IReadOnlyCollection<CityBlock>? Matches => _matches;

    // A Debt city building's roof and windows (null in the other modes).
    internal DebtWear? WearOf(CityBlock block) => _wear.GetValueOrDefault(block);

    internal Vector3 CameraTarget => _target;

    internal float CameraDistance => _distance;

    internal Vector3 Eye => _eye;

    // Where the camera is flying to (its target when it is not flying).
    internal Vector3 FlyDestination => _flying ? _flyTo.Target : _target;

    // Lands a fly at once (headless tests have no running timers).
    internal void FinishFly()
    {
        if (_flying)
        {
            _flyStart = DateTime.MinValue;
            FlyStep();
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
        else if (change.Property == ShowFpsProperty && ShowFps)
        {
            _hud.Reset();
        }
    }

    // Every redraw goes through here, so the FPS panel can tell a busy
    // stretch from the user simply pausing.
    private new void InvalidateVisual()
    {
        _hud.Requested();
        base.InvalidateVisual();
    }

    private void OnModelChanged(bool resetCamera)
    {
        _gpuGeometryFresh = false;
        _labelCandidates = Model is { } labelled
            ? labelled.Districts.Where(d => d.Level <= 2).OrderBy(d => d.Level).ThenByDescending(d => d.Footprint.Area).ToList()
            : new List<CityBlock>();
        _colors.Clear();
        _wear = new Dictionary<CityBlock, DebtWear>();
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

            _wear = DebtWear.For(model);

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
        if (UsesGpu && _gpuGeometryFresh && Model is { } model)
        {
            _gpu!.SetColors(GpuColors(model));
        }
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
        _flying = false;
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
        _flying = true;
        RequestFrame();
    }

    private void FlyStep()
    {
        var t = (float)Math.Min(1, (DateTime.UtcNow - _flyStart).TotalMilliseconds / 450);
        var ease = 1 - MathF.Pow(1 - t, 3);
        _target = Vector3.Lerp(_flyFrom.Target, _flyTo.Target, ease);
        _distance = _flyFrom.Distance + (_flyTo.Distance - _flyFrom.Distance) * ease;
        _yaw = _flyFrom.Yaw + (_flyTo.Yaw - _flyFrom.Yaw) * ease;
        _pitch = _flyFrom.Pitch + (_flyTo.Pitch - _flyFrom.Pitch) * ease;
        if (t >= 1)
        {
            _flying = false;
        }

        InvalidateVisual();
    }

    // Flying and WASD advance once per display frame (not on a timer, which
    // would cap them near 60 FPS).
    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _frameRequested = true;
        _hud.Scheduled();
        top.RequestAnimationFrame(OnAnimationFrame);
    }

    // The benchmark (BUDIFF_CITY_BENCH): orbit nonstop, then report.
    private DateTime _benchStart;
    private DateTime _benchUntil;
    private Action<string>? _benchDone;
    private bool _benchWarm;

    internal void RunBench(TimeSpan warmUp, TimeSpan duration, Action<string> done)
    {
        _benchStart = DateTime.UtcNow + warmUp;
        _benchUntil = _benchStart + duration;
        _benchDone = done;
        _benchWarm = false;
        RequestFrame();
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        _frameRequested = false;
        var seconds = _lastFrameTime is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.1) : 1 / 60.0;
        _lastFrameTime = time;
        if (_benchDone is { } done)
        {
            _yaw += (float)seconds * 0.6f;
            InvalidateVisual();
            if (!_benchWarm && DateTime.UtcNow >= _benchStart)
            {
                _benchWarm = true;
                _hud.Reset();
            }

            if (DateTime.UtcNow >= _benchUntil)
            {
                _benchDone = null;
                done(PerfReport());
                return;
            }

            RequestFrame();
            return;
        }

        var moving = _held.Any(k => k is not (Key.LeftShift or Key.RightShift));
        if (_flying)
        {
            FlyStep();
        }
        else if (moving)
        {
            Move(seconds);
        }

        if (_flying || moving)
        {
            RequestFrame();
        }
        else
        {
            _lastFrameTime = null;
        }
    }

    // ---- input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (_hud.Bounds.Contains(point.Position))
        {
            // The performance panel: a click copies its report.
            CopyReport();
            e.Handled = true;
            return;
        }

        _pressed = true;
        _dragged = false;
        _panning = !point.Properties.IsLeftButtonPressed;
        _pressPoint = _lastPoint = point.Position;
        _flying = false;
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
        if (!_dragged && e.InitialPressMouseButton == MouseButton.Right && PickNear(e.GetPosition(this)) is { } picked
            && RevealCommand is { } reveal && reveal.CanExecute(picked))
        {
            // A right-click that did not pan: the building's menu.
            BlockMenu = FileManager.Menu(() => reveal.Execute(picked));
            _menuBlock = picked;
            BlockMenu.Closed += (_, _) =>
            {
                _menuBlock = null;
                InvalidateVisual();
            };
            BlockMenu.Open(this);
            InvalidateVisual();
        }
        else if (!_dragged && !_panning && PickNear(e.GetPosition(this)) is { } hit)
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
        _flying = false;
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
        else if (IsMoveKey(e.Key))
        {
            // Shooter-style flying: held keys move the camera every tick.
            if (_held.Add(e.Key))
            {
                _flying = false;
                RequestFrame();
            }

            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_held.Remove(e.Key))
        {
            e.Handled = true;
        }

    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _held.Clear();
    }

    private static bool IsMoveKey(Key key)
        => key is Key.W or Key.A or Key.S or Key.D or Key.Space
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift;

    internal IReadOnlyCollection<Key> HeldKeys => _held;

    // One step of WASD flying for the keys held: W/S along the view on the
    // ground, A/D sideways, Space up, Ctrl down; Shift triples the speed.
    // The speed follows the zoom, so it feels the same near and far.
    internal void Move(double seconds)
    {
        UpdateCamera();
        var flatForward = Vector3.Normalize(new Vector3(_forward.X, 0, _forward.Z));
        var flatRight = Vector3.Normalize(new Vector3(_right.X, 0, _right.Z));
        var direction = Vector3.Zero;
        foreach (var key in _held)
        {
            direction += key switch
            {
                Key.W => flatForward,
                Key.S => -flatForward,
                Key.D => flatRight,
                Key.A => -flatRight,
                Key.Space => Vector3.UnitY,
                Key.LeftCtrl or Key.RightCtrl => -Vector3.UnitY,
                _ => Vector3.Zero,
            };
        }

        if (direction == Vector3.Zero)
        {
            return;
        }

        var fast = _held.Contains(Key.LeftShift) || _held.Contains(Key.RightShift) ? 3f : 1f;
        _target += Vector3.Normalize(direction) * _distance * 0.8f * fast * (float)seconds;
        var size = (float)(Model?.Size ?? CityLayout.DefaultSize);
        _target.X = Math.Clamp(_target.X, -size * 0.25f, size * 1.25f);
        _target.Y = Math.Clamp(_target.Y, 0, size * 0.6f);
        _target.Z = Math.Clamp(_target.Z, -size * 0.25f, size * 1.25f);
        _hover = null;
        InvalidateVisual();
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
        var hit = _pointer is { } p && !_hud.Bounds.Contains(p) ? PickNear(p) : null;
        if (!ReferenceEquals(hit, _hover) || hit is not null)
        {
            _hover = hit;
            InvalidateVisual();
        }
    }

    // ---- camera and picking ----

    // What the pointer means: the block under it, except that a district
    // hit within a few pixels of a building means that building - in a dense
    // folder the streets between buildings are only a few pixels wide.
    internal CityBlock? PickNear(Point screen)
    {
        var hit = Pick(screen);
        if (hit is not { IsFile: false })
        {
            return hit;
        }

        foreach (var radius in new[] { 2.5, 5.0 })
        {
            for (var step = 0; step < 8; step++)
            {
                var angle = step * Math.PI / 4;
                if (Pick(screen + new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius)) is { IsFile: true } near)
                {
                    return near;
                }
            }
        }

        return hit;
    }

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

    // The city's faces, projected and ordered, are kept until what they show
    // changes (camera, size, model, search); a frame - a hover, say - only
    // hands them to Skia again. They are drawn straight into the window's own
    // Skia canvas (GPU-backed in the app), at its own scaling, so the overlay,
    // the labels and picking all share one frame.
    private FaceBatch? _faces;
    private static readonly FaceBatch EmptyFaces = new(1);
    private (Vector3, float, float, float, Size, CityModel?, HashSet<CityBlock>?) _sceneKey;
    private readonly CityPerfHud _hud = new();

    internal CityPerfHud Hud => _hud;

    public override void Render(DrawingContext context)
    {
        var frameClock = System.Diagnostics.Stopwatch.StartNew();
        var bounds = new Rect(Bounds.Size);
        var gpu = UsesGpu;
        if (!gpu)
        {
            context.FillRectangle(BackgroundBrush, bounds);
        }

        if (Model is not { } model || bounds.Width < 2 || bounds.Height < 2)
        {
            return;
        }

        if (_fitPending)
        {
            _fitPending = false;
            var home = new Vector3((float)model.Size / 2, 0, (float)model.Size / 2);
            _distance = FitDistance(home, _yaw, _pitch);
        }

        UpdateCamera();
        var key = (_target, _distance, _yaw, _pitch, bounds.Size, Model, _matches);
        if (gpu && !_gpuGeometryFresh)
        {
            PushGpuGeometry(model);
        }

        if (_faces is null || key != _sceneKey)
        {
            var redraw = System.Diagnostics.Stopwatch.StartNew();
            _hud.Labels = 0;
            _hud.OrderMs = 0;
            _hud.Pairs = 0;
            _faces = gpu ? EmptyFaces : BuildFaces(model);
            _hud.Faces = gpu ? _gpuBlocks.Count * 10 + 2 : _faces.Count;
            _gpu?.RequestNextFrameRendering();
            _hud.RedrawMs = redraw.Elapsed.TotalMilliseconds;
            _hud.Redraws++;
            _sceneKey = key;
        }
        else
        {
            _hud.CachedFrames++;
        }

        if (!gpu)
        {
            context.Custom(new FacesDrawOperation(bounds, _faces));
        }
        // The overlays are drawn straight into the frame: recording them into
        // a fresh DrawingGroup each frame cost ~250 KB of garbage a frame.
        if (gpu && _matches is { Count: <= 300 } matches)
        {
            foreach (var match in matches)
            {
                DrawTopOutline(context, match, MatchPen);
            }
        }

        _hud.Labels = 0;
        DrawLabels(context, model);
        DrawLegend(context, model, bounds);
        if (_menuBlock is { } menuBlock)
        {
            DrawHighlight(context, menuBlock);
        }
        else if (_hover is { } hover)
        {
            DrawHighlight(context, hover);
            if (_pointer is { } pointer)
            {
                DrawCard(context, hover, pointer, bounds);
            }
        }

        _hud.Frame(frameClock.Elapsed.TotalMilliseconds);
        if (ShowFps)
        {
            _hud.Draw(context, bounds, UsesGpu ? "GPU" : "CPU");
        }
        else
        {
            _hud.Hide();
        }
    }

    // The last report copied (for tests).
    internal string? LastReport { get; private set; }

    internal string PerfReport()
    {
        var degrees = 180 / Math.PI;
        var model = Model;
        return _hud.Report(new[]
        {
            model is null ? "No city" : $"{model.Buildings.Count:N0} buildings, {model.Districts.Count:N0} districts, {model.HotspotCount:N0} hotspots, {model.Mode} mode",
            $"Camera target {_target.X:0},{_target.Y:0},{_target.Z:0}, distance {_distance:0}, yaw {_yaw * degrees:0} deg, pitch {_pitch * degrees:0} deg",
            $"Viewport {Bounds.Width:0}x{Bounds.Height:0} DIP at {TopLevel.GetTopLevel(this)?.RenderScaling ?? 1:0.##}x scaling",
            UsesGpu ? $"Renderer: GPU, {_gpu!.Renderer}, last submit {_gpu.LastGpuSubmitMs:0.00} ms" : $"Renderer: software (Skia paths){(_gpu is { Renderer.Length: > 0 } g ? ", " + g.Renderer : string.Empty)}",
        });
    }

    private void CopyReport()
    {
        LastReport = PerfReport();
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(LastReport);
        }

        _hud.CopiedUntil = _hud.Now + TimeSpan.FromSeconds(1.5);
        InvalidateVisual();
        DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromSeconds(1.6));
    }

    private FaceBatch BuildFaces(CityModel model)
    {
        var batch = new FaceBatch(model.Buildings.Count * 3 + model.Districts.Count * 3 + 1);
        var size = (float)model.Size;
        AddQuad(batch, GroundColor, 0, new Vector3(0, 0, 0), new Vector3(size, 0, 0), new Vector3(size, 0, size), new Vector3(0, 0, size));
        var orderClock = System.Diagnostics.Stopwatch.StartNew();
        var drawOrder = DrawOrder(model);
        _hud.OrderMs = orderClock.Elapsed.TotalMilliseconds;
        foreach (var block in drawOrder)
        {
            var ghost = _matches is not null && (!block.IsFile || !_matches.Contains(block));
            AddBox(batch, block, _colors[block], ghost, match: block.IsFile && _matches is not null && !ghost);
        }

        return batch;
    }

    // Every plinth and building, back to front. The treemap nests: all of a
    // district's contents stand inside its footprint, on top of it. So a
    // district goes before its contents, and two siblings' whole subtrees
    // follow the order of the siblings themselves - which BoxDrawOrder sets
    // exactly from their separating plane. Only siblings are ever compared.
    internal List<CityBlock> DrawOrder(CityModel model)
    {
        var order = new List<CityBlock>(model.Districts.Count + model.Buildings.Count);
        var pairCount = 0;
        Emit(_topLevel);
        _hud.Pairs = pairCount;
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

            var pairs = OverlappingPairs(extents);
            pairCount += pairs.Count;
            foreach (var i in BoxDrawOrder.Sort(boxes, _eye, pairs, i => FarDistance(siblings[i])))
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

    private void AddBox(FaceBatch batch, CityBlock block, Color color, bool ghost, bool match)
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

        // The roof's corner heights: level, unless debt has bent it.
        var wear = _wear.GetValueOrDefault(block);
        var (r00, r10, r11, r01) = wear is null ? (y1, y1, y1, y1) : (wear.Y00, wear.Y10, wear.Y11, wear.Y01);

        // Only the faces turned toward the eye, each wall with its windows.
        if (_eye.X < x0)
        {
            AddQuad(batch, ShadeColor(color, 0.70), 0, new(x0, y0, z0), new(x0, r00, z0), new(x0, r01, z1), new(x0, y0, z1));
            AddWear(batch, wear, DebtWear.Wall.Left, ghost);
        }
        else if (_eye.X > x1)
        {
            AddQuad(batch, ShadeColor(color, 0.70), 0, new(x1, y0, z0), new(x1, r10, z0), new(x1, r11, z1), new(x1, y0, z1));
            AddWear(batch, wear, DebtWear.Wall.Right, ghost);
        }

        if (_eye.Z < z0)
        {
            AddQuad(batch, ShadeColor(color, 0.52), 0, new(x0, y0, z0), new(x1, y0, z0), new(x1, r10, z0), new(x0, r00, z0));
            AddWear(batch, wear, DebtWear.Wall.Front, ghost);
        }
        else if (_eye.Z > z1)
        {
            AddQuad(batch, ShadeColor(color, 0.52), 0, new(x0, y0, z1), new(x1, y0, z1), new(x1, r11, z1), new(x0, r01, z1));
            AddWear(batch, wear, DebtWear.Wall.Back, ghost);
        }

        if (_eye.Y > Math.Min(Math.Min(r00, r10), Math.Min(r11, r01)))
        {
            AddQuad(batch, color, match ? FaceBatch.Match : FaceBatch.Edge, new(x0, r00, z0), new(x1, r10, z0), new(x1, r11, z1), new(x0, r01, z1));
            AddWear(batch, wear, DebtWear.Wall.Roof, ghost);
        }
    }

    private void AddWear(FaceBatch batch, DebtWear? wear, DebtWear.Wall wall, bool ghost)
    {
        if (wear is null)
        {
            return;
        }

        foreach (var piece in wear.Pieces)
        {
            if (piece.Wall == wall)
            {
                AddQuad(batch, ghost ? Blend(piece.Color, Background, 0.78) : piece.Color, 0, piece.A, piece.B, piece.C, piece.D);
            }
        }
    }

    private void AddQuad(FaceBatch batch, Color color, byte flags, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        if (Project(a) is not { } pa || Project(b) is not { } pb || Project(c) is not { } pc || Project(d) is not { } pd)
        {
            return;
        }

        var extent = Math.Max(Math.Max(Math.Abs(pa.X - pc.X), Math.Abs(pb.X - pd.X)), Math.Max(Math.Abs(pa.Y - pc.Y), Math.Abs(pb.Y - pd.Y)));
        if (extent < 0.6 && flags != FaceBatch.Match)
        {
            return;
        }

        // Roof edges only where they help (and are cheap to see).
        if (flags == FaceBatch.Edge && extent < 8)
        {
            flags = 0;
        }

        batch.Add(pa, pb, pc, pd, color, flags);
    }

    private static Color ShadeColor(Color color, double factor)
        => Color.FromRgb((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    // The hovered block lit up: every face turned to the eye of a building
    // (so pointing at a side shows it), the roof outline of a district.
    private void DrawHighlight(DrawingContext context, CityBlock block)
    {
        if (!block.IsFile)
        {
            DrawTopOutline(context, block, HoverPen);
            return;
        }

        var f = block.Footprint;
        var (x0, x1, z0, z1) = ((float)f.X, (float)(f.X + f.Width), (float)f.Y, (float)(f.Y + f.Height));
        var (y0, y1) = ((float)block.Base, (float)block.Top);
        if (_eye.X < x0)
        {
            DrawQuad(context, HighlightBrush, HighlightPen, new(x0, y0, z0), new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1));
        }
        else if (_eye.X > x1)
        {
            DrawQuad(context, HighlightBrush, HighlightPen, new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1));
        }

        if (_eye.Z < z0)
        {
            DrawQuad(context, HighlightBrush, HighlightPen, new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0));
        }
        else if (_eye.Z > z1)
        {
            DrawQuad(context, HighlightBrush, HighlightPen, new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
        }

        DrawQuad(context, HighlightBrush, HoverPen, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1));
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
        if (extent < 0.6 && pen != HoverPen && pen != MatchPen && pen != HighlightPen)
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

        context.DrawGeometry(fill, fill is null || extent >= 8 || pen == HoverPen || pen == HighlightPen ? pen : null, geometry);
    }

    // District names on their plinths, the top level always, deeper ones when
    // there is room.
    private readonly List<Rect> _taken = new();

    // The labels and name tags drawn last frame (for tests).
    internal IReadOnlyList<Rect> LabelBoxes => _taken;
    private List<CityBlock> _labelCandidates = new();

    private bool Place(Rect rect)
    {
        var padded = rect.Inflate(2);
        foreach (var r in _taken)
        {
            if (r.Intersects(padded))
            {
                return false;
            }
        }

        _taken.Add(rect);
        return true;
    }

    private void DrawLabels(DrawingContext context, CityModel model)
    {
        _taken.Clear();

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

        // Map-style labels: on top of everything in a dark pill, so orbiting
        // never hides them behind a building. A pill sits just inside the
        // district's own front edge, like a street sign at its kerb: at the
        // centre of a big district it landed among its sub-folders and seemed
        // to name one of them. Top-level folders always get one; deeper ones
        // when their plinth is big enough on screen. Bigger districts win a
        // clash, so the choice stays steady while the camera moves.
        var view = new Rect(Bounds.Size);
        foreach (var district in _labelCandidates)
        {
            var f = district.Footprint;
            var y = (float)district.Top;
            if (Project(new Vector3((float)f.X, y, (float)f.Y)) is not { } c0
                || Project(new Vector3((float)(f.X + f.Width), y, (float)f.Y)) is not { } c1
                || Project(new Vector3((float)(f.X + f.Width), y, (float)(f.Y + f.Height))) is not { } c2
                || Project(new Vector3((float)f.X, y, (float)(f.Y + f.Height))) is not { } c3)
            {
                continue;
            }

            if (district.Level > 0 && Distance(c0, c2) < 120 * district.Level)
            {
                continue;
            }

            var top = district.Level == 0;
            var text = Text(district.Name, top ? 12.5 : 11, top ? TextBrush : LabelBrush, top);
            var center = new Point((c0.X + c1.X + c2.X + c3.X) / 4, (c0.Y + c1.Y + c2.Y + c3.Y) / 4);
            if (EdgeAnchor(c0, c1, c2, c3, center, view.Deflate(text.Width / 2 + 8), text.Height / 2 + 5) is not { } at)
            {
                continue;
            }

            var pill = new Rect(at.X - text.Width / 2 - 6, at.Y - text.Height / 2 - 2, text.Width + 12, text.Height + 4);
            if (!Place(pill))
            {
                continue;
            }

            context.DrawRectangle(LabelPillBrush, top ? LabelPillPen : null, pill, 4, 4);
            context.DrawText(text, pill.TopLeft + new Point(6, 2));
            _hud.Labels++;
        }
    }

    // Where a district's label goes: the middle of the visible part of its
    // nearest edge on screen (the lowest), moved inward by `inset` so the
    // pill sits on the district. The next edges are tried when that one is
    // off screen; null when no edge shows.
    internal static Point? EdgeAnchor(Point c0, Point c1, Point c2, Point c3, Point center, Rect view, double inset)
    {
        Span<(Point A, Point B)> edges = stackalloc (Point, Point)[] { (c0, c1), (c1, c2), (c2, c3), (c3, c0) };
        edges.Sort((p, q) => (q.A.Y + q.B.Y).CompareTo(p.A.Y + p.B.Y));
        foreach (var (a, b) in edges)
        {
            if (Clip(a, b, view) is not var (t0, t1))
            {
                continue;
            }

            var mid = a + (b - a) * ((t0 + t1) / 2);
            var inward = center - mid;
            var length = Math.Sqrt(inward.X * inward.X + inward.Y * inward.Y);
            if (length < 1e-3)
            {
                continue;
            }

            return mid + inward * (Math.Min(inset, length / 2) / length);
        }

        return null;
    }

    // The part of segment a-b inside the view, as parameters 0..1
    // (Liang-Barsky); null when none of it is.
    private static (double, double)? Clip(Point a, Point b, Rect view)
    {
        double t0 = 0, t1 = 1;
        var d = b - a;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 1e-9)
            {
                return q >= 0;
            }

            var r = q / p;
            if (p < 0)
            {
                t0 = Math.Max(t0, r);
            }
            else
            {
                t1 = Math.Min(t1, r);
            }

            return t0 <= t1;
        }

        return Edge(-d.X, a.X - view.Left) && Edge(d.X, view.Right - a.X)
            && Edge(-d.Y, a.Y - view.Top) && Edge(d.Y, view.Bottom - a.Y) && t1 - t0 > 1e-6
            ? (t0, t1)
            : null;
    }

    private static void DrawLegend(DrawingContext context, CityModel model, Rect bounds)
    {
        var (title, low, high, ramp, none) = model.Mode == CityMode.Debt
            ? ("Height = code lines · colour and wear = debt (size, nesting, width" + (model.HasHistory ? ", churn)" : ")"), "clean", "dilapidated", DebtRamp, DebtNone)
            : !model.HasHistory
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

            // The Debt city has no unscored buildings to explain.
            if (model.Mode != CityMode.Debt)
            {
                var swatch = new Rect(bar.Right + 18, bar.Y, 10, 10);
                context.DrawRectangle(new ImmutableSolidColorBrush(none), null, swatch, 2, 2);
                context.DrawText(Text("no change", 10.5, MutedBrush), new Point(swatch.Right + 5, swatch.Y - 2));
            }
        }

        var hint = Text("Drag: orbit · Right-drag: pan · Wheel: zoom · WASD: move · Space/Ctrl: up/down · Click: open · Home: reset", 11, MutedBrush);
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
        foreach (var detail in Describe(block, Model!))
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
    // district: the facts, then why it has its shape - what sets the height,
    // the footprint and the colour, and how it ranks among the city's files.
    internal static IEnumerable<CardRun[]> Describe(CityBlock block, CityModel model)
    {
        foreach (var line in Facts(block, model.HasHistory))
        {
            yield return line;
        }

        foreach (var line in Shape(block, model))
        {
            yield return line;
        }
    }

    private static IEnumerable<CardRun[]> Shape(CityBlock block, CityModel model)
    {
        var node = block.Node;
        if (!block.IsFile)
        {
            yield return new[]
            {
                new CardRun("Area ", CardTone.Muted),
                new CardRun($"all {Count(node.Lines.Total, "line")} inside", CardTone.Text),
                new CardRun(", a step up per folder level", CardTone.Muted),
            };
            yield break;
        }

        if (model.Mode == CityMode.Debt)
        {
            foreach (var line in DebtShape(block, model))
            {
                yield return line;
            }

            yield break;
        }

        // The same measures CityLayout builds from.
        Func<AnalysisNode, double> heightOf = !model.HasHistory ? n => n.Lines.Total
            : model.Mode == CityMode.Hotspots ? n => n.Churn
            : n => n.CommitCount;
        var height = !model.HasHistory ? Count(node.Lines.Total, "line")
            : model.Mode == CityMode.Hotspots ? Count(node.Churn, "changed line")
            : Count(node.CommitCount, "commit");
        yield return new[]
        {
            new CardRun("Height ", CardTone.Muted),
            new CardRun(height, CardTone.Text),
            new CardRun(Rank(model, block, heightOf, "taller", "tallest", "shortest"), CardTone.Muted),
        };
        yield return new[]
        {
            new CardRun("Footprint ", CardTone.Muted),
            new CardRun(Count(node.Lines.Total, "line"), CardTone.Text),
            new CardRun(Rank(model, block, n => n.Lines.Total, "bigger", "biggest", "smallest"), CardTone.Muted),
        };

        if (!model.HasHistory)
        {
            yield break;
        }

        if (block.Heat < 0)
        {
            yield return new[] { new CardRun("Colour ", CardTone.Muted), new CardRun("grey: no change in the period", CardTone.Muted) };
        }
        else if (model.Mode == CityMode.Hotspots)
        {
            yield return new[]
            {
                new CardRun("Colour ", CardTone.Muted),
                new CardRun($"hotspot #{block.HotspotRank} of {model.HotspotCount:N0}", CardTone.Heat),
                new CardRun(" (churn × size)", CardTone.Muted),
            };
        }
        else
        {
            var age = CodeAnalysisPanelViewModel.Age(node.LastChanged, DateTimeOffset.Now);
            yield return new[]
            {
                new CardRun("Colour ", CardTone.Muted),
                new CardRun("changed " + age + " ago", CardTone.Heat),
                new CardRun(" (brighter = newer)", CardTone.Muted),
            };
        }
    }

    // The Debt city's card: the score and what it is made of.
    private static IEnumerable<CardRun[]> DebtShape(CityBlock block, CityModel model)
    {
        var node = block.Node;
        yield return new[]
        {
            new CardRun("Height ", CardTone.Muted),
            new CardRun(Count(node.Lines.Code, "code line"), CardTone.Text),
            new CardRun(Rank(model, block, n => n.Lines.Code, "taller", "tallest", "shortest"), CardTone.Muted),
        };
        if (block.Heat < 0)
        {
            yield return new[] { new CardRun("Debt ", CardTone.Muted), new CardRun("none: a binary file", CardTone.Muted) };
            yield break;
        }

        var heatOf = model.Buildings.ToDictionary(b => b.Node, b => b.Heat);
        yield return new[]
        {
            new CardRun("Debt ", CardTone.Muted),
            new CardRun($"{block.Heat * 100:0}% - {CodeDebt.Describe(block.Heat)}", CardTone.Text),
            new CardRun(Rank(model, block, n => heatOf.GetValueOrDefault(n, -1), "worse", "worst", "best"), CardTone.Muted),
        };
        var shape = node.Shape;
        var nesting = new List<CardRun>
        {
            new("Nesting ", CardTone.Muted),
            new($"{shape.MaxDepth} level{(shape.MaxDepth == 1 ? string.Empty : "s")} deep", CardTone.Text),
        };
        if (shape.DeepLines > 0)
        {
            nesting.Add(new CardRun($" · {Count(shape.DeepLines, "line")} at {CodeShape.DeepFrom}+", CardTone.Muted));
        }

        yield return nesting.ToArray();
        if (shape.LongLines > 0)
        {
            yield return new[] { new CardRun("Width ", CardTone.Muted), new CardRun($"{Count(shape.LongLines, "line")} over {CodeShape.LongFrom} characters", CardTone.Text) };
        }
    }

    // ", taller than 92% of files" - the share of the city's files this one beats.
    private static string Rank(CityModel model, CityBlock block, Func<AnalysisNode, double> measure, string more, string most, string least)
    {
        var mine = measure(block.Node);
        var below = 0;
        foreach (var building in model.Buildings)
        {
            if (measure(building.Node) < mine)
            {
                below++;
            }
        }

        var share = (int)(100.0 * below / Math.Max(1, model.Buildings.Count - 1));
        return share >= 99 ? $", among the {most}"
            : share == 0 ? $", among the {least}"
            : $", {more} than {share}% of files";
    }

    private static IEnumerable<CardRun[]> Facts(CityBlock block, bool hasHistory)
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
        Add(age.Length > 0 ? "changed " + age + " ago" : "no change in period", age.Length == 0 ? CardTone.Muted : CardTone.Text);
        yield return git.ToArray();
    }

    private static string Count(long value, string noun)
        => $"{value.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(value == 1 ? string.Empty : "s")}";

    private static FormattedText Text(string text, double size, IBrush brush, bool bold = false)
        => CityText.Get(text, size, brush, bold ? UiBold : UiFont);

    // ---- colours ----

    internal static Color BuildingColor(CityMode mode, double heat)
    {
        if (heat < 0)
        {
            return mode switch { CityMode.Hotspots => HotspotNone, CityMode.Debt => DebtNone, _ => AgeNone };
        }

        var ramp = mode switch { CityMode.Hotspots => HotspotRamp, CityMode.Debt => DebtRamp, _ => AgeRamp };
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
