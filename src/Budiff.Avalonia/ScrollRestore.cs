using System;
using System.Collections.Generic;
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

    // A pane height to budget with before the scroller has one.
    private const double FallbackViewport = 200;

    // Moves to land once the rows above are measured. In the sweep test
    // (TypeScriptStart_Settles_AtFractionalScales, 125% / 150% / 175%) a
    // laid-out pane lands in 1-3; the first file of a window, opened before
    // the pane has a height, circles a couple of device pixels around the
    // row and ends on the budget, within 2 px of it.
    private const int SettleMoves = 4;

    // One at a time per scroller: the next file's spot replaces the last one's.
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollRestore> Active = new();

    private readonly ScrollViewer _scroller;
    private readonly double _offset;

    // Where the spot is now, when it is a row whose place shifts as the rows
    // above it are measured (wrapped lines); null until it can tell.
    private readonly Func<double?>? _target;
    private bool _putting;

    // Whether a layout pass has run since the restore started (see Put).
    private bool _seenLayout;

    // Offsets already moved to while following _target, how far each left
    // the target once laid out, and how many moves that may take (see Put).
    private readonly List<double> _tried = new();
    private readonly List<(double Offset, double Miss)> _missed = new();
    private readonly int _maxMoves;
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

        // The target moves only while rows above it get their real heights,
        // and each layout pass measures at least a pane of them: one move
        // per pane between the top and the spot, then SettleMoves to land
        // on it as the panel rounds rows to device pixels.
        var pane = scroller.Viewport.Height > 0 ? scroller.Viewport.Height : FallbackViewport;
        _maxMoves = (int)Math.Ceiling(offset / pane) + SettleMoves;
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

        _seenLayout = true;
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

        // Follow a row only once the new rows have been laid out. Brought into
        // view before that, the row is placed by a guessed row height: in a
        // pane not laid out yet (the diff pane just opening) its text wraps
        // at no width and every row is guessed tall; at 125% a row is 22 or
        // 23 device pixels and the guess is a little off. The panel keeps
        // that guess for the rows above the target, so a short file opened
        // past its imports - with nothing to scroll - showed line 1 below a
        // blank band. LayoutUpdated runs the first Put.
        if (_target is not null && !_seenLayout)
        {
            return;
        }

        _putting = true;
        try
        {
            var offset = _target?.Invoke() ?? _offset;
            if (_target is not null)
            {
                // How far off the target the current offset leaves it.
                _missed.Add((_scroller.Offset.Y, Math.Abs(offset - _scroller.Offset.Y)));
            }

            if (Math.Abs(_scroller.Offset.Y - offset) > 0.5)
            {
                // At a fractional display scale a row is no whole number of
                // device pixels, and the panel places the target row a little
                // differently for every offset: chasing it can circle forever
                // (one layout pass after another - the window hangs). Back at
                // a spot already tried, or over the move budget: settle on
                // the closest spot seen and stop following.
                if (_target is not null && (_tried.Exists(t => Math.Abs(t - offset) <= 0.5) || _tried.Count >= _maxMoves))
                {
                    Settle();
                    return;
                }

                _tried.Add(offset);
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

    private void Settle()
    {
        var best = _missed[0];
        foreach (var seen in _missed)
        {
            // Ties go to the later spot: it was laid out with more rows measured.
            if (seen.Miss <= best.Miss)
            {
                best = seen;
            }
        }

        Stop();
        if (Math.Abs(_scroller.Offset.Y - best.Offset) > 0.5)
        {
            _scroller.Offset = new Vector(_scroller.Offset.X, best.Offset);
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
