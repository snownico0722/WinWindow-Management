namespace NeatWin.Core;

public static class WindowLayerSafety
{
    // Reorder a normal-window group on the same monitor. Windows on other monitors or completely
    // disjoint normal windows on the same monitor commute safely; overlapping external or topmost windows do not.
    public static bool CanReorder(IReadOnlyList<WindowSnapshot> desired, IReadOnlyList<WindowSnapshot> desktop,
        IReadOnlyDictionary<nint, RectI>? targetRects = null)
    {
        if (desired.Count < 2 || desired.Any(w => !w.IsManageable || w.IsTopmost) ||
            desired.Select(w => w.MonitorHandle).Distinct().Count() != 1 ||
            desired.Select(w => w.Handle).Distinct().Count() != desired.Count) return false;
        var monitor = desired[0].MonitorHandle;
        var current = desktop.OrderBy(w => w.ZOrder).ToArray();
        var handles = desired.Select(w => w.Handle).ToHashSet();
        var slots = current.Select((w, i) => (w, i)).Where(p => handles.Contains(p.w.Handle)).ToArray();
        if (slots.Length != desired.Count) return false;
        if (!desired.All(w => slots.Any(p => p.w.Handle == w.Handle && p.w.ProcessId == w.ProcessId &&
            p.w.MonitorHandle == w.MonitorHandle && p.w.WorkArea == w.WorkArea && p.w.IsManageable && !p.w.IsTopmost)))
            return false;

        if (current.Any(w => w.Owner != 0 && (handles.Contains(w.Handle) || handles.Contains(w.Owner))))
        {
            // 按候选顺序先推演一次层级。所有者窗口若盖住自己的对话框，用户可能看得到主窗却无法继续操作。
            var ranks = current.Select((w, i) => (w.Handle, i)).ToDictionary(p => p.Handle, p => p.i);
            for (int i = 0; i < desired.Count; i++) ranks[desired[i].Handle] = slots[i].i;
            foreach (var window in current)
                if (window.Owner != 0 && (handles.Contains(window.Handle) || handles.Contains(window.Owner)) &&
                    ranks.TryGetValue(window.Owner, out var ownerRank) && ranks[window.Handle] > ownerRank)
                    return false;
        }

        for (int idx = slots[0].i + 1; idx < slots[^1].i; idx++)
        {
            var interleaved = current[idx];
            if (handles.Contains(interleaved.Handle)) continue;
            // Never jump across a topmost surface or an unmanageable dialog/overlay on the same monitor.
            if (interleaved.MonitorHandle == monitor && (interleaved.IsTopmost || !interleaved.IsManageable)) return false;
            bool overlapsAny = slots.Any(s =>
            {
                if (!s.w.VisualRect.Intersect(interleaved.VisualRect).IsEmpty) return true;
                return targetRects != null && targetRects.TryGetValue(s.w.Handle, out var target) &&
                    !target.Intersect(interleaved.VisualRect).IsEmpty;
            });
            if (overlapsAny) return false;
        }
        return true;
    }

    public static bool MatchesOrder(WindowLayerOrder order, IReadOnlyList<WindowSnapshot> desktop) =>
        desktop.Where(w => order.FrontToBack.Any(d => d.Handle == w.Handle)).OrderBy(w => w.ZOrder)
            .Select(w => w.Handle).SequenceEqual(order.FrontToBack.Select(w => w.Handle));
}
