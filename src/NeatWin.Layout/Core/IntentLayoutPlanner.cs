namespace NeatWin.Core;

/// <summary>
/// Compare feasible layouts against window access, readability, and observed working relationships.
/// </summary>
internal static partial class IntentLayoutPlanner
{
    static partial void SearchReplayTranslations(TaskLayoutContext context, VisibleWindow[] group, RectI area,
        Func<Candidate> winner, Action<Candidate> evaluate);

    internal static IReadOnlyList<TidyMove> CreatePlan(IReadOnlyList<VisibleWindow> visible,
        TidyOptions options, IntentReferenceDocument? reference = null) =>
        CreateDetailedPlan(visible, options, reference).Moves;

    internal static IntentLayoutPlan CreateDetailedPlan(IReadOnlyList<VisibleWindow> visible,
        TidyOptions options, IntentReferenceDocument? reference = null,
        IReadOnlyList<WindowSnapshot>? desktop = null, TaskLayoutContext? taskContext = null)
    {
        if (options.AlgorithmMode == TidyAlgorithmMode.Classic)
            return new(new TidyEngine().CreatePlan(visible, options), [], []);
        taskContext ??= new TaskLayoutContext();
        // 已匹配的个人场景可以明确要求重新比较已就绪的布局；其余单纯切换沿用实际验证过的摆法。
        if (!taskContext.ExplicitArrange && (taskContext.VerifiedRepeat ||
            (taskContext.VerifiedTaskRepeat && taskContext.Scene?.Settings?.ReconsiderSettled != true)))
        {
            if (!taskContext.TraceCandidateAxes) return new([], [], []);
            var reason = taskContext.VerifiedRepeat ? "verified-exact-repeat" : "verified-task-repeat";
            return new([], [], [new(visible.Select(v => v.Window.Handle).ToArray(), "keep", 0, [], new([], "search-skipped", [reason]))]);
        }
        // Anchored monitors keep their original location and remain in the desktop occlusion checks.
        desktop ??= visible.Select(v => v.Window).ToArray();
        if (taskContext is { ExplicitArrange: false, IsWorkspaceObservation: true, Attention: not null } &&
            !desktop.Any(w => w.IsForeground)) return new([], [], []);
        taskContext = GesturePlacement.Resolve(ResolveExplicitFocus(visible, taskContext.WithCurrentCorrections(desktop)), visible, options, reference);
        visible = IncludeWorkingCompanion(visible, desktop, taskContext);
        if (taskContext is { ExplicitArrange: false, Attention: not null })
        {
            var focus = ResolveWorkingPair(visible.ToArray(), taskContext).Focus;
            if (focus is not { Main: not 0 } || visible.FirstOrDefault(v => v.Window.Handle == focus.Main) is not { } primary ||
                primary.Window.WorkArea.IsEmpty || primary.Window.VisualRect.IsEmpty)
                return new([], [], []);
            var monitor = primary.Window.MonitorHandle;
            var taskWindows = visible.Where(v => v.Window.MonitorHandle == monitor && !v.Window.VisualRect.IsEmpty && !v.Window.WorkArea.IsEmpty &&
                (v.Window.Handle == focus.Main || v.Window.Handle == focus.Companion || focus.IsNavigationAncestor(v.Window.Handle) ||
                 v.VisibleRatio > 0 && (taskContext.HasRecentInput(v.Window.Handle) ||
                    taskContext.Scene?.PrefersGlance(v.Window.Handle, taskContext) == true))).ToArray();
            // Only invite bystanders that physically cover a working surface (or its needed
            // expansion). Other background windows stay in the occlusion model as obstacles.
            var targets = AttentionLayout.Targets(taskWindows, taskContext with { Focus = focus }, options.SmartStrength)!;
            var needed = taskWindows.Select((item, i) => (item.Window.ZOrder, Current: item.Window.VisualRect,
                Rect: AttentionLayout.WorkingArea(item.Window, targets[i]))).ToArray();
            var bystanders = visible.Except(taskWindows).Where(v => v.Window.MonitorHandle == monitor &&
                v.Window.IsManageable && !v.Window.IsTopmost && v.VisibleRatio > 0)
                .Select(v => (Item: v, Cover: needed.Where(n => v.Window.ZOrder < n.ZOrder &&
                    (!n.Current.Intersect(v.Window.VisualRect).IsEmpty ||
                     !n.Rect.Intersect(v.Window.VisualRect).IsEmpty && RectRegion.FreeRectangles(v.Window.WorkArea,
                         [v.Window.VisualRect], n.Rect.Width, n.Rect.Height).Count == 0))
                    .Select(n => n.Rect.Intersect(v.Window.VisualRect).Area).DefaultIfEmpty().Max()))
                .Where(x => x.Cover > 0).OrderByDescending(x => x.Cover)
                .Take(options.SmartStrength switch { SmartTidyStrength.Gentle => 1, SmartTidyStrength.Balanced => 2, _ => 4 });
            visible = taskWindows.Concat(bystanders.Select(x => x.Item)).ToArray();
            taskContext = taskContext with { Focus = focus };
        }
        var anchors = (taskContext.WindowHints ?? []).Where(h => h.Anchor).Select(h => h.Handle).ToHashSet();
        var moves = new List<TidyMove>();
        var layers = new List<WindowLayerOrder>();
        var traces = new List<LayoutGroupTrace>();
        var strength = options.SmartStrength;
        var localOnly = strength == SmartTidyStrength.Gentle;
        var wholeDisplay = strength == SmartTidyStrength.Assertive;
        var movable = visible.Where(v => v.Window.IsManageable && !v.Window.IsTopmost && !v.Window.VisualRect.IsEmpty &&
            (!anchors.Contains(v.Window.Handle) || taskContext.Attention != null && taskContext.Focus?.Main == v.Window.Handle)).ToArray();
        var settled = movable.ToDictionary(v => v.Window.Handle, v => v.Window.VisualRect);
        var settledRanks = movable.ToDictionary(v => v.Window.Handle, v => v.Window.ZOrder);
        foreach (var monitor in movable.GroupBy(v => v.Window.MonitorHandle))
        {
            var all = monitor.OrderBy(v => v.Window.ZOrder).ThenBy(v => v.Window.Handle).ToArray();
            var area = all[0].Window.WorkArea;
            if (area.IsEmpty) continue;
            var monitorHandles = all.Select(v => v.Window.Handle).ToHashSet();
            bool sharedTask = taskContext is { Attention: not null, ExplicitArrange: false };
            foreach (var group in wholeDisplay || sharedTask ? [all] : Groups(all, taskContext))
            {
                var groupContext = ResolveWorkingPair(group, taskContext);
                var scene = groupContext.Scene?.Monitor == monitor.Key ? groupContext.Scene : null;
                if (scene?.Settings?.AllowResize is bool allow)
                    groupContext = groupContext with { Profile = groupContext.Preferences with { AllowUsefulResize = allow } };
                var weights = scene?.Settings?.Weights;
                var indices = scene?.References.Length > 0 ? group.Select((v, i) => (v.Window.Handle, Index: i))
                    .ToDictionary(p => p.Handle, p => p.Index) : null;
                var handles = group.Select(v => v.Window.Handle).ToHashSet();
                // 配对习惯可用于上一步寻找搭档。确定本轮成员后，和缺席窗口一起学到的几何偏好就不该继续改变当前两栏。
                if (groupContext.IsWorkspaceObservation && groupContext.Placement is { Length: > 0 } preferences)
                    groupContext = groupContext with { Placement = preferences.Where(p => handles.Contains(p.Window) &&
                        (p.Partner == 0 || handles.Contains(p.Partner))).ToArray() };
                var hint = IntentEvidence.Resolve(reference, area, group[0].Window.Dpi, groupContext.ShadowTime ?? DateTimeOffset.UtcNow);
                var original = group.Select(v => v.Window.VisualRect).ToArray();
                var task = InferTask(group, original, groupContext);
                var goals = AttentionLayout.Targets(group, groupContext, strength);
                var alternative = AmbiguousPair.Create(group, task, goals, groupContext);
                var demands = CompileObjectives(group, task, groupContext, goals);
                var order = Enumerable.Range(0, group.Length).ToArray();
                int localPlacement = groupContext.GeometryOnlyWindow == 0 ? -1 :
                    Array.FindIndex(group, v => v.Window.Handle == groupContext.GeometryOnlyWindow);
                if (groupContext.GeometryOnlyWindow != 0 && localPlacement < 0) continue;
                var obstacles = desktop.Where(w => !handles.Contains(w.Handle)).Select(w => w with
                    { VisualRect = settled.GetValueOrDefault(w.Handle, w.VisualRect),
                      ZOrder = settledRanks.GetValueOrDefault(w.Handle, w.ZOrder) })
                    .Where(w => !w.VisualRect.Intersect(area).IsEmpty).ToArray();
                AttentionLayout.SetRestingCenter(group, goals, obstacles, groupContext);
                // Small topmost widgets still affect exposure and suggest obstacle-free candidates.
                var lastRank = group[^1].Window.ZOrder;
                var blockers = obstacles.Where(w => !monitorHandles.Contains(w.Handle) && w.ZOrder < lastRank &&
                    (!w.IsTopmost || w.VisualRect.Intersect(area).Area >= area.Area * .15)).ToArray();
                var packingAreas = localOnly || group.Length < 2 ? [area] : RectRegion.FreeRectangles(area,
                        obstacles.Where(w => w.ZOrder < lastRank || monitorHandles.Contains(w.Handle) || anchors.Contains(w.Handle))
                            .Select(w => w.VisualRect),
                        group.Min(v => Math.Min(v.Window.VisualRect.Width, options.MinimumWidth)),
                        group.Min(v => Math.Min(v.Window.VisualRect.Height, options.MinimumHeight)))
                    .Where(r => r != area).Take(6).Prepend(area).ToArray();
                var beforeExposure = MeasureBeforeExposure(group, original, area, obstacles);
                var stackSignal = StackSignal(group, original);
                TaskCostBreakdown StandardCost(Candidate c) => EffectiveBreakdown(
                    TaskCost(group, original, c, task, demands, area, groupContext, hint, obstacles, goals), options.SmartStrength, task);
                bool priceExternal = demands.Any(o => o.WorkingSurface != null);
                (double Cost, string? Rejection) MeasureReferences(Candidate candidate)
                {
                    if (indices == null) return (0, null);
                    // 已落位窗口和交错层级也参与遮挡并集，以完整桌面的实际露出给后台参照计价。
                    RectI Box(WindowSnapshot w) => indices.TryGetValue(w.Handle, out int i) ? candidate.Rects[i] :
                        settled.GetValueOrDefault(w.Handle, w.VisualRect);
                    int Rank(WindowSnapshot w) => indices.TryGetValue(w.Handle, out int i) ?
                        group[Array.IndexOf(candidate.Order, i)].Window.ZOrder : settledRanks.GetValueOrDefault(w.Handle, w.ZOrder);
                    double cost = 0;
                    foreach (var reference in scene!.References)
                    {
                        var view = reference.Measure(desktop!, Box, Rank);
                        if (view.Lost > reference.Limit + 1e-9) return (cost, "visible-reference:" + reference.Rule);
                        if (priceExternal && !indices.ContainsKey(reference.Window.Handle)) cost += reference.ExposurePrice(view.After);
                    }
                    return (cost, null);
                }
                TaskCostBreakdown Breakdown(Candidate c, double? referenceCost = null)
                {
                    var cost = StandardCost(c);
                    if (priceExternal) cost = cost with { InformationLoss = cost.InformationLoss +
                        (referenceCost ?? MeasureReferences(c).Cost) };
                    if (alternative != null)
                    {
                        // 歧义代价同时考虑一起读和轮流看；所有算法按同一组事前解释比价。
                        cost = cost with { Uncertainty = .5 * cost.Uncertainty + .5 * task[0].Uncertainty *
                            alternative.ExpectedLoss(c, goals!, area) };
                    }
                    return weights?.Apply(cost) ?? cost;
                }
                var viewingModel = groupContext.Calibration?.Matches(area, group[0].Window.Dpi) == true
                    ? "calibrated-flat-screen-angle" : "normalized-distance-not-gaze";
                if (scene?.Settings?.ReconsiderSettled != true && AttentionLayout.SettledWorkspaceReason(group, goals, beforeExposure, obstacles, groupContext) is { } reason)
                {
                    var kept = Breakdown(new("keep", original, order));
                    traces.Add(new(group.Select(v => v.Window.Handle).ToArray(), "keep", stackSignal,
                        [new("keep", kept.Total, null, kept)], new(task, viewingModel, [reason], demands)));
                    continue;
                }
                var generationNotes = new List<LayoutCandidateTrace>();
                var candidates = new List<Candidate> { new("keep", original, order) };
                var rescue = group.Select(v => Fit(v.Window.VisualRect, v.Window, options, groupContext.Preferences.AllowUsefulResize)).ToArray();
                if (!rescue.SequenceEqual(original)) candidates.Add(new("rescue", rescue, order));
                // 给约束一个可用的同侧候选，否则“不要横移”很容易退化成偏小的阅读窗口永远得不到放大。
                if (scene?.Lane is { } inherited && goals != null)
                {
                    int active = Array.FindIndex(group, v => v.Window.Handle == inherited.Front.Handle);
                    if (active >= 0 && goals[active].Movable && inherited.Restore is { } old)
                    {
                        // 先比较用当前尺寸回到熟悉的位置；缩放仍取决于可读性和邻窗。
                        var frames = (RectI[])original.Clone();
                        frames[active] = Fit(frames[active] with { X = old.X, Y = old.Y }, group[active].Window, options, false);
                        candidates.Add(new("restore-working-frame", frames, order));
                    }
                    if (active >= 0 && goals[active] is { Movable: true, Resizable: true } target)
                    {
                        var window = group[active].Window;
                        var frame = window.VisualRect;
                        var grown = AttentionLayout.WorkingArea(window, target);
                        int width = target.LearnedWidth ? target.Width : grown.Width;
                        int x = (int)Math.Round(CenterX(inherited.Restore ?? frame) - width / 2d);
                        var fitted = grown with { X = Math.Clamp(x, area.Left, area.Right - width), Width = width };
                        // 只放大一轴也可能足以改善正文，且能留下来源窗的标题或正文。
                        foreach (var rect in new[] { fitted, frame with { X = fitted.X, Width = fitted.Width },
                            frame with { Y = fitted.Y, Height = fitted.Height } })
                        {
                            var frames = (RectI[])original.Clone();
                            frames[active] = rect;
                            candidates.Add(new("attention-same-side", frames, order));
                        }
                    }
                }
                var local = LogicalLocalPlan(group, options).ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
                candidates.Add(new("local", group.Select((v, i) => local.GetValueOrDefault(v.Window.Handle, original[i])).ToArray(), order));
                if (!localOnly && group.Length >= 2)
                {
                    AddReferenceClearance(candidates, group, original, area, hint.GapPixels, groupContext, goals, order);
                    AddGroupLayouts(candidates, original);
                    if (group.Length is > 2 and <= 12)
                        AddCoveredPairCandidates(candidates, group, rescue, task, area, hint.GapPixels, order);
                    if (groupContext.PairPlanningEnabled) AddWorkingPair(candidates, group, rescue, area, hint.GapPixels, groupContext, order);
                    if (group.Length <= 8)
                    {
                        AddOverlapping(candidates, group, original, area, hint);
                        AddOrders(candidates, "restack", original, group);
                    }
                    AddTaskCandidates(candidates, group, original, area, task, groupContext, hint.GapPixels);
                    if (wholeDisplay)
                        foreach (var packingArea in packingAreas)
                            AddDisplayLayouts(candidates, group, original, packingArea, task, options, groupContext, hint.GapPixels, order);
                }
                if (options.RescueOffscreenWindows) AddClippedEdgeRepairs(candidates, group, original, area, groupContext, goals, order);
                AddAccessRepairs(candidates, group, original, beforeExposure, obstacles, area, hint.GapPixels, order);
                if (localOnly && groupContext.PairPlanningEnabled)
                    AddCoveredWorkingPairRepair(candidates, group, original, obstacles, area, hint.GapPixels, groupContext, order);
                if (groupContext.PairPlanningEnabled && group.Length == 1 &&
                    groupContext.Placement?.FirstOrDefault(p => p.Window == group[0].Window.Handle) is { Confidence: > .40, PreferCenter: true } habit)
                {
                    int width = habit.PreferWidth && !localOnly && group[0].Window.IsResizable && groupContext.Preferences.AllowUsefulResize
                        ? Math.Clamp((int)Math.Round(habit.Width * area.Width), 1, area.Width) : rescue[0].Width;
                    int x = Math.Clamp((int)Math.Round(area.Left + habit.Center * area.Width - width / 2d),
                        area.Left, Math.Max(area.Left, area.Right - width));
                    candidates.Add(new("learned-position", [rescue[0] with { X = x, Width = width }], order));
                }
                if (!localOnly || groupContext.PreferReversibleVerticalFill)
                    AddVerticalFillCandidates(candidates, group, original, groupContext);
                AddLayeredSurfaces(candidates, group, original, goals, demands, area, order, obstacles, allowResize: !localOnly);
                if (!localOnly)
                {
                    alternative?.Propose(candidates, original, goals!, area, order);
                    AddPairProjections(candidates, group, original, task, demands, goals, area, groupContext, hint.GapPixels, order, alternative != null);
                    AddOrderedChain(candidates, group, original, goals, demands, groupContext, area, hint.GapPixels, order);
                }
                if (goals != null)
                    AddAttentionLayouts(candidates, group, original, goals, area, obstacles, hint.GapPixels, order, strength, groupContext, c => Breakdown(c));
                var portfolio = new PortfolioBuilder();
                candidates = candidates.Where(portfolio.Offer).ToList();
                var best = candidates[0]; var baselineBreakdown = Breakdown(best);
                var baseline = baselineBreakdown.Total; var bestScore = baseline;
                var baseThreshold = SelectionThreshold(strength, task) * .60;
                var needsRescue = options.RescueOffscreenWindows && original.Any(r =>
                    r.Intersect(area).Area < r.Area * .90 || r.Top < area.Top || r.Bottom > area.Bottom);
                var audits = new List<LayoutCandidateTrace>(generationNotes) { new("keep", baseline, null, baselineBreakdown) };
                portfolio.Scored(best, baseline, false);
                double minimumThreshold = baseThreshold / (groupContext.InterventionWeight(false) * (weights?.Intervention ?? 1));
                HashSet<RectI>? localTargets = localPlacement >= 0 ? [original[localPlacement]] : null;
                int gestureIndex = Array.FindIndex(group, v => v.Window.Handle == groupContext.GestureAnchor);
                var shadowAxes = (groupContext.ShadowAxes ?? []).Where(axis => handles.Contains(axis.Window)).ToArray();
                var axes = groupContext.TraceCandidateAxes ? new LayoutAxisEvidence() : null;
                axes?.Observe(original, true);
                HeldLayoutAxes? hold = null;
                double fullRank = double.NegativeInfinity;
                foreach (var candidate in candidates.Skip(1)) Evaluate(candidate, 0);
                if (shadowAxes.Any(axis => axis.Change == ShadowChange.Generate))
                {
                    // 每个新候选只改变经过人工回拖验证的那一轴。完整的安全拦截仍由 Evaluate 执行。
                    var seeds = new[] { best, candidates[0] };
                    foreach (var axis in shadowAxes.Where(a => a.Change == ShadowChange.Generate))
                    foreach (var seed in seeds)
                    {
                        int index = Array.FindIndex(group, v => v.Window.Handle == axis.Window);
                        var rects = (RectI[])seed.Rects.Clone();
                        rects[index] = axis.Apply(rects[index]);
                        var candidate = new Candidate("shadow-placement", rects, seed.Order);
                        if (portfolio.Offer(candidate)) Evaluate(candidate, 0);
                    }
                }
                // Finish useful geometry in this call, but freeze task evidence, content demand,
                // safety floors and movement origin. Never learn or re-infer from our own output.
                // This is bounded candidate lookahead, not repeated native window application.
                if (!localOnly && group.Length is >= 2 and <= 8)
                for (var generation = 1; generation <= (wholeDisplay ? 3 : 2) && best.Kind != "keep"; generation++)
                {
                    var seed = best;
                    var completion = new List<Candidate>();
                    AddGroupLayouts(completion, seed.Rects);
                    if (group.Length > 2)
                        AddCoveredPairCandidates(completion, group, seed.Rects, task, area, hint.GapPixels, order);
                    AddTaskCandidates(completion, group, seed.Rects, area, task, groupContext, hint.GapPixels);
                    AddOverlapping(completion, group, seed.Rects, area, hint);
                    foreach (var proposal in completion)
                        if (portfolio.Offer(proposal)) Evaluate(proposal, generation);
                    if (ReferenceEquals(seed, best)) break;
                }

                // 从实际胜出的方案中逐窗试着保留一组原边界。联合解决遮挡仍然可选；没有额外收益的横移或邻窗联动可被删掉。
                // 常见小组比较整窗、两个方向和单独的轴；所有选择沿用本轮证据和提交门槛，不会引发第二次系统摆放。
                // 纯几何调用没有打断的观察依据，保留其对明确整理命令选出的完整方案。
                if (best.Kind != "keep" && group.Length <= 8 && groupContext.Attention != null)
                {
                    fullRank = bestScore;
                    Candidate? measured = null;
                    double withCost = 0;
                    foreach (int i in order.Reverse())
                    {
                        var start = original[i];
                        if (best.Rects[i] == start) continue;
                        var compared = new HashSet<(RectI Current, RectI Simpler)>();
                        foreach (string mask in new[] { "XYWH", "XW", "YH", "X", "Y", "W", "H" })
                        {
                            var target = best.Rects[i];
                            var simpler = new RectI(mask.Contains('X') ? start.X : target.X, mask.Contains('Y') ? start.Y : target.Y,
                                mask.Contains('W') ? start.Width : target.Width, mask.Contains('H') ? start.Height : target.Height);
                            if (simpler == target || !compared.Add((target, simpler))) continue;
                            if (axes != null && !ReferenceEquals(measured, best))
                            {
                                measured = best;
                                withCost = Breakdown(best).Total;
                            }
                            hold = new(i, mask, withCost, null);
                            if (order.Any(j => j != i && (demands[i].WorkingSurface == null || demands[j].WorkingSurface == null) &&
                                simpler.Intersect(best.Rects[j]).Area > target.Intersect(best.Rects[j]).Area))
                            {
                                var blocked = (RectI[])best.Rects.Clone();
                                blocked[i] = simpler;
                                portfolio.Offer(new("retain-existing-edges", blocked, best.Order));
                                if (axes != null)
                                {
                                    axes.Observe(blocked, false);
                                    audits.Add(new("retain-existing-edges", null, "new-peer-overlap", Held: hold));
                                }
                                continue;
                            }
                            var frames = (RectI[])best.Rects.Clone();
                            frames[i] = simpler;
                            Evaluate(new("retain-existing-edges", frames, best.Order), 1);
                        }
                    }
                    hold = null;
                }

                void Evaluate(Candidate candidate, int generation)
                {
                    if (localPlacement >= 0)
                    {
                        // 只让这次获准操作的窗口试用候选位置；按其他窗口保持原位与原层级的真实桌面重算收益。
                        var target = candidate.Rects[localPlacement];
                        var frames = (RectI[])original.Clone();
                        frames[localPlacement] = target;
                        candidate = candidate with { Rects = frames, Order = order };
                        portfolio.Offer(candidate);
                        if (!localTargets!.Add(target)) return;
                    }
                    else portfolio.Offer(candidate);
                    axes?.Observe(candidate.Rects, false);
                    groupContext.ShadowProbe?.Observe(group, candidate.Rects, double.PositiveInfinity);
                    RectI[]? fitted = null, held = null;
                    if (groupContext.VideoFits is { Count: > 0 } fits)
                    for (int i = 0; i < group.Length; i++)
                    {
                        var window = group[i].Window;
                        var slot = candidate.Rects[i];
                        if (!fits.TryGetValue(window.Handle, out var fit) || slot == original[i] || fit.Preserves(original[i], slot) ||
                            window.Handle == groupContext.GestureAnchor || slot.Width == original[i].Width && slot.Height == original[i].Height)
                            continue;
                        // 开窗与焦点切换也比较两条路径：视频随分栏等比缩放，或保住视频、仅调整搭档。所有候选仍经同一套遮挡和阅读检查。
                        fitted ??= (RectI[])candidate.Rects.Clone();
                        held ??= (RectI[])candidate.Rects.Clone();
                        fitted[i] = fit.Inside(slot, original[i]);
                        held[i] = original[i];
                    }
                    if (fitted != null)
                    {
                        audits.Add(new(candidate.Kind, null, "video-viewport-aspect", Generation: generation));
                        foreach (var rects in new[] { fitted, held! })
                        {
                            var adjusted = candidate with { Rects = rects };
                            if (portfolio.Offer(adjusted)) EvaluateFeasible(adjusted, generation);
                        }
                        return;
                    }
                    EvaluateFeasible(candidate, generation);
                }

                void EvaluateFeasible(Candidate candidate, int generation)
                {
                    var targetLookup = candidate.Order.SequenceEqual(order) ? null :
                        Enumerable.Range(0, group.Length).ToDictionary(i => group[i].Window.Handle, i => candidate.Rects[i]);
                    if (groupContext.Attention != null && Array.Exists(order, i =>
                        groupContext.LimitReturn(group[i].Window, candidate.Rects[i]) != candidate.Rects[i]))
                    {
                        var bounded = order.Select(i => groupContext.LimitReturn(group[i].Window, candidate.Rects[i])).ToArray();
                        bool repeated = Array.Exists(order, i => groupContext.RepeatsReversal(group[i].Window, candidate.Rects[i]));
                        audits.Add(new(candidate.Kind, null, repeated ? "repeated-manual-reversal" : "manual-return-amplitude", Generation: generation));
                        var smaller = candidate with { Rects = bounded };
                        if (portfolio.Offer(smaller)) Evaluate(smaller, generation);
                        return;
                    }
                    var rejection = scene?.Lane is { } lane && Array.Exists(order, i => !lane.Allows(group[i].Window, candidate.Rects[i]))
                        ? "attention-side-transfer" : !candidate.Order.SequenceEqual(order) &&
                        !LayerOrderSafe(group, candidate, order)
                        ? "layer-preserve-foreground-or-occluded" : !candidate.Order.SequenceEqual(order) &&
                        !WindowLayerSafety.CanReorder(candidate.Order.Select(i => group[i].Window).ToArray(), desktop ?? all.Select(v => v.Window).ToArray(), targetLookup)
                        ? "layer-band-or-interleaved-window" : !TaskSafe(group, original, candidate, area, options, groupContext, obstacles, beforeExposure, goals, demands) ? "geometry-budget" :
                        HitsOutsideGroup(settled, group, original, candidate.Rects) ? "other-group" :
                        blockers.Any(w => candidate.Rects.Select((r, i) => r.Intersect(w.VisualRect).Area > original[i].Intersect(w.VisualRect).Area).Any(b => b)) ? "fixed-occluder" :
                        null;
                    double referenceCost = 0;
                    if (rejection == null)
                        (referenceCost, rejection) = MeasureReferences(candidate);
                    if (rejection is not null)
                    {
                        audits.Add(new(candidate.Kind, null, rejection, Generation: generation, Held: axes != null ? hold : null));
                        // 只有一轴挤到参考内容时仍可改善另一轴。拆开试算后继续走相同的可读性、比例、输入与遮挡检查。
                        if (rejection.StartsWith("visible-reference:", StringComparison.Ordinal) &&
                            !candidate.Kind.StartsWith("reference-fit-", StringComparison.Ordinal) && candidate.Order.SequenceEqual(order))
                        {
                            foreach (bool horizontal in new[] { true, false })
                            {
                                var slots = candidate.Rects.Select((r, i) => horizontal ?
                                    original[i] with { X = r.X, Width = r.Width } : original[i] with { Y = r.Y, Height = r.Height }).ToArray();
                                var split = new Candidate(horizontal ? "reference-fit-x" : "reference-fit-y", slots, order);
                                if (portfolio.Offer(split)) Evaluate(split, generation);
                            }
                        }
                        return;
                    }
                    var breakdown = Breakdown(candidate, referenceCost);
                    var score = breakdown.Total;
                    axes?.Observe(candidate.Rects, breakdown.Feedback == 0);
                    groupContext.ShadowProbe?.Observe(group, candidate.Rects, breakdown.Feedback);
                    // 松手位置提供摆放意图。补齐落点的收益依然要和原位比较，不能给整组窗口无条件让位。
                    bool followsGesture = groupContext.AllowGestureAssist && gestureIndex >= 0 &&
                        groupContext.GestureTarget == candidate.Rects[gestureIndex] && candidate.Rects[gestureIndex] != original[gestureIndex];
                    var weight = groupContext.InterventionWeight(followsGesture) * (weights?.Intervention ?? 1);
                    var threshold = baseThreshold / weight;
                    // 默认居中仅为已值得执行的候选打破近似平局，不能补齐对原位方案的收益门槛。
                    // 偏好最多影响半个基础门槛。已学习的个人槽位仍有自己的评分项。
                    double? centerTieCost = null;
                    if (goals is [{ DefaultCenter: true, RestingCenter: double middle }])
                        centerTieCost = baseThreshold * Math.Abs(CenterX(candidate.Rects[0]) - middle) / area.Width;
                    audits.Add(new(candidate.Kind, score, null, breakdown, generation, weight, threshold, centerTieCost,
                        axes != null && hold != null ? hold with { GainFromMoving = score - hold.OriginalCost } : null));
                    // 只给已满足可读性和收益门槛的候选一个小的排序优势。影子偏好无法替代实际空间收益。
                    bool matchesShadowRank = shadowAxes.Any(axis => axis.Change == ShadowChange.Rank &&
                        axis.Matches(candidate.Rects[Array.FindIndex(group, v => v.Window.Handle == axis.Window)]));
                    double rank = score + (centerTieCost ?? 0) - (matchesShadowRank ? baseThreshold * .5 : 0);
                    minimumThreshold = Math.Min(minimumThreshold, threshold);
                    if (breakdown.Feedback == 0) portfolio.Scored(candidate, rank, score < baseline - threshold);
                    // 对未经手势点名的附带位移，若省下该轴仍有几乎相同的净收益，保留原边界。
                    // 所有删减共同使用完整方案的一份余量，避免逐轴放宽后把本来要解决的问题丢掉。
                    bool sameBenefit = hold != null && candidate.Kind == "retain-existing-edges" &&
                        groupContext.IsWorkspaceObservation && candidate.Order.SequenceEqual(best.Order) &&
                        rank <= fullRank + Math.Min(threshold * .10, baseThreshold * .25);
                    if (breakdown.Feedback == 0 && (score < baseline - threshold && (best.Kind == "keep" || rank < bestScore || sameBenefit) ||
                        best.Kind == "keep" && needsRescue && candidate.Kind == "rescue"))
                    { best = candidate; bestScore = rank; }
                }

                // 这个可选的 partial 方法只在离线回放项目中实现。桌面构建会消除整个调用。
                SearchReplayTranslations(groupContext, group, area, () => best,
                    proposal => { if (portfolio.Offer(proposal)) Evaluate(proposal, 1); });

                string reputationContext = alternative != null ? "ambiguous-pair" : task.Select(r => r.Purpose)
                    .FirstOrDefault(p => p != null) ?? (group.Length == 1 ? "single" : "workspace");
                if (group.Length >= 3) reputationContext = "multi-window";
                var continuation = alternative == null ? null :
                    PairContinuation.Create(alternative.Main, alternative.Peer, group, goals!, area, obstacles, groupContext);
                var decision = portfolio.Finish(best, bestScore, reputationContext, .10 * minimumThreshold,
                    groupContext.SolverRatings, groupContext.ShadowTime ?? DateTimeOffset.UtcNow, alternative?.Alternatives,
                    continuation, continuationBudget: .25 * minimumThreshold);
                best = decision.Chosen;
                for (var i = 0; i < group.Length; i++)
                {
                    settled[group[i].Window.Handle] = best.Rects[i];
                    settledRanks[group[best.Order[i]].Window.Handle] = group[i].Window.ZOrder;
                    if (best.Rects[i] != group[i].Window.VisualRect) moves.Add(new(group[i].Window, best.Rects[i]));
                }
                if (!best.Order.SequenceEqual(order)) layers.Add(new(best.Order.Select(i => group[i].Window).ToArray()));
                traces.Add(new(group.Select(v => v.Window.Handle).ToArray(), best.Kind, stackSignal, audits.ToArray(),
                    new(task, viewingModel,
                        ["hypothesis-weights-not-calibrated-probabilities", "content-prior-not-semantic-recognition", "stable-is-not-satisfaction",
                         .. shadowAxes.Select(axis => $"shadow-placement:{axis.Axis}:{axis.Change}")], demands), axes?.Export(), decision.Portfolio));

                void AddGroupLayouts(List<Candidate> output, RectI[] seed)
                {
                    foreach (var packingArea in packingAreas)
                    foreach (var columns in RowShapes(group.Length, packingArea))
                    {
                        var kind = columns == 1 ? "rows" : columns == group.Length ? "columns" : "grid";
                        var packed = new List<RectI[]>();
                        AddPacked(packed, group, seed, packingArea, hint.GapPixels, columns, false);
                        if (columns == group.Length && groupContext.Preferences.AllowUsefulResize)
                            AddPacked(packed, group, seed, packingArea, hint.GapPixels, columns, true);
                        if (packed.Count == 0 && groupContext.Preferences.AllowUsefulResize && group.All(v => v.Window.IsResizable) &&
                            task.Any(r => r.Joint > r.Parked && CoversHalfOfRear(original[r.First], original[r.Second])))
                            AddPacked(packed, group, seed, packingArea, hint.GapPixels, columns, false, shrinkToFit: true);
                        if (packed.Count == 0 && packingArea == area && ReferenceEquals(output, candidates))
                            generationNotes.Add(new(kind, null, "no-size-safe-packing"));
                        foreach (var rects in packed) output.Add(new(kind, rects, order));
                    }
                }
            }
        }
        return new(moves, layers, traces);
    }

    // A manual command can arrive after the utility window has held focus for a while.
    // Anchor the request on the foremost eligible external surface in the captured desktop.
    private static TaskLayoutContext ResolveExplicitFocus(IReadOnlyList<VisibleWindow> visible, TaskLayoutContext context)
    {
        if (!context.ExplicitArrange || context.Attention == null || context.Focus is { Main: not 0 }) return context;
        var main = visible.Where(v => v.Window.IsManageable && !v.Window.IsTopmost &&
                !v.Window.WorkArea.IsEmpty && !v.Window.VisualRect.IsEmpty)
            .OrderBy(v => v.Window.ZOrder).FirstOrDefault();
        return main == null ? context : context with { Focus = new(main.Window.Handle, 0, 0, "explicit-front-window") };
    }

    internal static TaskCostBreakdown EffectiveBreakdown(TaskCostBreakdown breakdown, SmartTidyStrength strength,
        TaskEvidence[]? relations = null)
    {
        // Stronger Smart means lower resistance to useful geometry reflow, not weaker visibility,
        // access, edge protection or negative feedback. A confidently staged/parked task remains
        // deliberately conservative: "I left this nearby" is different from "finish arranging it".
        var parked = ConfidentlyParked(relations);
        var adaptationScale = parked ? strength switch
        {
            SmartTidyStrength.Gentle => 1.15,
            SmartTidyStrength.Assertive => 0.75,
            _ => 1.00,
        } : strength switch
        {
            SmartTidyStrength.Gentle => 1.05,
            SmartTidyStrength.Assertive => 0.40,
            _ => 0.68,
        };
        return breakdown with
        {
            Continuity = breakdown.Continuity * adaptationScale,
            Reflow = breakdown.Reflow * adaptationScale,
        };
    }

    internal static double TaskScore(TaskCostBreakdown breakdown, SmartTidyStrength strength,
        TaskEvidence[]? relations = null) => EffectiveBreakdown(breakdown, strength, relations).Total;

    internal static double SelectionThreshold(SmartTidyStrength strength, TaskEvidence[]? relations = null)
    {
        if (ConfidentlyParked(relations))
            return strength switch
            {
                SmartTidyStrength.Gentle => 0.18,
                SmartTidyStrength.Assertive => 0.025,
                _ => 0.065,
            };
        return strength switch
        {
            SmartTidyStrength.Gentle => 0.14,
            SmartTidyStrength.Assertive => 0.005,
            _ => 0.03,
        };
    }

    private static bool ConfidentlyParked(TaskEvidence[]? relations)
    {
        if (relations is null || relations.Length == 0) return false;
        var parked = relations.Max(r => r.Parked * (1 - r.Uncertainty));
        var joint = relations.Max(r => r.Joint * (1 - r.Uncertainty));
        return parked >= .65 && parked > joint + .15;
    }

    private static bool LayerOrderSafe(VisibleWindow[] group, Candidate candidate, int[] natural)
    {
        if (candidate.Order.SequenceEqual(natural)) return true;
        var positions = new int[group.Length];
        for (var rank = 0; rank < candidate.Order.Length; rank++) positions[candidate.Order[rank]] = rank;

        // Geometry may be assertive, but Smart must not "surface" a background card by stealing
        // the user's current front relation. Foreground is a strong explicit interaction signal.
        var foreground = Array.FindIndex(group, item => item.Window.IsForeground);
        if (foreground >= 0 && positions[foreground] != 0) return false;

        // Clicking our own UI leaves no external IsForeground flag. Preserve existing
        // occlusion relationships anyway; a visible-area gain is not permission to surface a
        // parked window. Use geometry, not potentially stale/caller-supplied visible ratios.
        for (var back = 1; back < group.Length; back++)
        for (var front = 0; front < back; front++)
            if (positions[back] < positions[front] &&
                group[front].Window.VisualRect.Intersect(group[back].Window.VisualRect).Area > 0)
                return false;
        return true;
    }

    private static string CandidateKey(Candidate c) => string.Join(";", c.Rects) + ":" + string.Join(",", c.Order);

    private static IEnumerable<VisibleWindow[]> Groups(VisibleWindow[] windows, TaskLayoutContext context)
    {
        var remaining = new HashSet<int>(Enumerable.Range(0, windows.Length));
        while (remaining.Count != 0)
        {
            var indices = new List<int> { remaining.Min() };
            remaining.Remove(indices[0]);
            for (var i = 0; i < indices.Count; i++)
                foreach (var j in remaining.ToArray())
                    if (TaskAffinity(windows[indices[i]].Window, windows[j].Window, context) > 0)
                    {
                        indices.Add(j);
                        remaining.Remove(j);
                    }
            yield return indices.Order().Select(i => windows[i]).ToArray();
        }
    }

    private static void AddPacked(List<RectI[]> candidates, VisibleWindow[] windows, RectI[] original,
        RectI area, int gap, int columns, bool equalize, bool shrinkToFit = false)
    {
        var horizontal = columns == windows.Length;
        var order = Enumerable.Range(0, windows.Length);
        var sorted = horizontal
            ? order.OrderBy(i => CenterX(original[i])).ThenBy(i => i).ToArray()
            : order.OrderBy(i => CenterY(original[i])).ThenBy(i => CenterX(original[i])).ToArray();
        var rows = sorted.Chunk(columns).Select(row => row.OrderBy(i => CenterX(original[i])).ToArray()).ToArray();
        var sizes = original.ToArray();
        if (equalize)
        {
            var median = original.Select(r => r.Height).Order().ElementAt(original.Length / 2);
            for (var i = 0; i < windows.Length; i++)
                if (windows[i].Window.IsResizable && Math.Abs(median - sizes[i].Height) <= sizes[i].Height * 0.12)
                    sizes[i] = sizes[i] with { Height = median };
        }
        if (shrinkToFit)
        {
            // Three large overlapping windows can fit in two rows even when neither the
            // original heights nor a single row fit. Fit only the constrained axes.
            var heightScale = (area.Height - gap * (rows.Length - 1)) /
                (double)rows.Sum(row => row.Max(i => sizes[i].Height));
            var widthScale = rows.Min(row => (area.Width - gap * (row.Length - 1)) /
                (double)row.Sum(i => sizes[i].Width));
            var sx = Math.Min(1, widthScale); var sy = Math.Min(1, heightScale);
            if (sx <= 0 || sy <= 0) return;
            for (var i = 0; i < sizes.Length; i++)
                sizes[i] = sizes[i] with { Width = (int)Math.Floor(sizes[i].Width * sx),
                    Height = (int)Math.Floor(sizes[i].Height * sy) };
        }
        foreach (var row in rows)
        {
            var width = row.Sum(i => sizes[i].Width) + gap * (row.Length - 1);
            if (width <= area.Width) continue;
            var fixedWidth = row.Where(i => !windows[i].Window.IsResizable).Sum(i => sizes[i].Width);
            var flexibleWidth = row.Where(i => windows[i].Window.IsResizable).Sum(i => sizes[i].Width);
            var available = area.Width - fixedWidth - gap * (row.Length - 1);
            if (flexibleWidth == 0 || available <= 0) return;
            var ratio = available / (double)flexibleWidth;
            if (ratio < 0.75) return;
            foreach (var i in row.Where(i => windows[i].Window.IsResizable))
                sizes[i] = sizes[i] with { Width = (int)Math.Floor(sizes[i].Width * ratio) };
        }
        var rowHeights = rows.Select(row => row.Max(i => sizes[i].Height)).ToArray();
        var totalHeight = rowHeights.Sum() + gap * (rows.Length - 1);
        if (totalHeight > area.Height) return;
        var totalWidth = rows.Max(row => row.Sum(i => sizes[i].Width) + gap * (row.Length - 1));
        var bounds = Bounds(original);
        var left = Math.Clamp((int)Math.Round(CenterX(bounds) - totalWidth / 2.0), area.Left, area.Right - totalWidth);
        var top = Math.Clamp((int)Math.Round(CenterY(bounds) - totalHeight / 2.0), area.Top, area.Bottom - totalHeight);
        var edgeRadius = (int)Math.Round(24 * windows[0].Window.Dpi / 96.0);
        if (Math.Abs(bounds.Left - area.Left) <= edgeRadius) left = area.Left;
        else if (Math.Abs(bounds.Right - area.Right) <= edgeRadius) left = area.Right - totalWidth;
        if (Math.Abs(bounds.Top - area.Top) <= edgeRadius) top = area.Top;
        else if (Math.Abs(bounds.Bottom - area.Bottom) <= edgeRadius) top = area.Bottom - totalHeight;
        var target = new RectI[windows.Length];
        var y = top;
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var x = left;
            foreach (var i in rows[rowIndex])
            {
                target[i] = new RectI(x, y, sizes[i].Width, sizes[i].Height);
                x += sizes[i].Width + gap;
            }
            y += rowHeights[rowIndex] + gap;
        }
        candidates.Add(target);
    }

    private static bool HitsOutsideGroup(IReadOnlyDictionary<nint, RectI> settled, VisibleWindow[] group, RectI[] original, RectI[] target)
    {
        var handles = group.Select(v => v.Window.Handle).ToHashSet();
        foreach (var other in settled.Where(v => !handles.Contains(v.Key)))
            for (var i = 0; i < group.Length; i++)
                if (target[i].Intersect(other.Value).Area > original[i].Intersect(other.Value).Area)
                    return true;
        return false;
    }

    private static RectI Fit(RectI rect, WindowSnapshot window, TidyOptions options, bool allowResize = true)
    {
        if (!options.RescueOffscreenWindows || window.WorkArea.IsEmpty) return rect;
        var area = window.WorkArea;
        var width = window.IsResizable && allowResize ? Math.Min(rect.Width, area.Width) : rect.Width;
        var height = window.IsResizable && allowResize ? Math.Min(rect.Height, area.Height) : rect.Height;
        return new RectI(width <= area.Width ? Math.Clamp(rect.Left, area.Left, area.Right - width) : area.Left,
            height <= area.Height ? Math.Clamp(rect.Top, area.Top, area.Bottom - height) : area.Top, width, height);
    }

    private static RectI Bounds(RectI[] rects) => RectI.FromEdges(rects.Min(r => r.Left), rects.Min(r => r.Top), rects.Max(r => r.Right), rects.Max(r => r.Bottom));
    private static double CenterX(RectI r) => r.Left + r.Width / 2.0;
    private static double CenterY(RectI r) => r.Top + r.Height / 2.0;
}
