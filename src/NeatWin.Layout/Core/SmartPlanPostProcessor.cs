namespace NeatWin.Core;

/// <summary>
/// Applies user-facing Smart behavior preferences after the constraint solver has produced its
/// cosmetic plan. This layer deliberately contains only high-level behaviors that are easier to
/// express as a projection than as another public solver coefficient: reversible vertical fill
/// preference and overlap separation.
/// </summary>
public static class SmartPlanPostProcessor
{
    public static IReadOnlyList<TidyMove> Refine(
        IReadOnlyList<VisibleWindow> visibleWindows,
        IReadOnlyList<TidyMove> basePlan,
        TidyOptions tidyOptions,
        SmartBehaviorOptions behaviorOptions)
    {
        if (tidyOptions.AlgorithmMode != TidyAlgorithmMode.Smart || visibleWindows.Count == 0)
        {
            return basePlan;
        }

        var targetByHandle = basePlan.ToDictionary(
            static move => move.Window.Handle,
            static move => move.TargetVisualRect);
        var result = new List<TidyMove>();

        foreach (var monitorGroup in visibleWindows.GroupBy(static item => item.Window.MonitorHandle))
        {
            var states = monitorGroup
                .Select(item => new State(
                    item,
                    targetByHandle.TryGetValue(item.Window.Handle, out var target)
                        ? target
                        : item.Window.VisualRect))
                .ToList();

            if (behaviorOptions.PreferReversibleVerticalFill)
            {
                ApplyVerticalFillPreference(states, tidyOptions);
            }

            ApplyOverlapAvoidance(states, tidyOptions, behaviorOptions.OverlapAvoidance);

            foreach (var state in states)
            {
                var target = tidyOptions.RescueOffscreenWindows
                    ? FitInsideWorkArea(state.Snapshot, state.Current)
                    : state.Current;

                if (HasMeaningfulChange(state.Snapshot.VisualRect, target))
                {
                    result.Add(new TidyMove(state.Snapshot, target));
                }
            }
        }

        return result;
    }

    private static void ApplyVerticalFillPreference(List<State> states, TidyOptions options)
    {
        var minimumHeightRatio = options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => 0.84,
            SmartTidyStrength.Assertive => 0.62,
            _ => 0.72,
        };
        var edgeRadius = options.SmartHitTendency switch
        {
            SmartHitTendency.Cautious => 56,
            SmartHitTendency.Sensitive => 156,
            _ => 104,
        };
        var maximumGrowthRatio = options.SmartSizeTendency switch
        {
            SmartSizeTendency.Preserve => 0.08,
            SmartSizeTendency.Expand => 0.34,
            _ => 0.20,
        };

        foreach (var state in states)
        {
            if (!state.Snapshot.IsResizable || HasLikelyVerticalPeer(state, states))
            {
                continue;
            }

            var workArea = state.Snapshot.WorkArea;
            var current = state.Current;
            if (workArea.IsEmpty || current.IsEmpty || current.Height <= 0)
            {
                continue;
            }

            if (Math.Abs(current.Top - workArea.Top) <= 1 &&
                Math.Abs(current.Bottom - workArea.Bottom) <= 1)
            {
                continue;
            }

            var heightRatio = (double)current.Height / workArea.Height;
            if (heightRatio < minimumHeightRatio)
            {
                continue;
            }

            var topDistance = Math.Abs(current.Top - workArea.Top);
            var bottomDistance = Math.Abs(workArea.Bottom - current.Bottom);
            var bothEdgesPlausible = topDistance <= edgeRadius && bottomDistance <= edgeRadius;
            var oneEdgeStrong =
                (topDistance <= edgeRadius / 2 || bottomDistance <= edgeRadius / 2) &&
                Math.Max(topDistance, bottomDistance) <= edgeRadius * 1.5;
            if (!bothEdgesPlausible && !oneEdgeStrong)
            {
                continue;
            }

            var growthRatio = Math.Max(0, (double)(workArea.Height - current.Height) / current.Height);
            if (growthRatio > maximumGrowthRatio)
            {
                continue;
            }

            state.Current = RectI.FromEdges(
                current.Left,
                workArea.Top,
                current.Right,
                workArea.Bottom);
        }
    }

    private static bool HasLikelyVerticalPeer(State state, IReadOnlyList<State> states)
    {
        foreach (var other in states)
        {
            if (ReferenceEquals(state, other))
            {
                continue;
            }

            var horizontalOverlap = state.Original.HorizontalOverlapRatio(other.Original);
            if (horizontalOverlap < 0.55)
            {
                continue;
            }

            var minimumHeight = Math.Max(1, Math.Min(state.Original.Height, other.Original.Height));
            var centerDelta = Math.Abs(CenterY(state.Original) - CenterY(other.Original)) / minimumHeight;
            if (centerDelta < 0.18)
            {
                continue;
            }

            var verticalGap = RectI.IntervalGap(
                state.Original.Top,
                state.Original.Bottom,
                other.Original.Top,
                other.Original.Bottom);
            var peerRadius = Math.Max(72, state.Snapshot.WorkArea.Height / 9);
            if (state.Original.VerticalOverlapRatio(other.Original) > 0 || verticalGap <= peerRadius)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyOverlapAvoidance(
        List<State> states,
        TidyOptions options,
        SmartOverlapAvoidance level)
    {
        if (states.Count < 2)
        {
            return;
        }

        var profile = level switch
        {
            SmartOverlapAvoidance.Gentle => new OverlapProfile(0.16, 3, 96),
            SmartOverlapAvoidance.Strong => new OverlapProfile(1.00, 12, 520),
            _ => new OverlapProfile(0.72, 8, 280),
        };

        var strengthBudget = options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => 64,
            SmartTidyStrength.Assertive => 144,
            _ => 96,
        };
        var movementBudget = Math.Max(strengthBudget, profile.MinimumMovementBudget);

        for (var pass = 0; pass < profile.Passes; pass++)
        {
            var changed = false;

            for (var i = 0; i < states.Count; i++)
            {
                for (var j = i + 1; j < states.Count; j++)
                {
                    var a = states[i];
                    var b = states[j];
                    var overlapX = Math.Min(a.Current.Right, b.Current.Right) - Math.Max(a.Current.Left, b.Current.Left);
                    var overlapY = Math.Min(a.Current.Bottom, b.Current.Bottom) - Math.Max(a.Current.Top, b.Current.Top);
                    if (overlapX <= 0 || overlapY <= 0)
                    {
                        continue;
                    }

                    var smallerArea = Math.Min(a.Current.Area, b.Current.Area);
                    var overlapArea = (long)overlapX * overlapY;
                    var overlapRatio = smallerArea <= 0 ? 0 : (double)overlapArea / smallerArea;
                    if (overlapRatio > profile.MaximumOverlapAreaRatio)
                    {
                        continue;
                    }

                    var axis = ChooseSeparationAxis(
                        states,
                        a,
                        b,
                        overlapX,
                        overlapY,
                        movementBudget,
                        options.RescueOffscreenWindows);

                    changed |= axis == SeparationAxis.Vertical
                        ? SeparateVertically(a, b, overlapY, movementBudget, options.RescueOffscreenWindows)
                        : SeparateHorizontally(a, b, overlapX, movementBudget, options.RescueOffscreenWindows);
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    private static SeparationAxis ChooseSeparationAxis(
        IReadOnlyList<State> states,
        State a,
        State b,
        int overlapX,
        int overlapY,
        int movementBudget,
        bool keepInsideWorkArea)
    {
        var minimumWidth = Math.Max(1, Math.Min(a.Original.Width, b.Original.Width));
        var minimumHeight = Math.Max(1, Math.Min(a.Original.Height, b.Original.Height));
        var horizontalOrderSignal = Math.Abs(CenterX(a.Original) - CenterX(b.Original)) / minimumWidth;
        var verticalOrderSignal = Math.Abs(CenterY(a.Original) - CenterY(b.Original)) / minimumHeight;

        var columnEvidence =
            a.Original.HorizontalOverlapRatio(b.Original) *
            (0.55 + Math.Min(1.5, verticalOrderSignal));
        var rowEvidence =
            a.Original.VerticalOverlapRatio(b.Original) *
            (0.55 + Math.Min(1.5, horizontalOrderSignal));

        foreach (var anchor in states)
        {
            if (ReferenceEquals(anchor, a) || ReferenceEquals(anchor, b))
            {
                continue;
            }

            if (SharesColumnContext(anchor, a, b))
            {
                columnEvidence += 0.75;
            }

            if (SharesRowContext(anchor, a, b))
            {
                rowEvidence += 0.75;
            }
        }

        var horizontalCapacity = GetHorizontalSeparationCapacity(a, b, movementBudget, keepInsideWorkArea);
        var verticalCapacity = GetVerticalSeparationCapacity(a, b, movementBudget, keepInsideWorkArea);

        // Structural intent wins while that axis still has useful travel. This is what keeps an
        // A | (B over C) arrangement as a left column plus right stack instead of pushing B/C apart
        // horizontally and destroying the user's apparent topology.
        if (columnEvidence > rowEvidence + 0.15 && verticalCapacity > 0)
        {
            return SeparationAxis.Vertical;
        }

        if (rowEvidence > columnEvidence + 0.15 && horizontalCapacity > 0)
        {
            return SeparationAxis.Horizontal;
        }

        if (verticalCapacity <= 0)
        {
            return SeparationAxis.Horizontal;
        }

        if (horizontalCapacity <= 0)
        {
            return SeparationAxis.Vertical;
        }

        var normalizedX = (double)overlapX / Math.Max(1, Math.Min(a.Current.Width, b.Current.Width));
        var normalizedY = (double)overlapY / Math.Max(1, Math.Min(a.Current.Height, b.Current.Height));
        var horizontalFeasibility = Math.Min(1.0, (double)horizontalCapacity / (overlapX + 1));
        var verticalFeasibility = Math.Min(1.0, (double)verticalCapacity / (overlapY + 1));
        var horizontalCost = normalizedX / Math.Max(0.25, horizontalFeasibility);
        var verticalCost = normalizedY / Math.Max(0.25, verticalFeasibility);

        return verticalCost < horizontalCost
            ? SeparationAxis.Vertical
            : SeparationAxis.Horizontal;
    }

    private static bool SharesColumnContext(State anchor, State a, State b)
    {
        var tolerance = Math.Max(32, anchor.Snapshot.WorkArea.Width / 50);
        var bothRight =
            a.Original.Left >= anchor.Original.Right - tolerance &&
            b.Original.Left >= anchor.Original.Right - tolerance;
        var bothLeft =
            a.Original.Right <= anchor.Original.Left + tolerance &&
            b.Original.Right <= anchor.Original.Left + tolerance;
        if (!bothRight && !bothLeft)
        {
            return false;
        }

        return a.Original.VerticalOverlapRatio(anchor.Original) >= 0.20 &&
               b.Original.VerticalOverlapRatio(anchor.Original) >= 0.20;
    }

    private static bool SharesRowContext(State anchor, State a, State b)
    {
        var tolerance = Math.Max(24, anchor.Snapshot.WorkArea.Height / 45);
        var bothBelow =
            a.Original.Top >= anchor.Original.Bottom - tolerance &&
            b.Original.Top >= anchor.Original.Bottom - tolerance;
        var bothAbove =
            a.Original.Bottom <= anchor.Original.Top + tolerance &&
            b.Original.Bottom <= anchor.Original.Top + tolerance;
        if (!bothBelow && !bothAbove)
        {
            return false;
        }

        return a.Original.HorizontalOverlapRatio(anchor.Original) >= 0.20 &&
               b.Original.HorizontalOverlapRatio(anchor.Original) >= 0.20;
    }

    private static bool SeparateHorizontally(
        State a,
        State b,
        int overlap,
        int movementBudget,
        bool keepInsideWorkArea)
    {
        var aBeforeB = CenterX(a.Current) <= CenterX(b.Current);
        var left = aBeforeB ? a : b;
        var right = aBeforeB ? b : a;
        var total = overlap + 1;
        var leftCapacity = CapacityLeft(left, movementBudget, keepInsideWorkArea);
        var rightCapacity = CapacityRight(right, movementBudget, keepInsideWorkArea);
        AllocateMovement(left, right, total, leftCapacity, rightCapacity, out var leftShare, out var rightShare);

        if (leftShare == 0 && rightShare == 0)
        {
            return false;
        }

        var beforeLeft = left.Current;
        var beforeRight = right.Current;
        left.TranslateX(-leftShare, movementBudget);
        right.TranslateX(rightShare, movementBudget);

        if (keepInsideWorkArea)
        {
            left.Current = FitInsideWorkArea(left.Snapshot, left.Current);
            right.Current = FitInsideWorkArea(right.Snapshot, right.Current);
        }

        return left.Current != beforeLeft || right.Current != beforeRight;
    }

    private static bool SeparateVertically(
        State a,
        State b,
        int overlap,
        int movementBudget,
        bool keepInsideWorkArea)
    {
        var aBeforeB = CenterY(a.Current) <= CenterY(b.Current);
        var top = aBeforeB ? a : b;
        var bottom = aBeforeB ? b : a;
        var total = overlap + 1;
        var topCapacity = CapacityUp(top, movementBudget, keepInsideWorkArea);
        var bottomCapacity = CapacityDown(bottom, movementBudget, keepInsideWorkArea);
        AllocateMovement(top, bottom, total, topCapacity, bottomCapacity, out var topShare, out var bottomShare);

        if (topShare == 0 && bottomShare == 0)
        {
            return false;
        }

        var beforeTop = top.Current;
        var beforeBottom = bottom.Current;
        top.TranslateY(-topShare, movementBudget);
        bottom.TranslateY(bottomShare, movementBudget);

        if (keepInsideWorkArea)
        {
            top.Current = FitInsideWorkArea(top.Snapshot, top.Current);
            bottom.Current = FitInsideWorkArea(bottom.Snapshot, bottom.Current);
        }

        return top.Current != beforeTop || bottom.Current != beforeBottom;
    }

    private static int GetHorizontalSeparationCapacity(
        State a,
        State b,
        int movementBudget,
        bool keepInsideWorkArea)
    {
        var aBeforeB = CenterX(a.Current) <= CenterX(b.Current);
        var left = aBeforeB ? a : b;
        var right = aBeforeB ? b : a;
        return CapacityLeft(left, movementBudget, keepInsideWorkArea) +
               CapacityRight(right, movementBudget, keepInsideWorkArea);
    }

    private static int GetVerticalSeparationCapacity(
        State a,
        State b,
        int movementBudget,
        bool keepInsideWorkArea)
    {
        var aBeforeB = CenterY(a.Current) <= CenterY(b.Current);
        var top = aBeforeB ? a : b;
        var bottom = aBeforeB ? b : a;
        return CapacityUp(top, movementBudget, keepInsideWorkArea) +
               CapacityDown(bottom, movementBudget, keepInsideWorkArea);
    }

    private static int CapacityLeft(State state, int budget, bool keepInsideWorkArea)
    {
        var minimumLeft = state.Original.Left - budget;
        if (keepInsideWorkArea && state.Current.Width <= state.Snapshot.WorkArea.Width)
        {
            minimumLeft = Math.Max(minimumLeft, state.Snapshot.WorkArea.Left);
        }

        return Math.Max(0, state.Current.Left - minimumLeft);
    }

    private static int CapacityRight(State state, int budget, bool keepInsideWorkArea)
    {
        var maximumLeft = state.Original.Left + budget;
        if (keepInsideWorkArea && state.Current.Width <= state.Snapshot.WorkArea.Width)
        {
            maximumLeft = Math.Min(maximumLeft, state.Snapshot.WorkArea.Right - state.Current.Width);
        }

        return Math.Max(0, maximumLeft - state.Current.Left);
    }

    private static int CapacityUp(State state, int budget, bool keepInsideWorkArea)
    {
        var minimumTop = state.Original.Top - budget;
        if (keepInsideWorkArea && state.Current.Height <= state.Snapshot.WorkArea.Height)
        {
            minimumTop = Math.Max(minimumTop, state.Snapshot.WorkArea.Top);
        }

        return Math.Max(0, state.Current.Top - minimumTop);
    }

    private static int CapacityDown(State state, int budget, bool keepInsideWorkArea)
    {
        var maximumTop = state.Original.Top + budget;
        if (keepInsideWorkArea && state.Current.Height <= state.Snapshot.WorkArea.Height)
        {
            maximumTop = Math.Min(maximumTop, state.Snapshot.WorkArea.Bottom - state.Current.Height);
        }

        return Math.Max(0, maximumTop - state.Current.Top);
    }

    private static void AllocateMovement(
        State first,
        State second,
        int total,
        int firstCapacity,
        int secondCapacity,
        out int firstShare,
        out int secondShare)
    {
        if (total <= 0 || firstCapacity + secondCapacity <= 0)
        {
            firstShare = 0;
            secondShare = 0;
            return;
        }

        var firstMobility = 1.0 / Math.Max(0.1, first.Importance);
        var secondMobility = 1.0 / Math.Max(0.1, second.Importance);
        var sum = firstMobility + secondMobility;
        var desiredFirst = (int)Math.Round(total * firstMobility / sum);

        firstShare = Math.Min(desiredFirst, firstCapacity);
        secondShare = Math.Min(total - firstShare, secondCapacity);

        var remaining = total - firstShare - secondShare;
        while (remaining > 0)
        {
            var firstRoom = firstCapacity - firstShare;
            var secondRoom = secondCapacity - secondShare;
            if (firstRoom <= 0 && secondRoom <= 0)
            {
                break;
            }

            if ((firstMobility >= secondMobility && firstRoom > 0) || secondRoom <= 0)
            {
                var take = Math.Min(remaining, firstRoom);
                firstShare += take;
                remaining -= take;
            }
            else
            {
                var take = Math.Min(remaining, secondRoom);
                secondShare += take;
                remaining -= take;
            }
        }
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

    private static double CenterX(RectI rect) => (rect.Left + rect.Right) / 2.0;
    private static double CenterY(RectI rect) => (rect.Top + rect.Bottom) / 2.0;

    private static bool HasMeaningfulChange(RectI original, RectI target) =>
        Math.Abs(original.Left - target.Left) >= 2 ||
        Math.Abs(original.Top - target.Top) >= 2 ||
        Math.Abs(original.Right - target.Right) >= 2 ||
        Math.Abs(original.Bottom - target.Bottom) >= 2;

    private sealed class State
    {
        internal State(VisibleWindow visible, RectI current)
        {
            Snapshot = visible.Window;
            Original = visible.Window.VisualRect;
            Current = current;
            Importance = 0.80 + (0.45 * Math.Clamp(visible.VisibleRatio, 0, 1));
            if (Snapshot.IsForeground)
            {
                Importance += 0.95;
            }
        }

        internal WindowSnapshot Snapshot { get; }
        internal RectI Original { get; }
        internal RectI Current { get; set; }
        internal double Importance { get; }

        internal void TranslateX(int delta, int budget)
        {
            var desired = Current.X + delta;
            var clamped = Math.Clamp(desired, Original.X - budget, Original.X + budget);
            Current = new RectI(clamped, Current.Y, Current.Width, Current.Height);
        }

        internal void TranslateY(int delta, int budget)
        {
            var desired = Current.Y + delta;
            var clamped = Math.Clamp(desired, Original.Y - budget, Original.Y + budget);
            Current = new RectI(Current.X, clamped, Current.Width, Current.Height);
        }
    }

    private enum SeparationAxis
    {
        Horizontal,
        Vertical,
    }

    private readonly record struct OverlapProfile(
        double MaximumOverlapAreaRatio,
        int Passes,
        int MinimumMovementBudget);
}
