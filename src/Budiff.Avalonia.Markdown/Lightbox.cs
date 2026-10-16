using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Input;
using global::Avalonia.Layout;
using global::Avalonia.Media;
using global::Avalonia.Media.Imaging;
using global::Avalonia.Media.Immutable;
using global::Avalonia.Rendering;
using global::Avalonia.Threading;
using ShapePath = global::Avalonia.Controls.Shapes.Path;

namespace Budiff.Avalonia.Markdown;

// A closer look at the document's pictures, over the whole window. The wheel
// zooms around the pointer, a zoomed picture pans by dragging, double-click
// toggles actual size; Left/Right (or the side buttons) step through the
// pictures; Esc, the close button or a click beside the picture closes it.
internal sealed class Lightbox : Grid
{
    // Material Design icons (Apache 2.0).
    private const string PreviousIcon = "M15.41,16.58L10.83,12L15.41,7.41L14,6L8,12L14,18L15.41,16.58Z";
    private const string NextIcon = "M8.59,16.58L13.17,12L8.59,7.41L10,6L16,12L10,18L8.59,16.58Z";
    private const string CloseIcon = "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z";

    // Dark whatever the theme, like any photo viewer.
    private static readonly IBrush Backdrop = new ImmutableSolidColorBrush(Color.FromArgb(0xE8, 0x0E, 0x10, 0x12));
    private static readonly IBrush Chrome = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
    private static readonly IBrush ChromeHover = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x3A, 0x3D, 0x41));
    private static readonly IBrush Ink = new ImmutableSolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));

    private readonly OverlayLayer _layer;
    private readonly Visual _anchor;
    private readonly IReadOnlyList<Item> _items;
    private readonly Bitmap?[] _full;
    private readonly IInputElement? _focusedBefore;
    private readonly Border _previous;
    private readonly Border _next;
    private readonly TextBlock _caption;
    private int _index;
    private int _loadGen;

    // One picture: as the preview shows it, what to call it, and (for a
    // picture the preview scaled down) how to get it at full size.
    internal sealed record Item(Bitmap Picture, string Caption, Func<Task<Bitmap?>>? LoadFull);

    private Lightbox(OverlayLayer layer, Visual anchor, IReadOnlyList<Item> items, int index)
    {
        _layer = layer;
        _anchor = anchor;
        _items = items;
        _full = new Bitmap?[items.Count];
        _focusedBefore = TopLevel.GetTopLevel(anchor)?.FocusManager?.GetFocusedElement();

        Background = Backdrop;
        Focusable = true;
        Width = layer.Bounds.Width;
        Height = layer.Bounds.Height;

        View = new PictureView();
        View.Changed += UpdateCaption;
        View.BackdropClicked += Close;
        _previous = RoundButton(PreviousIcon, 44, HorizontalAlignment.Left, VerticalAlignment.Center, () => Step(-1));
        _next = RoundButton(NextIcon, 44, HorizontalAlignment.Right, VerticalAlignment.Center, () => Step(1));
        var close = RoundButton(CloseIcon, 36, HorizontalAlignment.Right, VerticalAlignment.Top, Close);
        _caption = new TextBlock
        {
            Foreground = Ink,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Children.Add(View);
        Children.Add(new Border
        {
            Background = Chrome,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 4),
            Margin = new Thickness(80, 0, 80, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = _caption,
        });
        Children.Add(_previous);
        Children.Add(_next);
        Children.Add(close);

        layer.SizeChanged += OnLayerSizeChanged;
        anchor.DetachedFromVisualTree += OnAnchorDetached;
        Show(index);
    }

    internal PictureView View { get; }

    internal int Index => _index;

    internal string Caption => _caption.Text ?? string.Empty;

    public static Lightbox? Open(Visual anchor, IReadOnlyList<Item> items, int index)
    {
        if (items.Count == 0 || OverlayLayer.GetOverlayLayer(anchor) is not { } layer)
        {
            return null;
        }

        var box = new Lightbox(layer, anchor, items, Math.Clamp(index, 0, items.Count - 1));
        layer.Children.Add(box);
        box.Focus();
        return box;
    }

    internal void Step(int delta)
    {
        var index = _index + delta;
        if (index >= 0 && index < _items.Count && index != _index)
        {
            Show(index);
        }
    }

    internal void Close()
    {
        if (!_layer.Children.Contains(this))
        {
            return;
        }

        _loadGen++;
        _layer.SizeChanged -= OnLayerSizeChanged;
        _anchor.DetachedFromVisualTree -= OnAnchorDetached;
        _layer.Children.Remove(this);
        _focusedBefore?.Focus();
    }

    // Modal: every key stays here, so nothing (Back, say) acts behind it.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left or Key.Up or Key.PageUp:
                Step(-1);
                break;
            case Key.Right or Key.Down or Key.PageDown or Key.Space:
                Step(1);
                break;
            case Key.Home:
                Step(-_index);
                break;
            case Key.End:
                Step(_items.Count - 1 - _index);
                break;
            case Key.Add or Key.OemPlus:
                View.ZoomBy(1.25);
                break;
            case Key.Subtract or Key.OemMinus:
                View.ZoomBy(0.8);
                break;
            case Key.D0 or Key.NumPad0:
                View.Fit();
                break;
        }

        e.Handled = true;
    }

    // The mouse's back button closes it, as it would leave a page.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed)
        {
            Close();
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
    }

    private void Show(int index)
    {
        _index = index;
        var item = _items[index];
        View.SetPicture(_full[index] ?? item.Picture);
        _previous.IsVisible = index > 0;
        _next.IsVisible = index < _items.Count - 1;
        UpdateCaption();

        var gen = ++_loadGen;
        if (_full[index] is null && item.LoadFull is { } loadFull)
        {
            var ui = Dispatcher.UIThread;
            loadFull().ContinueWith(
                loaded => ui.Post(() =>
                {
                    if (loaded.IsCompletedSuccessfully && loaded.Result is { } full)
                    {
                        _full[index] = full;
                        if (gen == _loadGen)
                        {
                            View.Replace(full);
                        }
                    }
                }),
                TaskScheduler.Default);
        }
    }

    private void UpdateCaption()
    {
        var text = $"{_index + 1} / {_items.Count}";
        if (_items[_index].Caption.Length > 0)
        {
            text += "  ·  " + _items[_index].Caption;
        }

        if (!View.IsFitted)
        {
            text += $"  ·  {Math.Round(View.Scale * 100)}%";
        }

        _caption.Text = text;
    }

    private void OnLayerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Width = e.NewSize.Width;
        Height = e.NewSize.Height;
    }

    // The preview went away (another file, a re-render): so does the look.
    private void OnAnchorDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close();

    private static Border RoundButton(string icon, double size, HorizontalAlignment horizontal, VerticalAlignment vertical, Action click)
    {
        var button = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = Chrome,
            Cursor = MarkdownRenderer.HandCursor,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = new Thickness(16),
            Child = new ShapePath
            {
                Data = StreamGeometry.Parse(icon),
                Fill = Ink,
                Stretch = Stretch.Uniform,
                Width = size / 2,
                Height = size / 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        button.PointerEntered += (_, _) => button.Background = ChromeHover;
        button.PointerExited += (_, _) => button.Background = Chrome;
        MarkdownRenderer.OnClick(button, click);
        return button;
    }
}

// Draws one picture at some zoom: fitted and centered until zoomed in, then
// held where the reader puts it (never dragged off the screen).
internal sealed class PictureView : Control, ICustomHitTest
{
    private const double MaxZoom = 8;

    // Room kept free around a fitted picture for the buttons and caption.
    private static readonly Thickness FitMargin = new(76, 60);
    private static readonly Cursor Grab = new(StandardCursorType.SizeAll);

    private Bitmap? _picture;
    private double _scale = 1;
    private Point _origin;
    private bool _fitted = true;
    private Point? _pressedAt;
    private Point _originAtPress;
    private bool _dragged;

    // The zoom or the picture changed.
    public event Action? Changed;

    // A click (not a drag) beside the picture.
    public event Action? BackdropClicked;

    public Bitmap? Picture => _picture;

    // Screen units per picture unit (1 = the picture's natural size).
    public double Scale => _scale;

    public bool IsFitted => _fitted;

    public Rect PictureBounds => _picture is null
        ? default
        : new Rect(_origin.X, _origin.Y, _picture.Size.Width * _scale, _picture.Size.Height * _scale);

    // Panning is possible once the picture overflows the view.
    public bool CanPan => _picture is not null
        && (_picture.Size.Width * _scale > Bounds.Width + 0.5 || _picture.Size.Height * _scale > Bounds.Height + 0.5);

    // Natural size, scaled down to fit.
    private double FitScale
    {
        get
        {
            if (_picture is null)
            {
                return 1;
            }

            var room = new Rect(Bounds.Size).Deflate(FitMargin);
            var fit = Math.Min(room.Width / _picture.Size.Width, room.Height / _picture.Size.Height);
            return Math.Clamp(fit, 0.02, 1);
        }
    }

    public void SetPicture(Bitmap picture)
    {
        _picture = picture;
        Fit();
    }

    // The same picture at a higher resolution: what is on screen stays put.
    public void Replace(Bitmap sharper)
    {
        if (_picture is null || _fitted)
        {
            SetPicture(sharper);
            return;
        }

        _scale *= _picture.Size.Width / sharper.Size.Width;
        _picture = sharper;
        Clamp();
        Refresh();
    }

    public void Fit()
    {
        _fitted = true;
        _scale = FitScale;
        Clamp();
        Refresh();
    }

    // Zooms by a factor, keeping the picture point under 'around' (the view's
    // center by default) where it is; at or below the fit it fits again.
    public void ZoomBy(double factor, Point? around = null)
    {
        if (_picture is null)
        {
            return;
        }

        var fit = FitScale;
        var scale = Math.Clamp(_scale * factor, fit, Math.Max(fit, MaxZoom));
        if (scale <= fit + 1e-6)
        {
            Fit();
            return;
        }

        ZoomTo(scale, around ?? new Point(Bounds.Width / 2, Bounds.Height / 2));
    }

    // Double-click: a fitted picture goes to its natural size (twice that
    // when it already fits at natural size); a zoomed one fits again.
    public void ToggleZoom(Point at)
    {
        if (_picture is null)
        {
            return;
        }

        if (_fitted)
        {
            ZoomTo(FitScale < 0.999 ? 1 : 2, at);
        }
        else
        {
            Fit();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_picture is null)
        {
            return;
        }

        // Smooth when shrunk, crisp pixels when blown up.
        var mode = _scale >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            context.DrawImage(_picture, new Rect(_picture.Size), PictureBounds);
        }
    }

    // The whole view takes the pointer: the picture, and the backdrop for
    // closing.
    public bool HitTest(Point point) => true;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fitted)
        {
            Fit();
        }
        else
        {
            Clamp();
            Refresh();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        // Every second click of a quick run is a double-click.
        if (e.ClickCount % 2 == 0 && PictureBounds.Contains(point.Position))
        {
            _pressedAt = null;
            ToggleZoom(point.Position);
            return;
        }

        _pressedAt = point.Position;
        _originAtPress = _origin;
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressedAt is not { } start)
        {
            return;
        }

        var at = e.GetPosition(this);
        var dx = at.X - start.X;
        var dy = at.Y - start.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 4)
        {
            return;
        }

        _dragged = true;
        e.Handled = true;
        if (CanPan)
        {
            _origin = new Point(_originAtPress.X + dx, _originAtPress.Y + dy);
            Clamp();
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedAt is null || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _pressedAt = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (!_dragged && !PictureBounds.Contains(e.GetPosition(this)))
        {
            BackdropClicked?.Invoke();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressedAt = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y != 0)
        {
            ZoomBy(Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        }

        e.Handled = true;
    }

    private void ZoomTo(double scale, Point at)
    {
        var ratio = scale / _scale;
        _origin = new Point(at.X - (at.X - _origin.X) * ratio, at.Y - (at.Y - _origin.Y) * ratio);
        _scale = scale;
        _fitted = false;
        Clamp();
        Refresh();
    }

    // Centered on an axis the picture fits; otherwise no gap at either edge.
    private void Clamp()
    {
        if (_picture is null)
        {
            return;
        }

        static double Axis(double origin, double size, double room)
            => size <= room ? (room - size) / 2 : Math.Clamp(origin, room - size, 0);

        _origin = new Point(
            Axis(_origin.X, _picture.Size.Width * _scale, Bounds.Width),
            Axis(_origin.Y, _picture.Size.Height * _scale, Bounds.Height));
    }

    private void Refresh()
    {
        Cursor = CanPan ? Grab : null;
        InvalidateVisual();
        Changed?.Invoke();
    }
}
