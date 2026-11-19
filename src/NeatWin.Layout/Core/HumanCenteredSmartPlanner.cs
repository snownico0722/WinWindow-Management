namespace NeatWin.Core;

/// <summary>
/// Smart behavior A: generate a small set of complete layouts that a person could plausibly have
/// arranged by hand, score them with a population human-factors model, then add a bounded personal
/// preference residual. This avoids the old failure mode where four window edges were optimized as
/// mostly independent forces and could collectively stretch or pull a window in surprising ways.
/// </summary>
internal static class HumanCenteredSmartPlanner
{
    private const int HumanGapPixels = 8;
    private const double MinimumUsefulAttention = 0.08;

    internal static SmartPlanResult CreatePlan(
        IReadOnlyList<VisibleWindow> visibleWindows,
        TidyOptions options,
        SmartInteractionContext? interactionContext,
        SmartPersonalizationState personalization)
    {
        if (visibleWindows.Count == 0)
        {
            return new SmartPlanResult(Array.Empty<TidyMove>(), null);
        }

        interactionContext ??= SmartInteractionContext.Empty;
        personalization = personalization.Normalize();
        var moves = new List<TidyMove>();
        SmartLearningSnapshot? learning = null;
        var learningAttention = double.NegativeInfinity;

        foreach (var monitorGroup in visibleWindows.GroupBy(static item => item.Window.MonitorHandle))
        {
            var windows = monitorGroup.ToArray();
            if (windows.Length == 0)
            {
                continue;
            }

            var workArea = windows[0].Window.WorkArea;
            if (workArea.IsEmpty)
            {
                continue;
            }

            var candidate = ChooseCandidate(
                windows,
                workArea,
                options,
                interactionContext,
                personalization);

            foreach (var window in windows)
            {
                var target = candidate.Targets[window.Window.Handle];
                if (options.RescueOffscreenWindows)
                {
                    target = FitInsideWorkArea(window.Window, target);
                }

                if (HasMeaningfulChange(window.Window.VisualRect, target))
                {
                    moves.Add(new TidyMove(window.Window, target));
                }
            }

            var attention = windows.Sum(item => interactionContext.AttentionFor(item.Window.Handle));
            if (windows.Any(static item => item.Window.IsForeground))
            {
                attention += 2.0;
            }

            if (attention > learningAttention)
            {
                learningAttention = attention;
                learning = new SmartLearningSnapshot(
                    candidate.Archetype,
                    candidate.Features.ToArray(),
                    candidate.Targets.ToDictionary(static pair => pair.Key, static pair => pair.Value));
            }
        }

        return new SmartPlanResult(moves, learning);
    }

    internal static SmartLearningSnapshot DescribeCurrentLayout(
        IReadOnlyList<VisibleWindow> visibleWindows,
        SmartInteractionContext? interactionContext,
        SmartPersonalizationState personalization)
    {
        interactionContext ??= SmartInteractionContext.Empty;
        personalization = personalization.Normalize();
        if (visibleWindows.Count == 0)
        {
            return new SmartLearningSnapshot(
                AutoLayoutArchetype.Preserve,
                new double[SmartPersonalizationState.AutoFeatureCount],
                new Dictionary<nint, RectI>());
        }

        var monitor = visibleWindows
            .GroupBy(static item => item.Window.MonitorHandle)
            .OrderByDescending(group =>
                group.Sum(item => interactionContext.AttentionFor(item.Window.Handle)) +
                (group.Any(static item => item.Window.IsForeground) ? 2.0 : 0.0))
            .First();
        var windows = monitor.ToArray();
        var targets = windows.ToDictionary(static item => item.Window.Handle, static item => item.Window.VisualRect);
        var workArea = windows[0].Window.WorkArea;
        var archetype = ClassifyArchetype(windows, targets, workArea, interactionContext);
        var features = ExtractFeatures(windows, targets, workArea, interactionContext, personalization);
        return new SmartLearningSnapshot(archetype, features, targets);
    }

    private static Candidate ChooseCandidate(
        IReadOnlyList<VisibleWindow> windows,
        RectI workArea,
        TidyOptions options,
        SmartInteractionContext interaction,
        SmartPersonalizationState personalization)
    {
        var gap = EffectiveGap(personalization);
        var focus = SelectFocusWindow(windows, interaction);
        var candidates = new List<Candidate>(10);

        AddCandidate(candidates, AutoLayoutArchetype.Preserve, windows.ToDictionary(
            static item => item.Window.Handle,
            static item => item.Window.VisualRect));
        AddCandidate(candidates, AutoLayoutArchetype.CleanCurrent, CleanCurrentLayout(windows, workArea, options, interaction, gap));

        if (windows.Count >= 2)
        {
            AddCandidate(candidates, AutoLayoutArchetype.EqualColumns, BuildEqualColumns(windows, workArea, gap));
            AddCandidate(candidates, AutoLayoutArchetype.EqualRows, BuildEqualRows(windows, workArea, gap));

            if (windows.Count >= 3)
            {
                AddCandidate(candidates, AutoLayoutArchetype.Grid, BuildGrid(windows, workArea, gap));
            }

            var mainRatio = EffectiveMainRatio(personalization);
            AddCandidate(candidates, AutoLayoutArchetype.FocusLeft, BuildFocusLayout(windows, focus, workArea, gap, mainRatio, AutoLayoutArchetype.FocusLeft));
            AddCandidate(candidates, AutoLayoutArchetype.FocusRight, BuildFocusLayout(windows, focus, workArea, gap, mainRatio, AutoLayoutArchetype.FocusRight));
            AddCandidate(candidates, AutoLayoutArchetype.FocusTop, BuildFocusLayout(windows, focus, workArea, gap, mainRatio, AutoLayoutArchetype.FocusTop));
            AddCandidate(candidates, AutoLayoutArchetype.FocusBottom, BuildFocusLayout(windows, focus, workArea, gap, mainRatio, AutoLayoutArchetype.FocusBottom));
        }

        var intentEvidence = LayoutIntentEvidence(windows, workArea);
        var focusStrength = FocusStrength(windows, focus, interaction);
        Candidate? preserve = null;

        foreach (var candidate in candidates)
        {
            candidate.Features = ExtractFeatures(
                windows,
                candidate.Targets,
                workArea,
                interaction,
                personalization);
            candidate.HumanScore = HumanFactorScore(
                candidate,
                windows,
                intentEvidence,
                focusStrength,
                options.SmartStrength);
            candidate.PersonalScore = SmartPersonalizationLearner.ScoreAutoCandidate(
                personalization,
                candidate.Archetype,
                candidate.Features);
            candidate.TotalScore = candidate.HumanScore + candidate.PersonalScore;

            if (candidate.Archetype == AutoLayoutArchetype.Preserve)
            {
                preserve = candidate;
            }
        }

        var best = candidates.OrderByDescending(static item => item.TotalScore).First();
        if (preserve is not null && best.Archetype != AutoLayoutArchetype.Preserve)
        {
            var requiredImprovement = options.SmartStrength switch
            {
                SmartTidyStrength.Gentle => 0.80,
                SmartTidyStrength.Assertive => -0.05,
                _ => 0.28,
            };

            if (best.TotalScore < preserve.TotalScore + requiredImprovement)
            {
                best = preserve;
            }
        }

        return best;
    }

    private static void AddCandidate(
        ICollection<Candidate> candidates,
        AutoLayoutArchetype archetype,
        Dictionary<nint, RectI> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        // Avoid scoring exact duplicates under different labels. The simpler archetype wins because
        // it carries fewer assumptions about the user's intent.
        foreach (var existing in candidates)
        {
            if (SameLayout(existing.Targets, targets))
            {
                return;
            }
        }

        candidates.Add(new Candidate(archetype, targets));
    }

    private static Dictionary<nint, RectI> CleanCurrentLayout(
        IReadOnlyList<VisibleWindow> windows,
        RectI workArea,
        TidyOptions options,
        SmartInteractionContext interaction,
        int preferredGap)
    {
        var result = windows.ToDictionary(
            static item => item.Window.Handle,
            static item => item.Window.VisualRect);
        var snapRadius = options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => 12,
            SmartTidyStrength.Assertive => 28,
            _ => 20,
        };
        var alignRadius = options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => 8,
            SmartTidyStrength.Assertive => 18,
            _ => 12,
        };

        // Screen snap is a translation, not a resize. If opposite margins are both plausible and
        // nearly symmetric, leave them alone rather than arbitrarily choosing a side.
        foreach (var window in windows)
        {
            var rect = result[window.Window.Handle];
            var left = workArea.Left - rect.Left;
            var right = workArea.Right - rect.Right;
            var top = workArea.Top - rect.Top;
            var bottom = workArea.Bottom - rect.Bottom;
            var dx = ChooseUnambiguousSnapShift(left, right, snapRadius);
            var dy = ChooseUnambiguousSnapShift(top, bottom, snapRadius);
            result[window.Window.Handle] = Translate(rect, dx, dy);
        }

        for (var pass = 0; pass < 2; pass++)
        {
            for (var first = 0; first < windows.Count; first++)
            {
                for (var second = first + 1; second < windows.Count; second++)
                {
                    var aWindow = windows[first];
                    var bWindow = windows[second];
                    var a = result[aWindow.Window.Handle];
                    var b = result[bWindow.Window.Handle];

                    if (a.VerticalOverlapRatio(b) >= 0.30)
                    {
                        var aBeforeB = CenterX(a) <= CenterX(b);
                        var leftWindow = aBeforeB ? aWindow : bWindow;
                        var rightWindow = aBeforeB ? bWindow : aWindow;
                        var leftRect = aBeforeB ? a : b;
                        var rightRect = aBeforeB ? b : a;
                        var gap = rightRect.Left - leftRect.Right;
                        if (Math.Abs(gap - preferredGap) <= snapRadius)
                        {
                            MovePairTowardGap(
                                result,
                                leftWindow,
                                rightWindow,
                                horizontal: true,
                                gap - preferredGap,
                                interaction);
                        }
                    }

                    a = result[aWindow.Window.Handle];
                    b = result[bWindow.Window.Handle];
                    if (a.HorizontalOverlapRatio(b) >= 0.30)
                    {
                        var aBeforeB = CenterY(a) <= CenterY(b);
                        var topWindow = aBeforeB ? aWindow : bWindow;
                        var bottomWindow = aBeforeB ? bWindow : aWindow;
                        var topRect = aBeforeB ? a : b;
                        var bottomRect = aBeforeB ? b : a;
                        var gap = bottomRect.Top - topRect.Bottom;
                        if (Math.Abs(gap - preferredGap) <= snapRadius)
                        {
                            MovePairTowardGap(
                                result,
                                topWindow,
                                bottomWindow,
                                horizontal: false,
                                gap - preferredGap,
                                interaction);
                        }
                    }

                    AlignPair(result, aWindow, bWindow, alignRadius, interaction);
                }
            }
        }

        foreach (var window in windows)
        {
            result[window.Window.Handle] = FitInsideWorkArea(window.Window, result[window.Window.Handle]);
        }

        return result;
    }

    private static void MovePairTowardGap(
        IDictionary<nint, RectI> targets,
        VisibleWindow first,
        VisibleWindow second,
        bool horizontal,
        int gapError,
        SmartInteractionContext interaction)
    {
        if (gapError == 0)
        {
            return;
        }

        var firstAttention = 1.0 + interaction.AttentionFor(first.Window.Handle) + (first.Window.IsForeground ? 1.4 : 0);
        var secondAttention = 1.0 + interaction.AttentionFor(second.Window.Handle) + (second.Window.IsForeground ? 1.4 : 0);
        var mobilityFirst = 1.0 / firstAttention;
        var mobilitySecond = 1.0 / secondAttention;
        var sum = mobilityFirst + mobilitySecond;
        var firstShare = (int)Math.Round(gapError * (mobilityFirst / sum));
        var secondShare = gapError - firstShare;
        var a = targets[first.Window.Handle];
        var b = targets[second.Window.Handle];

        if (horizontal)
        {
            targets[first.Window.Handle] = Translate(a, firstShare, 0);
            targets[second.Window.Handle] = Translate(b, -secondShare, 0);
        }
        else
        {
            targets[first.Window.Handle] = Translate(a, 0, firstShare);
            targets[second.Window.Handle] = Translate(b, 0, -secondShare);
        }
    }

    private static void AlignPair(
        IDictionary<nint, RectI> targets,
        VisibleWindow first,
        VisibleWindow second,
        int radius,
        SmartInteractionContext interaction)
    {
        var a = targets[first.Window.Handle];
        var b = targets[second.Window.Handle];
        var verticalRelation = a.VerticalOverlapRatio(b) >= 0.25 ||
                               RectI.IntervalGap(a.Top, a.Bottom, b.Top, b.Bottom) <= 40;
        var horizontalRelation = a.HorizontalOverlapRatio(b) >= 0.25 ||
                                 RectI.IntervalGap(a.Left, a.Right, b.Left, b.Right) <= 40;

        if (verticalRelation)
        {
            var leftError = b.Left - a.Left;
            var rightError = b.Right - a.Right;
            var error = Math.Abs(leftError) <= Math.Abs(rightError) ? leftError : rightError;
            if (Math.Abs(error) <= radius)
            {
                MovePairTowardAlignment(targets, first, second, horizontal: true, error, interaction);
            }
        }

        a = targets[first.Window.Handle];
        b = targets[second.Window.Handle];
        if (horizontalRelation)
        {
            var topError = b.Top - a.Top;
            var bottomError = b.Bottom - a.Bottom;
            var error = Math.Abs(topError) <= Math.Abs(bottomError) ? topError : bottomError;
            if (Math.Abs(error) <= radius)
            {
                MovePairTowardAlignment(targets, first, second, horizontal: false, error, interaction);
            }
        }
    }

    private static void MovePairTowardAlignment(
        IDictionary<nint, RectI> targets,
        VisibleWindow first,
        VisibleWindow second,
        bool horizontal,
        int error,
        SmartInteractionContext interaction)
    {
        var firstWeight = 1.0 + interaction.AttentionFor(first.Window.Handle) + (first.Window.IsForeground ? 1.4 : 0);
        var secondWeight = 1.0 + interaction.AttentionFor(second.Window.Handle) + (second.Window.IsForeground ? 1.4 : 0);
        var firstMobility = 1.0 / firstWeight;
        var secondMobility = 1.0 / secondWeight;
        var total = firstMobility + secondMobility;
        var firstShift = (int)Math.Round(error * firstMobility / total);
        var secondShift = firstShift - error;
        var a = targets[first.Window.Handle];
        var b = targets[second.Window.Handle];

        targets[first.Window.Handle] = horizontal ? Translate(a, firstShift, 0) : Translate(a, 0, firstShift);
        targets[second.Window.Handle] = horizontal ? Translate(b, secondShift, 0) : Translate(b, 0, secondShift);
    }

    private static Dictionary<nint, RectI> BuildEqualColumns(
        IReadOnlyList<VisibleWindow> windows,
        RectI workArea,
        int gap)
    {
        var ordered = windows.OrderBy(item => CenterX(item.Window.VisualRect)).ToArray();
        var result = new Dictionary<nint, RectI>(ordered.Length);
        var available = Math.Max(1, workArea.Width - (gap * (ordered.Length - 1)));
        var consumed = 0;

        for (var index = 0; index < ordered.Length; index++)
        {
            var remainingColumns = ordered.Length - index;
            var remainingWidth = available - consumed;
            var width = index == ordered.Length - 1
                ? remainingWidth
                : (int)Math.Round((double)remainingWidth / remainingColumns);
            var left = workArea.Left + consumed + (gap * index);
            result[ordered[index].Window.Handle] = new RectI(left, workArea.Top, Math.Max(1, width), workArea.Height);
            consumed += width;
        }

        return result;
    }

    private static Dictionary<nint, RectI> BuildEqualRows(
        IReadOnlyList<VisibleWindow> windows,
        RectI workArea,
        int gap)
    {
        var ordered = windows.OrderBy(item => CenterY(item.Window.VisualRect)).ToArray();
        var result = new Dictionary<nint, RectI>(ordered.Length);
        var available = Math.Max(1, workArea.Height - (gap * (ordered.Length - 1)));
        var consumed = 0;

        for (var index = 0; index < ordered.Length; index++)
        {
            var remainingRows = ordered.Length - index;
            var remainingHeight = available - consumed;
            var height = index == ordered.Length - 1
                ? remainingHeight
                : (int)Math.Round((double)remainingHeight / remainingRows);
            var top = workArea.Top + consumed + (gap * index);
            result[ordered[index].Window.Handle] = new RectI(workArea.Left, top, workArea.Width, Math.Max(1, height));
            consumed += height;
        }

        return result;
    }

    private static Dictionary<nint, RectI> BuildGrid(
        IReadOnlyList<VisibleWindow> windows,
        RectI workArea,
        int gap)
    {
        var count = windows.Count;
        var columns = (int)Math.Ceiling(Math.Sqrt(count * Math.Max(0.55, (double)workArea.Width / Math.Max(1, workArea.Height))));
        columns = Math.Clamp(columns, 1, count);
        var rows = (int)Math.Ceiling((double)count / columns);
        var ordered = windows
            .OrderBy(item => CenterY(item.Window.VisualRect))
            .ThenBy(item => CenterX(item.Window.VisualRect))
            .ToArray();
        var result = new Dictionary<nint, RectI>(count);
        var usableWidth = Math.Max(1, workArea.Width - (gap * (columns - 1)));
        var usableHeight = Math.Max(1, workArea.Height - (gap * (rows - 1)));

        for (var index = 0; index < count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var rowCount = Math.Min(columns, count - (row * columns));
            var columnWidth = Math.Max(1, usableWidth / columns);
            var rowHeight = Math.Max(1, usableHeight / rows);
            var left = workArea.Left + (column * (columnWidth + gap));
            var top = workArea.Top + (row * (rowHeight + gap));
            var width = column == columns - 1 || column == rowCount - 1
                ? Math.Max(1, workArea.Right - left)
                : columnWidth;
            var height = row == rows - 1
                ? Math.Max(1, workArea.Bottom - top)
                : rowHeight;
            result[ordered[index].Window.Handle] = new RectI(left, top, width, height);
        }

        return result;
    }

    private static Dictionary<nint, RectI> BuildFocusLayout(
        IReadOnlyList<VisibleWindow> windows,
        VisibleWindow focus,
        RectI workArea,
        int gap,
        double mainRatio,
        AutoLayoutArchetype archetype)
    {
        var result = new Dictionary<nint, RectI>(windows.Count);
        var others = windows
            .Where(item => item.Window.Handle != focus.Window.Handle)
            .OrderBy(item => archetype is AutoLayoutArchetype.FocusLeft or AutoLayoutArchetype.FocusRight
                ? CenterY(item.Window.VisualRect)
                : CenterX(item.Window.VisualRect))
            .ToArray();

        if (others.Length == 0)
        {
            result[focus.Window.Handle] = focus.Window.VisualRect;
            return result;
        }

        if (archetype is AutoLayoutArchetype.FocusLeft or AutoLayoutArchetype.FocusRight)
        {
            var mainWidth = Math.Clamp(
                (int)Math.Round((workArea.Width - gap) * mainRatio),
                Math.Max(1, workArea.Width / 2),
                Math.Max(1, workArea.Width - gap - 1));
            var sideWidth = Math.Max(1, workArea.Width - gap - mainWidth);
            var focusLeft = archetype == AutoLayoutArchetype.FocusLeft;
            var mainLeft = focusLeft ? workArea.Left : workArea.Right - mainWidth;
            var sideLeft = focusLeft ? workArea.Left + mainWidth + gap : workArea.Left;
            result[focus.Window.Handle] = new RectI(mainLeft, workArea.Top, mainWidth, workArea.Height);
            FillStack(others, result, sideLeft, workArea.Top, sideWidth, workArea.Height, gap, vertical: true);
        }
        else
        {
            var mainHeight = Math.Clamp(
                (int)Math.Round((workArea.Height - gap) * mainRatio),
                Math.Max(1, workArea.Height / 2),
                Math.Max(1, workArea.Height - gap - 1));
            var sideHeight = Math.Max(1, workArea.Height - gap - mainHeight);
            var focusTop = archetype == AutoLayoutArchetype.FocusTop;
            var mainTop = focusTop ? workArea.Top : workArea.Bottom - mainHeight;
            var sideTop = focusTop ? workArea.Top + mainHeight + gap : workArea.Top;
            result[focus.Window.Handle] = new RectI(workArea.Left, mainTop, workArea.Width, mainHeight);
            FillStack(others, result, workArea.Left, sideTop, workArea.Width, sideHeight, gap, vertical: false);
        }

        return result;
    }

    private static void FillStack(
        IReadOnlyList<VisibleWindow> windows,
        IDictionary<nint, RectI> result,
        int left,
        int top,
        int width,
        int height,
        int gap,
        bool vertical)
    {
        if (vertical)
        {
            var usable = Math.Max(1, height - (gap * (windows.Count - 1)));
            var consumed = 0;
            for (var index = 0; index < windows.Count; index++)
            {
                var remaining = windows.Count - index;
                var size = index == windows.Count - 1
                    ? usable - consumed
                    : (int)Math.Round((double)(usable - consumed) / remaining);
                result[windows[index].Window.Handle] = new RectI(
                    left,
                    top + consumed + (gap * index),
                    width,
                    Math.Max(1, size));
                consumed += size;
            }
        }
        else
        {
            var usable = Math.Max(1, width - (gap * (windows.Count - 1)));
            var consumed = 0;
            for (var index = 0; index < windows.Count; index++)
            {
                var remaining = windows.Count - index;
                var size = index == windows.Count - 1
                    ? usable - consumed
                    : (int)Math.Round((double)(usable - consumed) / remaining);
                result[windows[index].Window.Handle] = new RectI(
                    left + consumed + (gap * index),
                    top,
                    Math.Max(1, size),
                    height);
                consumed += size;
            }
        }
    }

    private static double[] ExtractFeatures(
        IReadOnlyList<VisibleWindow> windows,
        IReadOnlyDictionary<nint, RectI> targets,
        RectI workArea,
        SmartInteractionContext interaction,
        SmartPersonalizationState personalization)
    {
        var diagonal = Math.Sqrt((double)workArea.Width * workArea.Width + (double)workArea.Height * workArea.Height);
        diagonal = Math.Max(1, diagonal);
        var workAreaPixels = Math.Max(1L, workArea.Area);
        var movement = 0.0;
        var resize = 0.0;
        var aspect = 0.0;
        var changed = 0;
        var attentionWeight = 0.0;
        var attentionArea = 0.0;
        var attentionStabilityCost = 0.0;

        foreach (var window in windows)
        {
            var original = window.Window.VisualRect;
            var target = targets[window.Window.Handle];
            var centerDistance = Distance(CenterX(original), CenterY(original), CenterX(target), CenterY(target));
            movement += centerDistance / diagonal;
            var widthRatio = original.Width <= 0 ? 1.0 : (double)target.Width / original.Width;
            var heightRatio = original.Height <= 0 ? 1.0 : (double)target.Height / original.Height;
            resize += Math.Abs(Math.Log(Math.Max(0.05, widthRatio))) + Math.Abs(Math.Log(Math.Max(0.05, heightRatio)));
            var originalAspect = original.Height <= 0 ? 1.0 : (double)original.Width / original.Height;
            var targetAspect = target.Height <= 0 ? originalAspect : (double)target.Width / target.Height;
            aspect += Math.Abs(Math.Log(Math.Max(0.05, targetAspect / Math.Max(0.05, originalAspect))));
            if (HasMeaningfulChange(original, target))
            {
                changed++;
            }

            var attention = Math.Max(MinimumUsefulAttention, interaction.AttentionFor(window.Window.Handle));
            attentionWeight += attention;
            attentionArea += attention * ((double)target.Area / workAreaPixels);
            attentionStabilityCost += attention * (centerDistance / diagonal);
        }

        var overlapArea = 0L;
        for (var first = 0; first < windows.Count; first++)
        {
            for (var second = first + 1; second < windows.Count; second++)
            {
                overlapArea += targets[windows[first].Window.Handle]
                    .Intersect(targets[windows[second].Window.Handle])
                    .Area;
            }
        }

        var offscreenArea = 0L;
        foreach (var target in targets.Values)
        {
            offscreenArea += Math.Max(0, target.Area - target.Intersect(workArea).Area);
        }

        var unionArea = UnionArea(targets.Values, workArea);
        var coverage = Math.Clamp((double)unionArea / workAreaPixels, 0, 1);
        var topology = TopologyPreservation(windows, targets);
        var alignment = AlignmentQuality(targets.Values.ToArray());
        var gapRegularity = GapRegularity(targets.Values.ToArray(), EffectiveGap(personalization));
        var count = Math.Max(1, windows.Count);

        return
        [
            -Math.Clamp(movement / count, 0, 2),
            -Math.Clamp(resize / count, 0, 2),
            -Math.Clamp((double)overlapArea / workAreaPixels, 0, 2),
            -Math.Clamp((double)offscreenArea / workAreaPixels, 0, 2),
            coverage,
            topology,
            attentionWeight <= 0 ? 0 : Math.Clamp(attentionArea / attentionWeight, 0, 1),
            attentionWeight <= 0 ? 0 : 1.0 - Math.Clamp(attentionStabilityCost / attentionWeight, 0, 1),
            alignment,
            gapRegularity,
            -(double)changed / count,
            -Math.Clamp(aspect / count, 0, 2),
        ];
    }

    private static double HumanFactorScore(
        Candidate candidate,
        IReadOnlyList<VisibleWindow> windows,
        double layoutIntentEvidence,
        double focusStrength,
        SmartTidyStrength strength)
    {
        var f = candidate.Features;
        var score =
            (3.10 * f[0]) +
            (2.35 * f[1]) +
            (7.50 * f[2]) +
            (12.0 * f[3]) +
            (0.55 * f[4]) +
            (1.80 * f[5]) +
            (1.30 * f[6]) +
            (1.70 * f[7]) +
            (0.95 * f[8]) +
            (0.70 * f[9]) +
            (0.70 * f[10]) +
            (1.50 * f[11]);

        switch (candidate.Archetype)
        {
            case AutoLayoutArchetype.Preserve:
                score += 0.20 - (0.65 * layoutIntentEvidence);
                break;
            case AutoLayoutArchetype.CleanCurrent:
                score += 0.55 + (0.35 * layoutIntentEvidence);
                break;
            case AutoLayoutArchetype.EqualColumns:
            case AutoLayoutArchetype.EqualRows:
            case AutoLayoutArchetype.Grid:
                score += (layoutIntentEvidence - 0.56) * 2.2;
                break;
            case AutoLayoutArchetype.FocusLeft:
            case AutoLayoutArchetype.FocusRight:
            case AutoLayoutArchetype.FocusTop:
            case AutoLayoutArchetype.FocusBottom:
                score += ((layoutIntentEvidence - 0.45) * 1.45) + ((focusStrength - 0.30) * 1.65);
                break;
        }

        if (windows.Count == 2 && candidate.Archetype is AutoLayoutArchetype.EqualColumns or AutoLayoutArchetype.EqualRows)
        {
            score += 0.18;
        }

        score += strength switch
        {
            SmartTidyStrength.Gentle when candidate.Archetype is not AutoLayoutArchetype.Preserve and not AutoLayoutArchetype.CleanCurrent => -0.65,
            SmartTidyStrength.Assertive when candidate.Archetype is not AutoLayoutArchetype.Preserve => 0.22,
            _ => 0,
        };

        return score;
    }

    private static double LayoutIntentEvidence(IReadOnlyList<VisibleWindow> windows, RectI workArea)
    {
        var originals = windows.Select(static item => item.Window.VisualRect).ToArray();
        var coverage = (double)UnionArea(originals, workArea) / Math.Max(1L, workArea.Area);
        var edgeHits = 0;
        var closeRelations = 0;
        foreach (var rect in originals)
        {
            if (Math.Min(
                    Math.Min(Math.Abs(rect.Left - workArea.Left), Math.Abs(workArea.Right - rect.Right)),
                    Math.Min(Math.Abs(rect.Top - workArea.Top), Math.Abs(workArea.Bottom - rect.Bottom))) <= 48)
            {
                edgeHits++;
            }
        }

        for (var first = 0; first < originals.Length; first++)
        {
            for (var second = first + 1; second < originals.Length; second++)
            {
                var a = originals[first];
                var b = originals[second];
                var xGap = RectI.IntervalGap(a.Left, a.Right, b.Left, b.Right);
                var yGap = RectI.IntervalGap(a.Top, a.Bottom, b.Top, b.Bottom);
                if ((a.VerticalOverlapRatio(b) >= 0.25 && xGap <= 64) ||
                    (a.HorizontalOverlapRatio(b) >= 0.25 && yGap <= 64))
                {
                    closeRelations++;
                }
            }
        }

        var possiblePairs = Math.Max(1, (windows.Count * (windows.Count - 1)) / 2);
        return Math.Clamp(
            (0.60 * coverage) +
            (0.20 * ((double)edgeHits / Math.Max(1, windows.Count))) +
            (0.20 * ((double)closeRelations / possiblePairs)),
            0,
            1);
    }

    private static VisibleWindow SelectFocusWindow(
        IReadOnlyList<VisibleWindow> windows,
        SmartInteractionContext interaction) =>
        windows
            .OrderByDescending(item =>
                interaction.AttentionFor(item.Window.Handle) +
                (item.Window.IsForeground ? 1.5 : 0) +
                (0.20 * item.VisibleRatio))
            .First();

    private static double FocusStrength(
        IReadOnlyList<VisibleWindow> windows,
        VisibleWindow focus,
        SmartInteractionContext interaction)
    {
        var focusScore = interaction.AttentionFor(focus.Window.Handle) + (focus.Window.IsForeground ? 1.5 : 0.0);
        var others = windows
            .Where(item => item.Window.Handle != focus.Window.Handle)
            .Select(item => interaction.AttentionFor(item.Window.Handle) + (item.Window.IsForeground ? 1.5 : 0.0))
            .DefaultIfEmpty(0)
            .Average();
        return Math.Clamp((focusScore - others + 0.4) / 2.4, 0, 1);
    }

    private static int EffectiveGap(SmartPersonalizationState personalization)
    {
        personalization = personalization.Normalize();
        var personalConfidence = Math.Clamp(personalization.FollowGestureSamples / 24.0, 0, 0.72);
        return (int)Math.Round(
            (HumanGapPixels * (1.0 - personalConfidence)) +
            (personalization.PreferredGapPixels * personalConfidence));
    }

    private static double EffectiveMainRatio(SmartPersonalizationState personalization)
    {
        personalization = personalization.Normalize();
        var personalConfidence = Math.Clamp(personalization.AutoCorrectionSamples / 20.0, 0, 0.70);
        return (0.62 * (1.0 - personalConfidence)) +
               (personalization.PreferredMainRatio * personalConfidence);
    }

    private static AutoLayoutArchetype ClassifyArchetype(
        IReadOnlyList<VisibleWindow> windows,
        IReadOnlyDictionary<nint, RectI> targets,
        RectI workArea,
        SmartInteractionContext interaction)
    {
        if (windows.Count <= 1)
        {
            return AutoLayoutArchetype.CleanCurrent;
        }

        var rects = windows.Select(item => targets[item.Window.Handle]).ToArray();
        var verticalFillCount = rects.Count(rect => Math.Abs(rect.Top - workArea.Top) <= 4 && Math.Abs(rect.Bottom - workArea.Bottom) <= 4);
        var horizontalFillCount = rects.Count(rect => Math.Abs(rect.Left - workArea.Left) <= 4 && Math.Abs(rect.Right - workArea.Right) <= 4);
        if (verticalFillCount == windows.Count)
        {
            return AutoLayoutArchetype.EqualColumns;
        }
        if (horizontalFillCount == windows.Count)
        {
            return AutoLayoutArchetype.EqualRows;
        }

        var focus = SelectFocusWindow(windows, interaction);
        var focusRect = targets[focus.Window.Handle];
        var areaRatio = (double)focusRect.Area / Math.Max(1L, workArea.Area);
        if (areaRatio >= 0.45)
        {
            if (Math.Abs(focusRect.Left - workArea.Left) <= 4 && focusRect.Right < workArea.Right - 8)
            {
                return AutoLayoutArchetype.FocusLeft;
            }
            if (Math.Abs(focusRect.Right - workArea.Right) <= 4 && focusRect.Left > workArea.Left + 8)
            {
                return AutoLayoutArchetype.FocusRight;
            }
            if (Math.Abs(focusRect.Top - workArea.Top) <= 4 && focusRect.Bottom < workArea.Bottom - 8)
            {
                return AutoLayoutArchetype.FocusTop;
            }
            if (Math.Abs(focusRect.Bottom - workArea.Bottom) <= 4 && focusRect.Top > workArea.Top + 8)
            {
                return AutoLayoutArchetype.FocusBottom;
            }
        }

        var coverage = (double)UnionArea(rects, workArea) / Math.Max(1L, workArea.Area);
        return coverage >= 0.80 && windows.Count >= 3
            ? AutoLayoutArchetype.Grid
            : AutoLayoutArchetype.CleanCurrent;
    }

    private static double TopologyPreservation(
        IReadOnlyList<VisibleWindow> windows,
        IReadOnlyDictionary<nint, RectI> targets)
    {
        var comparisons = 0;
        var preserved = 0;
        for (var first = 0; first < windows.Count; first++)
        {
            for (var second = first + 1; second < windows.Count; second++)
            {
                var originalA = windows[first].Window.VisualRect;
                var originalB = windows[second].Window.VisualRect;
                var targetA = targets[windows[first].Window.Handle];
                var targetB = targets[windows[second].Window.Handle];

                var originalX = Math.Sign(CenterX(originalA) - CenterX(originalB));
                var targetX = Math.Sign(CenterX(targetA) - CenterX(targetB));
                if (originalX != 0)
                {
                    comparisons++;
                    if (targetX == originalX)
                    {
                        preserved++;
                    }
                }

                var originalY = Math.Sign(CenterY(originalA) - CenterY(originalB));
                var targetY = Math.Sign(CenterY(targetA) - CenterY(targetB));
                if (originalY != 0)
                {
                    comparisons++;
                    if (targetY == originalY)
                    {
                        preserved++;
                    }
                }
            }
        }

        return comparisons == 0 ? 1.0 : (double)preserved / comparisons;
    }

    private static double AlignmentQuality(IReadOnlyList<RectI> rects)
    {
        if (rects.Count < 2)
        {
            return 1.0;
        }

        var related = 0;
        var aligned = 0;
        for (var first = 0; first < rects.Count; first++)
        {
            for (var second = first + 1; second < rects.Count; second++)
            {
                var a = rects[first];
                var b = rects[second];
                if (a.VerticalOverlapRatio(b) >= 0.25)
                {
                    related++;
                    if (Math.Min(Math.Abs(a.Top - b.Top), Math.Abs(a.Bottom - b.Bottom)) <= 2)
                    {
                        aligned++;
                    }
                }
                if (a.HorizontalOverlapRatio(b) >= 0.25)
                {
                    related++;
                    if (Math.Min(Math.Abs(a.Left - b.Left), Math.Abs(a.Right - b.Right)) <= 2)
                    {
                        aligned++;
                    }
                }
            }
        }

        return related == 0 ? 0.5 : (double)aligned / related;
    }

    private static double GapRegularity(IReadOnlyList<RectI> rects, int preferredGap)
    {
        var errors = new List<double>();
        for (var first = 0; first < rects.Count; first++)
        {
            for (var second = first + 1; second < rects.Count; second++)
            {
                var a = rects[first];
                var b = rects[second];
                if (a.VerticalOverlapRatio(b) >= 0.30)
                {
                    var gap = RectI.IntervalGap(a.Left, a.Right, b.Left, b.Right);
                    if (gap <= 64)
                    {
                        errors.Add(Math.Abs(gap - preferredGap));
                    }
                }
                if (a.HorizontalOverlapRatio(b) >= 0.30)
                {
                    var gap = RectI.IntervalGap(a.Top, a.Bottom, b.Top, b.Bottom);
                    if (gap <= 64)
                    {
                        errors.Add(Math.Abs(gap - preferredGap));
                    }
                }
            }
        }

        if (errors.Count == 0)
        {
            return 0.45;
        }

        var mean = errors.Average();
        return Math.Exp(-mean / 12.0);
    }

    private static long UnionArea(IEnumerable<RectI> source, RectI clip)
    {
        var pieces = new List<RectI>();
        foreach (var rect in source)
        {
            var clipped = rect.Intersect(clip);
            if (clipped.IsEmpty)
            {
                continue;
            }

            var remaining = new List<RectI> { clipped };
            foreach (var existing in pieces)
            {
                remaining = RectRegion.Subtract(remaining, existing, maxFragments: 512);
                if (remaining.Count == 0)
                {
                    break;
                }
            }
            pieces.AddRange(remaining);
        }

        return pieces.Sum(static rect => rect.Area);
    }

    private static int ChooseUnambiguousSnapShift(int startShift, int endShift, int radius)
    {
        var startNear = Math.Abs(startShift) <= radius;
        var endNear = Math.Abs(endShift) <= radius;
        if (!startNear && !endNear)
        {
            return 0;
        }
        if (startNear && endNear && Math.Abs(Math.Abs(startShift) - Math.Abs(endShift)) <= Math.Max(2, radius / 5))
        {
            return 0;
        }
        return !endNear || (startNear && Math.Abs(startShift) < Math.Abs(endShift))
            ? startShift
            : endShift;
    }

    private static RectI FitInsideWorkArea(WindowSnapshot snapshot, RectI rect)
    {
        var workArea = snapshot.WorkArea;
        if (workArea.IsEmpty || rect.IsEmpty)
        {
            return rect;
        }

        var width = snapshot.IsResizable ? Math.Min(rect.Width, workArea.Width) : rect.Width;
        var height = snapshot.IsResizable ? Math.Min(rect.Height, workArea.Height) : rect.Height;
        var left = width <= workArea.Width
            ? Math.Clamp(rect.Left, workArea.Left, workArea.Right - width)
            : workArea.Left;
        var top = height <= workArea.Height
            ? Math.Clamp(rect.Top, workArea.Top, workArea.Bottom - height)
            : workArea.Top;
        return new RectI(left, top, width, height);
    }

    private static RectI Translate(RectI rect, int dx, int dy) =>
        new(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);

    private static bool HasMeaningfulChange(RectI original, RectI target) =>
        Math.Abs(original.Left - target.Left) >= 2 ||
        Math.Abs(original.Top - target.Top) >= 2 ||
        Math.Abs(original.Right - target.Right) >= 2 ||
        Math.Abs(original.Bottom - target.Bottom) >= 2;

    private static bool SameLayout(
        IReadOnlyDictionary<nint, RectI> first,
        IReadOnlyDictionary<nint, RectI> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        foreach (var pair in first)
        {
            if (!second.TryGetValue(pair.Key, out var rect) || rect != pair.Value)
            {
                return false;
            }
        }
        return true;
    }

    private static double CenterX(RectI rect) => (rect.Left + rect.Right) / 2.0;
    private static double CenterY(RectI rect) => (rect.Top + rect.Bottom) / 2.0;
    private static double Distance(double x1, double y1, double x2, double y2) =>
        Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    private sealed class Candidate(
        AutoLayoutArchetype archetype,
        Dictionary<nint, RectI> targets)
    {
        internal AutoLayoutArchetype Archetype { get; } = archetype;
        internal Dictionary<nint, RectI> Targets { get; } = targets;
        internal double[] Features { get; set; } = new double[SmartPersonalizationState.AutoFeatureCount];
        internal double HumanScore { get; set; }
        internal double PersonalScore { get; set; }
        internal double TotalScore { get; set; }
    }
}
