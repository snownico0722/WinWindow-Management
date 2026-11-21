namespace NeatWin.Core;

// Persisted numeric values. These are trigger/arrangement policies, not Smart scoring weights.
public enum AutomaticLayoutMode { Off, LightAssist, FullAssist, AutoFullAssist }
public enum LayoutTrigger { Manual, AfterGesture, WorkspaceChange }
public enum LayoutRoute { ManualAlgorithm, LightAssist, Smart, FullTiling }
public sealed record AutomaticLayoutRequest(AutomaticLayoutMode Mode, LayoutTrigger Trigger);

internal enum GestureReleaseState { Discard, Wait, Ready }

public static class AutomaticLayoutPolicy
{
    public static AutomaticLayoutMode Read(int? value, bool? legacyEnabled)
    {
        // The old fifth mode was a misunderstanding of laying out the UI controls. Migrate
        // that stored value to automatic full Smart; never keep automatic tiling enabled.
        if (value is int raw)
        {
            if (raw == 4) return AutomaticLayoutMode.AutoFullAssist;
            return Enum.IsDefined(typeof(AutomaticLayoutMode), raw)
                ? (AutomaticLayoutMode)raw
                : AutomaticLayoutMode.Off;
        }
        return legacyEnabled == true ? AutomaticLayoutMode.LightAssist : AutomaticLayoutMode.Off;
    }
    public static bool WatchesWorkspace(AutomaticLayoutMode mode) => mode == AutomaticLayoutMode.AutoFullAssist;
    public static bool UsesFullPlanner(AutomaticLayoutMode mode) => mode is AutomaticLayoutMode.FullAssist or AutomaticLayoutMode.AutoFullAssist;
    public static LayoutRoute? Route(AutomaticLayoutMode mode, LayoutTrigger trigger) => trigger == LayoutTrigger.Manual
        ? LayoutRoute.ManualAlgorithm
        : mode switch
        {
            AutomaticLayoutMode.LightAssist when trigger == LayoutTrigger.AfterGesture => LayoutRoute.LightAssist,
            AutomaticLayoutMode.FullAssist when trigger == LayoutTrigger.AfterGesture => LayoutRoute.Smart,
            AutomaticLayoutMode.AutoFullAssist => LayoutRoute.Smart,
            _ => null,
        };
    // An END event is authoritative about the window-frame gesture. The pointer can still
    // belong to a native move loop, menu or file drag when the event reaches our UI thread.
    internal static GestureReleaseState ReleaseState(ManualWindowGesture gesture, nint foreground, uint pid, long now, bool busy)
    {
        if (foreground == 0 || foreground != gesture.WindowHandle || gesture.ProcessId != 0 && gesture.ProcessId != pid ||
            gesture.ReleasedTick is long released && now - released > 4000) return GestureReleaseState.Discard;
        return busy ? GestureReleaseState.Wait : GestureReleaseState.Ready;
    }

    public static string Name(AutomaticLayoutMode mode) => mode switch
    {
        AutomaticLayoutMode.LightAssist => "轻度辅助",
        AutomaticLayoutMode.FullAssist => "完整辅助",
        AutomaticLayoutMode.AutoFullAssist => "自动完整辅助",
        _ => "自动关",
    };
    public static string Description(AutomaticLayoutMode mode) => mode switch
    {
        AutomaticLayoutMode.LightAssist => "拖动或缩放后，只小幅修正刚调整的窗口（原逻辑）。",
        AutomaticLayoutMode.FullAssist => "拖动或缩放结束后，运行与手点相同的完整 Smart 整理。",
        AutomaticLayoutMode.AutoFullAssist => "窗口开关、前后台、层级或位置改变后，自动运行完整 Smart；忽略自身改动。",
        _ => "不自动改变窗口；仍可点击整理按钮或使用快捷键。",
    };
}

/// <summary>
/// Event coalescing and own-action attribution. Pure, deterministic, session-local; neither the
/// quiet interval nor the absence of a correction is a satisfaction label. Times are monotonic ms.
/// </summary>
public sealed class AutomaticLayoutScheduler
{
    public const int SettleMilliseconds = 450;
    internal const int ReleaseRetryMilliseconds = 16;
    public const int MinimumIntervalMilliseconds = 1000;
    private WindowSnapshot[] _observed = [];
    private readonly Dictionary<nint, WindowSnapshot> _own = new();
    private readonly HashSet<nint> _manual = [];
    private long _ownUntil, _settleUntil, _notBefore, _lastChange;
    private bool _pending;
    private LayoutTrigger _trigger;
    private nint _foreground, _dragging;
    private ManualWindowGesture? _release;
    internal ManualWindowGesture? PendingRelease => _release;
    private (nint Handle, uint Pid) _inputTarget;
    private long _inputStarted, _lastInput;
    private bool _sustainedInputReported;
    public AutomaticLayoutMode Mode { get; private set; }
    public bool HasPending => _pending;

    public void Reset(AutomaticLayoutMode mode, IReadOnlyList<WindowSnapshot> snapshot, long now)
    {
        Mode = AutomaticLayoutPolicy.Read((int)mode, false);
        _own.Clear(); _manual.Clear(); _pending = false; _foreground = _dragging = 0; _release = null;
        _inputTarget = default;
        _observed = Normalize(snapshot); _notBefore = now; _lastChange = now;
    }

    public void RequestCurrent(long now)
    {
        if (AutomaticLayoutPolicy.WatchesWorkspace(Mode)) Queue(LayoutTrigger.WorkspaceChange, now);
    }

    internal void DeferWorkspaceWhileBusy(long now)
    {
        if (_pending && _release == null) _lastChange = now;
    }

    internal bool TaskInput(nint handle, uint pid, long now)
    {
        if (!AutomaticLayoutPolicy.WatchesWorkspace(Mode) || handle == 0 || pid == 0) return false;
        // A scroll can transfer attention to a reference without changing native focus.
        // One more pass after sustained interaction lets the previous task decay.
        bool newTarget = (handle, pid) != _inputTarget || now - _lastInput > ForegroundHistory.RecentInputMilliseconds;
        _lastInput = now;
        if (newTarget)
        {
            _inputTarget = (handle, pid);
            _inputStarted = now;
            _sustainedInputReported = false;
        }
        else
        {
            if (_sustainedInputReported || now - _inputStarted < ForegroundHistory.EstablishedTaskMilliseconds) return false;
            _sustainedInputReported = true;
        }
        if (!_pending) RequestCurrent(now);
        return true;
    }

    public void GestureStarted(nint handle)
    {
        if (!AutomaticLayoutPolicy.UsesFullPlanner(Mode)) return;
        if (_release != null) _pending = false;
        _release = null; _dragging = handle;
        if (_manual.Count >= 128) _manual.Clear();
        _manual.Add(handle); _own.Remove(handle);
    }

    public void GestureCompleted(ManualWindowGesture gesture, long now)
    {
        if (!AutomaticLayoutPolicy.UsesFullPlanner(Mode)) return;
        if (_dragging != 0 && _dragging != gesture.WindowHandle) return;
        _dragging = 0;
        var previous = _observed.FirstOrDefault(w => w.Handle == gesture.WindowHandle);
        if (gesture.StartRect == gesture.EndRect)
        {
            // Intermediate drag geometry was never an independent workspace change. Restore
            // the baseline for this window while retaining unrelated events from the same drag.
            if (previous != null && (gesture.ProcessId == 0 || previous.ProcessId == gesture.ProcessId))
                _observed[Array.IndexOf(_observed, previous)] = previous with { VisualRect = gesture.EndRect };
            _manual.Remove(gesture.WindowHandle);
            return;
        }
        _release = gesture.ProcessId == 0 && previous != null ? gesture with { ProcessId = previous.ProcessId } : gesture;
        _manual.Add(gesture.WindowHandle); _own.Remove(gesture.WindowHandle);
        Queue(LayoutTrigger.AfterGesture, now);
    }

    public void ApplicationRequested(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan, long now)
    {
        _pending = false; _release = null; _dragging = 0; _manual.Clear(); _observed = Normalize(before);
        _own.Clear();
        var targets = plan.Moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        foreach (var window in plan.Moves.Select(m => m.Window).Concat(plan.Layers.SelectMany(l => l.FrontToBack)))
            _own[window.Handle] = window with { VisualRect = targets.GetValueOrDefault(window.Handle, window.VisualRect) };
        _ownUntil = now + 2000; _settleUntil = now + 750;
        _notBefore = now + MinimumIntervalMilliseconds;
    }

    public AutomaticLayoutRequest? Observe(IReadOnlyList<WindowSnapshot> snapshot, long now, bool busy,
        IReadOnlySet<nint>? markedByNeatWin = null, bool desktopFocused = false, long navigationSettleUntil = 0)
    {
        // The desktop and taskbar are excluded from window capture. Clear the last app and
        // queued plan here, so activating the shell cannot arrange the previous task.
        if (desktopFocused)
        {
            Reset(Mode, [], now);
            return null;
        }
        var next = Normalize(snapshot);
        var ignored = new HashSet<nint>();
        var ignoredOrder = new HashSet<nint>();
        foreach (var w in next)
        {
            if (busy && w.Handle == _dragging) { ignored.Add(w.Handle); continue; }
            if (_manual.Contains(w.Handle)) continue;
            if (_own.TryGetValue(w.Handle, out var expected) && expected.ProcessId == w.ProcessId)
            {
                // Permit intermediate/clamped native output briefly, then ignore only echoes of
                // the requested geometry. A later new change on this same window is external.
                if (now < _ownUntil && (now < _settleUntil || WindowStateRules.Near(expected.VisualRect, w.VisualRect)))
                    ignored.Add(w.Handle);
                if (now < _settleUntil) ignoredOrder.Add(w.Handle);
            }
            else if (markedByNeatWin?.Contains(w.Handle) == true)
            {
                ignored.Add(w.Handle); ignoredOrder.Add(w.Handle);
            }
        }
        var changed = UpdateObserved(next, ignored, ignoredOrder);
        if (now >= _ownUntil) _own.Clear();
        if (changed)
        {
            if (AutomaticLayoutPolicy.WatchesWorkspace(Mode)) Queue(LayoutTrigger.WorkspaceChange, now);
            else if (_pending) _lastChange = now;
        }
        if (!_pending) return null;
        if (_release is { } gesture)
        {
            // Normalize intentionally remembers an excluded foreground for workspace diffs.
            // A release instead needs the actual captured foreground and process identity.
            var active = snapshot.FirstOrDefault(w => w.IsForeground && w.IsManageable && !w.IsTopmost);
            var state = AutomaticLayoutPolicy.ReleaseState(gesture, active?.Handle ?? 0, active?.ProcessId ?? 0, now, busy);
            if (state == GestureReleaseState.Wait) return null;
            _release = null;
            if (state == GestureReleaseState.Discard)
            {
                _manual.Clear(); _pending = false;
                if (AutomaticLayoutPolicy.WatchesWorkspace(Mode) && active != null) Queue(LayoutTrigger.WorkspaceChange, now);
                return null;
            }
        }
        else
        {
            if (busy) { _lastChange = now; return null; }
            if (now - _lastChange < SettleMilliseconds || now < _notBefore || now < navigationSettleUntil) return null;
        }
        _pending = false; _manual.Clear(); _notBefore = now + MinimumIntervalMilliseconds;
        return new(Mode, _trigger);
    }

    private void Queue(LayoutTrigger trigger, long now)
    {
        if (!_pending || trigger == LayoutTrigger.AfterGesture) _trigger = trigger;
        _pending = true; _lastChange = now;
    }

    private WindowSnapshot[] Normalize(IReadOnlyList<WindowSnapshot> snapshot)
    {
        var foreground = snapshot.FirstOrDefault(w => w.IsForeground)?.Handle ?? 0;
        // Activating our own settings, the taskbar or another excluded utility is not a new task.
        if (foreground != 0) _foreground = foreground;
        if (!snapshot.Any(w => w.Handle == _foreground)) _foreground = 0;
        return snapshot.OrderBy(w => w.ZOrder).Select((w, rank) => w with
            { ZOrder = rank, IsForeground = w.Handle == _foreground }).ToArray();
    }

    private bool UpdateObserved(WindowSnapshot[] next, HashSet<nint> ignoreGeometry, HashSet<nint> ignoreOrder)
    {
        var before = _observed;
        var old = before.ToDictionary(w => w.Handle);
        var changed = before.Length != next.Length;
        for (var i = 0; i < next.Length; i++)
        {
            var w = next[i];
            if (!old.TryGetValue(w.Handle, out var a) || a.ProcessId != w.ProcessId ||
                a.WorkArea != w.WorkArea || a.MonitorHandle != w.MonitorHandle || a.Dpi != w.Dpi ||
                a.IsManageable != w.IsManageable || a.IsResizable != w.IsResizable ||
                a.IsTopmost != w.IsTopmost || a.FrameInsets != w.FrameInsets || a.IsForeground != w.IsForeground)
            {
                changed = true;
                continue;
            }
            if (ignoreGeometry.Contains(w.Handle) || a.VisualRect == w.VisualRect) continue;
            if (WindowStateRules.Near(a.VisualRect, w.VisualRect))
            {
                // Keep the last significant rect so slow external movement still accumulates.
                // The planner captures fresh geometry when the request runs.
                next[i] = w with { VisualRect = a.VisualRect };
            }
            else changed = true;
        }
        _observed = next;
        // Moving a member within our layer block renumbers unrelated windows. Compare their
        // relative sequence, not raw Z ranks; a real external restack still causes a new decision.
        return changed || !before.Where(w => !ignoreOrder.Contains(w.Handle)).Select(w => w.Handle)
            .SequenceEqual(next.Where(w => !ignoreOrder.Contains(w.Handle)).Select(w => w.Handle));
    }
}

internal static class LayoutDispatch
{
    internal static IntentLayoutPlan Create(LayoutRoute route, IReadOnlyList<VisibleWindow> visible,
        TidyOptions options, IntentReferenceDocument? reference, IReadOnlyList<WindowSnapshot> desktop, TaskLayoutContext? context) =>
        route == LayoutRoute.FullTiling ? FullTilingPlanner.Create(desktop, options) :
        IntentLayoutPlanner.CreateDetailedPlan(visible,
            route == LayoutRoute.Smart ? options with { AlgorithmMode = TidyAlgorithmMode.Smart } : options,
            reference, desktop, context);
}
