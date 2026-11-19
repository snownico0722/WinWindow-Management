using System.Runtime.InteropServices;
using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.App;

/// <summary>
/// Tracks short-lived interaction context for Smart behavior B and requests follow-hand assistance
/// after a real user move/resize. It never records keys, titles or screenshots. Pointer history is
/// kept only in memory and high-frequency sampling runs only while a window is in a move/size loop.
/// </summary>
internal sealed class AutoTidyManager : NativeWindow, IDisposable
{
    private const int MoveSizeStartMessage = 0x8000 + 71;
    private const int MoveSizeEndMessage = 0x8000 + 72;
    private const int ClickMessage = 0x8000 + 73;
    private const int DebounceMilliseconds = 140;
    private const int AttentionSampleMilliseconds = 120;
    private const int DragSampleMilliseconds = 25;
    private const int MaximumPointerSamples = 96;

    private readonly NativeMethods.WinEventProc _winEventProc;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly System.Windows.Forms.Timer _attentionTimer;
    private readonly System.Windows.Forms.Timer _dragTimer;
    private readonly nint _eventHook;
    private nint _mouseHook;
    private readonly Dictionary<nint, AttentionState> _attention = new();
    private long _suppressUntilTick;
    private bool _enabled;
    private bool _disposed;
    private ActiveGesture? _activeGesture;
    private ManualWindowGesture? _pendingGesture;
    private NativeMethods.Point _lastAttentionPoint;
    private long _lastAttentionTick;
    private bool _hasAttentionPoint;

    internal AutoTidyManager()
    {
        CreateHandle(new CreateParams
        {
            Caption = "NeatWin.InteractionWindow",
            Parent = NativeMethods.HwndMessage,
        });

        _debounceTimer = new System.Windows.Forms.Timer { Interval = DebounceMilliseconds };
        _debounceTimer.Tick += OnDebounceTick;
        _attentionTimer = new System.Windows.Forms.Timer { Interval = AttentionSampleMilliseconds };
        _attentionTimer.Tick += OnAttentionTick;
        _dragTimer = new System.Windows.Forms.Timer { Interval = DragSampleMilliseconds };
        _dragTimer.Tick += OnDragTick;

        _winEventProc = OnWinEvent;
        _eventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemMoveSizeStart,
            NativeMethods.EventSystemMoveSizeEnd,
            nint.Zero,
            _winEventProc,
            0,
            0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);

        _mouseProc = OnLowLevelMouse;
        // Follow-hand is initially disabled. Geometry WinEvents remain available for explicit
        // tidy, but pointer polling and the global mouse hook are installed only when needed.
    }

    internal event Action<nint>? GestureStarted;
    internal event Action<ManualWindowGesture>? GestureObserved;
    internal event Action<ManualWindowGesture>? TidyRequested;

    internal bool IsGestureActive => _activeGesture is not null;

    internal bool IsAvailable => _eventHook != nint.Zero;
    internal bool PointerTrackingActive => _attentionTimer.Enabled || _dragTimer.Enabled || _mouseHook != nint.Zero;

    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed) return;
            _enabled = value && IsAvailable;
            SetPointerTracking(_enabled);
            if (!_enabled)
            {
                _debounceTimer.Stop();
                _pendingGesture = null;
            }
        }
    }

    private void SetPointerTracking(bool enabled)
    {
        if (enabled)
        {
            if (_mouseHook == nint.Zero)
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseProc, nint.Zero, 0);
            _attentionTimer.Start();
            if (_activeGesture is not null) _dragTimer.Start();
        }
        else
        {
            _attentionTimer.Stop(); _dragTimer.Stop();
            if (_mouseHook != nint.Zero) _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
            _attention.Clear(); _hasAttentionPoint = false;
        }
    }

    internal ManualWindowGesture? LastGesture { get; private set; }

    internal SmartInteractionContext CaptureInteractionContext(IReadOnlyList<VisibleWindow> visibleWindows)
    {
        var now = Environment.TickCount64;
        _ = NativeMethods.GetCursorPos(out var cursor);
        var foreground = NativeMethods.GetForegroundWindow();
        var signals = new List<WindowAttentionSignal>(visibleWindows.Count);

        foreach (var item in visibleWindows)
        {
            var handle = item.Window.Handle;
            var state = _attention.TryGetValue(handle, out var stored)
                ? stored
                : default;
            var score = Decayed(state.Score, state.LastScoreTick, now, 4300);
            var dwell = Decayed(state.Dwell, state.LastDwellTick, now, 3600);
            var click = state.LastClickTick <= 0 ? 0 : Math.Exp(-(now - state.LastClickTick) / 2700.0);
            var manipulation = state.LastManipulationTick <= 0 ? 0 : Math.Exp(-(now - state.LastManipulationTick) / 5200.0);
            var proximity = CursorProximity(cursor, item.Window.VisualRect, item.Window.WorkArea);
            var attention = Math.Clamp(
                (0.72 * score) +
                (0.72 * click) +
                (0.58 * dwell) +
                (0.92 * manipulation) +
                (0.24 * proximity),
                0,
                2.4);

            signals.Add(new WindowAttentionSignal(
                handle,
                attention,
                click,
                dwell,
                manipulation,
                proximity));
        }

        return new SmartInteractionContext(
            foreground,
            new PointI(cursor.X, cursor.Y),
            signals,
            LastGesture);
    }

    internal void SuppressFor(int milliseconds)
    {
        var until = Environment.TickCount64 + Math.Max(0, milliseconds);
        Interlocked.Exchange(ref _suppressUntilTick, until);
        _debounceTimer.Stop();
        _pendingGesture = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _enabled = false;
        _debounceTimer.Stop();
        _attentionTimer.Stop();
        _dragTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTick;
        _attentionTimer.Tick -= OnAttentionTick;
        _dragTimer.Tick -= OnDragTick;
        _debounceTimer.Dispose();
        _attentionTimer.Dispose();
        _dragTimer.Dispose();

        if (_eventHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_eventHook);
        }
        if (_mouseHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }

        _attention.Clear();
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    private bool IsSuppressed => Environment.TickCount64 < Interlocked.Read(ref _suppressUntilTick);

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || hwnd == nint.Zero || AutomationGuard.IsUtility(hwnd))
        {
            return;
        }

        var message = eventType == NativeMethods.EventSystemMoveSizeStart
            ? MoveSizeStartMessage
            : eventType == NativeMethods.EventSystemMoveSizeEnd
                ? MoveSizeEndMessage
                : 0;
        if (message != 0)
        {
            _ = NativeMethods.PostMessage(Handle, (uint)message, hwnd, nint.Zero);
        }
    }

    private nint OnLowLevelMouse(int code, nint message, nint dataPointer)
    {
        try
        {
            if (!_disposed && _enabled && code >= NativeMethods.HcAction && message == NativeMethods.WmLButtonDown)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MsllHookStruct>(dataPointer);
                var hit = NativeMethods.WindowFromPoint(data.Point);
                var root = hit == nint.Zero ? nint.Zero : NativeMethods.GetAncestor(hit, NativeMethods.GaRoot);
                if (root != nint.Zero)
                {
                    _ = NativeMethods.PostMessage(Handle, ClickMessage, root, nint.Zero);
                }
            }
        }
        catch
        {
            // Never let a managed exception escape a global low-level hook.
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case MoveSizeStartMessage:
                BeginGesture(message.WParam);
                return;
            case MoveSizeEndMessage:
                EndGesture(message.WParam);
                return;
            case ClickMessage:
                if (_enabled) TouchClick(message.WParam);
                return;
        }

        base.WndProc(ref message);
    }

    private void BeginGesture(nint hwnd)
    {
        if (_disposed || AutomationGuard.IsUtility(hwnd) || !TryGetVisualRect(hwnd, out var rect))
        {
            return;
        }

        // A genuine new drag overrides our own placement cooldown. SetWindowPos does not
        // start a user move/size loop, and must not hide a quick corrective gesture.
        Interlocked.Exchange(ref _suppressUntilTick, 0);
        GestureStarted?.Invoke(hwnd);
        _debounceTimer.Stop();
        _pendingGesture = null;
        var now = Environment.TickCount64;
        _ = NativeMethods.GetCursorPos(out var cursor);
        var samples = new List<PointerMotionSample>(MaximumPointerSamples)
        {
            new(new PointI(cursor.X, cursor.Y), 0),
        };
        _activeGesture = new ActiveGesture(
            hwnd,
            rect,
            GetWorkArea(hwnd),
            now,
            samples);
        TouchManipulation(hwnd, now, impulse: 0.45);
        if (_enabled) _dragTimer.Start();
    }

    private void EndGesture(nint hwnd)
    {
        var active = _activeGesture;
        _activeGesture = null;
        _dragTimer.Stop();
        if (_disposed || active is null || active.WindowHandle != hwnd ||
            !TryGetVisualRect(hwnd, out var endRect))
        {
            return;
        }

        AddPointerSample(active);
        var duration = Math.Max(1, Environment.TickCount64 - active.StartTick);
        var kind = ClassifyGesture(active.StartRect, endRect);
        var gesture = new ManualWindowGesture(
            hwnd,
            active.StartRect,
            endRect,
            active.WorkArea,
            kind,
            active.Samples.ToArray(),
            duration,
            EstimateEndSpeed(active.Samples));
        LastGesture = gesture;
        TouchManipulation(hwnd, Environment.TickCount64, impulse: 1.0);
        GestureObserved?.Invoke(gesture);

        if (_enabled)
        {
            _pendingGesture = gesture;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    private void OnDebounceTick(object? sender, EventArgs eventArgs)
    {
        _debounceTimer.Stop();
        if (_disposed || !_enabled || IsSuppressed || _activeGesture is not null ||
            _pendingGesture is not ManualWindowGesture gesture)
        {
            return;
        }

        _pendingGesture = null;
        TidyRequested?.Invoke(gesture);
    }

    private void OnDragTick(object? sender, EventArgs eventArgs)
    {
        if (_activeGesture is ActiveGesture active)
        {
            AddPointerSample(active);
        }
    }

    private void AddPointerSample(ActiveGesture active)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var elapsed = Math.Max(0, Environment.TickCount64 - active.StartTick);
        var sample = new PointerMotionSample(new PointI(cursor.X, cursor.Y), elapsed);
        if (active.Samples.Count >= MaximumPointerSamples)
        {
            active.Samples.RemoveAt(0);
        }
        active.Samples.Add(sample);
    }

    private void OnAttentionTick(object? sender, EventArgs eventArgs)
    {
        if (_disposed || !_enabled || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var now = Environment.TickCount64;
        var speed = 0.0;
        if (_hasAttentionPoint)
        {
            var dt = Math.Max(1, now - _lastAttentionTick) / 1000.0;
            var dx = cursor.X - _lastAttentionPoint.X;
            var dy = cursor.Y - _lastAttentionPoint.Y;
            speed = Math.Sqrt((dx * dx) + (dy * dy)) / dt;
        }

        _lastAttentionPoint = cursor;
        _lastAttentionTick = now;
        _hasAttentionPoint = true;

        var hit = NativeMethods.WindowFromPoint(cursor);
        var root = hit == nint.Zero ? nint.Zero : NativeMethods.GetAncestor(hit, NativeMethods.GaRoot);
        if (root == nint.Zero)
        {
            return;
        }

        // A cursor that settles over a window is useful attention evidence. A fast fly-over is not.
        var dwellImpulse = speed switch
        {
            < 180 => 0.070,
            < 700 => 0.030,
            < 1400 => 0.010,
            _ => 0.0,
        };
        if (dwellImpulse > 0)
        {
            TouchDwell(root, now, dwellImpulse);
        }
    }

    private void TouchClick(nint hwnd)
    {
        var now = Environment.TickCount64;
        var state = GetDecayedState(hwnd, now);
        state.Score = Math.Clamp(state.Score + 0.72, 0, 2.0);
        state.LastScoreTick = now;
        state.LastClickTick = now;
        _attention[hwnd] = state;
    }

    private void TouchDwell(nint hwnd, long now, double impulse)
    {
        var state = GetDecayedState(hwnd, now);
        state.Score = Math.Clamp(state.Score + impulse, 0, 2.0);
        state.Dwell = Math.Clamp(state.Dwell + impulse * 1.3, 0, 1.6);
        state.LastScoreTick = now;
        state.LastDwellTick = now;
        _attention[hwnd] = state;
    }

    private void TouchManipulation(nint hwnd, long now, double impulse)
    {
        if (!_enabled) return;
        var state = GetDecayedState(hwnd, now);
        state.Score = Math.Clamp(state.Score + impulse, 0, 2.0);
        state.LastScoreTick = now;
        state.LastManipulationTick = now;
        _attention[hwnd] = state;
    }

    private AttentionState GetDecayedState(nint hwnd, long now)
    {
        if (_attention.Count >= 512 && !_attention.ContainsKey(hwnd))
            foreach (var key in _attention.OrderBy(pair => pair.Value.LastScoreTick).Take(128).Select(pair => pair.Key).ToArray())
                _attention.Remove(key);
        var state = _attention.TryGetValue(hwnd, out var existing) ? existing : default;
        state.Score = Decayed(state.Score, state.LastScoreTick, now, 4300);
        state.Dwell = Decayed(state.Dwell, state.LastDwellTick, now, 3600);
        state.LastScoreTick = now;
        state.LastDwellTick = now;
        return state;
    }

    private static double Decayed(double value, long lastTick, long now, double tauMilliseconds)
    {
        if (value <= 0 || lastTick <= 0 || now <= lastTick)
        {
            return Math.Max(0, value);
        }
        return value * Math.Exp(-(now - lastTick) / tauMilliseconds);
    }

    private static double CursorProximity(NativeMethods.Point cursor, RectI rect, RectI workArea)
    {
        var point = new PointI(cursor.X, cursor.Y);
        var acquisition = HumanFactorsMetrics.PointerAcquisitionQuality(point, rect);
        var locality = HumanFactorsMetrics.VisualLocalityQuality(point, rect, workArea);
        // Fitts-style target acquisition is the stronger signal; visual locality is a lighter
        // prior for wide/multi-monitor workspaces. Neither is treated as literal eye gaze.
        return Math.Clamp((0.72 * acquisition) + (0.28 * locality), 0, 1);
    }

    private static ManualGestureKind ClassifyGesture(RectI start, RectI end)
    {
        var moved = Math.Abs(start.Left - end.Left) >= 3 || Math.Abs(start.Top - end.Top) >= 3;
        var resized = Math.Abs(start.Width - end.Width) >= 3 || Math.Abs(start.Height - end.Height) >= 3;
        return moved && resized
            ? ManualGestureKind.MoveAndResize
            : resized ? ManualGestureKind.Resize : ManualGestureKind.Move;
    }

    private static double EstimateEndSpeed(IReadOnlyList<PointerMotionSample> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        var end = samples[^1];
        var startIndex = samples.Count - 2;
        while (startIndex > 0 && end.ElapsedMilliseconds - samples[startIndex].ElapsedMilliseconds < 90)
        {
            startIndex--;
        }
        var start = samples[startIndex];
        var dt = Math.Max(1, end.ElapsedMilliseconds - start.ElapsedMilliseconds) / 1000.0;
        var dx = end.Point.X - start.Point.X;
        var dy = end.Point.Y - start.Point.Y;
        return Math.Sqrt((dx * dx) + (dy * dy)) / dt;
    }

    private static bool TryGetVisualRect(nint hwnd, out RectI rect)
    {
        var status = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaExtendedFrameBounds,
            out NativeMethods.Rect nativeRect,
            Marshal.SizeOf<NativeMethods.Rect>());
        if (status == 0)
        {
            rect = RectI.FromEdges(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            return !rect.IsEmpty;
        }

        if (NativeMethods.GetWindowRect(hwnd, out nativeRect))
        {
            rect = RectI.FromEdges(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            return !rect.IsEmpty;
        }

        rect = default;
        return false;
    }

    private static RectI GetWorkArea(nint hwnd)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor != nint.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return RectI.FromEdges(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        }
        return default;
    }

    private sealed class ActiveGesture(
        nint windowHandle,
        RectI startRect,
        RectI workArea,
        long startTick,
        List<PointerMotionSample> samples)
    {
        internal nint WindowHandle { get; } = windowHandle;
        internal RectI StartRect { get; } = startRect;
        internal RectI WorkArea { get; } = workArea;
        internal long StartTick { get; } = startTick;
        internal List<PointerMotionSample> Samples { get; } = samples;
    }

    private struct AttentionState
    {
        internal double Score;
        internal double Dwell;
        internal long LastScoreTick;
        internal long LastDwellTick;
        internal long LastClickTick;
        internal long LastManipulationTick;
    }
}
