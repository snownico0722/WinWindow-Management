namespace NeatWin.Core;

/// <summary>
/// Compare task-conditioned local layouts. A manual endpoint is evidence, not the optimum.
/// Follow-hand assistance remains a separate small correction.
/// </summary>
internal static partial class IntentLayoutPlanner
{
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
        if (taskContext.VerifiedRepeat) return new([], [], []);
        var moves = new List<TidyMove>();
        var layers = new List<WindowLayerOrder>();
        var traces = new List<LayoutGroupTrace>();
        foreach (var monitor in visible.Where(v => v.Window.IsManageable && !v.Window.VisualRect.IsEmpty)
            .GroupBy(v => v.Window.MonitorHandle))
        {
            var all = monitor.OrderBy(v => v.Window.ZOrder).ThenBy(v => v.Window.Handle).ToArray();
            var area = all[0].Window.WorkArea;
            if (area.IsEmpty) continue;
            var settled = all.ToDictionary(v => v.Window.Handle, v => v.Window.VisualRect);
            var settledRanks = all.ToDictionary(v => v.Window.Handle, v => v.Window.ZOrder);
            foreach (var group in Groups(all, taskContext))
            {
                var hint = IntentEvidence.Resolve(reference, area, group[0].Window.Dpi, DateTimeOffset.UtcNow);
                var original = group.Select(v => v.Window.VisualRect).ToArray();
                var task = InferTask(group, original, taskContext);
                var order = Enumerable.Range(0, group.Length).ToArray();
                var blockers = desktop?.Where(w => !all.Any(v => v.Window.Handle == w.Handle) &&
                    w.MonitorHandle == group[0].Window.MonitorHandle && w.ZOrder < group.Max(v => v.Window.ZOrder)).ToArray() ?? [];
                var obstacles = desktop?.Where(w => !group.Any(v => v.Window.Handle == w.Handle) &&
                    w.MonitorHandle == group[0].Window.MonitorHandle).Select(w => w with
                    { VisualRect = settled.GetValueOrDefault(w.Handle, w.VisualRect),
                      ZOrder = settledRanks.GetValueOrDefault(w.Handle, w.ZOrder) }).ToArray() ?? [];
                var generationNotes = new List<LayoutCandidateTrace>();
                var candidates = new List<Candidate> { new("keep", original, order) };
                var rescue = group.Select(v => Fit(v.Window.VisualRect, v.Window, options, taskContext.Preferences.AllowUsefulResize)).ToArray();
                if (!rescue.SequenceEqual(original)) candidates.Add(new("rescue", rescue, order));
                var local = LogicalLocalPlan(group, options).ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
                candidates.Add(new("local", group.Select((v, i) => local.GetValueOrDefault(v.Window.Handle, original[i])).ToArray(), order));
                if (group.Length is >= 2 and <= 12)
                {
                    AddPackedCandidate("columns", group.Length, false);
                    AddPackedCandidate("columns", group.Length, true);
                    AddPackedCandidate("rows", 1, false);
                    for (var columns = 2; columns < group.Length; columns++)
                        AddPackedCandidate("grid", columns, false);
                    AddOverlapping(candidates, group, original, area, hint);
                    AddOrders(candidates, "restack", original, group);
                    AddTaskCandidates(candidates, group, original, area, task, taskContext, hint.GapPixels);
                }
                AddVerticalFillCandidates(candidates, group, original, taskContext);
                var stackSignal = StackSignal(group, original);
                TaskCostBreakdown Breakdown(Candidate c) => EffectiveBreakdown(
                    TaskCost(group, original, c, task, area, taskContext, hint, obstacles), options.SmartStrength, task);
                candidates = candidates.DistinctBy(c => string.Join(";", c.Rects) + ":" + string.Join(",", c.Order)).ToList();
                var best = candidates[0]; var baselineBreakdown = Breakdown(best);
                var baseline = baselineBreakdown.Total; var bestScore = baseline;
                var beforeExposure = MeasureBeforeExposure(group, original, area, obstacles);
                var audits = new List<LayoutCandidateTrace>(generationNotes) { new("keep", baseline, null, baselineBreakdown) };
                var seen = candidates.Select(CandidateKey).ToHashSet();
                foreach (var candidate in candidates.Skip(1)) Evaluate(candidate, 0);
                // Finish useful geometry in this call, but freeze task evidence, content demand,
                // safety floors and movement origin. Never learn or re-infer from our own output.
                // This is bounded candidate lookahead, not repeated native window application.
                if (group.Length is >= 2 and <= 8)
                for (var generation = 1; generation <= 2 && best.Kind != "keep"; generation++)
                {
                    var seed = best;
                    var completion = new List<Candidate>();
                    for (var columns = 1; columns <= group.Length; columns++)
                    {
                        var packed = new List<RectI[]>();
                        AddPacked(packed, group, seed.Rects, area, hint.GapPixels, columns, false);
                        var kind = columns == 1 ? "rows" : columns == group.Length ? "columns" : "grid";
                        foreach (var rects in packed) AddOrders(completion, kind, rects, group);
                    }
                    AddTaskCandidates(completion, group, seed.Rects, area, task, taskContext, hint.GapPixels);
                    AddOverlapping(completion, group, seed.Rects, area, hint);
                    foreach (var proposal in completion)
                        if (seen.Add(CandidateKey(proposal))) Evaluate(proposal, generation);
                    if (ReferenceEquals(seed, best)) break;
                }

                void Evaluate(Candidate candidate, int generation)
                {
                    var rejection = !candidate.Order.SequenceEqual(order) &&
                        !LayerOrderSafe(group, candidate, order)
                        ? "layer-preserve-foreground-or-occluded" : !candidate.Order.SequenceEqual(order) &&
                        !WindowLayerSafety.CanReorder(group.Select(v => v.Window).ToArray(), desktop ?? all.Select(v => v.Window).ToArray())
                        ? "layer-band-or-interleaved-window" : !TaskSafe(group, original, candidate, area, options, taskContext, obstacles, beforeExposure) ? "geometry-budget" :
                        HitsOutsideGroup(settled, group, original, candidate.Rects) ? "other-group" :
                        blockers.Any(w => candidate.Rects.Select((r, i) => r.Intersect(w.VisualRect).Area > original[i].Intersect(w.VisualRect).Area).Any(b => b)) ? "fixed-occluder" :
                        null;
                    if (rejection is not null) { audits.Add(new(candidate.Kind, null, rejection, Generation: generation)); return; }
                    var breakdown = Breakdown(candidate);
                    var score = breakdown.Total;
                    audits.Add(new(candidate.Kind, score, null, breakdown, generation));
                    var threshold = SelectionThreshold(options.SmartStrength, task);
                    var needsRescue = options.RescueOffscreenWindows && original.Any(r =>
                        r.Intersect(area).Area < r.Area * .90 || r.Top < area.Top || r.Bottom > area.Bottom);
                    if ((score < bestScore && score < baseline - threshold) || (best.Kind == "keep" && needsRescue && candidate.Kind == "rescue"))
                    { best = candidate; bestScore = score; }
                }

                for (var i = 0; i < group.Length; i++)
                {
                    settled[group[i].Window.Handle] = best.Rects[i];
                    settledRanks[group[best.Order[i]].Window.Handle] = group[i].Window.ZOrder;
                    if (best.Rects[i] != group[i].Window.VisualRect) moves.Add(new(group[i].Window, best.Rects[i]));
                }
                if (!best.Order.SequenceEqual(order)) layers.Add(new(best.Order.Select(i => group[i].Window).ToArray()));
                traces.Add(new(group.Select(v => v.Window.Handle).ToArray(), best.Kind, stackSignal, audits.ToArray(),
                    new(task, taskContext.Calibration?.Matches(area, group[0].Window.Dpi) == true ? "calibrated-flat-screen-angle" : "normalized-distance-not-gaze",
                        ["hypothesis-weights-not-calibrated-probabilities", "content-prior-not-semantic-recognition", "stable-is-not-satisfaction"])));

                void AddPackedCandidate(string kind, int columns, bool equalize)
                {
                    var packed = new List<RectI[]>();
                    AddPacked(packed, group, original, area, hint.GapPixels, columns, equalize);
                    if (packed.Count == 0) generationNotes.Add(new(kind, null, "no-size-safe-packing"));
                    foreach (var rects in packed) candidates.Add(new(kind, rects, order));
                }
            }
        }
        return new(moves, layers, traces);
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
        RectI area, int gap, int columns, bool equalize)
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
