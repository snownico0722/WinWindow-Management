namespace NeatWin.Core;

internal enum AttentionRole { Main, Reference, Return, Background }

internal sealed record AttentionTarget(AttentionRole Role, int Width, int Height, int AccessWidth, bool Movable, bool Resizable, bool LearnedHeight = false, bool LearnedWidth = false);

// Frozen at the beginning of a solve. Dimensions are pixels in the window's current work area.
internal static class AttentionLayout
{
    internal static RectI WorkingArea(WindowSnapshot window, AttentionTarget goal)
    {
        var rect = window.VisualRect;
        var area = window.WorkArea;
        int width = Math.Min(area.Width, Math.Max(rect.Width, goal.Width));
        int height = Math.Min(area.Height, Math.Max(rect.Height, goal.Height));
        return new(Math.Clamp(rect.Left + (rect.Width - width) / 2, area.Left, area.Right - width),
            Math.Clamp(rect.Top + (rect.Height - height) / 2, area.Top, area.Bottom - height), width, height);
    }

    internal static AttentionTarget[]? Targets(VisibleWindow[] group, TaskLayoutContext context, SmartTidyStrength strength)
    {
        if (context.Attention is null || context.Focus is not { Main: not 0 } focus ||
            !group.Any(v => v.Window.Handle == focus.Main) ||
            group.Any(v => v.Window.VisualRect.IsEmpty || v.Window.WorkArea.IsEmpty)) return null;
        var profile = context.Preferences;
        double comfort = strength switch { SmartTidyStrength.Gentle => .65, SmartTidyStrength.Balanced => .84, _ => 1 };
        var targets = group.Select(v =>
        {
            var w = v.Window;
            var a = w.VisualRect;
            var role = w.Handle == focus.Main ? AttentionRole.Main :
                w.Handle == focus.Companion ? (focus.Evidence == "handoff" ? AttentionRole.Return : AttentionRole.Reference) :
                context.HasRecentInput(w.Handle) ? AttentionRole.Reference : AttentionRole.Background;
            if (role == AttentionRole.Reference && context.PairHints?.Any(h => h.Relation == TaskRelation.Alternating &&
                (h.First == w.Handle && h.Second == focus.Main || h.Second == w.Handle && h.First == focus.Main)) == true)
                role = AttentionRole.Return;
            var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle);
            bool movable = context.GestureAnchor != w.Handle && hint?.Anchor != true;
            bool resize = movable && w.IsResizable && profile.AllowUsefulResize && context.VideoHint?.WindowHandle != w.Handle &&
                !(context.RecentGesture is { } g && !context.ExplicitArrange && g.WindowHandle == w.Handle &&
                  (g.StartRect.Width != g.EndRect.Width || g.StartRect.Height != g.EndRect.Height));
            double scale = w.Dpi > 0 ? w.Dpi / 96d : 1;
            var preference = context.Placement?.FirstOrDefault(p => p.Window == w.Handle && p.Confidence >= .5);
            double width = hint?.UsefulWidthDip is > 0 and <= 4000 ? hint.UsefulWidthDip.Value * scale :
                preference?.Width is > 0 and <= 1 ? preference.Width * w.WorkArea.Width : profile.ComfortableWidthDip * scale * comfort;
            bool learnedHeight = preference?.Height is > 0 and <= 1 && hint?.UsefulHeightDip is not (> 0 and <= 3000);
            double height = hint?.UsefulHeightDip is > 0 and <= 3000 ? hint.UsefulHeightDip.Value * scale :
                learnedHeight ? preference!.Height!.Value * w.WorkArea.Height : profile.ComfortableHeightDip * scale * comfort;
            var floor = context.MinimumSize(w.Handle);
            bool content = resize && role is AttentionRole.Main or AttentionRole.Reference;
            int Snap(double requested, int current, int minimum, int available)
            {
                int value = Math.Clamp((int)Math.Round(requested), Math.Clamp(minimum, 1, available), available);
                return Math.Abs(current - value) <= Math.Max(8 * scale, value * .02) ? Math.Min(current, available) : value;
            }
            return new AttentionTarget(role,
                content ? Snap(width, a.Width, floor.Width, w.WorkArea.Width) : a.Width,
                content ? Snap(height, a.Height, floor.Height, w.WorkArea.Height) : a.Height,
                Math.Min(a.Width, Math.Max(1, (int)Math.Round(140 * scale))), movable, resize, content && learnedHeight,
                content && preference?.Width is > 0 and <= 1 && hint?.UsefulWidthDip is not (> 0 and <= 4000));
        }).ToArray();
        // For tall reading surfaces sharing the same vertical band, both comfortable widths
        // may not coexist. Budget the shared span before optimizing either window in isolation.
        var area = group[0].Window.WorkArea;
        var readers = Enumerable.Range(0, group.Length).Where(i => targets[i].Role is AttentionRole.Main or AttentionRole.Reference &&
            targets[i].Height > area.Height / 2).ToArray();
        int usable = area.Width - Math.Max(0, readers.Length - 1) * (int)Math.Round(8 * group[0].Window.Dpi / 96d);
        int widthDemand = readers.Sum(i => targets[i].Width);
        if (readers.Length > 1 && widthDemand > usable)
        {
            // A reference can occupy a partial-width viewport when the desktop is crowded.
            // Keep fixed physical frames intact; the goal expresses useful visible content.
            double fraction = Math.Max(.60, usable / (double)widthDemand);
            foreach (var i in readers)
                targets[i] = targets[i] with { Width = Math.Max(1, (int)Math.Floor(targets[i].Width * fraction)) };
        }
        return targets;
    }
}

internal static partial class IntentLayoutPlanner
{
    // Retain a small frontier of provisional desktop geometries. A temporarily covered peer may
    // move on the next step. Only complete candidates that pass TaskSafe reach the native layer.
    private static void AddAttentionLayouts(List<Candidate> candidates, VisibleWindow[] group, RectI[] original,
        AttentionTarget[] targets, RectI area, WindowSnapshot[] obstacles, int gap, int[] order, SmartTidyStrength strength,
        Func<Candidate, TaskCostBreakdown> score)
    {
        int main = Array.FindIndex(targets, t => t.Role == AttentionRole.Main);
        var budget = strength switch { SmartTidyStrength.Gentle => 2, SmartTidyStrength.Balanced => 4, _ => 6 };
        var steps = order.OrderBy(i => targets[i].Role)
            .ThenBy(i => Math.Abs(CenterX(original[i]) - CenterX(original[main])))
            .Take(budget).ToArray();
        var frontier = new List<Candidate> { new("attention-layout", original, order) };
        foreach (int i in steps)
        {
            var t = targets[i];
            if (!t.Movable) continue;
            var next = new Dictionary<string, Candidate>();
            foreach (var seed in frontier)
            {
                next.TryAdd(CandidateKey(seed), seed);
                foreach (var r in Positions(seed, i).Distinct())
                {
                    if (r == seed.Rects[i]) continue;
                    var rects = seed.Rects.ToArray();
                    rects[i] = r;
                    var candidate = new Candidate("attention-layout", rects, order);
                    if (next.TryAdd(CandidateKey(candidate), candidate)) candidates.Add(candidate);
                }
            }
            if (i != steps[^1])
                frontier = next.Values.Select(c => (Candidate: c, Score: score(c)))
                    .OrderBy(x => x.Score.Goal).ThenBy(x => x.Score.Total).Take(4).Select(x => x.Candidate).ToList();
        }

        IEnumerable<RectI> Positions(Candidate seed, int i)
        {
            var a = original[i];
            var target = targets[i];
            // Consider original dimensions too. A side step alone can free enough room.
            var sizes = new List<RectI> { a };
            if (target.Resizable && target.Role is AttentionRole.Main or AttentionRole.Reference)
                sizes.Add(a with { Width = target.LearnedWidth ? target.Width : Math.Max(a.Width, target.Width),
                    Height = target.LearnedHeight ? target.Height : Math.Max(a.Height, target.Height) });
            if (i == main && target.Resizable)
            {
                // Intermediate widths reserve enough room for a useful reference. Searching only
                // original and full comfort widths would miss layouts where both windows grow.
                foreach (int j in steps.Where(j => targets[j].Role == AttentionRole.Reference))
                {
                    int usable = area.Width - gap;
                    int both = target.Width + targets[j].Width;
                    int fair = (int)Math.Round(usable * target.Width / (double)both);
                    foreach (int w in new[] { usable - targets[j].Width, fair, usable - original[j].Width })
                        if (w > a.Width && w < target.Width)
                            sizes.Add(a with { Width = w, Height = Math.Max(a.Height, target.Height) });
                }
            }
            var mainRect = seed.Rects[main];
            if (target.Role == AttentionRole.Background)
            {
                // Yield to the surface actually obstructed, including a rear reference window.
                mainRect = order.Where(j => j != i && targets[j].Role != AttentionRole.Background &&
                    group[j].Window.ZOrder > group[i].Window.ZOrder)
                    .Select(j => seed.Rects[j]).OrderByDescending(r => r.Intersect(a).Area).FirstOrDefault(mainRect);
            }
            if (i != main && target.Resizable && strength != SmartTidyStrength.Gentle)
            {
                int available = Math.Max(mainRect.Left - area.Left, area.Right - mainRect.Right) - gap;
                if (available > 0 && available < Math.Max(a.Width, target.Width))
                    sizes.Add(a with { Width = available, Height = Math.Max(a.Height, target.Height) });
            }
            foreach (var size in sizes.Distinct())
            {
                if (size.Width > area.Width || size.Height > area.Height) continue;
                RectI Place(int x, int y) => size with { X = Math.Clamp(x, area.Left, area.Right - size.Width),
                    Y = Math.Clamp(y, area.Top, area.Bottom - size.Height) };
                yield return Place(a.Left, a.Top);
                yield return Place(a.Right - size.Width, a.Top);
                yield return Place(a.Left + (a.Width - size.Width) / 2, a.Top + (a.Height - size.Height) / 2);
                if (i == main)
                {
                    var covers = obstacles.Where(w => w.ZOrder < group[i].Window.ZOrder)
                        .Select(w => w.VisualRect).ToArray();
                    if (covers.Length > 0)
                        foreach (var slot in RectRegion.FreeRectangles(area, covers, size.Width, size.Height).Take(4))
                            yield return Place(Math.Clamp(a.Left, slot.Left, slot.Right - size.Width),
                                Math.Clamp(a.Top, slot.Top, slot.Bottom - size.Height));
                    int returning = Array.FindIndex(targets, x => x.Role == AttentionRole.Return);
                    if (returning >= 0)
                    {
                        var source = seed.Rects[returning];
                        yield return Place(source.Left + target.AccessWidth, source.Top);
                        yield return Place(source.Right - size.Width - target.AccessWidth, source.Top);
                    }
                    // Reserve physical space for a return/reference window on either side.
                    if (targets.Length > 1)
                    {
                        yield return Place(area.Left, a.Top);
                        yield return Place(area.Right - size.Width, a.Top);
                    }
                    continue;
                }
                yield return Place(mainRect.Left - size.Width - gap, a.Top);
                yield return Place(mainRect.Right + gap, a.Top);
                yield return Place(a.Left, mainRect.Top - size.Height - gap);
                yield return Place(a.Left, mainRect.Bottom + gap);
                if (target.Role == AttentionRole.Return)
                {
                    yield return Place(mainRect.Left - target.AccessWidth, a.Top);
                    yield return Place(mainRect.Right - size.Width + target.AccessWidth, a.Top);
                    yield return Place(a.Left, mainRect.Top - (int)Math.Ceiling(32 * group[i].Window.Dpi / 96d));
                }
            }
        }
    }
}
