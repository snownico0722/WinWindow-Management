namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    private sealed record PairPreference(double? Overlap, int Order);

    // Order is -1 when the first window belongs on the left. Personal, pair-specific evidence
    // takes precedence over an installed pair rule, then over the media default.
    private static PairPreference? PairPreferenceFor(nint first, nint second, TaskLayoutContext context)
    {
        if (!context.ExplicitArrange || context.PairHints?.Any(h => h.Relation == TaskRelation.Alternating &&
            (h.First == first && h.Second == second || h.First == second && h.Second == first)) == true) return null;
        if (context.RulePairs is not { Length: > 0 } && context.Placement is not { Length: > 0 } &&
            context.WindowHints is not { Length: > 0 }) return null;
        var rule = context.RulePairs?.FirstOrDefault(p => p.Matches(first, second));
        var personal = context.Placement?.Where(p => p.Confidence >= .4 &&
            (p.Window == first && p.Partner == second || p.Window == second && p.Partner == first))
            .OrderByDescending(p => p.Confidence).FirstOrDefault();
        bool mediaPair = context.Focus is { } f && (f.Main == first && f.Companion == second || f.Main == second && f.Companion == first) &&
            context.WindowHints?.Any(h => !h.Anchor && h.MediaCompanion && (h.Handle == first || h.Handle == second)) == true;
        double? overlap = personal?.InnerOverlap ?? rule?.Overlap ??
            (mediaPair ? (context.Rules ?? CompiledLayoutRules.Default).MediaOverlap : null);
        int order = rule?.FirstSide switch { "left" => -1, "right" => 1, _ => 0 };
        if (rule?.Second == first) order = -order;
        if (personal != null && Math.Abs(personal.Order) >= .5)
            order = Math.Sign(personal.Order) * (personal.Window == first ? 1 : -1);
        return overlap != null || rule != null ? new(overlap, order) : null;
    }

    private static void AddPartialOverlap(List<Candidate> candidates, VisibleWindow[] windows, RectI[] seed,
        int main, int peer, RectI area, TaskLayoutContext context, int[] order)
    {
        var a = seed[main]; var b = seed[peer];
        if (a.Width + b.Width <= area.Width) return;
        var preference = PairPreferenceFor(windows[main].Window.Handle, windows[peer].Window.Handle, context);
        if (preference?.Overlap is not double target || target <= 0) return;
        int narrow = Math.Min(a.Width, b.Width);
        foreach (double ratio in new[] { Math.Min(target, (a.Width + b.Width - area.Width) / (double)narrow), target }.Distinct())
        {
            double scale = Math.Min(1, area.Width / (a.Width + b.Width - narrow * ratio));
            if (scale < 1 && (!context.Preferences.AllowUsefulResize || !windows[main].Window.IsResizable || !windows[peer].Window.IsResizable)) continue;
            int aw = (int)Math.Floor(a.Width * scale), bw = (int)Math.Floor(b.Width * scale);
            int shared = (int)Math.Round(Math.Min(aw, bw) * ratio);
            int span = aw + bw - shared;
            if (shared <= 0 || span > area.Width || Math.Max(a.Height, b.Height) > area.Height) continue;
            int y = Math.Clamp(a.Top, area.Top, area.Bottom - Math.Max(a.Height, b.Height));
            foreach (bool firstLeft in new[] { true, false })
            {
                int x = Math.Clamp((int)CenterX(a) - aw / 2 - (firstLeft ? 0 : bw - shared), area.Left, area.Right - span);
                var targets = seed.ToArray();
                targets[main] = new(firstLeft ? x : x + bw - shared, y, aw, a.Height);
                targets[peer] = new(firstLeft ? x + aw - shared : x, y, bw, b.Height);
                candidates.Add(new("working-overlap", targets, order));
            }
        }
    }

    private static double PairPlacementCost(RectI a, RectI b, RectI originalA, RectI originalB, RectI area,
        PairPreference preference)
    {
        double cost = preference.Order != 0 && Math.Sign(CenterX(a) - CenterX(b)) != preference.Order ? 1.8 : 0;
        if (preference.Overlap is not double desired || originalA.Width + originalB.Width <= area.Width) return cost;
        // Zero overlap is always available. Under horizontal pressure, compare the benefit of
        // preserving reading width against the cost of hiding part of the adjacent edge.
        double overlap = a.VerticalOverlapRatio(b) >= .5 ? a.HorizontalOverlapRatio(b) : 0;
        cost += 5 * Math.Abs(overlap - desired);
        return cost;
    }
}
