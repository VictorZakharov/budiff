using System;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;

namespace Budiff.Avalonia;

// Scrolls back to a remembered spot and holds it while the content is still
// growing (pictures above the spot loading in), until the reader scrolls,
// clicks or types - or a few seconds pass.
internal sealed class ScrollRestore
{
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(5);

    private readonly ScrollViewer _scroller;
    private readonly double _offset;
    private readonly DateTime _until;
    private readonly EventHandler<PointerWheelEventArgs> _wheel;
    private readonly EventHandler<PointerPressedEventArgs> _press;
    private readonly EventHandler<KeyEventArgs> _key;

    private ScrollRestore(ScrollViewer scroller, double offset)
    {
        _scroller = scroller;
        _offset = offset;
        _until = DateTime.UtcNow + HoldFor;
        _wheel = (_, _) => Stop();
        _press = (_, _) => Stop();
        _key = (_, _) => Stop();
    }

    public static void Start(ScrollViewer? scroller, double offset)
    {
        if (scroller is null)
        {
            return;
        }

        var restore = new ScrollRestore(scroller, offset);
        scroller.ScrollChanged += restore.OnScrollChanged;
        scroller.AddHandler(InputElement.PointerWheelChangedEvent, restore._wheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(InputElement.PointerPressedEvent, restore._press, RoutingStrategies.Tunnel, handledEventsToo: true);
        scroller.AddHandler(InputElement.KeyDownEvent, restore._key, RoutingStrategies.Tunnel, handledEventsToo: true);
        restore.Put();
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
    }

    private void Stop()
    {
        _scroller.ScrollChanged -= OnScrollChanged;
        _scroller.RemoveHandler(InputElement.PointerWheelChangedEvent, _wheel);
        _scroller.RemoveHandler(InputElement.PointerPressedEvent, _press);
        _scroller.RemoveHandler(InputElement.KeyDownEvent, _key);
    }
}
