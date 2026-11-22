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
    private const int PointerReleasedMessage = 0x8000 + 74;
    private const int PointerInputMessage = 0x8000 + 73;
    private const int GesturePreviewMilliseconds = 48;
    private const int SettledPreviewRefreshMilliseconds = 300;
    private const int AttentionSampleMilliseconds = 120;
    private const int DragSampleMilliseconds = 25;
    private const int MaximumPointerSamples = 96;

    private readonly NativeMethods.WinEventProc _winEventProc;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly System.Windows.Forms.Timer _releaseTimer;
    private readonly System.Windows.Forms.Timer _attentionTimer;
    private readonly System.Windows.Forms.Timer _dragTimer;
    private readonly nint _eventHook;
    private nint _mouseHook;
    private readonly Dictionary<nint, AttentionState> _attention = new();
    private long _suppressUntilTick;
    private bool _enabled;
    private readonly Dictionary<int, PointerObservation> _pointerMessages = [];
    private int _pointerSequence;
    private bool _tracePointer;
    private bool _trackTaskInput, _previewEnabled, _awaitingPointerUp;
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

        _releaseTimer = new System.Windows.Forms.Timer { Interval = AutomaticLayoutScheduler.ReleaseRetryMilliseconds };
        _releaseTimer.Tick += OnReleaseTick;
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
    internal event Action<ManualWindowGesture>? GestureProgress;
    internal event Action? GesturePointerReleased;
    internal event Action<ManualWindowGesture>? GestureObserved;
    internal event Action<ManualWindowGesture>? TidyRequested;
    internal event Action<nint, uint, long, bool>? TaskInput;

    internal event Action<PointerObservation>? PointerObserved;
    internal void SetPointerObservation(bool enabled)
    {
        if (_disposed) return;
        _tracePointer = enabled;
        UpdatePointerTracking();
    }

    internal bool PointerHookAvailable => _mouseHook != 0;
    internal bool TaskInputAvailable => _trackTaskInput && _mouseHook != 0;
    internal bool IsGestureInProgress => _activeGesture != null;
    internal bool IsAvailable => _eventHook != nint.Zero;
    internal bool PointerTrackingActive => _attentionTimer.Enabled || _dragTimer.Enabled || _mouseHook != nint.Zero;

    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed) return;
            _enabled = value && IsAvailable;
            UpdatePointerTracking();
            if (!_enabled)
            {
                _releaseTimer.Stop();
                _pendingGesture = null;
            }
        }
    }

    internal void SetAttentionInput(bool enabled)
    {
        if (_disposed) return;
        _trackTaskInput = enabled;
        UpdatePointerTracking();
    }

    internal void SetGesturePreview(bool enabled)
    {
        if (_disposed) return;
        _previewEnabled = enabled && IsAvailable;
        UpdatePointerTracking();
    }

    private void UpdatePointerTracking()
    {
        if (_enabled || _trackTaskInput || _tracePointer)
        {
            if (_mouseHook == nint.Zero)
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseProc, nint.Zero, 0);
        }
        else if (_mouseHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }
        if (_activeGesture != null && (_enabled || _previewEnabled)) _dragTimer.Start();
        else _dragTimer.Stop();
        if (_enabled) _attentionTimer.Start();
        else
        {
            _attentionTimer.Stop();
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
        _releaseTimer.Stop();
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
        _releaseTimer.Stop();
        _attentionTimer.Stop();
        _dragTimer.Stop();
        _releaseTimer.Tick -= OnReleaseTick;
        _attentionTimer.Tick -= OnAttentionTick;
        _dragTimer.Tick -= OnDragTick;
        _releaseTimer.Dispose();
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
            _ = NativeMethods.PostMessage(Handle, (uint)message, hwnd, unchecked((nint)(int)eventTime));
        }
    }

    private nint OnLowLevelMouse(int code, nint message, nint dataPointer)
    {
        try
        {
            int kind = (int)message;
            if (!_disposed && code >= NativeMethods.HcAction && kind == NativeMethods.WmLButtonUp &&
                (_activeGesture != null || _awaitingPointerUp))
                _ = NativeMethods.PostMessage(Handle, PointerReleasedMessage, 0, 0);
            string? action = kind switch
            {
                NativeMethods.WmLButtonDown => "left-down", NativeMethods.WmLButtonUp => "left-up",
                NativeMethods.WmRButtonDown => "right-down", NativeMethods.WmRButtonUp => "right-up",
                NativeMethods.WmMButtonDown => "middle-down", NativeMethods.WmMButtonUp => "middle-up",
                NativeMethods.WmMouseWheel => "wheel", NativeMethods.WmMouseHWheel => "horizontal-wheel",
                _ => null
            };
            if (!_disposed && code >= NativeMethods.HcAction && action != null)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MsllHookStruct>(dataPointer);
                if ((_trackTaskInput || _tracePointer) && (data.Flags & NativeMethods.MouseInputInjected) != 0)
                    return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
                var hit = NativeMethods.WindowFromPoint(data.Point);
                var root = hit == 0 ? 0 : NativeMethods.GetAncestor(hit, NativeMethods.GaRoot);
                var foreground = NativeMethods.GetForegroundWindow();
                bool wheel = kind is NativeMethods.WmMouseWheel or NativeMethods.WmMouseHWheel;
                var target = wheel && !NativeMethods.ScrollsInactiveWindows() ? foreground : root;
                NativeMethods.GetWindowThreadProcessId(target, out uint pid);
                long now = Environment.TickCount64;
                long tick = now - unchecked((uint)now - data.Time);
                var input = new PointerObservation(action, target, pid, hit, foreground, new(data.Point.X, data.Point.Y),
                    wheel ? unchecked((short)(data.MouseData >> 16)) : null, tick);
                // WH_MOUSE_LL 内保持轻量。用仅本线程可解析的序号投递，窗口销毁后不重新命中测试。
                int id = unchecked(++_pointerSequence);
                if (NativeMethods.PostMessage(Handle, PointerInputMessage, id, 0))
                    _pointerMessages[id] = input;
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
            case PointerReleasedMessage:
                _awaitingPointerUp = false;
                if (_pendingGesture != null) DispatchLightRelease();
                GesturePointerReleased?.Invoke();
                return;
            case MoveSizeEndMessage:
                EndGesture(message.WParam, unchecked((uint)(int)message.LParam));
                return;
            case PointerInputMessage:
                if (!_pointerMessages.Remove((int)message.WParam, out var input)) return;
                if (_tracePointer || _trackTaskInput) PointerObserved?.Invoke(input);
                if (_enabled && input.Kind == "left-down" && input.Target != 0) TouchClick(input.Target);
                // 松开是诊断证据，不能重复增加注意力。诊断单独开启时也不能给布局授权。
                if (_trackTaskInput && input.Kind is not ("left-up" or "right-up" or "middle-up") &&
                    input.Target != 0 && input.TargetPid != 0 && !AutomationGuard.IsUtility(input.Target))
                {
                    NativeMethods.GetWindowThreadProcessId(input.Target, out uint currentPid);
                    if (currentPid == input.TargetPid)
                        TaskInput?.Invoke(input.Target, input.TargetPid, input.Tick, input.Kind is "left-down" or "middle-down");
                }
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

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;
        // A genuine new drag overrides our own placement cooldown. SetWindowPos does not
        // start a user move/size loop, and must not hide a quick corrective gesture.
        Interlocked.Exchange(ref _suppressUntilTick, 0);
        _releaseTimer.Stop();
        _pendingGesture = null;
        var now = Environment.TickCount64;
        _ = NativeMethods.GetCursorPos(out var cursor);
        var samples = new List<PointerMotionSample>(MaximumPointerSamples)
        {
            new(new PointI(cursor.X, cursor.Y), 0),
        };
        _awaitingPointerUp = false;
        var active = new ActiveGesture(hwnd, pid, rect, GetWorkArea(hwnd), now, samples);
        _activeGesture = active;
        GestureStarted?.Invoke(hwnd);
        TouchManipulation(hwnd, now, impulse: 0.45);
        if (_enabled || _previewEnabled) _dragTimer.Start();
        if (_previewEnabled) PublishPreview(active, rect, now);
    }

    private void EndGesture(nint hwnd, uint eventTime)
    {
        var active = _activeGesture;
        if (_disposed || active == null || active.WindowHandle != hwnd) return;
        _activeGesture = null;
        _dragTimer.Stop();
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != active.ProcessId || !TryGetVisualRect(hwnd, out var endRect)) return;
        AddPointerSample(active);
        var observedAt = Environment.TickCount64;
        long releasedAt = observedAt - unchecked((uint)observedAt - eventTime);
        var gesture = BuildGesture(active, endRect, releasedAt, releasedAt);
        LastGesture = gesture;
        _awaitingPointerUp = NativeMethods.GetAsyncKeyState(0x01) < 0;
        if (_trackTaskInput && active.StartRect != endRect) TaskInput?.Invoke(hwnd, pid, observedAt, false);
        TouchManipulation(hwnd, observedAt, impulse: 1.0);
        GestureObserved?.Invoke(gesture);
        if (_enabled && gesture.StartRect != gesture.EndRect)
        {
            _pendingGesture = gesture;
            DispatchLightRelease();
        }
    }

    private static ManualWindowGesture BuildGesture(ActiveGesture active, RectI rect, long now, long? releasedAt = null) =>
        new(active.WindowHandle, active.StartRect, rect, active.WorkArea, ClassifyGesture(active.StartRect, rect),
            active.Samples.ToArray(), Math.Max(1, now - active.StartTick), EstimateEndSpeed(active.Samples), releasedAt)
        { ProcessId = active.ProcessId };

    private void OnReleaseTick(object? sender, EventArgs e) => DispatchLightRelease();

    private void DispatchLightRelease()
    {
        _releaseTimer.Stop();
        if (_disposed || !_enabled || IsSuppressed || _activeGesture != null || _pendingGesture is not { } gesture) return;
        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        var state = AutomaticLayoutPolicy.ReleaseState(gesture, foreground, pid, Environment.TickCount64, NativeWindowActivity.PointerBusy);
        if (state == GestureReleaseState.Wait) { _releaseTimer.Start(); return; }
        _pendingGesture = null;
        if (state == GestureReleaseState.Ready) TidyRequested?.Invoke(gesture);
    }

    private void OnDragTick(object? sender, EventArgs e)
    {
        if (_activeGesture is not { } active) return;
        AddPointerSample(active);
        long now = Environment.TickCount64;
        if (!_previewEnabled || now - active.PreviewTick < GesturePreviewMilliseconds ||
            !TryGetVisualRect(active.WindowHandle, out var rect)) return;
        NativeMethods.GetWindowThreadProcessId(active.WindowHandle, out var pid);
        if (active.ProcessId != pid || NativeMethods.GetForegroundWindow() != active.WindowHandle) return;
        PublishPreview(active, rect, now);
    }

    private void PublishPreview(ActiveGesture active, RectI rect, long now)
    {
        // Even if the frame stops changing, emit one settled speed sample after a fast
        // approach. A held, settled frame keeps existing preview data fresh without
        // submitting a new computation at pointer-sampling frequency.
        active.PreviewTick = now;
        bool settled = EstimateEndSpeed(active.Samples) < 100;
        if (active.PreviewRect is { } last && WindowStateRules.Near(last, rect) && active.PreviewSettled == settled &&
            (!settled || now - active.PreviewOfferedAt < SettledPreviewRefreshMilliseconds)) return;
        active.PreviewOfferedAt = now;
        active.PreviewRect = rect;
        active.PreviewSettled = settled;
        GestureProgress?.Invoke(BuildGesture(active, rect, now));
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
        nint windowHandle, uint processId,
        RectI startRect,
        RectI workArea,
        long startTick,
        List<PointerMotionSample> samples)
    {
        internal nint WindowHandle { get; } = windowHandle;
        internal uint ProcessId { get; } = processId;
        internal long PreviewTick { get; set; }
        internal long PreviewOfferedAt { get; set; }
        internal RectI? PreviewRect { get; set; }
        internal bool PreviewSettled { get; set; }
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
