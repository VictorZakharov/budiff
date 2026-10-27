using System;
using System.Runtime.CompilerServices;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;

namespace Budiff.Avalonia;

// Scrolls a freshly rendered view back to a remembered spot. It stays
// invisible until it is there (no flash of the top, then a jump), and holds
// the spot while the content still grows - pictures above it loading in -
// until the reader scrolls, clicks or types, leaves, or a few seconds pass.
internal sealed class ScrollRestore
{
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(5);

    // Shown anyway after this, placed or not (content shorter than the spot).
    private static readonly TimeSpan RevealAfter = TimeSpan.FromMilliseconds(400);

    // One at a time per scroller: the next file's spot replaces the last one's.
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollRestore> Active = new();

    private readonly ScrollViewer _scroller;
    private readonly double _offset;
    private readonly DateTime _until;
    private readonly double _opacity;
    private readonly DispatcherTimer _reveal;
    private readonly EventHandler<PointerWheelEventArgs> _wheel;
    private readonly EventHandler<PointerPressedEventArgs> _press;
    private readonly EventHandler<KeyEventArgs> _key;
    private readonly EventHandler<VisualTreeAttachmentEventArgs> _detached;

    private ScrollRestore(ScrollViewer scroller, double offset)
    {
        _scroller = scroller;
        _offset = offset;
        _until = DateTime.UtcNow + HoldFor;
        _opacity = scroller.Opacity;
        _reveal = new DispatcherTimer { Interval = RevealAfter };
        _reveal.Tick += (_, _) => Reveal();
        _wheel = (_, _) => Stop();
        _press = (_, _) => Stop();
        _key = (_, _) => Stop();
        _detached = (_, _) => Stop();
    }

    public static void Start(ScrollViewer? scroller, double offset)
    {
        if (scroller is null)
        {
            return;
        }

        Cancel(scroller);
        var restore = new ScrollRestore(scroller, offset);
        Active.AddOrUpdate(scroller, restore);
        scroller.ScrollChanged += restore.OnScrollChanged;
        scroller.DetachedFromVisualTree += restore._detached;
        scroller.AddHandler(InputElement.PointerWheelChangedEvent, restore._wheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(InputElement.PointerPressedEvent, restore._press, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(InputElement.KeyDownEvent, restore._key, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (offset > 0)
        {
            scroller.Opacity = 0;
            restore._reveal.Start();
        }

        restore.Put();
    }

    // Stops holding a spot on this scroller, if one is held.
    public static void Cancel(ScrollViewer scroller)
    {
        if (Active.TryGetValue(scroller, out var running))
        {
            running.Stop();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DateTime.UtcNow > _until)
        {
            Stop();
            return;
        }

        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            Put();
        }
    }

    // Offset is clamped to the content so far; growing content re-applies it.
    private void Put()
    {
        if (Math.Abs(_scroller.Offset.Y - _offset) > 0.5)
        {
            _scroller.Offset = new Vector(_scroller.Offset.X, _offset);
        }

        if (Math.Abs(_scroller.Offset.Y - _offset) <= 0.5)
        {
            Reveal();
        }
    }

    private void Reveal()
    {
        _reveal.Stop();
        _scroller.Opacity = _opacity;
    }

    private void Stop()
    {
        if (Active.TryGetValue(_scroller, out var running) && running == this)
        {
            Active.Remove(_scroller);
        }

        Reveal();
        _scroller.ScrollChanged -= OnScrollChanged;
        _scroller.DetachedFromVisualTree -= _detached;
        _scroller.RemoveHandler(InputElement.PointerWheelChangedEvent, _wheel);
        _scroller.RemoveHandler(InputElement.PointerPressedEvent, _press);
        _scroller.RemoveHandler(InputElement.KeyDownEvent, _key);
    }
}
