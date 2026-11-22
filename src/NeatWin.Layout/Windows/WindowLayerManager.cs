using NeatWin.Core;

namespace NeatWin.Windows;

public sealed record WindowMoveFailure(TidyMove Move, int Win32Error);
public sealed record LayoutApplication(IntentLayoutPlan Plan, string[] Notes)
{
    public bool Interrupted { get; init; }
    internal long? FirstGeometrySubmittedTick { get; init; }
    internal long? GestureWindowSubmittedTick { get; init; }
    public WindowSnapshot? MinimizeRequested { get; init; }
    public IReadOnlyList<WindowMoveFailure> Failures { get; init; } = [];
}

public sealed partial class WindowManager
{
    private static bool InteractionChanged(nint? automaticForeground) => automaticForeground.HasValue
        ? !NativeWindowActivity.AllowsAutomaticPlacement(automaticForeground.Value) : NativeWindowActivity.IsMoving;

    public LayoutApplication ApplyLayout(IntentLayoutPlan plan) => ApplyLayout(plan, null);

    internal LayoutApplication ApplyLayout(IntentLayoutPlan plan, nint? automaticForeground, TaskLayoutContext? context = null, nint gestureWindow = default, ForegroundReading? initialReading = null, ForegroundReading? workspaceReading = null)
    {
        var current = Capture();
        var needed = plan.Moves.Select(m => m.Window).Concat(plan.Layers.SelectMany(l => l.FrontToBack))
            .GroupBy(w => w.Handle).Select(g => g.First()).ToArray();
        if (InteractionChanged(automaticForeground))
            return new(plan with { Moves = [], Layers = [] }, ["检测到新的手动调整，未覆盖窗口位置。"]) { Interrupted = true };
        if (needed.Any(w => !current.Any(c => c.Handle == w.Handle &&
            c.IsManageable && WindowStateRules.Matches(w, c, w.VisualRect))))
            return new(plan with { Moves = [], Layers = [] }, ["桌面在规划后发生变化，本次未执行。"]);

        // A click can enter an edit field in the same foreground HWND during a solve.
        // Read the input queue again immediately before sending the automatic layout.
        nint ProtectedInputRoot(IReadOnlyList<WindowSnapshot> desktop)
        {
            if (context is not { ExplicitArrange: false } || needed.Length == 0 || !automaticForeground.HasValue) return 0;
            var live = ForegroundReading.Read(desktop.FirstOrDefault(w => w.IsForeground));
            if (initialReading is { } initial)
                return initial.AllowsInitialPlacement(live) ? 0 : automaticForeground.Value;
            // 后台求解时用户又开始操作，普通浏览器未必暴露系统插入点。只保护当前视图，仍允许邻窗自己改善。
            if (workspaceReading is { } start && (live.Root != start.Root || live.LastInput != start.LastInput))
                return automaticForeground.Value;
            return live.Caret ? live.Root : 0;
        }
        var caret = ProtectedInputRoot(current);
        if (context != null && (!IntentLayoutPlanner.TextViewAcceptsPlan(current, plan, context, caret) ||
            plan.Layers.Count > 0 && !IntentLayoutPlanner.TextViewAcceptsPlan(current, plan with { Moves = [] }, context, caret)))
            return new(plan with { Moves = [], Layers = [] }, ["已保留当前文字窗口的阅读位置。"])
                { Interrupted = caret != 0 && context.WindowHints?.Any(h => h.Handle == caret && h.TextFocus) != true };

        if (plan.Scene is { } scene && (!scene.Allows(current, plan) ||
            plan.Layers.Count > 0 && !scene.Allows(current, plan with { Moves = [] })))
            return new(plan with { Moves = [], Layers = [] }, ["当前参照窗的可见区域已变化，本轮保留现场。"]);
        var targetRects = plan.Moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        var notes = ApplyLayers(plan.Layers, targetRects, automaticForeground).ToList();
        var afterLayers = plan.Layers.Count == 0 ? current : Capture();
        var failed = plan.Layers.Where(l => !WindowLayerSafety.CanReorder(l.FrontToBack, afterLayers, targetRects) || !WindowLayerSafety.MatchesOrder(l, afterLayers)).ToArray();
        var failedHandles = failed.SelectMany(l => l.FrontToBack.Select(w => w.Handle)).ToHashSet();
        var accepted = plan with
        {
            Moves = plan.Moves.Where(m => !failedHandles.Contains(m.Window.Handle)).ToArray(),
            Layers = plan.Layers.Except(failed).ToArray(),
        };
        if (failed.Length != 0) notes.Add("指定层级未实现，未套用依赖该层级的窗口位置。");
        var afterCaret = plan.Layers.Count > 0 || initialReading.HasValue || workspaceReading.HasValue ? ProtectedInputRoot(afterLayers) : caret;
        bool readingChanged = false;
        if (context != null && (failed.Length > 0 || afterCaret != caret) &&
            !IntentLayoutPlanner.TextViewAcceptsPlan(afterLayers, accepted with { Layers = [] }, context, afterCaret))
        {
            accepted = accepted with { Moves = [] };
            readingChanged = afterCaret != 0 && afterCaret != caret;
            notes.Add("层级变化后已保留当前文字窗口的阅读位置。");
        }
        if (failed.Length > 0 && accepted.Scene?.Allows(afterLayers, accepted with { Layers = [] }) == false)
        {
            accepted = accepted with { Moves = [] };
            notes.Add("层级未完全实现，已保留后台参照窗的可见区域。");
        }
        bool interrupted = InteractionChanged(automaticForeground);
        if (interrupted)
        {
            accepted = accepted with { Moves = [] };
            notes.Add("检测到新的手动调整，未覆盖窗口位置。");
        }
        var failures = SubmitMoves(accepted.Moves, notes, gestureWindow, out var firstSubmit, out var gestureSubmit);
        if (failures.Count > 0)
        {
            var refused = failures.Select(f => f.Move.Window.Handle).ToHashSet();
            accepted = accepted with { Moves = accepted.Moves.Where(m => !refused.Contains(m.Window.Handle)).ToArray() };
        }
        return new(accepted, notes.ToArray()) { Failures = failures, Interrupted = interrupted || readingChanged,
            FirstGeometrySubmittedTick = firstSubmit, GestureWindowSubmittedTick = gestureSubmit };
    }

    public string[] ApplyLayers(IReadOnlyList<WindowLayerOrder> orders, IReadOnlyDictionary<nint, RectI>? targetRects = null) =>
        ApplyLayers(orders, targetRects, null);

    private string[] ApplyLayers(IReadOnlyList<WindowLayerOrder> orders, IReadOnlyDictionary<nint, RectI>? targetRects, nint? automaticForeground)
    {
        var notes = new List<string>();
        foreach (var order in orders)
        {
            var current = Capture();
            if (InteractionChanged(automaticForeground) || !WindowLayerSafety.CanReorder(order.FrontToBack, current, targetRects))
            {
                notes.Add("层级未执行：窗口状态改变或被其他窗口隔开。");
                continue;
            }
            var originalOrder = current.Where(w => order.FrontToBack.Any(d => d.Handle == w.Handle)).OrderBy(w => w.ZOrder).ToArray();
            InvalidateCache();
            var succeeded = SetOrder(order.FrontToBack, automaticForeground);
            var actual = Capture();
            if (!succeeded || !WindowLayerSafety.MatchesOrder(order, actual))
            {
                if (!InteractionChanged(automaticForeground) && WindowLayerSafety.CanReorder(originalOrder, actual))
                    _ = SetOrder(originalOrder, automaticForeground);
                notes.Add("层级调用未全部成功，已在安全条件允许时恢复原顺序。");
            }
            else notes.Add("组内层级已核对，未设置永久置顶。");
        }
        return notes.ToArray();
    }

    private static bool SetOrder(IReadOnlyList<WindowSnapshot> order, nint? automaticForeground)
    {
        // Following members move behind their desired predecessor, preserving external neighbors.
        // No HWND_TOPMOST, foreground activation or cross-monitor promotion is used.
        for (var i = 1; i < order.Count; i++)
        {
            var window = order[i]; var predecessor = order[i - 1];
            if (InteractionChanged(automaticForeground) || !StillNormal(window) || !StillNormal(predecessor)) return false;
            AutomationGuard.Mark(window.Handle);
            if (!NativeMethods.SetWindowPos(window.Handle, predecessor.Handle, 0, 0, 0, 0,
                0x0001 | 0x0002 | NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder)) return false;
        }
        return true;
    }

    private static bool StillNormal(WindowSnapshot window)
    {
        NativeMethods.GetWindowThreadProcessId(window.Handle, out var pid);
        return pid == window.ProcessId && NativeMethods.IsWindowVisible(window.Handle) && NativeMethods.IsWindowEnabled(window.Handle) &&
            !NativeMethods.IsIconic(window.Handle) && !NativeMethods.IsHungAppWindow(window.Handle) &&
            (NativeMethods.GetWindowLongPtr(window.Handle, NativeMethods.GwlExStyle).ToInt64() & 8) == 0;
    }
}
