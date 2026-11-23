namespace NeatWin.Core;

// 宽、高是切回此窗时有用的外框，单位是本轮工作区像素。它们不参加总宽度归一化。
public sealed record WorkingSurfaceObjective(int Width, int Height, double Value, double Simultaneous)
{
    internal double ExposureLoss(RectI frame, OcclusionMetrics.Exposure exposure)
    {
        // 使用裁掉屏外和前方遮挡后的实际像素。缩窄后台窗不能仅靠提高“自身可见比例”得到收益。
        double visible = frame.Area * exposure.Visible / ((double)Width * Height);
        // 舒适面积以上仍有露出收益，但应递减，否则大窗会仅为覆盖桌面而膨胀。
        return 4 * Value * (1 - Math.Sqrt(Math.Min(1, visible)) - .12 * Math.Log(1 + Math.Max(0, visible - 1)));
    }

    internal double FrameLoss(RectI frame)
    {
        // 宽度变小通常会改变换行、表格列与工具栏。切回后依然要付这个代价，单纯损失一个边缘条带则不会。
        return 5 * Value * (Math.Max(0, Math.Log(Width / (double)Math.Max(1, frame.Width))) +
            .6 * Math.Max(0, Math.Log(Height / (double)Math.Max(1, frame.Height))));
    }

    internal static bool IsGlanceSurface(nint handle, TaskLayoutContext ctx) =>
        ctx.Scene?.PrefersGlance(handle, ctx) == true ||
        ctx.WindowHints?.Any(h => h.Handle == handle && !h.Anchor && (h.PassiveVisual || h.MediaCompanion)) == true;

    internal static WorkingSurfaceObjective?[]? Compile(VisibleWindow[] group, TaskEvidence[] task,
        AttentionTarget[]? goals, TaskLayoutContext ctx, ContentViewport?[] content)
    {
        if (goals == null || group.Length < 2 || ctx.GeometryOnlyWindow != 0 || ctx.GestureAnchor != 0) return null;
        var members = Enumerable.Range(0, group.Length).Where(i =>
            goals[i].Role is AttentionRole.Main or AttentionRole.Reference or AttentionRole.Return ||
            IsGlanceSurface(group[i].Window.Handle, ctx)).ToArray();
        if (members.Length < 2) return null;
        var area = group[0].Window.WorkArea;
        var working = members.ToDictionary(i => i, i => WorkingFrame(group[i].Window, ctx));
        // 两个方向都拥挤时，牺牲外框来换取并排尤其昂贵。用已有或明确需要的尺寸，
        // 在接近满屏到超出约两成之间连续过渡。候选几何不能改变这次竞争的预算。
        double load = Math.Min(members.Sum(i => (double)Math.Max(working[i].Width, group[i].Window.VisualRect.Width)) / area.Width,
            members.Sum(i => (double)Math.Max(working[i].Height, group[i].Window.VisualRect.Height)) / area.Height);
        double pressure = Math.Clamp((load - .98) / .18, 0, 1);
        var result = new WorkingSurfaceObjective?[group.Length];
        foreach (int i in members)
        {
            var w = group[i].Window;
            var g = goals[i];
            var h = ctx.WindowHints?.FirstOrDefault(hint => hint.Handle == w.Handle);
            var relationships = task.Where(r => r.First == i && members.Contains(r.Second) ||
                                                r.Second == i && members.Contains(r.First));
            double simultaneous = relationships.Select(r => r.Purpose switch
            {
                "read-together" => 1, "alternate" => .08, "keep-in-view" => .25,
                // 弱证据留出两种可能。两窗可切回的宽高与当前露出的面积分别计价。
                _ => 1 - pressure * (1 - .35 * r.Joint / Math.Max(.01, r.Joint + r.Parked))
            }).DefaultIfEmpty(1).Max();
            // 看得久的后台窗也有工作价值；一次点击或输入法消息不会改变它的面积预算。
            var signal = ctx.Attention?.GetValueOrDefault(w.Handle);
            double sustained = signal == null ? 0 : signal.Recency * (.65 * signal.Dwell + .35 * signal.Association);
            double value = (g.Role == AttentionRole.Main ? .85 : .55) + .45 * Math.Clamp(sustained, 0, 1)
                + (h?.PassiveVisual == true ? .25 : 0);
            // 当前正文被顶层工具挡住与主动留给搭档的叠放性质不同，前者仍要恢复可读性。
            if (g.Role == AttentionRole.Main) simultaneous = Math.Max(simultaneous, 1 - group[i].VisibleRatio);
            var (width, height) = working[i];
            if (content[i] is { } measured)
            {
                // The measured picture, rather than its bars or browser chrome, is the working surface.
                var picture = measured.Content(w.VisualRect);
                width = picture.Width;
                height = picture.Height;
            }
            result[i] = new(width, height, value, simultaneous);
        }
        return result;
    }

    private static (int Width, int Height) WorkingFrame(WindowSnapshot w, TaskLayoutContext ctx)
    {
        var hint = ctx.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle);
        var prior = ctx.Placement?.FirstOrDefault(p => p.Window == w.Handle && p.Confidence >= .4);
        double scale = w.Dpi == 0 ? 1 : w.Dpi / 96d;
        var signal = ctx.Attention?.GetValueOrDefault(w.Handle);
        double used = Math.Clamp((signal?.Dwell ?? 0) * (signal?.Recency ?? 0), 0, 1);
        int Extent(int current, int screen, double? dip, double? fraction, PlacementBand? band, double comfort)
        {
            if (dip.HasValue) return Math.Max(1, (int)Math.Round(dip.Value * scale));
            if (fraction is > 0 and <= 1)
            {
                int size = Math.Max(1, (int)Math.Round(fraction.Value * screen));
                return band?.SizeTarget(current, screen, size) ?? size;
            }
            // 小窗不凭空获得扩张需求。对长期使用的大外框保留部分额外宽高，切回时能够少一次重新排版。
            double typical = comfort * scale;
            return Math.Max(1, (int)Math.Round(Math.Min(current, typical) + used * Math.Max(0, current - typical)));
        }
        // 工作目标可大于物理屏幕。并排提案已经分摊过的 FrameWidth 不能反过来成为用户的理想宽度。
        return (Extent(w.VisualRect.Width, w.WorkArea.Width, hint?.UsefulWidthDip is > 0 and <= 4000 ? hint.UsefulWidthDip : null,
                prior?.WidthConfidence >= .4 ? prior.Width : null, prior?.Bands?.Width, ctx.Preferences.ComfortableWidthDip),
            Extent(w.VisualRect.Height, w.WorkArea.Height, hint?.UsefulHeightDip is > 0 and <= 3000 ? hint.UsefulHeightDip : null,
                prior?.HeightConfidence >= .4 ? prior.Height : null, prior?.Bands?.Height, ctx.Preferences.ComfortableHeightDip));
    }
}

internal static partial class IntentLayoutPlanner
{
    private static void AddLayeredSurfaces(List<Candidate> candidates, VisibleWindow[] group, RectI[] original,
        AttentionTarget[]? goals, WindowLayoutObjective[] objectives, RectI area, int[] order, WindowSnapshot[] obstacles, bool allowResize)
    {
        if (goals == null || !objectives.Any(o => o.WorkingSurface != null)) return;
        var spread = original.ToArray();
        // 先保住上层窗。对每个后台窗试工作区边界和已有前景边界，既有左右也有上下。
        // 各单窗提案和一次组内组合都进入全局评估，层级不变，多个遮挡物只扣一次面积。
        for (int i = 1; i < group.Length; i++)
        {
            if (objectives[i].WorkingSurface is not { } surface || !goals[i].Movable) continue;
            var frame = original[i];
            if (frame.Width > area.Width || frame.Height > area.Height) continue;
            var front = spread.Take(i).Concat(obstacles.Where(w => w.ZOrder < group[i].Window.ZOrder)
                .Select(w => w.VisualRect)).Where(r => !r.Intersect(area).IsEmpty).ToArray();
            if (!front.Any(r => !r.Intersect(frame).IsEmpty)) continue;
            var positions = new HashSet<RectI> { frame };
            void Offer(RectI r) => positions.Add(r with { X = Math.Clamp(r.X, area.Left, area.Right - r.Width),
                Y = Math.Clamp(r.Y, area.Top, area.Bottom - r.Height) });
            Offer(frame with { X = area.Left }); Offer(frame with { X = area.Right - frame.Width });
            Offer(frame with { Y = area.Top }); Offer(frame with { Y = area.Bottom - frame.Height });
            foreach (var cover in front.Distinct().OrderByDescending(r => r.Intersect(frame).Area)
                         .Where(r => !r.Intersect(frame).IsEmpty).Take(6))
            {
                Offer(frame with { X = cover.Right }); Offer(frame with { X = cover.Left - frame.Width });
                Offer(frame with { Y = cover.Bottom }); Offer(frame with { Y = cover.Top - frame.Height });
                // 在保留原框和完整并排之间增加一个中间解。当前仍留空的条带决定能少缩多少。
                if (!allowResize || !goals[i].Resizable) continue;
                foreach (bool below in new[] { false, true })
                {
                    int free = below ? area.Bottom - cover.Bottom : cover.Top - area.Top;
                    if (free <= 0 || free >= frame.Height) continue;
                    int height = (free + frame.Height) / 2;
                    Offer(frame with { Y = below ? area.Bottom - height : area.Top, Height = height });
                }
                foreach (bool right in new[] { false, true })
                {
                    int free = right ? area.Right - cover.Right : cover.Left - area.Left;
                    if (free <= 0 || free >= frame.Width) continue;
                    int width = (free + frame.Width) / 2;
                    Offer(frame with { X = right ? area.Right - width : area.Left, Width = width });
                }
            }
            double best = double.PositiveInfinity;
            foreach (var r in positions)
            {
                // 大组逐个全桌面评估所有接边会成倍放大求解时延。保留每扇的最佳单窗解和一次组合解。
                if (r != frame && group.Length <= 8)
                {
                    var seed = original.ToArray();
                    seed[i] = r;
                    candidates.Add(new("layered-workspace", seed, order));
                }
                var content = objectives[i].Content?.Content(r) ?? r;
                var exposure = ScreenExposure(content, front, area, group[i].Window.Dpi);
                double price = surface.ExposureLoss(content, exposure) + surface.FrameLoss(content) +
                    objectives[i].Motion.Cost(frame, r);
                if (price < best) { best = price; spread[i] = r; }
            }
            if (group.Length > 8 && spread[i] != frame)
            {
                var seed = original.ToArray();
                seed[i] = spread[i];
                candidates.Add(new("layered-workspace", seed, order));
            }
        }
        if (!spread.SequenceEqual(original)) candidates.Add(new("layered-workspace", spread, order));
    }
}
