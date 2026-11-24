namespace NeatWin.Core;

/// <summary>Identity and geometry contract shared by settling and undo planning.</summary>
public static class WindowStateRules
{
    // 普通窗口也可能已经占满工作区。逐边检查可排除半屏、跨屏和偏向一侧的大窗，任务栏不计入可用范围。
    internal static bool FillsWorkArea(RectI frame, RectI area, uint dpi)
    {
        if (area.IsEmpty || frame.IsEmpty) return false;
        double scale = dpi > 0 ? dpi / 96d : 1;
        bool NearEdge(long inset, int span) =>
            inset >= -4 * scale && inset <= Math.Min(32 * scale, .025 * span);
        return NearEdge((long)frame.Left - area.Left, area.Width) && NearEdge((long)area.Right - frame.Right, area.Width) &&
            NearEdge((long)frame.Top - area.Top, area.Height) && NearEdge((long)area.Bottom - frame.Bottom, area.Height);
    }

    public static bool Matches(WindowSnapshot expected, WindowSnapshot actual, RectI target, int tolerance = 3) =>
        actual.Handle == expected.Handle && actual.ProcessId == expected.ProcessId && actual.Owner == expected.Owner &&
        actual.MonitorHandle == expected.MonitorHandle && actual.WorkArea == expected.WorkArea &&
        actual.Dpi == expected.Dpi && actual.IsTopmost == expected.IsTopmost &&
        actual.IsManageable == expected.IsManageable && actual.IsResizable == expected.IsResizable &&
        actual.FrameInsets == expected.FrameInsets && Near(actual.VisualRect, target, tolerance);

    public static bool Near(RectI first, RectI second, int tolerance = 3) =>
        Math.Abs((long)first.X - second.X) <= tolerance && Math.Abs((long)first.Y - second.Y) <= tolerance &&
        Math.Abs((long)first.Width - second.Width) <= tolerance && Math.Abs((long)first.Height - second.Height) <= tolerance;
}

/// <summary>Short-lived placement constraints for live window instances.</summary>
internal sealed class WindowPlacementFeedback
{
    private readonly Dictionary<nint, (WindowSnapshot Window, long Expires)> _blocked = [];
    private const int LifetimeMs = 15_000;

    internal void Forget(nint handle) => _blocked.Remove(handle);

    internal bool BlockUnchanged(TidyMove move, WindowSnapshot observed, long now)
    {
        if (!WindowStateRules.Matches(move.Window, observed, move.Window.VisualRect) ||
            WindowStateRules.Near(observed.VisualRect, move.TargetVisualRect)) return false;
        if (_blocked.TryGetValue(observed.Handle, out var entry) && now < entry.Expires &&
            WindowStateRules.Matches(entry.Window, observed, entry.Window.VisualRect)) return false;
        _blocked[observed.Handle] = (observed, now + LifetimeMs);
        return true;
    }

    internal TaskWindowHint[] Bind(IReadOnlyList<WindowSnapshot> desktop, TaskWindowHint[] hints, long now)
    {
        if (_blocked.Count == 0) return hints;
        var byHandle = desktop.ToDictionary(w => w.Handle);
        var result = hints.ToList();
        foreach (var (handle, entry) in _blocked.ToArray())
        {
            if (now >= entry.Expires || !byHandle.TryGetValue(handle, out var current) ||
                !WindowStateRules.Matches(entry.Window, current, entry.Window.VisualRect))
            {
                Forget(handle);
                continue;
            }
            int index = result.FindIndex(h => h.Handle == handle);
            if (index < 0) result.Add(new(handle, Anchor: true));
            else result[index] = result[index] with { Anchor = true };
        }
        return result.ToArray();
    }
}

/// <summary>Build an undo only from still-matching windows; never undo a new DPI/pin state.</summary>
public static class LayoutUndoPlanner
{
    public static IntentLayoutPlan Create(IntentLayoutPlan applied, IReadOnlyList<WindowSnapshot> desktop)
    {
        var current = desktop.ToDictionary(w => w.Handle);
        var targets = applied.Moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        bool Restorable(WindowSnapshot old) => current.TryGetValue(old.Handle, out var now) && now.IsManageable &&
            WindowStateRules.Matches(old, now, targets.GetValueOrDefault(old.Handle, old.VisualRect));
        var reverse = applied.Moves.Where(m => Restorable(m.Window))
            .Select(m => new TidyMove(current[m.Window.Handle], m.Window.VisualRect)).ToList();
        var layers = applied.Layers.Where(l => WindowLayerSafety.MatchesOrder(l, desktop) &&
                WindowLayerSafety.CanReorder(l.FrontToBack, desktop) && l.FrontToBack.All(Restorable))
            .Select(l => new WindowLayerOrder(l.FrontToBack.OrderBy(w => w.ZOrder)
                .Select(w => current[w.Handle]).ToArray())).ToArray();
        var restored = layers.SelectMany(l => l.FrontToBack.Select(w => w.Handle)).ToHashSet();
        var blocked = applied.Layers.SelectMany(l => l.FrontToBack.Select(w => w.Handle))
            .Where(h => !restored.Contains(h)).ToHashSet();
        reverse.RemoveAll(m => blocked.Contains(m.Window.Handle));
        return new(reverse, layers, []);
    }
}
