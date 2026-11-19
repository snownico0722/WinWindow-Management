namespace NeatWin.Core;

/// <summary>Identity and geometry contract shared by settling and undo planning.</summary>
public static class WindowStateRules
{
    public static bool Matches(WindowSnapshot expected, WindowSnapshot actual, RectI target, int tolerance = 3) =>
        actual.Handle == expected.Handle && actual.ProcessId == expected.ProcessId &&
        actual.MonitorHandle == expected.MonitorHandle && actual.WorkArea == expected.WorkArea &&
        actual.Dpi == expected.Dpi && actual.IsTopmost == expected.IsTopmost &&
        actual.IsManageable == expected.IsManageable && actual.IsResizable == expected.IsResizable &&
        actual.FrameInsets == expected.FrameInsets && Near(actual.VisualRect, target, tolerance);

    public static bool Near(RectI first, RectI second, int tolerance = 3) =>
        Math.Abs((long)first.X - second.X) <= tolerance && Math.Abs((long)first.Y - second.Y) <= tolerance &&
        Math.Abs((long)first.Width - second.Width) <= tolerance && Math.Abs((long)first.Height - second.Height) <= tolerance;
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
