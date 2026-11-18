namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    // Search membership is broader than the old hard radius. Evidence fades to zero before
    // membership disappears, so the boundary is not itself a new task label. These are
    // engineering transition bands, not learned psychological probabilities.
    internal static double TaskAffinity(WindowSnapshot first, WindowSnapshot second, TaskLayoutContext context)
    {
        if (first.MonitorHandle != second.MonitorHandle || first.WorkArea != second.WorkArea) return 0;
        if (context.PairHints?.Any(h => h.Relation != TaskRelation.Automatic &&
            ((h.First == first.Handle && h.Second == second.Handle) ||
             (h.First == second.Handle && h.Second == first.Handle))) == true) return 1;
        var a = first.VisualRect; var b = second.VisualRect;
        if (a.Intersect(b).Area > 0) return 1;
        var scale = Math.Max(1, Math.Min(first.Dpi, second.Dpi)) / 96.0;
        var area = first.WorkArea;
        var radius = Math.Clamp(Math.Min(area.Width, area.Height) / scale * .18, 100, 260) * scale;
        // Retain full support inside the established reach; fade only through its outer band.
        double DistanceWeight(double gap) => 1 - SmoothStep((gap / radius - 1) / .35);
        double AxisWeight(double overlap) => SmoothStep((overlap - .15) / .20);
        return Math.Max(AxisWeight(a.VerticalOverlapRatio(b)) * DistanceWeight(RectI.IntervalGap(a.Left, a.Right, b.Left, b.Right)),
            AxisWeight(a.HorizontalOverlapRatio(b)) * DistanceWeight(RectI.IntervalGap(a.Top, a.Bottom, b.Top, b.Bottom)));
    }

    private static double SmoothStep(double value)
    {
        var x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }

    // Solve the local translation constraints exactly, in DIP space. This is one proposal
    // under the task objective, not the complete Smart algorithm and not a resize prohibition.
    // Keep Classic and the legacy edge-relaxation implementation unchanged.
    internal static IReadOnlyList<TidyMove> LogicalLocalPlan(VisibleWindow[] group, TidyOptions options)
    {
        var area = group[0].Window.WorkArea;
        var scale = Math.Max(1, group[0].Window.Dpi) / 96.0;
        RectI Logical(RectI r) => new((int)Math.Round((r.X - area.X) / scale),
            (int)Math.Round((r.Y - area.Y) / scale), Math.Max(1, (int)Math.Round(r.Width / scale)),
            Math.Max(1, (int)Math.Round(r.Height / scale)));
        var normalized = group.Select(v => v with { Window = v.Window with
            { VisualRect = Logical(v.Window.VisualRect), OuterRect = Logical(v.Window.OuterRect),
              WorkArea = Logical(area), Dpi = 96 } }).ToArray();
        var byHandle = group.ToDictionary(v => v.Window.Handle);
        var oldLogical = normalized.ToDictionary(v => v.Window.Handle, v => v.Window.VisualRect);
        return ComfortSmartTidySolver.CreatePlan(normalized, options).Select(m =>
        {
            var w = byHandle[m.Window.Handle].Window;
            var old = oldLogical[w.Handle]; var next = m.TargetVisualRect; var r = w.VisualRect;
            return new TidyMove(w, new(r.X + (int)Math.Round((next.X - old.X) * scale),
                r.Y + (int)Math.Round((next.Y - old.Y) * scale),
                r.Width + (int)Math.Round((next.Width - old.Width) * scale),
                r.Height + (int)Math.Round((next.Height - old.Height) * scale)));
        }).ToArray();
    }
}
