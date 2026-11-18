namespace NeatWin.Core;

/// <summary>
/// Smart behavior B: interpret a just-finished manual move/resize and, only when the intent is
/// sufficiently clear, finish the last few pixels or ratio adjustment. This follows the small snap
/// zones + speed gating used by mature window movers, then adds short kinematic evidence and a
/// bounded personal target prior learned from the user's own raw mouse-up rectangles.
/// </summary>
internal static class FollowHandAssistant
{
    private const int MinimumWidth = 180;
    private const int MinimumHeight = 120;
    private const double FlyThroughSpeedPixelsPerSecond = 3200.0;

    internal static FollowHandDecision Decide(
        ManualWindowGesture gesture,
        IReadOnlyList<VisibleWindow> visibleWindows,
        TidyOptions options,
        SmartInteractionContext interaction,
        SmartPersonalizationState personalization)
    {
        personalization = personalization.Normalize();
        var moved = visibleWindows.FirstOrDefault(item => item.Window.Handle == gesture.WindowHandle);
        if (moved is null || gesture.EndRect.IsEmpty)
        {
            return new FollowHandDecision(false, gesture.EndRect, FollowHandTargetKind.None, 0);
        }

        // A fast monitor-crossing fling is not an invitation to snap to every relation it crosses.
        // The raw endpoint still reaches the learning path with reduced confidence, but behavior B
        // stays out of the way for this gesture.
        if (double.IsFinite(gesture.EndSpeedPixelsPerSecond) &&
            gesture.EndSpeedPixelsPerSecond >= FlyThroughSpeedPixelsPerSecond)
        {
            return new FollowHandDecision(false, gesture.EndRect, FollowHandTargetKind.None, 0);
        }

        var radius = options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => 14,
            SmartTidyStrength.Assertive => 30,
            _ => 22,
        };
        var preferredGap = (int)Math.Round(personalization.PreferredGapPixels);
        var candidates = new List<Candidate>();
        AddScreenCandidates(candidates, gesture, radius);

        foreach (var other in visibleWindows)
        {
            if (other.Window.Handle == gesture.WindowHandle)
            {
                continue;
            }

            AddNeighborCandidates(candidates, gesture, other.Window.VisualRect, preferredGap, radius);
            AddAlignmentCandidates(candidates, gesture, other.Window.VisualRect, radius);
            AddSizeMatchCandidates(candidates, gesture, other.Window.VisualRect, radius);
        }

        AddRatioCandidates(candidates, gesture, personalization, radius);
        if (candidates.Count == 0)
        {
            return new FollowHandDecision(false, gesture.EndRect, FollowHandTargetKind.None, 0);
        }

        var speedGate = SpeedGate(gesture.EndSpeedPixelsPerSecond);
        Candidate? best = null;
        foreach (var candidate in candidates)
        {
            if (!IsUsable(candidate.Target, gesture.WorkArea))
            {
                continue;
            }

            var correction = RectDistance(gesture.EndRect, candidate.Target);
            // A relation already satisfied at mouse-up is context, not a possible next action. If
            // zero-distance candidates remain in the competition they always look artificially
            // attractive and can suppress another nearby relation the user actually meant to finish.
            if (correction < 1.5)
            {
                continue;
            }

            var proximity = Math.Clamp(1.0 - (correction / Math.Max(8.0, radius * 2.2)), 0, 1);
            var kinematic = KinematicIntentBonus(gesture, candidate.Target);
            var attentionPenalty = AttentionConflictPenalty(
                moved,
                candidate.Target,
                visibleWindows,
                interaction);
            var personalBias = SmartPersonalizationLearner.FollowBias(personalization, candidate.Kind);

            candidate.PersonalBias = personalBias;
            candidate.AttentionPenalty = attentionPenalty;
            candidate.Score =
                candidate.BaseScore +
                (1.35 * proximity) +
                (0.65 * speedGate) +
                (0.55 * kinematic) +
                (0.42 * personalBias) -
                (1.15 * attentionPenalty) -
                (0.020 * correction);

            if (best is null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return new FollowHandDecision(false, gesture.EndRect, FollowHandTargetKind.None, 0);
        }

        var maxCorrection = options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => 22.0,
            SmartTidyStrength.Assertive => 52.0,
            _ => 36.0,
        };
        var correctionDistance = RectDistance(gesture.EndRect, best.Target);
        var threshold = options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => 1.55,
            SmartTidyStrength.Assertive => 1.15,
            _ => 1.35,
        };

        if (gesture.EndSpeedPixelsPerSecond > 2200)
        {
            threshold += 0.55;
        }

        var learnedTargetSupport =
            personalization.FollowGestureSamples >= 12 &&
            best.PersonalBias >= 0.30 &&
            correctionDistance <= radius &&
            best.AttentionPenalty <= 0.20 &&
            gesture.EndSpeedPixelsPerSecond <= 1500;
        var scoreAccepted = best.Score >= threshold ||
                            (learnedTargetSupport && best.Score >= threshold - 0.55);
        var shouldApply = correctionDistance <= maxCorrection && scoreAccepted;
        var confidence = Math.Clamp(
            ((best.Score - 0.7) / 2.2) + (learnedTargetSupport ? 0.12 : 0),
            0,
            1);
        return new FollowHandDecision(
            shouldApply,
            shouldApply ? best.Target : gesture.EndRect,
            shouldApply ? best.Kind : FollowHandTargetKind.None,
            confidence);
    }

    internal static SmartPersonalizationState LearnFromRawGesture(
        SmartPersonalizationState personalization,
        ManualWindowGesture gesture,
        IReadOnlyList<VisibleWindow> visibleWindows)
    {
        personalization = personalization.Normalize();
        var rect = gesture.EndRect;
        var workArea = gesture.WorkArea;
        var bestKind = FollowHandTargetKind.None;
        var bestConfidence = 0.12;
        double? observedGap = null;
        double? observedRatio = null;

        var edgeDistance = new[]
        {
            Math.Abs(rect.Left - workArea.Left),
            Math.Abs(rect.Top - workArea.Top),
            Math.Abs(workArea.Right - rect.Right),
            Math.Abs(workArea.Bottom - rect.Bottom),
        }.Min();
        if (edgeDistance <= 16)
        {
            bestKind = FollowHandTargetKind.ScreenEdge;
            bestConfidence = 1.0 - (edgeDistance / 24.0);
        }

        foreach (var other in visibleWindows)
        {
            if (other.Window.Handle == gesture.WindowHandle)
            {
                continue;
            }

            var otherRect = other.Window.VisualRect;
            if (rect.VerticalOverlapRatio(otherRect) >= 0.30)
            {
                var gap = HorizontalGap(rect, otherRect);
                if (gap is >= 0 and <= 48)
                {
                    var confidence = 0.82 - (gap.Value / 90.0);
                    if (confidence > bestConfidence)
                    {
                        bestKind = FollowHandTargetKind.NeighborGap;
                        bestConfidence = confidence;
                        observedGap = gap.Value;
                    }
                }
            }

            if (rect.HorizontalOverlapRatio(otherRect) >= 0.30)
            {
                var gap = VerticalGap(rect, otherRect);
                if (gap is >= 0 and <= 48)
                {
                    var confidence = 0.82 - (gap.Value / 90.0);
                    if (confidence > bestConfidence)
                    {
                        bestKind = FollowHandTargetKind.NeighborGap;
                        bestConfidence = confidence;
                        observedGap = gap.Value;
                    }
                }
            }

            var alignmentError = new[]
            {
                Math.Abs(rect.Left - otherRect.Left),
                Math.Abs(rect.Right - otherRect.Right),
                Math.Abs(rect.Top - otherRect.Top),
                Math.Abs(rect.Bottom - otherRect.Bottom),
            }.Min();
            if (alignmentError <= 8 && 0.65 - (alignmentError / 20.0) > bestConfidence)
            {
                bestKind = FollowHandTargetKind.EdgeAlignment;
                bestConfidence = 0.65 - (alignmentError / 20.0);
            }
        }

        if (gesture.Kind != ManualGestureKind.Move && workArea.Width > 0 && workArea.Height > 0)
        {
            var widthRatio = (double)rect.Width / workArea.Width;
            var heightRatio = (double)rect.Height / workArea.Height;
            var commonRatios = new[] { 0.50, 0.60, 0.618, 2.0 / 3.0, 0.75 };
            var widthNearest = commonRatios.MinBy(value => Math.Abs(value - widthRatio));
            var heightNearest = commonRatios.MinBy(value => Math.Abs(value - heightRatio));
            var widthError = Math.Abs(widthNearest - widthRatio);
            var heightError = Math.Abs(heightNearest - heightRatio);
            var ratioError = Math.Min(widthError, heightError);
            if (ratioError <= 0.025 && 0.72 - (ratioError * 8.0) > bestConfidence)
            {
                bestKind = FollowHandTargetKind.WorkAreaRatio;
                bestConfidence = 0.72 - (ratioError * 8.0);
                observedRatio = widthError <= heightError ? widthRatio : heightRatio;
            }
        }

        // Fast releases are weaker teaching examples: they are more likely to be rough temporary
        // moves than deliberate final placement.
        bestConfidence *= SpeedGate(gesture.EndSpeedPixelsPerSecond) * 0.65 + 0.35;
        return SmartPersonalizationLearner.LearnFollowGesture(
            personalization,
            bestKind,
            observedGap,
            observedRatio,
            Math.Clamp(bestConfidence, 0.08, 1.0));
    }

    private static void AddScreenCandidates(
        ICollection<Candidate> candidates,
        ManualWindowGesture gesture,
        int radius)
    {
        var rect = gesture.EndRect;
        var area = gesture.WorkArea;
        if (area.IsEmpty)
        {
            return;
        }

        if (gesture.Kind == ManualGestureKind.Move)
        {
            AddTranslatedEdgeCandidate(candidates, rect, area.Left - rect.Left, horizontal: true, radius);
            AddTranslatedEdgeCandidate(candidates, rect, area.Right - rect.Right, horizontal: true, radius);
            AddTranslatedEdgeCandidate(candidates, rect, area.Top - rect.Top, horizontal: false, radius);
            AddTranslatedEdgeCandidate(candidates, rect, area.Bottom - rect.Bottom, horizontal: false, radius);
            return;
        }

        var edges = ChangedEdges(gesture.StartRect, rect);
        if (edges.Left && Math.Abs(rect.Left - area.Left) <= radius)
        {
            Add(candidates, FollowHandTargetKind.ScreenEdge, RectI.FromEdges(area.Left, rect.Top, rect.Right, rect.Bottom), 0.95);
        }
        if (edges.Right && Math.Abs(area.Right - rect.Right) <= radius)
        {
            Add(candidates, FollowHandTargetKind.ScreenEdge, RectI.FromEdges(rect.Left, rect.Top, area.Right, rect.Bottom), 0.95);
        }
        if (edges.Top && Math.Abs(rect.Top - area.Top) <= radius)
        {
            Add(candidates, FollowHandTargetKind.ScreenEdge, RectI.FromEdges(rect.Left, area.Top, rect.Right, rect.Bottom), 0.95);
        }
        if (edges.Bottom && Math.Abs(area.Bottom - rect.Bottom) <= radius)
        {
            Add(candidates, FollowHandTargetKind.ScreenEdge, RectI.FromEdges(rect.Left, rect.Top, rect.Right, area.Bottom), 0.95);
        }
    }

    private static void AddTranslatedEdgeCandidate(
        ICollection<Candidate> candidates,
        RectI rect,
        int shift,
        bool horizontal,
        int radius)
    {
        if (Math.Abs(shift) > radius)
        {
            return;
        }

        var target = horizontal
            ? new RectI(rect.X + shift, rect.Y, rect.Width, rect.Height)
            : new RectI(rect.X, rect.Y + shift, rect.Width, rect.Height);
        Add(candidates, FollowHandTargetKind.ScreenEdge, target, 0.92);
    }

    private static void AddNeighborCandidates(
        ICollection<Candidate> candidates,
        ManualWindowGesture gesture,
        RectI other,
        int preferredGap,
        int radius)
    {
        var rect = gesture.EndRect;
        var edges = ChangedEdges(gesture.StartRect, rect);
        if (rect.VerticalOverlapRatio(other) >= 0.25)
        {
            if (gesture.Kind == ManualGestureKind.Move)
            {
                var leftOfOther = other.Left - preferredGap - rect.Width;
                var rightOfOther = other.Right + preferredGap;
                if (Math.Abs(rect.Left - leftOfOther) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, new RectI(leftOfOther, rect.Top, rect.Width, rect.Height), 1.08);
                }
                if (Math.Abs(rect.Left - rightOfOther) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, new RectI(rightOfOther, rect.Top, rect.Width, rect.Height), 1.08);
                }
            }
            else
            {
                if (edges.Right && Math.Abs((other.Left - preferredGap) - rect.Right) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, RectI.FromEdges(rect.Left, rect.Top, other.Left - preferredGap, rect.Bottom), 1.08);
                }
                if (edges.Left && Math.Abs((other.Right + preferredGap) - rect.Left) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, RectI.FromEdges(other.Right + preferredGap, rect.Top, rect.Right, rect.Bottom), 1.08);
                }
            }
        }

        if (rect.HorizontalOverlapRatio(other) >= 0.25)
        {
            if (gesture.Kind == ManualGestureKind.Move)
            {
                var aboveOther = other.Top - preferredGap - rect.Height;
                var belowOther = other.Bottom + preferredGap;
                if (Math.Abs(rect.Top - aboveOther) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, new RectI(rect.Left, aboveOther, rect.Width, rect.Height), 1.08);
                }
                if (Math.Abs(rect.Top - belowOther) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, new RectI(rect.Left, belowOther, rect.Width, rect.Height), 1.08);
                }
            }
            else
            {
                if (edges.Bottom && Math.Abs((other.Top - preferredGap) - rect.Bottom) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, RectI.FromEdges(rect.Left, rect.Top, rect.Right, other.Top - preferredGap), 1.08);
                }
                if (edges.Top && Math.Abs((other.Bottom + preferredGap) - rect.Top) <= radius * 2)
                {
                    Add(candidates, FollowHandTargetKind.NeighborGap, RectI.FromEdges(rect.Left, other.Bottom + preferredGap, rect.Right, rect.Bottom), 1.08);
                }
            }
        }
    }

    private static void AddAlignmentCandidates(
        ICollection<Candidate> candidates,
        ManualWindowGesture gesture,
        RectI other,
        int radius)
    {
        if (gesture.Kind != ManualGestureKind.Move)
        {
            return;
        }

        var rect = gesture.EndRect;
        if (rect.VerticalOverlapRatio(other) >= 0.18 || RectI.IntervalGap(rect.Top, rect.Bottom, other.Top, other.Bottom) <= 48)
        {
            if (Math.Abs(rect.Left - other.Left) <= radius)
            {
                Add(candidates, FollowHandTargetKind.EdgeAlignment, new RectI(other.Left, rect.Top, rect.Width, rect.Height), 0.82);
            }
            if (Math.Abs(rect.Right - other.Right) <= radius)
            {
                Add(candidates, FollowHandTargetKind.EdgeAlignment, new RectI(other.Right - rect.Width, rect.Top, rect.Width, rect.Height), 0.82);
            }
        }

        if (rect.HorizontalOverlapRatio(other) >= 0.18 || RectI.IntervalGap(rect.Left, rect.Right, other.Left, other.Right) <= 48)
        {
            if (Math.Abs(rect.Top - other.Top) <= radius)
            {
                Add(candidates, FollowHandTargetKind.EdgeAlignment, new RectI(rect.Left, other.Top, rect.Width, rect.Height), 0.82);
            }
            if (Math.Abs(rect.Bottom - other.Bottom) <= radius)
            {
                Add(candidates, FollowHandTargetKind.EdgeAlignment, new RectI(rect.Left, other.Bottom - rect.Height, rect.Width, rect.Height), 0.82);
            }
        }
    }

    private static void AddSizeMatchCandidates(
        ICollection<Candidate> candidates,
        ManualWindowGesture gesture,
        RectI other,
        int radius)
    {
        if (gesture.Kind == ManualGestureKind.Move)
        {
            return;
        }

        var rect = gesture.EndRect;
        var edges = ChangedEdges(gesture.StartRect, rect);
        if (Math.Abs(rect.Width - other.Width) <= radius * 2)
        {
            var target = edges.Left && !edges.Right
                ? new RectI(rect.Right - other.Width, rect.Top, other.Width, rect.Height)
                : new RectI(rect.Left, rect.Top, other.Width, rect.Height);
            Add(candidates, FollowHandTargetKind.SizeMatch, target, 0.72);
        }
        if (Math.Abs(rect.Height - other.Height) <= radius * 2)
        {
            var target = edges.Top && !edges.Bottom
                ? new RectI(rect.Left, rect.Bottom - other.Height, rect.Width, other.Height)
                : new RectI(rect.Left, rect.Top, rect.Width, other.Height);
            Add(candidates, FollowHandTargetKind.SizeMatch, target, 0.72);
        }
    }

    private static void AddRatioCandidates(
        ICollection<Candidate> candidates,
        ManualWindowGesture gesture,
        SmartPersonalizationState personalization,
        int radius)
    {
        if (gesture.Kind == ManualGestureKind.Move || gesture.WorkArea.IsEmpty)
        {
            return;
        }

        var rect = gesture.EndRect;
        var area = gesture.WorkArea;
        var ratios = new[] { 0.50, 0.60, 0.618, 2.0 / 3.0, 0.75, personalization.PreferredMainRatio }
            .Distinct()
            .ToArray();
        var edges = ChangedEdges(gesture.StartRect, rect);

        foreach (var ratio in ratios)
        {
            var desiredWidth = (int)Math.Round(area.Width * ratio);
            if (Math.Abs(desiredWidth - rect.Width) <= radius * 2)
            {
                var left = edges.Left && !edges.Right ? rect.Right - desiredWidth : rect.Left;
                Add(candidates, FollowHandTargetKind.WorkAreaRatio, new RectI(left, rect.Top, desiredWidth, rect.Height), 0.78);
            }

            var desiredHeight = (int)Math.Round(area.Height * ratio);
            if (Math.Abs(desiredHeight - rect.Height) <= radius * 2)
            {
                var top = edges.Top && !edges.Bottom ? rect.Bottom - desiredHeight : rect.Top;
                Add(candidates, FollowHandTargetKind.WorkAreaRatio, new RectI(rect.Left, top, rect.Width, desiredHeight), 0.78);
            }
        }
    }

    private static double KinematicIntentBonus(ManualWindowGesture gesture, RectI target)
    {
        var samples = gesture.PointerSamples;
        if (samples.Count < 3)
        {
            return 0.35;
        }

        var last = samples[^1];
        var startIndex = samples.Count - 2;
        while (startIndex > 0 && last.ElapsedMilliseconds - samples[startIndex].ElapsedMilliseconds < 90)
        {
            startIndex--;
        }

        var start = samples[startIndex];
        var dt = Math.Max(1, last.ElapsedMilliseconds - start.ElapsedMilliseconds) / 1000.0;
        var vx = (last.Point.X - start.Point.X) / dt;
        var vy = (last.Point.Y - start.Point.Y) / dt;
        var speed = Math.Sqrt((vx * vx) + (vy * vy));
        if (speed < 35)
        {
            // A deliberate settle/dwell near a target is strong evidence even without direction.
            return 0.85;
        }

        var correctionX = CenterX(target) - CenterX(gesture.EndRect);
        var correctionY = CenterY(target) - CenterY(gesture.EndRect);
        var correctionLength = Math.Sqrt((correctionX * correctionX) + (correctionY * correctionY));
        if (correctionLength < 1)
        {
            return 1.0;
        }

        var cosine = ((vx * correctionX) + (vy * correctionY)) / Math.Max(1, speed * correctionLength);
        return Math.Clamp((cosine + 1.0) / 2.0, 0, 1);
    }

    private static double AttentionConflictPenalty(
        VisibleWindow moved,
        RectI target,
        IReadOnlyList<VisibleWindow> visibleWindows,
        SmartInteractionContext interaction)
    {
        var penalty = 0.0;
        foreach (var other in visibleWindows)
        {
            if (other.Window.Handle == moved.Window.Handle)
            {
                continue;
            }

            var overlap = target.Intersect(other.Window.VisualRect).Area;
            if (overlap <= 0)
            {
                continue;
            }

            var smaller = Math.Max(1L, Math.Min(target.Area, other.Window.VisualRect.Area));
            var ratio = (double)overlap / smaller;
            penalty += ratio * (0.45 + interaction.AttentionFor(other.Window.Handle));
        }

        return Math.Clamp(penalty, 0, 2.0);
    }

    private static ChangedEdgeSet ChangedEdges(RectI start, RectI end)
    {
        var left = Math.Abs(start.Left - end.Left);
        var right = Math.Abs(start.Right - end.Right);
        var top = Math.Abs(start.Top - end.Top);
        var bottom = Math.Abs(start.Bottom - end.Bottom);
        var horizontalMax = Math.Max(left, right);
        var verticalMax = Math.Max(top, bottom);
        return new ChangedEdgeSet(
            Left: left >= 2 && left >= right - 1,
            Right: right >= 2 && right >= left - 1,
            Top: top >= 2 && top >= bottom - 1,
            Bottom: bottom >= 2 && bottom >= top - 1,
            HorizontalMagnitude: horizontalMax,
            VerticalMagnitude: verticalMax);
    }

    private static void Add(
        ICollection<Candidate> candidates,
        FollowHandTargetKind kind,
        RectI target,
        double baseScore)
    {
        if (target.Width < MinimumWidth || target.Height < MinimumHeight)
        {
            return;
        }

        if (candidates.Any(item => item.Target == target))
        {
            return;
        }

        candidates.Add(new Candidate(kind, target, baseScore));
    }

    private static bool IsUsable(RectI rect, RectI workArea)
    {
        if (rect.Width < MinimumWidth || rect.Height < MinimumHeight)
        {
            return false;
        }

        if (workArea.IsEmpty)
        {
            return true;
        }

        var visible = rect.Intersect(workArea).Area;
        return visible >= rect.Area * 0.80;
    }

    private static double SpeedGate(double pixelsPerSecond)
    {
        if (!double.IsFinite(pixelsPerSecond) || pixelsPerSecond <= 0)
        {
            return 1.0;
        }

        // Smooth rather than binary so a normal quick drag still receives some assistance while a
        // monitor-crossing fling is effectively ignored.
        return 1.0 / (1.0 + Math.Pow(pixelsPerSecond / 1200.0, 2.0));
    }

    private static int? HorizontalGap(RectI a, RectI b)
    {
        if (a.Right <= b.Left)
        {
            return b.Left - a.Right;
        }
        if (b.Right <= a.Left)
        {
            return a.Left - b.Right;
        }
        return null;
    }

    private static int? VerticalGap(RectI a, RectI b)
    {
        if (a.Bottom <= b.Top)
        {
            return b.Top - a.Bottom;
        }
        if (b.Bottom <= a.Top)
        {
            return a.Top - b.Bottom;
        }
        return null;
    }

    private static double RectDistance(RectI first, RectI second)
    {
        var dx = CenterX(second) - CenterX(first);
        var dy = CenterY(second) - CenterY(first);
        var dw = second.Width - first.Width;
        var dh = second.Height - first.Height;
        return Math.Sqrt((dx * dx) + (dy * dy) + (0.35 * dw * dw) + (0.35 * dh * dh));
    }

    private static double CenterX(RectI rect) => (rect.Left + rect.Right) / 2.0;
    private static double CenterY(RectI rect) => (rect.Top + rect.Bottom) / 2.0;

    private sealed class Candidate(
        FollowHandTargetKind kind,
        RectI target,
        double baseScore)
    {
        internal FollowHandTargetKind Kind { get; } = kind;
        internal RectI Target { get; } = target;
        internal double BaseScore { get; } = baseScore;
        internal double PersonalBias { get; set; }
        internal double AttentionPenalty { get; set; }
        internal double Score { get; set; }
    }

    private readonly record struct ChangedEdgeSet(
        bool Left,
        bool Right,
        bool Top,
        bool Bottom,
        int HorizontalMagnitude,
        int VerticalMagnitude);
}
