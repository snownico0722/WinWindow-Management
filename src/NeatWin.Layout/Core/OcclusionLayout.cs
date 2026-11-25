namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    internal sealed record Candidate(string Kind, RectI[] Rects, int[] Order);

    private static void AddOverlapping(List<Candidate> candidates, VisibleWindow[] windows,
        RectI[] original, RectI area, IntentHint hint)
    {
        var count = windows.Length;
        if (count is < 2 or > 8) return;
        var indices = Enumerable.Range(0, count).OrderBy(i => CenterX(original[i]))
            .ThenBy(i => windows[i].Window.ZOrder).ToArray();
        var widthSum = original.Sum(r => r.Width);
        var overflow = widthSum - area.Width;
        if (overflow > 0 && overflow <= (count - 1) * original.Max(r => r.Width) * hint.MaximumEdgeOverlap)
        {
            var depth = (int)Math.Ceiling(overflow / (double)(count - 1));
            AddSpread("edge-row", depth, 0);
        }
        var hadOverlap = original.SelectMany((r, i) => original.Skip(i + 1).Select(b => r.Intersect(b).Area)).Any(a => a > 0);
        if (!hadOverlap) return;
        var maxWidth = original.Max(r => r.Width);
        var maxHeight = original.Max(r => r.Height);
        if (maxWidth > area.Width || maxHeight > area.Height) return;
        var scale = windows[0].Window.Dpi / 96.0;
        var yStep = Math.Min((int)Math.Round(40 * scale), (area.Height - maxHeight) / (count - 1));
        var xStep = Math.Min((int)Math.Round(original.Min(r => r.Width) * 0.48),
            (area.Width - maxWidth) / (count - 1));
        AddDeck(xStep, yStep);
        if (StackSignal(windows, original) >= 0.45)
            AddDeck(Math.Min((int)Math.Round(56 * scale), xStep), yStep);
        return;

        void AddSpread(string kind, int depth, int stepY)
        {
            var width = widthSum - depth * (count - 1);
            var height = original.Max(r => r.Height) + stepY * (count - 1);
            if (width > area.Width || height > area.Height) return;
            var bounds = Bounds(original);
            var x = Math.Clamp((int)Math.Round(CenterX(bounds) - width / 2.0), area.Left, area.Right - width);
            var y = Math.Clamp((int)Math.Round(CenterY(bounds) - height / 2.0), area.Top, area.Bottom - height);
            var targets = new RectI[count];
            for (var p = 0; p < count; p++)
            {
                var i = indices[p];
                targets[i] = new(x, y + p * stepY, original[i].Width, original[i].Height);
                x += original[i].Width - depth;
            }
            AddOrders(candidates, kind, targets, windows);
        }

        void AddDeck(int dx, int dy)
        {
            if (dx <= 0 && dy <= 0) return;
            // Actual footprint avoids drift from recentering an imaginary larger deck.
            var width = indices.Select((i, position) => position * dx + original[i].Width).Max();
            var height = indices.Select((i, position) => position * dy + original[i].Height).Max();
            var bounds = Bounds(original);
            var x = Math.Clamp((int)Math.Round(CenterX(bounds) - width / 2.0), area.Left, area.Right - width);
            var y = Math.Clamp((int)Math.Round(CenterY(bounds) - height / 2.0), area.Top, area.Bottom - height);
            var targets = new RectI[count];
            for (var p = 0; p < count; p++)
            {
                var i = indices[p];
                targets[i] = new(x + p * dx, y + p * dy, original[i].Width, original[i].Height);
            }
            AddOrders(candidates, "stack", targets, windows);
        }
    }

    internal static void AddOrders(List<Candidate> candidates, string kind, RectI[] rects, VisibleWindow[] windows)
    {
        var n = windows.Length;
        var natural = Enumerable.Range(0, n).ToArray();
        candidates.Add(new(kind, rects, natural));
        if (n is < 2 or > 8 || windows.Any(w => w.Window.IsTopmost)) return;

        var required = new int[n];
        var dependent = new int[n];
        bool hasFreeOverlap = false;
        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            var a = windows[i].Window;
            var b = windows[j].Window;
            bool oldOverlap = !a.VisualRect.Intersect(b.VisualRect).IsEmpty;
            bool newOverlap = !rects[i].Intersect(rects[j]).IsEmpty;
            bool aFirst = oldOverlap || a.IsForeground || a.Owner == b.Handle;
            bool bFirst = b.IsForeground || b.Owner == a.Handle;
            if (aFirst) required[j] |= 1 << i;
            if (bFirst) required[i] |= 1 << j;
            if (aFirst || bFirst || newOverlap)
            {
                dependent[i] |= 1 << j;
                dependent[j] |= 1 << i;
            }
            if (newOverlap && !aFirst && !bFirst) hasFreeOverlap = true;
        }
        if (!hasFreeOverlap) return;

        // 小组完整枚举。大组限制每种几何的候选数，避免 8 扇新交叠窗口耗尽桌面线程。
        // 不能让单一首窗口的 DFS 独占 64 个预算：候选结果会受数组索引顺序影响。
        // 对大组按合法首节点分配预算，先保证每个可作为首层的窗口都有代表性；
        // 某个首节点组合不足时，剩余预算再由后续首节点使用。
        int remaining = n <= 5 ? int.MaxValue : 64;
        var order = new int[n];
        if (n <= 5)
        {
            int unlimited = int.MaxValue;
            Search(0, 0, ref unlimited);
        }
        else
        {
            var roots = Enumerable.Range(0, n).Where(i => required[i] == 0).ToArray();
            for (int root = 0; root < roots.Length && remaining > 0; root++)
            {
                int rootsLeft = roots.Length - root;
                int rootBudget = (remaining + rootsLeft - 1) / rootsLeft;
                int rootRemaining = rootBudget;
                order[0] = roots[root];
                Search(1, 1 << roots[root], ref rootRemaining);
            }
        }

        void Search(int depth, int used, ref int rootRemaining)
        {
            if (depth == n)
            {
                if (!order.AsSpan().SequenceEqual(natural))
                {
                    candidates.Add(new(kind, rects, (int[])order.Clone()));
                    remaining--;
                    rootRemaining--;
                }
                return;
            }
            for (int next = 0; next < n && remaining > 0 && rootRemaining > 0; next++)
            {
                if ((used & (1 << next)) != 0 || (required[next] & ~used) != 0) continue;
                // 只跨过当前前缀末尾连续可交换的窗口。中间如有共同遮挡者，
                // A 与 C 即使不相交，也不能强制 A 永远在 C 前面。
                bool duplicate = false;
                for (int prev = depth - 1; prev >= 0; prev--)
                {
                    if ((dependent[next] & (1 << order[prev])) != 0) break;
                    if (order[prev] > next) { duplicate = true; break; }
                }
                if (duplicate) continue;
                order[depth] = next;
                Search(depth + 1, used | (1 << next), ref rootRemaining);
            }
        }
    }

    private static double StackSignal(VisibleWindow[] windows, RectI[] rects)
    {
        if (windows.Length < 2) return 0;
        var votes = 0;
        var pairs = 0;
        for (var i = 0; i < rects.Length; i++)
        for (var j = i + 1; j < rects.Length; j++)
        {
            var a = rects[i]; var b = rects[j];
            if (a.Intersect(b).Area == 0) continue;
            pairs++;
            var dy = Math.Abs(a.Top - b.Top) / (windows[i].Window.Dpi / 96.0);
            var closeX = Math.Abs(CenterX(a) - CenterX(b)) < Math.Min(a.Width, b.Width) * 0.25;
            if (closeX && dy is >= 22 and <= 130) votes++;
        }
        return pairs == 0 ? 0 : Math.Min(0.8, votes / (double)pairs);
    }

    internal static bool RefinementPreservesExposure(IReadOnlyList<VisibleWindow> visible,
        IReadOnlyList<TidyMove> before, IReadOnlyList<TidyMove> after, IReadOnlyList<WindowLayerOrder> layers)
    {
        var old = before.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        var next = after.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        foreach (var monitor in visible.GroupBy(v => v.Window.MonitorHandle))
        {
            var ordered = monitor.OrderBy(v => v.Window.ZOrder).Select(v => v.Window).ToList();
            foreach (var layer in layers)
            {
                var handles = layer.FrontToBack.Select(w => w.Handle).ToHashSet();
                var slots = ordered.Select((w, i) => (w, i)).Where(p => handles.Contains(p.w.Handle)).Select(p => p.i).ToArray();
                if (slots.Length == layer.FrontToBack.Length)
                    for (var i = 0; i < slots.Length; i++) ordered[slots[i]] = layer.FrontToBack[i];
            }
            var frontOld = new List<RectI>(); var frontNext = new List<RectI>();
            foreach (var w in ordered)
            {
                var a = old.GetValueOrDefault(w.Handle, w.VisualRect);
                var b = next.GetValueOrDefault(w.Handle, w.VisualRect);
                var ea = OcclusionMetrics.Measure(a, frontOld, w.Dpi);
                var eb = OcclusionMetrics.Measure(b, frontNext, w.Dpi);
                if (eb.CenterVisible + 0.02 < ea.CenterVisible || eb.Visible + 0.03 < ea.Visible) return false;
                frontOld.Add(a); frontNext.Add(b);
            }
        }
        return true;
    }
}
