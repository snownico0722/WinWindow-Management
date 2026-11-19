namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    private static bool OffersVerticalFill(WindowSnapshot window, RectI original, TaskLayoutContext context)
    {
        if (!context.PreferReversibleVerticalFill || !context.Preferences.AllowUsefulResize ||
            !window.IsResizable || window.IsTopmost || original.IsEmpty || window.WorkArea.IsEmpty) return false;
        var area = window.WorkArea;
        var radius = 40 * window.Dpi / 96.0;
        var top = original.Top - area.Top; var bottom = area.Bottom - original.Bottom;
        return top >= 0 && bottom >= 0 && top + bottom > 0 && top <= radius && bottom <= radius &&
            original.Height >= area.Height * .86 && area.Height / (double)original.Height <= 1.12;
    }

    private static void AddVerticalFillCandidates(List<Candidate> candidates, VisibleWindow[] windows,
        RectI[] original, TaskLayoutContext context)
    {
        var all = original.ToArray();
        var count = 0;
        var order = Enumerable.Range(0, windows.Length).ToArray();
        for (var i = 0; i < windows.Length; i++)
        {
            var window = windows[i].Window;
            if (!OffersVerticalFill(window, original[i], context)) continue;
            var target = original[i] with { Y = window.WorkArea.Top, Height = window.WorkArea.Height };
            var one = original.ToArray(); one[i] = target; all[i] = target; count++;
            candidates.Add(new("task-vertical-fill", one, order));
        }
        if (count > 1) candidates.Add(new("task-vertical-fill", all, order));
    }

    private static double VerticalFillPreferenceCost(WindowSnapshot window, RectI original, RectI target,
        TaskLayoutContext context)
    {
        if (!OffersVerticalFill(window, original, context)) return 0;
        var remaining = Math.Abs(target.Top - window.WorkArea.Top) + Math.Abs(window.WorkArea.Bottom - target.Bottom);
        // A declared preference participates in the same task score, never a forced second pass.
        return .18 * Math.Min(1, remaining / Math.Max(1, 16 * window.Dpi / 96.0));
    }
}
