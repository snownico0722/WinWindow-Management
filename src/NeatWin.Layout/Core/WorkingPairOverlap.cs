namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    private sealed record PairPreference(double? Overlap, int Order);

    // Order is -1 when the first window belongs on the left. Personal, pair-specific evidence
    // takes precedence over an installed pair rule, then over the media default.
    private static PairPreference? PairPreferenceFor(nint first, nint second, TaskLayoutContext context)
    {
        if (!context.PairPlanningEnabled || context.PairHints?.Any(h => h.Relation == TaskRelation.Alternating &&
            (h.First == first && h.Second == second || h.First == second && h.Second == first)) == true) return null;
        if (context.RulePairs is not { Length: > 0 } && context.Placement is not { Length: > 0 } &&
            context.WindowHints is not { Length: > 0 }) return null;
        const double minConfidence = .45;
        var rule = context.RulePairs?.FirstOrDefault(p => p.Matches(first, second));
        var personal = context.Placement?.Where(p => p.Confidence >= minConfidence &&
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
        var floorA = context.MinimumSize(windows[main].Window.Handle);
        var floorB = context.MinimumSize(windows[peer].Window.Handle);
        int narrow = Math.Min(a.Width, b.Width);
        foreach (bool useBleed in new[] { false, true })
        {
            foreach (bool firstLeft in new[] { true, false })
            {
                var leftWin = firstLeft ? windows[main].Window : windows[peer].Window;
                var rightWin = firstLeft ? windows[peer].Window : windows[main].Window;
                int bleedLeft = useBleed ? BleedBudget(leftWin, context).Left : 0;
                int bleedRight = useBleed ? BleedBudget(rightWin, context).Right : 0;
                if (useBleed && bleedLeft == 0 && bleedRight == 0) continue;
                int effectiveAreaWidth = area.Width + bleedLeft + bleedRight;
                if (a.Width + b.Width <= effectiveAreaWidth) continue;

                foreach (double ratio in new[] { Math.Min(target, (a.Width + b.Width - effectiveAreaWidth) / (double)narrow), target }.Distinct())
                {
                    if (ratio <= 0) continue;
                    double scale = Math.Min(1, effectiveAreaWidth / (a.Width + b.Width - narrow * ratio));
                    if (scale < 1 && (!context.Preferences.AllowUsefulResize || !windows[main].Window.IsResizable || !windows[peer].Window.IsResizable)) continue;
                    int aw = (int)Math.Floor(a.Width * scale), bw = (int)Math.Floor(b.Width * scale);
                    if ((floorA.Width > 0 && aw < Math.Min(a.Width, floorA.Width)) ||
                        (floorB.Width > 0 && bw < Math.Min(b.Width, floorB.Width))) continue;
                    int shared = (int)Math.Round(Math.Min(aw, bw) * ratio);
                    int span = aw + bw - shared;
                    if (shared <= 0 || span > effectiveAreaWidth || Math.Max(a.Height, b.Height) > area.Height) continue;
                    int y = Math.Clamp(a.Top, area.Top, area.Bottom - Math.Max(a.Height, b.Height));
                    int minLeft = area.Left - bleedLeft;
                    int maxLeft = area.Right + bleedRight - span;
                    int x = Math.Clamp((int)CenterX(a) - aw / 2 - (firstLeft ? 0 : bw - shared), minLeft, Math.Max(minLeft, maxLeft));
                    var targets = seed.ToArray();
                    targets[main] = new(firstLeft ? x : x + bw - shared, y, aw, a.Height);
                    targets[peer] = new(firstLeft ? x + aw - shared : x, y, bw, b.Height);
                    EmitPairAndAuxCandidates("working-overlap", candidates, windows, targets, main, peer, area, context, order);
                }
            }
        }
    }

    private static void EmitPairAndAuxCandidates(string kind, List<Candidate> candidates, VisibleWindow[] windows,
        RectI[] baseTargets, int main, int peer, RectI area, TaskLayoutContext context, int[] naturalOrder)
    {
        var orders = new List<int[]> { naturalOrder };
        if (main != 0 || peer != 1)
        {
            var promoted = new[] { main, peer }.Concat(Enumerable.Range(0, windows.Length).Where(i => i != main && i != peer)).ToArray();
            if (!promoted.SequenceEqual(naturalOrder)) orders.Add(promoted);
        }
        foreach (var ord in orders) candidates.Add(new(kind, baseTargets, ord));

        // On 3..6 window desktops, keep auxiliary windows' top title-bar access uncovered so the
        // primary pair is not vetoed by TaskSafe for burying a third window underneath.
        if (windows.Length is < 3 or > 6) return;
        double dpiScale = Math.Max(1, windows[main].Window.Dpi) / 96.0;
        int peekBand = (int)Math.Ceiling(36 * dpiScale);
        if (area.Height <= peekBand + 240) return;
        var tucked = baseTargets.ToArray();
        var auxIndices = Enumerable.Range(0, windows.Length).Where(i => i != main && i != peer).ToArray();
        int pairTop = Math.Max(area.Top + peekBand, Math.Min(tucked[main].Top, tucked[peer].Top));
        bool fitsPair = true;
        foreach (int idx in new[] { main, peer })
        {
            var r = tucked[idx];
            int maxH = area.Bottom - pairTop;
            int h = r.Height;
            if (h > maxH)
            {
                var floor = context.MinimumSize(windows[idx].Window.Handle);
                if (!windows[idx].Window.IsResizable || !context.Preferences.AllowUsefulResize ||
                    (floor.Height > 0 && maxH < Math.Min(r.Height, floor.Height)))
                { fitsPair = false; break; }
                h = maxH;
            }
            int top = Math.Clamp(Math.Max(pairTop, r.Top), pairTop, Math.Max(pairTop, area.Bottom - h));
            tucked[idx] = r with { Y = top, Height = h };
        }
        if (!fitsPair) return;
        int slotWidth = Math.Max(160, area.Width / Math.Max(1, auxIndices.Length));
        for (int k = 0; k < auxIndices.Length; k++)
        {
            int idx = auxIndices[k];
            var orig = tucked[idx];
            int w = Math.Min(orig.Width, area.Width);
            if (!windows[idx].Window.IsResizable || !context.Preferences.AllowUsefulResize) w = orig.Width;
            int h = Math.Min(orig.Height, area.Height);
            if (!windows[idx].Window.IsResizable || !context.Preferences.AllowUsefulResize) h = orig.Height;
            if (w > area.Width || h > area.Height) continue;
            int targetX = Math.Clamp(area.Left + k * slotWidth, area.Left, Math.Max(area.Left, area.Right - w));
            tucked[idx] = new(targetX, area.Top, w, h);
        }
        foreach (var ord in orders) candidates.Add(new(kind, tucked, ord));
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
