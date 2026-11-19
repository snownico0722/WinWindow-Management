using System.Runtime.InteropServices;
using NeatWin.Core;

namespace NeatWin.Windows;

public sealed record LayoutApplication(IntentLayoutPlan Plan, string[] Notes);

public sealed partial class WindowManager
{
    public LayoutApplication ApplyLayout(IntentLayoutPlan plan)
    {
        var current = Capture();
        var needed = plan.Moves.Select(m => m.Window).Concat(plan.Layers.SelectMany(l => l.FrontToBack))
            .GroupBy(w => w.Handle).Select(g => g.First()).ToArray();
        if (NativeWindowActivity.IsMoving || needed.Any(w => !current.Any(c => c.Handle == w.Handle &&
            c.IsManageable && WindowStateRules.Matches(w, c, w.VisualRect))))
            return new(plan with { Moves = [], Layers = [] }, ["桌面在规划后发生变化，本次未执行。"]);

        var notes = ApplyLayers(plan.Layers).ToList();
        var afterLayers = plan.Layers.Count == 0 ? current : Capture();
        var failed = plan.Layers.Where(l => !WindowLayerSafety.CanReorder(l.FrontToBack, afterLayers) || !WindowLayerSafety.MatchesOrder(l, afterLayers)).ToArray();
        var failedHandles = failed.SelectMany(l => l.FrontToBack.Select(w => w.Handle)).ToHashSet();
        var accepted = plan with
        {
            Moves = plan.Moves.Where(m => !failedHandles.Contains(m.Window.Handle)).ToArray(),
            Layers = plan.Layers.Except(failed).ToArray(),
        };
        if (failed.Length != 0) notes.Add("指定层级未实现，未套用依赖该层级的窗口位置。");
        if (NativeWindowActivity.IsMoving)
        {
            accepted = accepted with { Moves = [] };
            notes.Add("检测到新的手动调整，未覆盖窗口位置。");
        }
        Apply(accepted.Moves);
        return new(accepted, notes.ToArray());
    }

    public string[] ApplyLayers(IReadOnlyList<WindowLayerOrder> orders)
    {
        var notes = new List<string>();
        foreach (var order in orders)
        {
            var current = Capture();
            if (NativeWindowActivity.IsMoving || !WindowLayerSafety.CanReorder(order.FrontToBack, current))
            {
                notes.Add("层级未执行：窗口状态改变或被其他窗口隔开。");
                continue;
            }
            var originalOrder = current.Where(w => order.FrontToBack.Any(d => d.Handle == w.Handle)).OrderBy(w => w.ZOrder).ToArray();
            var succeeded = SetOrder(order.FrontToBack);
            var actual = Capture();
            if (!succeeded || !WindowLayerSafety.MatchesOrder(order, actual))
            {
                if (!NativeWindowActivity.IsMoving && WindowLayerSafety.CanReorder(originalOrder, actual))
                    _ = SetOrder(originalOrder);
                notes.Add("层级调用未全部成功，已在安全条件允许时恢复原顺序。");
            }
            else notes.Add("组内层级已核对，未设置永久置顶。");
        }
        return notes.ToArray();
    }

    private static bool SetOrder(IReadOnlyList<WindowSnapshot> order)
    {
        // Following members move behind their desired predecessor, preserving external neighbors.
        // No HWND_TOPMOST, foreground activation or cross-monitor promotion is used.
        for (var i = 1; i < order.Count; i++)
        {
            var window = order[i]; var predecessor = order[i - 1];
            if (NativeWindowActivity.IsMoving || !StillNormal(window) || !StillNormal(predecessor)) return false;
            AutomationGuard.Mark(window.Handle);
            if (!NativeMethods.SetWindowPos(window.Handle, predecessor.Handle, 0, 0, 0, 0,
                0x0001 | 0x0002 | NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder)) return false;
        }
        return true;
    }

    private static bool StillNormal(WindowSnapshot window)
    {
        NativeMethods.GetWindowThreadProcessId(window.Handle, out var pid);
        return pid == window.ProcessId && NativeMethods.IsWindowVisible(window.Handle) &&
            !NativeMethods.IsIconic(window.Handle) && !IsHungAppWindow(window.Handle) &&
            (NativeMethods.GetWindowLongPtr(window.Handle, NativeMethods.GwlExStyle).ToInt64() & 8) == 0;
    }
    private static bool Near(RectI a, RectI b) => Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 &&
        Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(nint hwnd);
}
