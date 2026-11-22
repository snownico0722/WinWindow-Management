namespace NeatWin.Core;

// 左右顺序、允许的重叠和每扇窗的代价由任务层给出。求解器只看工作区内的一维长度与约束。
internal readonly record struct HorizontalSlot(int X, int PreferredWidth, int MinimumWidth,
    bool Movable, double Resistance);

internal static class PairWidthProjection
{
    internal static (int LeftX, int LeftWidth, int RightX, int RightWidth)? Solve(
        int start, int end, HorizontalSlot left, HorizontalSlot right, int separation)
    {
        // 底层分两步：先找最接近期望的可行宽度，然后在该宽度上最小化加权的水平位移平方和。
        // 允许负间距共享已获准的内边。宽度或固定位置使约束不可行时，交给其他候选。
        int extent = end - start;
        if (extent <= 0 || left.MinimumWidth < 1 || right.MinimumWidth < 1 ||
            left.MinimumWidth > extent || right.MinimumWidth > extent ||
            left.Resistance <= 0 || right.Resistance <= 0) return null;
        // 重叠量不能吞掉整扇窗。两窗至少各留一列未共享像素，连同原生最小宽度一起投影。
        int leftFloor = Math.Max(left.MinimumWidth, 1 - separation);
        int rightFloor = Math.Max(right.MinimumWidth, 1 - separation);
        int leftOrigin = left.Movable ? start : left.X;
        if (leftOrigin < start || leftOrigin >= end || !right.Movable && (right.X < start || right.X >= end)) return null;
        int maxLeft = right.Movable ? end - leftOrigin : Math.Min(end - leftOrigin, right.X - leftOrigin - separation);
        int maxRight = right.Movable ? extent : end - right.X;
        if (maxLeft < leftFloor || maxRight < rightFloor) return null;
        int lw = Math.Clamp(left.PreferredWidth, leftFloor, maxLeft);
        int rw = Math.Clamp(right.PreferredWidth, rightFloor, maxRight);
        double rightShare = right.Resistance / (left.Resistance + right.Resistance);
        int budget = end - leftOrigin - separation;
        if (lw + rw > budget)
        {
            int low = Math.Max(leftFloor, budget - maxRight);
            int high = Math.Min(maxLeft, budget - rightFloor);
            if (low > high) return null;
            // 用原始期望求分界。先把过大的期望裁到屏宽再分摊，会低估这扇窗损失的阅读宽度。
            double optimal = left.PreferredWidth + rightShare * (budget - (double)right.PreferredWidth - left.PreferredWidth);
            lw = (int)Math.Round(Math.Clamp(optimal, low, high));
            rw = budget - lw;
        }
        int leftMin = left.Movable ? start : left.X, leftMax = left.Movable ? end - lw : left.X;
        int rightMin = right.Movable ? start : right.X, rightMax = right.Movable ? end - rw : right.X;
        if (leftMin < start || leftMax + lw > end || rightMin < start || rightMax + rw > end) return null;
        int lx = Math.Clamp(left.X, leftMin, leftMax), rx = Math.Clamp(right.X, rightMin, rightMax);
        int offset = lw + separation;
        if (rx - lx < offset)
        {
            // 违反内边界约束时最优点位于两窗相接的直线上。投影原位置后只需裁到两侧共同可达区间。
            int low = Math.Max(leftMin, rightMin - offset), high = Math.Min(leftMax, rightMax - offset);
            if (low > high) return null;
            double seam = left.X + (right.X - offset - (double)left.X) * rightShare;
            lx = Math.Clamp((int)Math.Round(seam), low, high);
            rx = lx + offset;
        }
        return (lx, lw, rx, rw);
    }
}

internal static partial class IntentLayoutPlanner
{
    private static void AddPairProjections(List<Candidate> candidates, VisibleWindow[] group, RectI[] original,
        TaskEvidence[] relations, WindowLayoutObjective[] objectives, AttentionTarget[]? goals,
        RectI area, TaskLayoutContext context, int gap, int[] order)
    {
        // 位置相近只能说明两窗有几何关系。共同阅读、学习到的搭档或个人任务指令才提供分宽的理由。
        if (group.Length != 2 || goals == null || relations is not [{ Purpose: "read-together", SameColumn: false }] ||
            context.GeometryOnlyWindow != 0 || context.Focus?.NavigationAncestors is { Length: > 0 } ||
            goals.Any(g => g.Role is not (AttentionRole.Main or AttentionRole.Reference)) ||
            original[0].Top >= original[1].Bottom || original[1].Top >= original[0].Bottom) return;
        var pair = PairPreferenceFor(group[0].Window.Handle, group[1].Window.Handle, context);
        int left = pair?.Order switch { -1 => 0, 1 => 1, _ => CenterX(original[0]) <= CenterX(original[1]) ? 0 : 1 };
        int right = 1 - left;
        // 先保尺寸试平移，再把可读目标交给同一投影。已足够的间距不会因美观被收紧。
        Propose(false, gap);
        Propose(true, gap);
        int allowance = pair?.Overlap is double share
            ? (int)Math.Floor(Math.Min(original[left].Width, original[right].Width) * share)
            : Math.Max(0, goals[left].FrameWidth - goals[left].Width) + Math.Max(0, goals[right].FrameWidth - goals[right].Width);
        if (allowance > 0 && !context.Preferences.ProtectWindowEdges)
            Propose(true, -Math.Min(allowance, Math.Min(goals[left].Width, goals[right].Width) - 1));

        void Propose(bool resize, int separation)
        {
            HorizontalSlot Slot(int i)
            {
                var g = goals[i];
                int width = original[i].Width;
                bool canResize = resize && g.Resizable;
                int target = canResize ? Math.Max(g.Width, g.FrameWidth) : width;
                int floor = canResize ? Math.Max(1, Math.Max(context.MinimumSize(group[i].Window.Handle).Width,
                    g.MinimumFrameWidth)) : width;
                // 同样的像素位移，在窄窗和正在阅读的窗上更明显。几何投影使用正定代价，最终仍以完整二维投影计分。
                double resistance = (1 + objectives[i].Motion.Resistance * objectives[i].Motion.Timing) / Math.Max(1, width);
                return new(original[i].X, target, floor, g.Movable, resistance);
            }
            var result = PairWidthProjection.Solve(area.Left, area.Right, Slot(left), Slot(right), separation);
            if (result is not { } p || !goals[left].Resizable && p.LeftWidth != original[left].Width ||
                !goals[right].Resizable && p.RightWidth != original[right].Width) return;
            var frames = (RectI[])original.Clone();
            frames[left] = frames[left] with { X = p.LeftX, Width = p.LeftWidth };
            frames[right] = frames[right] with { X = p.RightX, Width = p.RightWidth };
            candidates.Add(new("weighted-pair-projection", frames, order));
        }
    }
}
