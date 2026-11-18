namespace NeatWin.Core;

public static class WindowLayerSafety
{
    // Reorder only a contiguous normal-window block. Never jump past a dialog, an unrelated
    // application, another monitor's interleaved window, or an always-on-top window.
    public static bool CanReorder(IReadOnlyList<WindowSnapshot> desired, IReadOnlyList<WindowSnapshot> desktop)
    {
        if (desired.Count < 2 || desired.Any(w => !w.IsManageable || w.IsTopmost) ||
            desired.Select(w => w.MonitorHandle).Distinct().Count() != 1 ||
            desired.Select(w => w.Handle).Distinct().Count() != desired.Count) return false;
        var current = desktop.OrderBy(w => w.ZOrder).ToArray();
        var handles = desired.Select(w => w.Handle).ToHashSet();
        var slots = current.Select((w, i) => (w, i)).Where(p => handles.Contains(p.w.Handle)).ToArray();
        if (slots.Length != desired.Count || slots[^1].i - slots[0].i + 1 != desired.Count) return false;
        return desired.All(w => slots.Any(p => p.w.Handle == w.Handle && p.w.ProcessId == w.ProcessId &&
            p.w.MonitorHandle == w.MonitorHandle && p.w.WorkArea == w.WorkArea && p.w.IsManageable && !p.w.IsTopmost));
    }

    public static bool MatchesOrder(WindowLayerOrder order, IReadOnlyList<WindowSnapshot> desktop) =>
        desktop.Where(w => order.FrontToBack.Any(d => d.Handle == w.Handle)).OrderBy(w => w.ZOrder)
            .Select(w => w.Handle).SequenceEqual(order.FrontToBack.Select(w => w.Handle));
}
