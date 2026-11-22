namespace NeatWin.Core;

// 先减去排在前面的宽度与间距，左右不颠倒、不碰撞就化为一串非递减坐标。
// 合并相互冲突的相邻块得到最小加权位移；不可移动的窗口截断这串约束，远处有余量的窗保持原位。
internal static class OrderedWindowChain
{
    private readonly record struct Block(int First, int Last, double Weight, double Sum)
    {
        internal double Mean => Sum / Weight;
    }

    internal static int[]? Place(int start, int end, HorizontalSlot[] slots, int gap)
    {
        if (slots.Length == 0 || gap < 0) return null;
        var offset = new int[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].PreferredWidth < 1 || !(slots[i].Resistance > 0)) return null;
            if (i > 0) offset[i] = offset[i-1] + slots[i-1].PreferredWidth + gap;
        }
        int upper = end - offset[^1] - slots[^1].PreferredWidth;
        if (upper < start) return null;
        var result = new int[slots.Length];
        var blocks = new List<Block>();
        int lower = start;
        for (int i = 0; i <= slots.Length; i++)
        {
            if (i < slots.Length && slots[i].Movable)
            {
                double w = slots[i].Resistance;
                var b = new Block(i, i, w, w * (slots[i].X - (double)offset[i]));
                while (blocks.Count > 0 && blocks[^1].Mean > b.Mean)
                {
                    var prev = blocks[^1]; blocks.RemoveAt(blocks.Count - 1);
                    b = new(prev.First, i, prev.Weight + b.Weight, prev.Sum + b.Sum);
                }
                blocks.Add(b);
                continue;
            }
            int bound = i == slots.Length ? upper : slots[i].X - offset[i];
            if (bound < lower || bound > upper) return null;
            foreach (var b in blocks)
            for (int j = b.First; j <= b.Last; j++)
                result[j] = offset[j] + (int)Math.Round(Math.Clamp(b.Mean, lower, bound));
            blocks.Clear();
            if (i < slots.Length) result[i] = slots[i].X;
            lower = bound;
        }
        return result;
    }
}

internal static partial class IntentLayoutPlanner
{
    private static void AddOrderedChain(List<Candidate> candidates, VisibleWindow[] group, RectI[] original,
        AttentionTarget[]? goals, WindowLayoutObjective[] objectives, TaskLayoutContext ctx, RectI area, int gap, int[] order)
    {
        if (group.Length < 3 || goals == null || ctx.GeometryOnlyWindow != 0 || ctx.GestureAnchor != 0 || ctx.Scene?.Lane != null ||
            ctx.Focus?.NavigationAncestors is { Length: > 0 } || ctx.Focus?.Navigation is { Length: > 0 } ||
            ctx.Focus?.Evidence == "handoff" || ctx.Scene?.Settings?.Intent is "keep-in-view" or "alternate" ||
            ctx.PairHints?.Any(h => h.Relation == TaskRelation.Alternating) == true) return;
        var members = order.Where(i => goals[i].Role is AttentionRole.Main or AttentionRole.Reference)
            .OrderBy(i => CenterX(original[i])).ToArray();
        // 纵向有共同阅读区域才视作同一排；窗外的参与者在完整方案内继续当作原位障碍。
        if (members.Length < 3 || members.Min(i => original[i].Bottom) <= members.Max(i => original[i].Top)) return;
        int budget = area.Width - gap * (members.Length-1);
        foreach (bool resizeMain in new[] { false, true })
        {
            var slots = members.Select(i =>
            {
                int width = original[i].Width;
                int floor = goals[i].Resizable ? Math.Clamp(Math.Max(ctx.MinimumSize(group[i].Window.Handle).Width,
                    goals[i].MinimumFrameWidth > 0 ? goals[i].MinimumFrameWidth : goals[i].Width), 1, width) : width;
                int wish = resizeMain && goals[i] is { Role: AttentionRole.Main, Resizable: true }
                    ? Math.Max(width, goals[i].Width) : width;
                return new HorizontalSlot(original[i].X, wish, floor, goals[i].Movable,
                    (1 + objectives[i].Motion.Resistance * objectives[i].Motion.Timing) / width);
            }).ToArray();
            if (slots.Sum(s => s.MinimumWidth) > budget) continue;
            if (slots.Sum(s => s.PreferredWidth) > budget)
            {
                // 拥挤时按阻力共同承担宽度损失。向下取整留出的几个像素保持为空隙。
                double lo = 0, hi = slots.Max(s => (s.PreferredWidth - s.MinimumWidth) * s.Resistance);
                for (int pass = 0; pass < 40; pass++)
                {
                    double mid = (lo + hi) / 2;
                    if (slots.Sum(s => Math.Max(s.MinimumWidth, s.PreferredWidth - mid / s.Resistance)) > budget) lo = mid;
                    else hi = mid;
                }
                slots = slots.Select(s => s with { PreferredWidth = (int)Math.Floor(Math.Max(s.MinimumWidth,
                    s.PreferredWidth - hi / s.Resistance)) }).ToArray();
            }
            var xs = OrderedWindowChain.Place(area.Left, area.Right, slots, gap);
            if (xs == null) continue;
            var frames = original.ToArray();
            for (int j = 0; j < members.Length; j++)
                frames[members[j]] = frames[members[j]] with { X = xs[j], Width = slots[j].PreferredWidth };
            candidates.Add(new("ordered-chain", frames, order));
        }
    }
}
