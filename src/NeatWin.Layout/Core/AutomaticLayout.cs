namespace NeatWin.Core;

// 1 和 2 保留旧授权。仅在用户选择新的轻度模式时才启用开窗辅助。
public enum AutomaticLayoutMode { Off = 0, LightAssist = 1, FullAssist = 2, AutoFullAssist = 3, Light = 5 }
public enum LayoutTrigger { Manual, AfterGesture, WorkspaceChange, NewTaskOpening }
[Flags]
public enum WorkspaceChangeCause { None = 0, Focus = 1, Membership = 2, Geometry = 4, Order = 8, WindowState = 16, Requested = 32 }

public enum LayoutRoute { ManualAlgorithm, LightAssist, Smart, FullTiling, LocalOpening }
public sealed record AutomaticLayoutRequest(AutomaticLayoutMode Mode, LayoutTrigger Trigger)
{
    internal long[]? ChangeEvents { get; init; }
    internal int OmittedChanges { get; init; }
    public WorkspaceChangeCause Cause { get; init; }
    internal ForegroundHistory.Opening? Opening { get; init; }
    internal ForegroundHistory.NewTaskOpening? LocalOpening { get; init; }
}

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
    internal static bool AllowsAutomaticDismissal(AutomaticLayoutMode mode) => mode == AutomaticLayoutMode.AutoFullAssist;
    internal static bool WatchesOpenings(AutomaticLayoutMode mode) => mode == AutomaticLayoutMode.Light;
    internal static bool UsesFollowHand(AutomaticLayoutMode mode) => mode is AutomaticLayoutMode.LightAssist or AutomaticLayoutMode.Light;
    internal static AutomaticLayoutMode VisibleChoice(AutomaticLayoutMode mode) =>
        mode is AutomaticLayoutMode.LightAssist or AutomaticLayoutMode.FullAssist ? AutomaticLayoutMode.Light : mode;
    public static bool UsesFullPlanner(AutomaticLayoutMode mode) => mode is AutomaticLayoutMode.FullAssist or AutomaticLayoutMode.AutoFullAssist;
    public static LayoutRoute? Route(AutomaticLayoutMode mode, LayoutTrigger trigger) => trigger == LayoutTrigger.Manual
        ? LayoutRoute.ManualAlgorithm
        : mode switch
        {
            AutomaticLayoutMode.LightAssist or AutomaticLayoutMode.Light when trigger == LayoutTrigger.AfterGesture => LayoutRoute.LightAssist,
            AutomaticLayoutMode.Light when trigger == LayoutTrigger.NewTaskOpening => LayoutRoute.LocalOpening,
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
        AutomaticLayoutMode.AutoFullAssist => "完整",
        AutomaticLayoutMode.Light => "轻度",
        _ => "关闭",
    };
    public static string Description(AutomaticLayoutMode mode) => mode switch
    {
        AutomaticLayoutMode.LightAssist => "拖动或缩放后，只小幅修正刚调整的窗口（原逻辑）。",
        AutomaticLayoutMode.FullAssist => "拖动或缩放结束后，运行与手点相同的完整 Smart 整理。",
        AutomaticLayoutMode.AutoFullAssist => "窗口开关、前后台、层级或位置改变后，自动整理工作区。",
        AutomaticLayoutMode.Light => "拖动、缩放结束或打开新的任务窗口时，只调整这扇窗口的位置和大小。",
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
    internal TaskSurfaces Surfaces { get; init; } = new();
    private bool _localForeground, _captureForeground;
    internal nint WorkingForeground => _foreground;
    private LayoutTrigger _trigger;
    private nint _foreground, _dragging;
    private ManualWindowGesture? _release;
    internal ManualWindowGesture? PendingRelease => _release;
    private WorkspaceChangeCause _cause;
    internal Func<WindowSnapshot, string, long?>? RecordChange { get; init; }
    private readonly List<long> _changeEvents = [];
    private int _omittedChanges;
    private void ClearChangeEvents() { _changeEvents.Clear(); _omittedChanges = 0; }
    private void Evidence(WindowSnapshot window, string change)
    {
        // 只连接经 Normalize、自己动作回声过滤之后实际参与调度的差异。时间上相邻的鼠标输入不是触发源。
        if (!AutomaticLayoutPolicy.WatchesWorkspace(Mode) || RecordChange?.Invoke(window, change) is not long id) return;
        _changeEvents.Add(id);
        if (_changeEvents.Count > 128) { _changeEvents.RemoveAt(0); _omittedChanges++; }
    }
    public AutomaticLayoutMode Mode { get; private set; }
    public bool HasPending => _pending;

    public void Reset(AutomaticLayoutMode mode, IReadOnlyList<WindowSnapshot> snapshot, long now)
    {
        Mode = AutomaticLayoutPolicy.Read((int)mode, false);
        _own.Clear(); _manual.Clear(); _pending = false; _foreground = _dragging = 0; _release = null;
        _cause = WorkspaceChangeCause.None; ClearChangeEvents();
        Surfaces.Reset();
        _observed = [];
        _observed = Normalize(snapshot); _notBefore = now; _lastChange = now; _settleUntil = now;
    }

    public void RequestCurrent(long now, WorkspaceChangeCause cause = WorkspaceChangeCause.Requested)
    {
        if (!_pending) ClearChangeEvents();
        if (AutomaticLayoutPolicy.WatchesWorkspace(Mode)) Queue(LayoutTrigger.WorkspaceChange, now, cause);
    }

    internal void DeferWorkspaceWhileBusy(long now)
    {
        if (_pending && _release == null) _lastChange = now;
    }

    internal void CancelLocalGesture() { _manual.Remove(_dragging); _dragging = 0; _release = null; }

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
        IReadOnlySet<nint>? markedByNeatWin = null, bool desktopFocused = false, long navigationSettleUntil = 0, bool openingReady = false,
        bool newTaskOpeningReady = false)
    {
        // The desktop and taskbar are excluded from window capture. Clear the last app and
        // queued plan here, so activating the shell cannot arrange the previous task.
        if (desktopFocused)
        {
            Reset(Mode, [], now);
            return null;
        }
        if (!_pending) ClearChangeEvents();
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
        // 轻度档只放行经独立开窗证据确认的事件。普通前台、层级或几何变化只更新现场。
        if (AutomaticLayoutPolicy.WatchesOpenings(Mode) && newTaskOpeningReady && !busy && !_localForeground &&
            now >= navigationSettleUntil)
            return new(Mode, LayoutTrigger.NewTaskOpening) { Cause = WorkspaceChangeCause.Membership };
        if (changed != WorkspaceChangeCause.None)
        {
            if (AutomaticLayoutPolicy.WatchesWorkspace(Mode)) Queue(LayoutTrigger.WorkspaceChange, now, changed);
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
            // 临时表面的停留不续期。原任务若已有真实变化，浮层离开即可依据原时钟判断。
            if (_localForeground && !_captureForeground) return null;
            // 已确认的开窗碰撞可以抢在常规静稳期之前处理。导航静默期仍优先，让用户有时间点开下一层内容。
            if (now < navigationSettleUntil || !openingReady && (now - _lastChange < SettleMilliseconds || now < _notBefore)) return null;
        }
        _pending = false; _manual.Clear(); _notBefore = now + MinimumIntervalMilliseconds;
        var events = _trigger == LayoutTrigger.WorkspaceChange && _changeEvents.Count > 0 ? _changeEvents.ToArray() : null;
        int omitted = events != null ? _omittedChanges : 0;
        ClearChangeEvents();
        return new(Mode, _trigger) { Cause = _cause, ChangeEvents = events, OmittedChanges = omitted };
    }

    private void Queue(LayoutTrigger trigger, long now, WorkspaceChangeCause cause = WorkspaceChangeCause.Requested)
    {
        _cause = trigger == LayoutTrigger.AfterGesture ? WorkspaceChangeCause.None : (_pending ? _cause | cause : cause);
        if (!_pending || trigger == LayoutTrigger.AfterGesture) _trigger = trigger;
        _pending = true; _lastChange = now;
    }

    private WindowSnapshot[] Normalize(IReadOnlyList<WindowSnapshot> snapshot)
    {
        var previous = _observed.FirstOrDefault(w => w.Handle == _foreground);
        Surfaces.Update(snapshot, previous);
        var front = snapshot.FirstOrDefault(w => w.IsForeground);
        _localForeground = front != null && Surfaces.IsLocal(front);
        _captureForeground = front != null && Surfaces.IsCaptureOverlay(front);
        var foreground = _localForeground ? 0 : front?.Handle ?? 0;
        // Activating our own settings, the taskbar or another excluded utility is not a new task.
        if (foreground != 0) _foreground = foreground;
        if (!snapshot.Any(w => w.Handle == _foreground)) _foreground = 0;
        // 输入候选、提示等小型被动浮层仍保留在求解时的真实遮挡快照里。它们自己出现或消失不代表新的工作窗口。
        var relevant = snapshot.Where(w => !Surfaces.IsLocal(w)).Where(w => w.IsManageable || w.SurfaceState != null || w.IsForeground ||
            !w.IsTopmost || w.VisualRect.Intersect(w.WorkArea).Area >= w.WorkArea.Area * .15);
        // 模态卡片会临时禁用宿主，关闭后又启用。两次状态回弹都不代表主窗变成了新的任务。
        var modalHosts = Surfaces.Hosts.Select(w => w.Handle).ToHashSet();
        return relevant.OrderBy(w => w.ZOrder).Select((w, rank) => w with
            { ZOrder = rank, IsForeground = w.Handle == _foreground,
              IsManageable = modalHosts.Contains(w.Handle) && _observed.FirstOrDefault(old =>
                  old.Handle == w.Handle && old.ProcessId == w.ProcessId) is { } prior ? prior.IsManageable : w.IsManageable }).ToArray();
    }

    private WorkspaceChangeCause UpdateObserved(WindowSnapshot[] next, HashSet<nint> ignoreGeometry, HashSet<nint> ignoreOrder)
    {
        var before = _observed;
        var old = before.ToDictionary(w => w.Handle);
        var changed = before.Length != next.Length ? WorkspaceChangeCause.Membership : WorkspaceChangeCause.None;
        for (var i = 0; i < next.Length; i++)
        {
            var w = next[i];
            if (!old.Remove(w.Handle, out var a) || a.ProcessId != w.ProcessId)
            {
                if (a != null) Evidence(a, "removed");
                Evidence(w, "added");
                changed |= WorkspaceChangeCause.Membership;
                continue;
            }
            if (a.IsForeground != w.IsForeground)
            {
                Evidence(w, w.IsForeground ? "focus-gained" : "focus-lost");
                changed |= WorkspaceChangeCause.Focus;
            }
            if (a.WorkArea != w.WorkArea || a.MonitorHandle != w.MonitorHandle || a.Dpi != w.Dpi ||
                a.IsManageable != w.IsManageable || a.IsResizable != w.IsResizable ||
                a.IsTopmost != w.IsTopmost || a.FrameInsets != w.FrameInsets || a.SurfaceState != w.SurfaceState)
            {
                Evidence(w, "state");
                changed |= WorkspaceChangeCause.WindowState;
                continue;
            }
            if (ignoreGeometry.Contains(w.Handle) || a.VisualRect == w.VisualRect) continue;
            if (WindowStateRules.Near(a.VisualRect, w.VisualRect))
            {
                // Keep the last significant rect so slow external movement still accumulates.
                // The planner captures fresh geometry when the request runs.
                next[i] = w with { VisualRect = a.VisualRect };
            }
            else { Evidence(w, "geometry"); changed |= WorkspaceChangeCause.Geometry; }
        }
        foreach (var removed in old.Values) Evidence(removed, "removed");
        _observed = next;
        // Moving a member within our layer block renumbers unrelated windows. Compare their
        // relative sequence, not raw Z ranks; a real external restack still causes a new decision.
        var previousOrder = before.Where(w => !ignoreOrder.Contains(w.Handle)).Select(w => w.Handle);
        var currentOrder = next.Where(w => !ignoreOrder.Contains(w.Handle)).ToArray();
        if (!previousOrder.SequenceEqual(currentOrder.Select(w => w.Handle)))
        {
            // 成员增减本身已有证据；纯层级变化记录相对顺序首次不同的窗口。
            if (!changed.HasFlag(WorkspaceChangeCause.Membership))
            {
                var reordered = previousOrder.Zip(currentOrder).FirstOrDefault(p => p.First != p.Second.Handle);
                if (reordered.Second != null) Evidence(reordered.Second, "order");
            }
            changed |= WorkspaceChangeCause.Order;
        }
        return changed;
    }
}

internal static class LayoutDispatch
{
    internal static string ModelVersion(TidyAlgorithmMode mode) =>
        mode == TidyAlgorithmMode.Classic ? "Classic v1.0" : "Smart v3.2";

    internal static IntentLayoutPlan Create(LayoutRoute route, IReadOnlyList<VisibleWindow> visible,
        TidyOptions options, IntentReferenceDocument? reference, IReadOnlyList<WindowSnapshot> desktop, TaskLayoutContext? context)
    {
        if (route == LayoutRoute.FullTiling) return FullTilingPlanner.Create(desktop, options);
        if (route == LayoutRoute.LocalOpening)
        {
            if (context == null || context.LocalPlacementWindow == 0) return new([], [], []);
            context = context with { VideoHint = null };
        }
        if (route is LayoutRoute.Smart or LayoutRoute.LocalOpening) options = options with { AlgorithmMode = TidyAlgorithmMode.Smart };
        // 普通布局使用独立的焦点、手势和空间证据。改变黑边观测时，其他窗口可选的位置不随视频权重改变。
        if (context != null)
        {
            var scene = ActiveLayoutScene.Resolve(desktop, context);
            context = (scene?.Interpret(context, desktop.FirstOrDefault(w => w.IsForeground)?.Handle ?? 0) ?? context)
                with { Scene = scene };
        }
        if (route == LayoutRoute.Smart && context != null && IntentLayoutPlanner.CoupledDivider(visible, desktop, context, options) is { } divider)
            return divider with { Scene = context.Scene };
        var ordinary = context?.VideoHint == null ? context : context with { VideoHint = null };
        var plan = IntentLayoutPlanner.CreateDetailedPlan(visible, options, reference, desktop, ordinary);
        if (context?.VideoHint != null && (context.GeometryOnlyWindow == 0 || context.GeometryOnlyWindow == context.VideoHint.WindowHandle)) plan = VideoAspectPostProcessor.Select(visible, desktop, plan, options, context);
        return context?.Scene is { } finalScene ? plan with { Scene = finalScene } : plan;
    }
}
