namespace NeatWin.Core;

/// <summary>Explicit tiling policy. Not a more aggressive Smart score and not a learned preference.</summary>
public static class FullTilingPlanner
{
    public static IntentLayoutPlan Create(IReadOnlyList<WindowSnapshot> desktop, TidyOptions? options = null)
        => Create(desktop, options ?? new(), null);

    private static IntentLayoutPlan Create(IReadOnlyList<WindowSnapshot> desktop, TidyOptions options,
        IReadOnlySet<nint>? eligible, bool inset = false)
    {
        var moves = new List<TidyMove>(); var traces = new List<LayoutGroupTrace>();
        foreach (var monitor in desktop.GroupBy(w => (w.MonitorHandle, w.WorkArea)))
        {
            var area = monitor.Key.WorkArea;
            if (area.IsEmpty) continue;
            // Capture already excludes minimized, hidden, cloaked and NeatWin-owned windows.
            // Fully occluded *normal* windows deliberately participate in complete tiling.
            var windows = monitor.Where(w => w.IsManageable && w.IsResizable && !w.IsTopmost &&
                    (eligible is null || eligible.Contains(w.Handle)))
                .OrderBy(w => w.VisualRect.Top).ThenBy(w => w.VisualRect.Left).ThenBy(w => w.Handle).ToArray();
            if (windows.Length == 0) continue;
            var scale = windows.Max(w => w.Dpi) / 96.0;
            if (inset)
            {
                var margin = (int)Math.Round(32 * scale);
                area = RectI.FromEdges(area.Left + margin, area.Top + margin, area.Right - margin, area.Bottom - margin);
            }
            var handles = windows.Select(w => w.Handle).ToHashSet();
            var free = new List<RectI> { area };
            foreach (var anchor in desktop.Where(w => !handles.Contains(w.Handle)))
                free = RectRegion.Subtract(free, anchor.VisualRect.Intersect(area));
            var gap = (int)Math.Round(8 * scale);
            var minWidth = Math.Max(1, (int)Math.Ceiling(options.MinimumWidth * scale));
            var minHeight = Math.Max(1, (int)Math.Ceiling(options.MinimumHeight * scale));
            var regions = free.Where(r => r.Width >= minWidth && r.Height >= minHeight)
                .OrderByDescending(r => r.Area).ThenBy(r => r.Top).ThenBy(r => r.Left).ToArray();
            var counts = new int[regions.Length];
            var capacities = regions.Select(r => Math.Min(windows.Length,
                ((r.Width + gap) / (minWidth + gap)) * ((r.Height + gap) / (minHeight + gap)))).ToArray();
            var fits = capacities.Sum() >= windows.Length;
            if (fits)
                for (var i = 0; i < windows.Length; i++)
                {
                    var slot = Enumerable.Range(0, regions.Length).Where(j => counts[j] < capacities[j])
                        .OrderByDescending(j => regions[j].Area / (double)(counts[j] + 1)).First();
                    counts[slot]++;
                }
            var cells = new List<RectI>();
            if (fits)
                for (var i = 0; i < regions.Length; i++)
                {
                    if (counts[i] == 0) continue;
                    var grid = Grid(regions[i], counts[i], gap, minWidth, minHeight);
                    if (grid is null) { fits = false; break; }
                    cells.AddRange(grid);
                }
            if (!fits)
            {
                traces.Add(new(windows.Select(w => w.Handle).ToArray(), "tiling-insufficient-space", 0,
                    [new("full-tiling", null, "fixed-obstacle-or-minimum-size")]));
                continue;
            }
            // Nearest unassigned window preserves spatial correspondence; exact existing cells
            // match at zero distance, making unchanged complete tiling naturally idempotent.
            var remaining = windows.ToList();
            foreach (var cell in cells.OrderBy(r => r.Top).ThenBy(r => r.Left))
            {
                var chosen = remaining.OrderBy(w => Distance(w.VisualRect, cell)).ThenBy(w => w.Handle).First();
                remaining.Remove(chosen);
                if (chosen.VisualRect != cell) moves.Add(new(chosen, cell));
            }
            traces.Add(new(windows.Select(w => w.Handle).ToArray(), "full-tiling", 0,
                [new("full-tiling", null, null)]));
        }
        return new(moves, [], traces);
    }

    internal static IntentLayoutPlan EnsureMovement(IntentLayoutPlan preferred, IReadOnlyList<VisibleWindow> visible,
        IReadOnlyList<WindowSnapshot> desktop, TidyOptions options, TaskLayoutContext? context)
    {
        if (preferred.Moves.Any(m => !WindowStateRules.Near(m.Window.VisualRect, m.TargetVisualRect)))
            return preferred;

        var anchors = (context?.WindowHints ?? []).Where(h => h.Anchor).Select(h => h.Handle).ToHashSet();
        var windows = visible.Select(v => v.Window).Where(w => w.IsManageable && !w.IsTopmost &&
            !w.WorkArea.IsEmpty && !w.VisualRect.IsEmpty && !anchors.Contains(w.Handle)).ToArray();
        var allowResize = context?.Preferences.AllowUsefulResize ?? true;
        if (allowResize)
        {
            var eligible = windows.GroupBy(w => w.MonitorHandle).Where(g => g.Count() > 1)
                .SelectMany(g => g).Select(w => w.Handle).ToHashSet();
            if (eligible.Count > 0)
            {
                foreach (var inset in new[] { false, true })
                {
                    var tiled = Create(desktop, options, eligible, inset);
                    if (tiled.Moves.Any(m => !WindowStateRules.Near(m.Window.VisualRect, m.TargetVisualRect)) &&
                        tiled.Moves.All(m => MeetsSizeFloor(m.Window, m.TargetVisualRect)))
                        return tiled with { Groups = tiled.Groups.Where(g => g.Selected == "full-tiling").ToArray() };
                }
            }
        }

        // Center a lone window, pick free space for fixed-size windows, or expose more of
        // a crowded stack. Keep the original size whenever a useful position is available.
        foreach (var window in windows.OrderBy(w => w.IsForeground).ThenByDescending(w => w.ZOrder))
        {
            var area = window.WorkArea;
            var original = window.VisualRect;
            var minimumTravel = Math.Max(4, (int)Math.Round(16 * window.Dpi / 96.0));
            var neighbors = desktop.Where(w => w.Handle != window.Handle && !w.VisualRect.Intersect(area).IsEmpty).ToArray();
            var fixedWindows = neighbors.Where(w => !w.IsManageable || w.IsTopmost || anchors.Contains(w.Handle)).ToArray();
            var originalOverlap = neighbors.Sum(n => original.Intersect(n.VisualRect).Area);
            var free = neighbors.Aggregate(new List<RectI> { area }, (regions, n) => RectRegion.Subtract(regions, n.VisualRect));
            var width = original.Width;
            var height = original.Height;
            var target = PickPosition(width, height);
            if (target.IsEmpty && allowResize && window.IsResizable)
            {
                // A window already taking up all available space needs a smaller, inset slot.
                var floor = context?.MinimumSize(window.Handle) ?? default;
                width = Math.Clamp(Math.Max(floor.Width, (int)(Math.Min(width, area.Width) * .85)), 1, width);
                height = Math.Clamp(Math.Max(floor.Height, (int)(Math.Min(height, area.Height) * .85)), 1, height);
                target = PickPosition(width, height);
            }
            if (!target.IsEmpty)
                return new([new(window, target)], [], [new([window.Handle], "reposition", 0, [])]);

            RectI PickPosition(int w, int h)
            {
                return (from region in free.Prepend(area).Distinct()
                        where region.Width >= w && region.Height >= h
                        from x in Positions(region.Left, region.Right - w)
                        from y in Positions(region.Top, region.Bottom - h)
                        let rect = new RectI(x, y, w, h)
                        where !WindowStateRules.Near(original, rect, minimumTravel - 1) && MeetsSizeFloor(window, rect) &&
                            fixedWindows.All(f => rect.Intersect(f.VisualRect).Area <= original.Intersect(f.VisualRect).Area)
                        let overlap = neighbors.Sum(n => rect.Intersect(n.VisualRect).Area)
                        where overlap <= originalOverlap
                        orderby overlap
                        select rect).FirstOrDefault();
            }
        }
        return preferred with { Moves = [] };

        static int[] Positions(int first, int last) => [first + (last - first) / 2, first, last];

        bool MeetsSizeFloor(WindowSnapshot w, RectI target)
        {
            var learned = context?.MinimumSize(w.Handle) ?? default;
            var scale = w.Dpi / 96.0;
            return target.Width >= Math.Min(w.VisualRect.Width, Math.Max(learned.Width, options.MinimumWidth * scale)) &&
                target.Height >= Math.Min(w.VisualRect.Height, Math.Max(learned.Height, options.MinimumHeight * scale));
        }
    }

    private static RectI[]? Grid(RectI area, int count, int gap, int minWidth, int minHeight)
    {
        RectI[]? best = null; var bestScore = double.PositiveInfinity;
        for (var rows = 1; rows <= count; rows++)
        {
            if ((area.Height - gap * (rows - 1)) / rows < minHeight) continue;
            var candidate = new List<RectI>();
            for (var row = 0; row < rows; row++)
            {
                var columns = count / rows + (row < count % rows ? 1 : 0);
                var availableWidth = area.Width - gap * (columns - 1);
                if (availableWidth / columns < minWidth) break;
                var availableHeight = area.Height - gap * (rows - 1);
                var top = area.Top + availableHeight * row / rows + gap * row;
                var bottom = area.Top + availableHeight * (row + 1) / rows + gap * row;
                for (var col = 0; col < columns; col++)
                {
                    var left = area.Left + availableWidth * col / columns + gap * col;
                    var right = area.Left + availableWidth * (col + 1) / columns + gap * col;
                    candidate.Add(RectI.FromEdges(left, top, right, bottom));
                }
            }
            if (candidate.Count != count) continue;
            // Finite readable cells, not a claim about an optimal human visual angle.
            var score = candidate.Average(r => Math.Pow(Math.Log(r.Width / (double)r.Height / 1.5), 2));
            if (score < bestScore) { bestScore = score; best = candidate.ToArray(); }
        }
        return best;
    }
    private static double Distance(RectI a, RectI b) =>
        Math.Pow(a.Left + a.Width / 2.0 - b.Left - b.Width / 2.0, 2) +
        Math.Pow(a.Top + a.Height / 2.0 - b.Top - b.Height / 2.0, 2);
}
