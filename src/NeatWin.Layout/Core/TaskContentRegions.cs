namespace NeatWin.Core;

// A declared region in window-relative coordinates, not semantic recognition or a screenshot.
// If initially below the requested floor, retain its existing visibility while improving it.
public sealed record TaskContentRegion(double X, double Y, double Width, double Height,
    double MinimumVisible = 1)
{
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && double.IsFinite(MinimumVisible) && X >= 0 && Y >= 0 &&
        Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1 && MinimumVisible is >= 0 and <= 1;

    internal RectI Project(RectI window) => RectI.FromEdges(
        window.X + (int)Math.Round(X * window.Width), window.Y + (int)Math.Round(Y * window.Height),
        window.X + (int)Math.Round((X + Width) * window.Width),
        window.Y + (int)Math.Round((Y + Height) * window.Height));
}

internal static partial class IntentLayoutPlanner
{
    private static readonly TaskContentRegion[] EdgeRegions =
        [new(0, 0, .12, 1), new(.88, 0, .12, 1), new(0, 0, 1, .12), new(0, .88, 1, .12)];

    private static TaskContentRegion[] ProtectedRegions(WindowSnapshot window, TaskLayoutContext context)
    {
        var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == window.Handle);
        var edges = context.Preferences.ProtectWindowEdges || hint?.ProtectPeriphery == true;
        var custom = hint?.ProtectedRegions?.Where(r => r is not null && r.IsValid).Take(16).ToArray() ?? [];
        return edges ? EdgeRegions.Concat(custom).ToArray() : custom;
    }

    private static bool HasRegionProtection(TaskLayoutContext context) => context.Preferences.ProtectWindowEdges ||
        context.WindowHints?.Any(h => h.ProtectPeriphery || h.ProtectedRegions?.Length > 0) == true;

    internal static bool DesktopRegionsPreserved(IReadOnlyList<WindowSnapshot> before,
        IReadOnlyList<WindowSnapshot> after, TaskLayoutContext context)
    {
        if (!HasRegionProtection(context)) return true;
        var next = after.ToDictionary(w => w.Handle);
        foreach (var w in before)
        {
            var regions = ProtectedRegions(w, context);
            if (regions.Length == 0) continue;
            if (!next.TryGetValue(w.Handle, out var target)) return false;
            var blockersBefore = before.Where(b => b.MonitorHandle == w.MonitorHandle && b.ZOrder < w.ZOrder).Select(b => b.VisualRect);
            var blockersAfter = after.Where(b => b.MonitorHandle == target.MonitorHandle && b.ZOrder < target.ZOrder).Select(b => b.VisualRect);
            if (!RegionsPreserved(w.VisualRect, target.VisualRect, blockersBefore, blockersAfter, w.WorkArea, regions)) return false;
        }
        return true;
    }

    internal static double RegionVisibility(RectI window, TaskContentRegion region,
        IEnumerable<RectI> blockers, RectI area)
    {
        var r = region.Project(window);
        if (r.IsEmpty) return 0;
        var visible = r.Intersect(area);
        if (visible.IsEmpty) return 0;
        var pieces = new List<RectI> { visible };
        foreach (var blocker in blockers) pieces = RectRegion.Subtract(pieces, blocker, maxFragments: 512);
        return pieces.Sum(p => p.Area) / (double)r.Area;
    }

    internal static bool RegionsPreserved(RectI original, RectI target,
        IEnumerable<RectI> originalBlockers, IEnumerable<RectI> targetBlockers,
        RectI area, IReadOnlyList<TaskContentRegion> regions)
    {
        var before = originalBlockers.ToArray(); var after = targetBlockers.ToArray();
        foreach (var region in regions)
        {
            if (!region.IsValid) continue;
            var floor = Math.Min(region.MinimumVisible, RegionVisibility(original, region, before, area));
            // Half a percentage point for rasterization; not a tradeable utility weight.
            if (RegionVisibility(target, region, after, area) + .005 < floor) return false;
        }
        return true;
    }
}
