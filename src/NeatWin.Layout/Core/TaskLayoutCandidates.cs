namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
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
