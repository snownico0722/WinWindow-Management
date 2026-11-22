using NeatWin.Core;

internal static class SemanticLayoutChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckProjection(check);
        CheckSceneDispatch(check);
        CheckTraffic(check);
    }

    private static void CheckProjection(Action<bool, string> check)
    {
        // 这个小工作区能穷举所有像素。参照答案逐项枚举，独立于生产公式和真实快照。
        const int start = -3, end = 5;
        var cases = 0;
        foreach (var lw in new[] { 2, 5, 11 })
        foreach (var rw in new[] { 2, 6, 13 })
        foreach (var lx in new[] { -4, -2, 1 })
        foreach (var rx in new[] { -2, 1, 5 })
        foreach (var movableLeft in new[] { false, true })
        foreach (var movableRight in new[] { false, true })
        foreach (var ratio in new[] { .5, 1, 3 })
        foreach (var gap in new[] { -3, -1, 0, 2 })
        {
            var left = new HorizontalSlot(lx, lw, 2, movableLeft, 1);
            var right = new HorizontalSlot(rx, rw, 2, movableRight, ratio);
            var solution = PairWidthProjection.Solve(start, end, left, right, gap);
            var best = double.PositiveInfinity;
            for (int a = start; a < end; a++)
            for (int b = start; b < end; b++)
            for (int p = 2; a + p <= end; p++)
            for (int q = 2; b + q <= end; q++)
            {
                if (Math.Min(p, q) <= -gap || b < a + p + gap || !movableLeft && a != lx || !movableRight && b != rx) continue;
                best = Math.Min(best, WidthCost(p, q));
            }
            // 投影分两层优化。等价最优宽度可以相差一个像素，此时只要求固定该宽度后的位移达到最优。
            if (solution is { } s)
            {
                var widthPrice = WidthCost(s.LeftWidth, s.RightWidth);
                var distance = (s.LeftX - lx) * (s.LeftX - lx) + ratio * (s.RightX - rx) * (s.RightX - rx);
                double positionBest = double.PositiveInfinity;
                for (int a = start; a + s.LeftWidth <= end; a++)
                for (int b = start; b + s.RightWidth <= end; b++)
                    if (b >= a + s.LeftWidth + gap && (movableLeft || a == lx) && (movableRight || b == rx))
                        positionBest = Math.Min(positionBest, (a - lx) * (a - lx) + ratio * (b - rx) * (b - rx));
                var repeat = PairWidthProjection.Solve(start, end,
                    left with { X = s.LeftX, PreferredWidth = s.LeftWidth },
                    right with { X = s.RightX, PreferredWidth = s.RightWidth }, gap);
                if (repeat != solution || s.LeftX < start || s.RightX + s.RightWidth > end || s.RightX < s.LeftX + s.LeftWidth + gap ||
                    s.LeftWidth < 2 || s.RightWidth < 2 || !movableLeft && s.LeftX != lx || !movableRight && s.RightX != rx ||
                    widthPrice != best || distance != positionBest)
                    throw new Exception($"Projection disagrees with enumeration at {left}, {right}, gap {gap}: {s}, cost {best}");
            }
            else if (double.IsFinite(best)) throw new Exception($"Missed feasible projection at {left}, {right}, gap {gap}");
            cases++;

            double WidthCost(int p, int q) => (p - lw) * (p - lw) + ratio * (q - rw) * (q - rw);
        }
        check(cases == 3888, "weighted projection agrees with exhaustive feasible widths and positions including oversized wishes, pins and overlap");
    }

    private static void CheckSceneDispatch(Action<bool, string> check)
    {
        const string activeKey = "111111111111111111111111", peerKey = "222222222222222222222222";
        var rule = new LayoutScene("two-readers", "workspace", [activeKey], peerKey, ForegroundUse: "editing",
            ReferenceUse: "reading", Intent: "read-together");
        var rules = new CompiledLayoutRules(Version: 7, Scenes: [rule]);
        check(rules.IsValid() && !(rules with { Version = 6 }).IsValid() &&
            !(rules with { Scenes = [rule with { ReferenceApp = null }] }).IsValid() &&
            !(rules with { Scenes = [rule with { ForegroundUse = "unknown" }] }).IsValid(),
            "scoped semantic fields require v7, a reference app and recognized instance uses");
        var keys = new Dictionary<nint,string?> { [1] = activeKey, [2] = peerKey, [3] = peerKey };
        var uses = new Dictionary<nint,string> { [1] = "editing", [2] = "reading", [3] = "reading" };
        var ctx = new TaskLayoutContext(Rules: rules, UseWorkingPairs: true, Focus: new(1, 0, 0),
            Attention: new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, .8, 0, true), [2] = new(.85, .8, .8, 0, false) })
            { IsWorkspaceObservation = true, AppKeys = keys, InstanceUses = uses };
        var projected = 0;
        var selected = 0;
        foreach (int width in new[] { 1600, 1920, 2520 })
        foreach (uint dpi in new uint[] { 96, 144 })
        foreach (bool mirror in new[] { false, true })
        foreach (double frontWidth in new[] { .42, .58, .74 })
        {
            int height = width == 2520 ? 1080 : width * 9 / 16;
            var area = new RectI(-width, 24, width, height);
            WindowSnapshot W(int id, double x, double y, double w, double h)
            {
                var r = new RectI(area.X + (int)((mirror ? 1 - x - w : x) * width), area.Y + (int)(y * height),
                    (int)(w * width), (int)(h * height));
                return new(id, r, r, area, 1, default, true, id == 1, true, id - 1, dpi, (uint)id);
            }
            var front = W(1, .08, .04, frontWidth, .77);
            var peer = W(2, .49, .12, .48, .73);
            WindowSnapshot[] windows = [front, peer];
            IntentLayoutPlan Run(TaskLayoutContext c) => LayoutDispatch.Create(LayoutRoute.Smart,
                new VisibilityAnalyzer().SelectForManualArrange(windows, true), new(), null, windows, c);
            TaskLayoutContext For(string purpose) => ctx with { Rules = rules with { Scenes = [rule with { Intent = purpose }] } };
            var read = Run(ctx);
            var group = read.Groups.Single();
            if (group.Candidates.Any(c => c.Kind == "weighted-pair-projection")) projected++;
            if (group.Selected == "weighted-pair-projection")
            {
                selected++;
                if (read.Moves.Any(m => m.Window.VisualRect.Y != m.TargetVisualRect.Y || m.Window.VisualRect.Height != m.TargetVisualRect.Height))
                    throw new Exception("Horizontal projection changed an unrelated vertical axis");
            }
            var alternate = Run(For("alternate"));
            var glance = Run(For("keep-in-view"));
            var guard = glance.Scene!.References.Single(r => r.Window.Handle == 2);
            if (group.Task is not { Relations: [{ Purpose: "read-together", Source: "scene:two-readers" }], Objectives.Length: 2 } ||
                alternate.Groups.Single().Task?.Relations.Single().Purpose != "alternate" ||
                glance.Groups.Single().Task?.Relations.Single().Purpose != "keep-in-view" ||
                alternate.Groups.Concat(glance.Groups).Any(g => g.Candidates.Any(c => c.Kind == "weighted-pair-projection")) ||
                guard.Limit != .10 || !glance.Scene.Allows(windows, glance))
                throw new Exception("A semantic instruction did not reach its own objective, exposure guard or candidate family");
            var duplicate = peer with { Handle = 3 };
            foreach (var missing in new[] { ctx with { InstanceUses = null }, ctx with { InstanceUses = new Dictionary<nint,string> { [1] = "reading", [2] = "reading" } } })
                if (ActiveLayoutScene.Resolve(windows, missing)?.Settings != null) throw new Exception("Guessed an unknown instance use");
            if (ActiveLayoutScene.Resolve([front, peer, duplicate], ctx)?.Settings != null ||
                ActiveLayoutScene.Resolve([front, peer with { MonitorHandle = 2 }], ctx)?.Settings != null)
                throw new Exception("Guessed an ambiguous or off-monitor reference");
            foreach (var stronger in new[] { ctx with { Focus = new(1, 2, 1, "handoff", [2]) },
                ctx with { PairHints = [new(1, 2, TaskRelation.Alternating)] }, ctx with { LocalPlacementWindow = 1 } })
                if (read.Scene!.Interpret(stronger, 1) != stronger || glance.Scene!.PrefersGlance(2, stronger))
                    throw new Exception("A prior rule overrode a current task");
        }
        check(projected > 0 && selected > 0, $"independent desktop permutations can propose and select horizontal projection ({projected}/36 proposed, {selected}/36 selected)");
    }

    private static void CheckTraffic(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1600, 900);
        var windows = Enumerable.Range(1, 6).Select(i => new WindowSnapshot(i, area, area, area, 1, default,
            true, i == 1, true, i, 96, (uint)i)).ToArray();
        (double Association, nint Companion) ReadVisits(int[] sequence)
        {
            var history = new ForegroundHistory();
            long tick = 0;
            foreach (int id in sequence) { history.Visit(id, (uint)id, tick); tick += 650; }
            var focus = history.Resolve(windows, tick, out var attention);
            return (attention[2].Association, focus.Companion);
        }
        var focused = ReadVisits([1, 2, 1, 2, 1, 2, 1]);
        var singleAlternative = ReadVisits([1, 2, 1, 3, 1, 2, 1, 3, 1, 2, 1, 3, 1]);
        var manyAlternatives = ReadVisits([1, 2, 1, 3, 1, 2, 1, 4, 1, 2, 1, 5, 1]);
        check(focused.Companion == 2 && focused.Association > singleAlternative.Association &&
            singleAlternative.Association > manyAlternatives.Association,
            "the same visits to a candidate provide less pair evidence when the foreground also switches among unrelated destinations");
    }
}
