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

    // Where the spot is now, when it is a row whose place shifts as the rows
    // above it are measured (wrapped lines); null until it can tell.
    private readonly Func<double?>? _target;
    private bool _putting;
    private readonly DateTime _until;
    private readonly double _opacity;
    private readonly DispatcherTimer _reveal;
    private readonly EventHandler<PointerWheelEventArgs> _wheel;
    private readonly EventHandler<PointerPressedEventArgs> _press;
    private readonly EventHandler<KeyEventArgs> _key;
    private readonly EventHandler<VisualTreeAttachmentEventArgs> _detached;
    private readonly EventHandler _laidOut;

    private ScrollRestore(ScrollViewer scroller, double offset, Func<double?>? target)
    {
        _scroller = scroller;
        _offset = offset;
        _target = target;
        _until = DateTime.UtcNow + HoldFor;
        _opacity = scroller.Opacity;
        _reveal = new DispatcherTimer { Interval = RevealAfter };
        _reveal.Tick += (_, _) => Reveal();
        _wheel = (_, _) => Stop();
        _press = (_, _) => Stop();
        _key = (_, _) => Stop();
        _detached = (_, _) => Stop();
        _laidOut = (_, _) => OnLaidOut();
    }

    // target: see _target; offset is then only the first guess.
    public static void Start(ScrollViewer? scroller, double offset, Func<double?>? target = null)
    {
        if (scroller is null)
        {
            return;
        }

        Cancel(scroller);
        var restore = new ScrollRestore(scroller, offset, target);
        Active.AddOrUpdate(scroller, restore);
        scroller.ScrollChanged += restore.OnScrollChanged;
        scroller.DetachedFromVisualTree += restore._detached;
        if (target is not null)
        {
            // Rows settle in layout passes that need not scroll anything.
            scroller.LayoutUpdated += restore._laidOut;
        }

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

    private void OnLaidOut()
    {
        if (DateTime.UtcNow > _until)
        {
            Stop();
            return;
        }

        Put();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DateTime.UtcNow > _until)
        {
            Stop();
            return;
        }

        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0 || _target is not null)
        {
            Put();
        }
    }

    // Offset is clamped to the content so far; growing content re-applies it.
    private void Put()
    {
        // Moving the scroller re-enters through ScrollChanged.
        if (_putting)
        {
            return;
        }

        _putting = true;
        try
        {
            var offset = _target?.Invoke() ?? _offset;
            if (Math.Abs(_scroller.Offset.Y - offset) > 0.5)
            {
                _scroller.Offset = new Vector(_scroller.Offset.X, offset);
                offset = _target?.Invoke() ?? offset;
            }

            if (Math.Abs(_scroller.Offset.Y - offset) <= 0.5)
            {
                Reveal();
            }
        }
        finally
        {
            _putting = false;
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
        _scroller.LayoutUpdated -= _laidOut;
        _scroller.RemoveHandler(InputElement.PointerWheelChangedEvent, _wheel);
        _scroller.RemoveHandler(InputElement.PointerPressedEvent, _press);
        _scroller.RemoveHandler(InputElement.KeyDownEvent, _key);
    }
}
