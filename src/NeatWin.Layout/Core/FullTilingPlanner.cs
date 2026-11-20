namespace NeatWin.Core;

/// <summary>Explicit tiling policy. Not a more aggressive Smart score and not a learned preference.</summary>
public static class FullTilingPlanner
{
    public static IntentLayoutPlan Create(IReadOnlyList<WindowSnapshot> desktop, TidyOptions? options = null)
    {
        options ??= new();
        var moves = new List<TidyMove>(); var traces = new List<LayoutGroupTrace>();
        foreach (var monitor in desktop.GroupBy(w => (w.MonitorHandle, w.WorkArea)))
        {
            var area = monitor.Key.WorkArea;
            if (area.IsEmpty) continue;
            // Capture already excludes minimized, hidden, cloaked and NeatWin-owned windows.
            // Fully occluded *normal* windows deliberately participate in complete tiling.
            var windows = monitor.Where(w => w.IsManageable && w.IsResizable && !w.IsTopmost)
                .OrderBy(w => w.VisualRect.Top).ThenBy(w => w.VisualRect.Left).ThenBy(w => w.Handle).ToArray();
            if (windows.Length == 0) continue;
            var handles = windows.Select(w => w.Handle).ToHashSet();
            var free = new List<RectI> { area };
            foreach (var anchor in desktop.Where(w => !handles.Contains(w.Handle)))
                free = RectRegion.Subtract(free, anchor.VisualRect.Intersect(area));
            var scale = windows.Max(w => w.Dpi) / 96.0;
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
