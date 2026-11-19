namespace NeatWin.Core;

// Persisted numeric values. These are trigger/arrangement policies, not Smart scoring weights.
public enum AutomaticLayoutMode { Off, LightAssist, FullAssist, AutoFullAssist }
public enum LayoutTrigger { Manual, AfterGesture, WorkspaceChange }
public enum LayoutRoute { ManualAlgorithm, LightAssist, Smart, FullTiling }
public sealed record AutomaticLayoutRequest(AutomaticLayoutMode Mode, LayoutTrigger Trigger);

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
    public const int MinimumIntervalMilliseconds = 1000;
    private WindowSnapshot[] _observed = [];
    private readonly Dictionary<nint, WindowSnapshot> _own = new();
    private readonly HashSet<nint> _manual = [];
    private long _ownUntil, _settleUntil, _notBefore, _lastChange;
    private bool _pending;
    private LayoutTrigger _trigger;
    private nint _foreground;
    public AutomaticLayoutMode Mode { get; private set; }
    public bool HasPending => _pending;

    public void Reset(AutomaticLayoutMode mode, IReadOnlyList<WindowSnapshot> snapshot, long now)
    {
        Mode = AutomaticLayoutPolicy.Read((int)mode, false);
        _own.Clear(); _manual.Clear(); _pending = false; _foreground = 0;
        _observed = Normalize(snapshot); _notBefore = now; _lastChange = now;
    }

    public void RequestCurrent(long now)
    {
        if (AutomaticLayoutPolicy.WatchesWorkspace(Mode)) Queue(LayoutTrigger.WorkspaceChange, now);
    }

    public void GestureStarted(nint handle)
    {
        if (!AutomaticLayoutPolicy.UsesFullPlanner(Mode)) return;
        _pending = false;
        if (_manual.Count >= 128) _manual.Clear();
        _manual.Add(handle); _own.Remove(handle);
    }

    public void GestureCompleted(ManualWindowGesture gesture, long now)
    {
        // A click on a title bar / cancelled drag is not a request to reorganize the desktop.
        if (gesture.StartRect == gesture.EndRect || !AutomaticLayoutPolicy.UsesFullPlanner(Mode)) return;
        _manual.Add(gesture.WindowHandle); _own.Remove(gesture.WindowHandle);
        Queue(LayoutTrigger.AfterGesture, now);
    }

    public void ApplicationRequested(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan, long now)
    {
        _pending = false; _manual.Clear(); _observed = Normalize(before);
        _own.Clear();
        var targets = plan.Moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        foreach (var window in plan.Moves.Select(m => m.Window).Concat(plan.Layers.SelectMany(l => l.FrontToBack)))
            _own[window.Handle] = window with { VisualRect = targets.GetValueOrDefault(window.Handle, window.VisualRect) };
        _ownUntil = now + 2000; _settleUntil = now + 750;
        _notBefore = now + MinimumIntervalMilliseconds;
    }

    public AutomaticLayoutRequest? Observe(IReadOnlyList<WindowSnapshot> snapshot, long now, bool busy,
        IReadOnlySet<nint>? markedByNeatWin = null)
    {
        var next = Normalize(snapshot);
        var ignored = new HashSet<nint>();
        var ignoredOrder = new HashSet<nint>();
        foreach (var w in next)
        {
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
        var changed = Changed(_observed, next, ignored, ignoredOrder);
        _observed = next;
        if (now >= _ownUntil) _own.Clear();
        if (changed)
        {
            if (AutomaticLayoutPolicy.WatchesWorkspace(Mode)) Queue(LayoutTrigger.WorkspaceChange, now);
            else if (_pending) _lastChange = now;
        }
        if (!_pending) return null;
        // Do not tidy while the mouse owns a drag/resize or an application is applying a plan.
        if (busy) { _lastChange = now; return null; }
        if (now - _lastChange < SettleMilliseconds || now < _notBefore) return null;
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

    private static bool Changed(WindowSnapshot[] before, WindowSnapshot[] after, HashSet<nint> ignoreGeometry, HashSet<nint> ignoreOrder)
    {
        if (before.Length != after.Length) return true;
        var old = before.ToDictionary(w => w.Handle);
        foreach (var w in after)
        {
            if (!old.TryGetValue(w.Handle, out var a) || a.ProcessId != w.ProcessId ||
                a.WorkArea != w.WorkArea || a.MonitorHandle != w.MonitorHandle || a.Dpi != w.Dpi ||
                a.IsManageable != w.IsManageable || a.IsResizable != w.IsResizable ||
                a.IsTopmost != w.IsTopmost || a.FrameInsets != w.FrameInsets || a.IsForeground != w.IsForeground)
                return true;
            if (!ignoreGeometry.Contains(w.Handle) && a.VisualRect != w.VisualRect) return true;
        }
        // Moving a member within our layer block renumbers unrelated windows. Compare their
        // relative sequence, not raw Z ranks; a real external restack still causes a new decision.
        return !before.Where(w => !ignoreOrder.Contains(w.Handle)).Select(w => w.Handle)
            .SequenceEqual(after.Where(w => !ignoreOrder.Contains(w.Handle)).Select(w => w.Handle));
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
