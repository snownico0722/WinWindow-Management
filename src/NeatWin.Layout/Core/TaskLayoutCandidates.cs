namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    private static void AddClippedEdgeRepairs(List<Candidate> candidates, VisibleWindow[] windows, RectI[] original,
        RectI area, TaskLayoutContext context, AttentionTarget[]? goals, int[] order)
    {
        if (!context.Preferences.AllowUsefulResize) return;
        for (int i = 0; i < windows.Length; i++)
        {
            var window = windows[i].Window;
            if (!window.IsResizable || goals?[i].Resizable == false || context.HoldsTextView(window) ||
                window.Handle == context.GestureAnchor) continue;
            var rect = original[i];
            var inside = rect.Intersect(area);
            if (inside.IsEmpty || inside == rect) continue;
            // 只修超出工作区的那一边。把整窗平移进屏会把已露出的内容挪到邻窗上；两种代价应在同轮比较。
            if (inside.Width < rect.Width) Add(rect with { X = inside.X, Width = inside.Width });
            if (inside.Height < rect.Height) Add(rect with { Y = inside.Y, Height = inside.Height });
            if (inside.Width < rect.Width && inside.Height < rect.Height) Add(inside);
            void Add(RectI target)
            {
                var frames = (RectI[])original.Clone();
                frames[i] = target;
                candidates.Add(new("clipped-edge-repair", frames, order));
            }
        }
    }

    // 两窗已有参考关系时，允许只平移遮挡的一方。沿实际相邻边界求最短让位，保留字号、另一轴和所有其他窗。
    // 普通切换与多跳导航没有给出并看意图。中心重合时也没有方向依据，不凭枚举顺序选边。
    private static void AddReferenceClearance(List<Candidate> candidates, VisibleWindow[] windows, RectI[] original,
        RectI area, int gap, TaskLayoutContext context, AttentionTarget[]? goals, int[] order)
    {
        if (context.Focus is not { Main: not 0, Companion: not 0, Evidence: "recent-switch-pair", Switches: >= 2 } focus ||
            focus.Navigation is { Length: > 0 } || !context.PairPlanningEnabled) return;
        int main = Array.FindIndex(windows, w => w.Window.Handle == focus.Main);
        int peer = Array.FindIndex(windows, w => w.Window.Handle == focus.Companion);
        if (main < 0 || peer < 0 || original[main].Intersect(original[peer]).IsEmpty) return;
        Add(main, peer);
        Add(peer, main);

        void Add(int moved, int fixedIndex)
        {
            var window = windows[moved].Window;
            if (goals?[moved].Movable == false || window.Handle == context.GestureAnchor || context.HoldsTextView(window)) return;
            var rect = original[moved];
            var other = original[fixedIndex];
            int dx = Math.Sign((long)rect.Left + rect.Right - other.Left - other.Right);
            int dy = Math.Sign((long)rect.Top + rect.Bottom - other.Top - other.Bottom);
            if (dx != 0 && rect.Width <= area.Width)
                Offer(rect with { X = Math.Clamp(dx < 0 ? other.Left - gap - rect.Width : other.Right + gap, area.Left, area.Right - rect.Width) });
            if (dy != 0 && rect.Height <= area.Height)
                Offer(rect with { Y = Math.Clamp(dy < 0 ? other.Top - gap - rect.Height : other.Bottom + gap, area.Top, area.Bottom - rect.Height) });

            void Offer(RectI target)
            {
                if (target.Intersect(other).Area >= rect.Intersect(other).Area) return;
                var frames = (RectI[])original.Clone();
                frames[moved] = target;
                candidates.Add(new("reference-clearance", frames, order));
            }
        }
    }

    private static void AddAccessRepairs(List<Candidate> candidates, VisibleWindow[] windows, RectI[] original,
        OcclusionMetrics.Exposure[] exposure, WindowSnapshot[] obstacles, RectI area, int gap, int[] order)
    {
        for (var i = 0; i < windows.Length; i++)
        {
            var w = windows[i].Window;
            var rect = original[i];
            var access = Math.Min(rect.Width / 2, (int)Math.Round(140 * w.Dpi / 96.0));
            if (access < 1 || exposure[i].AccessWidth >= access || rect.Width > area.Width || rect.Height > area.Height) continue;
            var title = rect with { Height = Math.Min(rect.Height, (int)Math.Round(32 * w.Dpi / 96.0)) };
            var covers = original.Take(i).Concat(obstacles.Where(o => o.ZOrder < w.ZOrder).Select(o => o.VisualRect))
                .Where(r => !r.Intersect(title).IsEmpty).Reverse().Take(3).ToArray();
            if (covers.Length == 0) continue;
            var occupied = original.Where((_, other) => other != i).Concat(obstacles.Select(o => o.VisualRect));
            foreach (var target in RectRegion.FreeRectangles(area, occupied, rect.Width, rect.Height)
                .Select(region => ClampTo(rect, region)).Where(target => target != rect).Distinct()
                .OrderBy(target => Math.Abs((long)rect.X - target.X) + Math.Abs((long)rect.Y - target.Y)).Take(4))
                AddTarget(target);

            foreach (var cover in covers)
            {
                RectI[] openings = [
                    rect with { X = cover.Left - access - gap },
                    rect with { X = cover.Right - rect.Width + access + gap },
                    rect with { Y = cover.Top - title.Height - gap },
                    rect with { Y = cover.Bottom + gap },
                ];
                foreach (var opening in openings) AddTarget(ClampTo(opening, area));
            }

            void AddTarget(RectI target)
            {
                if (target == rect) return;
                var layout = original.ToArray();
                layout[i] = target;
                candidates.Add(new("window-access", layout, order));
            }
        }

        static RectI ClampTo(RectI rect, RectI free) => rect with
        {
            X = Math.Clamp(rect.X, free.Left, free.Right - rect.Width),
            Y = Math.Clamp(rect.Y, free.Top, free.Bottom - rect.Height),
        };
    }

    private static IEnumerable<int> RowShapes(int count, RectI area)
    {
        if (count <= 8) return Enumerable.Range(1, count);
        var middle = (int)Math.Round(Math.Sqrt(count * (area.Width / (double)area.Height) / 1.5));
        return new[] { 1, count, 2, (count + 1) / 2, middle - 1, middle, middle + 1 }
            .Select(columns => Math.Clamp(columns, 1, count)).Distinct();
    }

    private static void AddCoveredWorkingPairRepair(List<Candidate> candidates, VisibleWindow[] windows, RectI[] original,
        WindowSnapshot[] obstacles, RectI area, int gap, TaskLayoutContext context, int[] order)
    {
        if (context.Focus is not { Main: not 0, Companion: not 0 } focus) return;
        int main = Array.FindIndex(windows, v => v.Window.Handle == focus.Main);
        int companion = Array.FindIndex(windows, v => v.Window.Handle == focus.Companion);
        if (main < 0 || companion < 0 || main == companion) return;

        int front = Math.Min(main, companion), rear = Math.Max(main, companion);
        var covered = original[rear];
        var cover = original[front];
        if (!CoversHalfOfRear(cover, covered)) return;

        // Open a severely covered task window at full size without moving the front window.
        // Surfaces below the target do not take away its available space.
        var paddedCover = RectI.FromEdges(cover.Left - gap, cover.Top - gap, cover.Right + gap, cover.Bottom + gap);
        var occupied = original.Where((_, i) => i < rear && i != front).Append(paddedCover)
            .Concat(obstacles.Where(w => w.ZOrder < windows[rear].Window.ZOrder).Select(w => w.VisualRect));
        foreach (var free in RectRegion.FreeRectangles(area, occupied, covered.Width, covered.Height))
        {
            var target = covered with { X = Math.Clamp(covered.X, free.Left, free.Right - covered.Width),
                Y = Math.Clamp(covered.Y, free.Top, free.Bottom - covered.Height) };
            var layout = original.ToArray();
            layout[rear] = target;
            candidates.Add(new("working-pair-access", layout, order));
        }
    }

    private static void AddDisplayLayouts(List<Candidate> candidates, VisibleWindow[] windows, RectI[] original,
        RectI area, TaskEvidence[] relations, TidyOptions options, TaskLayoutContext context, int gap, int[] order)
    {
        var indices = order.OrderBy(i => CenterY(original[i])).ThenBy(i => CenterX(original[i])).ToArray();
        var minWidths = new int[windows.Length];
        var minHeights = new int[windows.Length];
        var profile = context.Preferences;
        for (var i = 0; i < windows.Length; i++)
        {
            var w = windows[i].Window;
            var floor = context.MinimumSize(w.Handle);
            var scale = w.Dpi / 96.0;
            var canResize = w.IsResizable && profile.AllowUsefulResize;
            minWidths[i] = canResize ? Minimum(original[i].Width, floor.Width, options.MinimumWidth * scale, profile.ComfortableWidthDip * scale) : original[i].Width;
            minHeights[i] = canResize ? Minimum(original[i].Height, floor.Height, options.MinimumHeight * scale, profile.ComfortableHeightDip * scale) : original[i].Height;
        }
        foreach (var columns in RowShapes(windows.Length, area))
        {
            var rows = columns == windows.Length
                ? new[] { order.OrderBy(i => CenterX(original[i])).ToArray() }
                : indices.Chunk(columns).Select(row => row.OrderBy(i => CenterX(original[i])).ToArray()).ToArray();
            var heights = FitLengths(rows.Select(row => row.Max(i => original[i].Height)).ToArray(),
                rows.Select(row => row.Max(i => minHeights[i])).ToArray(), area.Height - (rows.Length - 1) * gap);
            if (heights is null) continue;
            var widths = rows.Select(row => FitLengths(row.Select(i => original[i].Width).ToArray(),
                row.Select(i => minWidths[i]).ToArray(), area.Width - (row.Length - 1) * gap)).ToArray();
            if (widths.Any(row => row is null)) continue;
            var spans = widths.Select(row => row!.Sum() + (row!.Length - 1) * gap).ToArray();
            var height = heights.Sum() + (rows.Length - 1) * gap;
            var span = spans.Max();
            var bounds = Bounds(original);
            var screenCenter = context.Focus is { Main: not 0 } focus &&
                relations.Any(r => r.Joint * (1 - r.Uncertainty) > .55)
                ? windows.FirstOrDefault(w => w.Window.Handle == focus.Main)?.Window.VisualRect ?? bounds
                : bounds;
            var left = Math.Clamp((int)Math.Round(CenterX(screenCenter) - span / 2d), area.Left, area.Right - span);
            var y = Math.Clamp((int)Math.Round(CenterY(screenCenter) - height / 2d), area.Top, area.Bottom - height);
            var targets = new RectI[windows.Length];
            for (var row = 0; row < rows.Length; row++)
            {
                var x = left;
                for (var col = 0; col < rows[row].Length; col++)
                {
                    var i = rows[row][col];
                    var width = widths[row]![col];
                    targets[i] = new(x, y, width, Math.Min(original[i].Height, heights[row]));
                    x += width + gap;
                }
                y += heights[row] + gap;
            }
            candidates.Add(new("display-reflow", targets, order));
        }

        static int Minimum(int original, int observed, double floor, double comfortable) =>
            Math.Min(original, Math.Max(observed, (int)Math.Ceiling(Math.Max(floor, .60 * Math.Min(original, comfortable)))));
    }

    // Distribute compression across the space each window can actually give up.
    private static int[]? FitLengths(int[] preferred, int[] floors, int available)
    {
        int minimum = floors.Sum();
        if (available < minimum) return null;
        int natural = preferred.Sum();
        if (available >= natural) return preferred;
        var keep = (available - minimum) / (double)(natural - minimum);
        return preferred.Select((size, i) => floors[i] + (int)Math.Floor((size - floors[i]) * keep)).ToArray();
    }

    private static void AddCoveredPairCandidates(List<Candidate> candidates, VisibleWindow[] group,
        RectI[] seed, TaskEvidence[] relations, RectI area, int gap, int[] order)
    {
        // A large window behind a pair can connect it to the rest of the monitor. Try exposing
        // the pair on its own, optionally leaving the underlying title bars accessible.
        foreach (var pair in relations.Where(r => CoversHalfOfRear(group[r.First].Window.VisualRect, group[r.Second].Window.VisualRect)))
        {
            var windows = new[] { group[pair.First], group[pair.Second] };
            var rects = new[] { seed[pair.First], seed[pair.Second] };
            var tops = group.Skip(pair.First + 1).Where(w => w != windows[1])
                .Select(w => w.Window.VisualRect.Top + (int)Math.Ceiling(32 * w.Window.Dpi / 96.0))
                .Where(y => y > area.Top && y < area.Bottom).Prepend(area.Top).Distinct();
            foreach (var top in tops)
            for (var columns = 1; columns <= 2; columns++)
            {
                var space = RectI.FromEdges(area.Left, top, area.Right, area.Bottom);
                var packed = new List<RectI[]>();
                AddPacked(packed, windows, rects, space, gap, columns, false);
                foreach (var layout in packed)
                {
                    var targets = seed.ToArray();
                    targets[pair.First] = layout[0];
                    targets[pair.Second] = layout[1];
                    candidates.Add(new(columns == 2 ? "columns" : "rows", targets, order));
                }
            }
        }
    }

    private static void AddWorkingPair(List<Candidate> candidates, VisibleWindow[] windows, RectI[] seed,
        RectI area, int gap, TaskLayoutContext context, int[] order)
    {
        foreach (var region in context.Placement ?? [])
        {
            if (region.RevealedCell is < 0 or >= 16 || region.RegionConfidence <= .25) continue;
            int cover = Array.FindIndex(windows, w => w.Window.Handle == region.Window);
            int watched = Array.FindIndex(windows, w => w.Window.Handle == region.Partner);
            if (cover < 0 || watched < 0) continue;
            var cell = WindowExposure.Cell(seed[watched], region.RevealedCell);
            var moving = seed[cover];
            foreach (int x in new[] { cell.Right + gap, cell.Left - moving.Width - gap })
            {
                var targets = seed.ToArray();
                targets[cover] = moving with { X = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - moving.Width)) };
                candidates.Add(new("working-region", targets, order));
            }
            foreach (int y in new[] { cell.Bottom + gap, cell.Top - moving.Height - gap })
            {
                var targets = seed.ToArray();
                targets[cover] = moving with { Y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - moving.Height)) };
                candidates.Add(new("working-region", targets, order));
            }
        }
        var main = Array.FindIndex(windows, w => w.Window.Handle == context.Focus?.Main);
        if (main < 0) main = Array.FindIndex(windows, w => w.Window.IsForeground);
        if (main < 0) main = 0;
        var peer = Array.FindIndex(windows, w => w.Window.Handle == context.Focus?.Companion);
        var pref = context.Placement?.FirstOrDefault(p => p.Window == windows[main].Window.Handle);
        if (peer < 0 && pref is { PreferCenter: true, Partner: not 0 })
            peer = Array.FindIndex(windows, w => w.Window.Handle == pref.Partner);
        if (peer < 0)
        {
            var mirror = context.Placement?.Where(p => p.Partner == windows[main].Window.Handle && p.PositionConfidence >= .4)
                .OrderByDescending(p => p.Confidence).FirstOrDefault();
            if (mirror != null) peer = Array.FindIndex(windows, w => w.Window.Handle == mirror.Window);
        }
        if (peer < 0 || peer == main) return;
        AddPartialOverlap(candidates, windows, seed, main, peer, area, context, order);
        var a = seed[main]; var b = seed[peer];
        int span = Math.Min(area.Width - gap, a.Width + b.Width);
        if (span <= 1) return;
        var peerPref = context.Placement?.FirstOrDefault(p => p.Window == windows[peer].Window.Handle && p.Partner == windows[main].Window.Handle);
        double preferredFraction = pref is { Confidence: > .35, PreferWidth: true }
            ? Math.Clamp(pref.Width * area.Width / span, .25, .75)
            : peerPref is { Confidence: > .35, PreferWidth: true }
                ? Math.Clamp(1 - peerPref.Width * area.Width / span, .25, .75)
                : a.Width / (double)(a.Width + b.Width);
        int width = (int)Math.Round(span * preferredFraction);
        if (!context.Preferences.AllowUsefulResize)
        {
            if (a.Width + b.Width > span) return;
            width = a.Width;
        }
        var floorMain = context.MinimumSize(windows[main].Window.Handle);
        var floorPeer = context.MinimumSize(windows[peer].Window.Handle);
        if (floorMain.Width > 0) width = Math.Max(width, Math.Min(a.Width, floorMain.Width));
        if (floorPeer.Width > 0) width = Math.Min(width, span - Math.Min(b.Width, floorPeer.Width));
        if (!windows[main].Window.IsResizable) width = a.Width;
        if (!windows[peer].Window.IsResizable) width = span - b.Width;
        if (width <= 0 || span <= width) return;
        foreach (bool mainLeft in new[] { true, false })
        {
            // On an ultrawide keep the pair together, with the primary close to its working position.
            int left = Math.Clamp((int)Math.Round(CenterX(a) - width / 2d) - (mainLeft ? 0 : span - width + gap),
                area.Left, area.Right - span - gap);
            var targets = seed.ToArray();
            targets[main] = a with { X = mainLeft ? left : left + span - width + gap, Width = width };
            targets[peer] = b with { X = mainLeft ? left + width + gap : left, Width = span - width };
            EmitPairAndAuxCandidates("working-pair", candidates, windows, targets, main, peer, area, context, order);
        }

    }

    private static void AddTaskCandidates(List<Candidate> candidates, VisibleWindow[] windows,
        RectI[] original, RectI area, TaskEvidence[] relations, TaskLayoutContext context, int gap)
    {
        if (windows.Length is < 2 or > 8) return;
        var profile = context.Preferences;
        // Pair/block proposals complete a rough relationship; no mandatory template.
        if (windows.Length == 2 && relations[0].Joint >= .5 && !relations[0].SameColumn)
        {
            var left = CenterX(original[0]) <= CenterX(original[1]) ? 0 : 1;
            var right = 1 - left;
            var a = original[left]; var b = original[right];
            var crowded = a.Width + b.Width >= area.Width * .78;
            if (crowded)
            {
                AddPair("task-edge", a.Width, a.Height, b.Width, b.Height, false, true);
                AddPair("task-bleed", a.Width, a.Height, b.Width, b.Height, true, true);
            }
            if (profile.AllowUsefulResize)
            {
                var scale = windows[0].Window.Dpi / 96.0;
                // Include smaller readable arrangements and useful enlargement, not fill at any cost.
                var total = Math.Min(area.Width * 1.12, DemandWidth(windows[left].Window, a) + DemandWidth(windows[right].Window, b));
                foreach (var ratio in new[] { a.Width / (double)(a.Width + b.Width), .50, .58, .42 }.Distinct())
                foreach (var occupancy in new[] { .82, .90, 1.0, 1.10, 1.18 })
                {
                    var targetTotal = Math.Min(area.Width * 1.18, total * occupancy);
                    var widthA = (int)Math.Round(targetTotal * ratio);
                    var widthB = (int)Math.Round(targetTotal * (1 - ratio));
                    var heightA = ResizeHeight(a, widthA);
                    var heightB = ResizeHeight(b, widthB);
                    AddPair("task-fit", widthA, a.Height, widthB, b.Height, false, crowded);
                    AddPair("task-fit", widthA, heightA, widthB, heightB, false, crowded);
                    if (crowded) AddPair("task-bleed", widthA, heightA, widthB, heightB, true, true);
                }
                double DemandWidth(WindowSnapshot w, RectI r)
                {
                    var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle)?.UsefulWidthDip;
                    return hint is double d && double.IsFinite(d) && d > 0 ? Math.Clamp(d, 160, 4000) * scale :
                        Math.Max(r.Width * .8, Math.Min(profile.ComfortableWidthDip * scale, r.Width * 1.25));
                }
                int ResizeHeight(RectI r, int width) => Math.Min(area.Height,
                    (int)Math.Round(r.Height * Math.Clamp(width / (double)r.Width, .75, 1.25)));
            }

            void AddPair(string kind, int aw, int ah, int bw, int bh, bool bleed, bool edgeAnchor)
            {
                if (!windows[left].Window.IsResizable) { aw = a.Width; ah = a.Height; }
                if (!windows[right].Window.IsResizable) { bw = b.Width; bh = b.Height; }
                if (aw <= 0 || bw <= 0 || ah > area.Height || bh > area.Height) return;
                var ba = bleed ? BleedBudget(windows[left].Window, context).Left : 0;
                var bb = bleed ? BleedBudget(windows[right].Window, context).Right : 0;
                if (bleed && ba == 0 && bb == 0) return;
                var span = edgeAnchor ? area.Width : Math.Min(area.Width, aw + bw + gap);
                var x = edgeAnchor ? area.Left : Math.Clamp((int)Math.Round(CenterX(Bounds(original)) - span / 2.0), area.Left, area.Right - span);
                var top = Math.Clamp((int)Math.Round((a.Top + b.Top) / 2.0), area.Top, area.Bottom - Math.Max(ah, bh));
                var targets = original.ToArray();
                targets[left] = new(x - ba, top, aw, ah);
                targets[right] = new(x + span - bw + bb, top, bw, bh);
                AddOrders(candidates, kind, targets, windows);
            }
        }

        // Preserve a same-column group as a block instead of spraying its members across columns.
        var columns = new List<List<int>>();
        foreach (var index in Enumerable.Range(0, windows.Length).OrderBy(i => CenterX(original[i])))
        {
            var column = columns.FirstOrDefault(c => c.Any(i => relations.Any(r => r.SameColumn &&
                ((r.First == i && r.Second == index) || (r.Second == i && r.First == index)))));
            if (column is null) columns.Add([index]); else column.Add(index);
        }
        if (columns.Count > 1 && columns.Count < windows.Length)
        {
            var widthSum = columns.Sum(c => c.Max(i => original[i].Width));
            var ratio = Math.Min(1, (area.Width - (columns.Count - 1) * gap) / (double)widthSum);
            if (ratio < .60) return;
            var target = original.ToArray();
            var x = area.Left;
            foreach (var column in columns)
            {
                var minTop = column.Min(i => original[i].Top);
                var maxBottom = column.Max(i => original[i].Bottom);
                var top = Math.Clamp(minTop, area.Top, Math.Max(area.Top, area.Bottom - (maxBottom - minTop)));
                foreach (var i in column)
                {
                    var r = original[i];
                    var width = windows[i].Window.IsResizable && profile.AllowUsefulResize ? (int)Math.Floor(r.Width * ratio) : r.Width;
                    target[i] = new(x, top + r.Top - minTop, width, r.Height);
                }
                x += column.Max(i => target[i].Width) + gap;
            }
            AddOrders(candidates, "task-columns", target, windows);
        }
    }
}
