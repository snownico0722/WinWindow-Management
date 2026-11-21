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

    // Preserve the actual on-screen text, including its wrap width and all previously
    // unobstructed parts. Equal area elsewhere cannot replace the line the user was reading.
    internal static bool TextViewAcceptsPlan(IReadOnlyList<WindowSnapshot> desktop, IntentLayoutPlan plan,
        TaskLayoutContext context, nint foregroundCaret = default)
    {
        if (context.ExplicitArrange || foregroundCaret == 0 && context.WindowHints?.Any(h => h.TextFocus) != true) return true;
        var window = desktop.FirstOrDefault(w => context.HoldsTextView(w) || w.Handle == foregroundCaret && w.IsForeground &&
            w.IsManageable && !w.IsTopmost && w.SurfaceState != "fullscreen");
        if (window == null) return true;
        var after = TaskLayoutSession.Project(desktop, plan);
        var target = after.First(w => w.Handle == window.Handle);
        return TextViewAcceptsFrame(window, target.VisualRect, context,
            desktop.Where(w => w.ZOrder < window.ZOrder).Select(w => w.VisualRect),
            after.Where(w => w.ZOrder < target.ZOrder).Select(w => w.VisualRect));
    }

    private static bool TextViewAcceptsFrame(WindowSnapshot window, RectI target, TaskLayoutContext context,
        IEnumerable<RectI> oldCovers, IEnumerable<RectI> newCovers)
    {
        if (target != window.VisualRect && !(context.AllowGestureAssist && context.GestureAnchor == window.Handle && context.GestureTarget == target))
            return false;
        // For an authorized release, compare the target with neighbors held in their old
        // positions. The gesture authorizes moving this frame, not newly covering it.
        var view = target.Intersect(window.WorkArea);
        var prior = oldCovers.Where(r => !r.Intersect(view).IsEmpty).ToHashSet();
        foreach (var rect in newCovers)
        {
            var cut = rect.Intersect(view);
            if (cut.IsEmpty || prior.Contains(rect)) continue;
            var uncovered = new List<RectI> { cut };
            foreach (var old in prior)
            {
                uncovered = RectRegion.Subtract(uncovered, old);
                if (uncovered.Count == 0) break;
            }
            if (uncovered.Count > 0) return false;
        }
        return true;
    }

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
            var blockersBefore = before.Where(b => b.ZOrder < w.ZOrder).Select(b => b.VisualRect);
            var blockersAfter = after.Where(b => b.ZOrder < target.ZOrder).Select(b => b.VisualRect);
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
