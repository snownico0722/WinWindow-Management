namespace NeatWin.Core;

/// <summary>
/// Explicit Smart behaviors that are intentionally kept outside candidate-layout scoring. Unlike
/// the old generic post-processor these operations are conservative and may not reverse a topology
/// decision made by behavior A.
/// </summary>
internal static class HumanCenteredExplicitBehaviors
{
    internal static IReadOnlyList<TidyMove> Refine(
        IReadOnlyList<VisibleWindow> visibleWindows,
        IReadOnlyList<TidyMove> basePlan,
        TidyOptions options,
        SmartBehaviorOptions behavior, bool preservePlannedOverlap = false)
    {
        if (visibleWindows.Count == 0)
        {
            return basePlan;
        }

        var targetByHandle = basePlan.ToDictionary(
            static move => move.Window.Handle,
            static move => move.TargetVisualRect);
        var result = new List<TidyMove>();

        foreach (var group in visibleWindows.GroupBy(static item => item.Window.MonitorHandle))
        {
            var states = group
                .Select(item => new State(
                    item.Window,
                    targetByHandle.TryGetValue(item.Window.Handle, out var target)
                        ? target
                        : item.Window.VisualRect))
                .ToList();

            if (behavior.PreferReversibleVerticalFill)
            {
                ApplyVerticalFill(states, options.SmartStrength);
            }

            if (!preservePlannedOverlap) RemoveOnlyAccidentalOverlap(states, options.SmartStrength, options.RescueOffscreenWindows);

            foreach (var state in states)
            {
                var target = options.RescueOffscreenWindows
                    ? FitInside(state.Window, state.Current)
                    : state.Current;
                if (Meaningful(state.Original, target))
                {
                    result.Add(new TidyMove(state.Window, target));
                }
            }
        }

        return result;
    }

    private static void ApplyVerticalFill(List<State> states, SmartTidyStrength strength)
    {
        var profile = strength switch
        {
            SmartTidyStrength.Gentle => new VerticalFillProfile(0.93, 16, 0.08),
            SmartTidyStrength.Assertive => new VerticalFillProfile(0.78, 42, 0.28),
            _ => new VerticalFillProfile(0.86, 28, 0.16),
        };

        foreach (var state in states)
        {
            if (!state.Window.IsResizable || HasVerticalPeer(state, states))
            {
                continue;
            }

            var rect = state.Current;
            var area = state.Window.WorkArea;
            if (area.IsEmpty || rect.IsEmpty || area.Height <= 0)
            {
                continue;
            }

            if (Math.Abs(rect.Top - area.Top) <= 1 && Math.Abs(rect.Bottom - area.Bottom) <= 1)
            {
                continue;
            }

            var heightRatio = (double)rect.Height / area.Height;
            var topDistance = Math.Abs(rect.Top - area.Top);
            var bottomDistance = Math.Abs(area.Bottom - rect.Bottom);
            var growthRatio = (double)Math.Max(0, area.Height - rect.Height) / Math.Max(1, rect.Height);
            if (heightRatio < profile.MinimumHeightRatio ||
                topDistance > profile.EdgeRadius ||
                bottomDistance > profile.EdgeRadius ||
                growthRatio > profile.MaximumGrowthRatio)
            {
                continue;
            }

            state.Current = RectI.FromEdges(rect.Left, area.Top, rect.Right, area.Bottom);
        }
    }

    private static bool HasVerticalPeer(State state, IReadOnlyList<State> states)
    {
        foreach (var other in states)
        {
            if (ReferenceEquals(state, other))
            {
                continue;
            }

            if (state.Original.HorizontalOverlapRatio(other.Original) < 0.50)
            {
                continue;
            }

            var verticalGap = RectI.IntervalGap(
                state.Original.Top,
                state.Original.Bottom,
                other.Original.Top,
                other.Original.Bottom);
            var minimumHeight = Math.Max(1, Math.Min(state.Original.Height, other.Original.Height));
            var centerDelta = Math.Abs(CenterY(state.Original) - CenterY(other.Original)) / minimumHeight;
            if (centerDelta >= 0.20 &&
                (state.Original.VerticalOverlapRatio(other.Original) > 0 || verticalGap <= 56))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveOnlyAccidentalOverlap(
        List<State> states,
        SmartTidyStrength strength,
        bool keepInside)
    {
        var profile = strength switch
        {
            SmartTidyStrength.Gentle => new OverlapProfile(0.08, 36),
            SmartTidyStrength.Assertive => new OverlapProfile(0.28, 104),
            _ => new OverlapProfile(0.18, 72),
        };

        for (var pass = 0; pass < 2; pass++)
        {
            var changed = false;
            for (var first = 0; first < states.Count; first++)
            {
                for (var second = first + 1; second < states.Count; second++)
                {
                    var a = states[first];
                    var b = states[second];
                    var originalSmaller = Math.Max(1L, Math.Min(a.Original.Area, b.Original.Area));
                    var originalOverlapRatio = (double)a.Original.Intersect(b.Original).Area / originalSmaller;
                    if (originalOverlapRatio > profile.MaximumOriginalOverlapRatio)
                    {
                        // Deep overlap before NeatWin acted is likely intentional stacking.
                        continue;
                    }

                    var overlapX = Math.Min(a.Current.Right, b.Current.Right) - Math.Max(a.Current.Left, b.Current.Left);
                    var overlapY = Math.Min(a.Current.Bottom, b.Current.Bottom) - Math.Max(a.Current.Top, b.Current.Top);
                    if (overlapX <= 0 || overlapY <= 0)
                    {
                        continue;
                    }

                    var axis = InferOriginalSeparationAxis(a.Original, b.Original, overlapX, overlapY);
                    changed |= axis == Axis.Horizontal
                        ? SeparateHorizontal(a, b, overlapX + 1, profile.MovementBudget, keepInside)
                        : SeparateVertical(a, b, overlapY + 1, profile.MovementBudget, keepInside);
                }
            }

            if (!changed)
            {
                break;
            }
        }
    }

    private static Axis InferOriginalSeparationAxis(RectI a, RectI b, int overlapX, int overlapY)
    {
        var xSignal = Math.Abs(CenterX(a) - CenterX(b)) / Math.Max(1, Math.Min(a.Width, b.Width));
        var ySignal = Math.Abs(CenterY(a) - CenterY(b)) / Math.Max(1, Math.Min(a.Height, b.Height));
        if (xSignal > ySignal + 0.12)
        {
            return Axis.Horizontal;
        }
        if (ySignal > xSignal + 0.12)
        {
            return Axis.Vertical;
        }

        var normalizedX = (double)overlapX / Math.Max(1, Math.Min(a.Width, b.Width));
        var normalizedY = (double)overlapY / Math.Max(1, Math.Min(a.Height, b.Height));
        return normalizedX <= normalizedY ? Axis.Horizontal : Axis.Vertical;
    }

    private static bool SeparateHorizontal(State a, State b, int amount, int budget, bool keepInside)
    {
        var aBefore = CenterX(a.Original) <= CenterX(b.Original);
        var left = aBefore ? a : b;
        var right = aBefore ? b : a;
        var leftRoom = Available(left, Axis.Horizontal, negative: true, budget, keepInside);
        var rightRoom = Available(right, Axis.Horizontal, negative: false, budget, keepInside);
        Allocate(amount, leftRoom, rightRoom, out var leftShift, out var rightShift);
        if (leftShift == 0 && rightShift == 0)
        {
            return false;
        }

        left.Current = Translate(left.Current, -leftShift, 0);
        right.Current = Translate(right.Current, rightShift, 0);
        return true;
    }

    private static bool SeparateVertical(State a, State b, int amount, int budget, bool keepInside)
    {
        var aBefore = CenterY(a.Original) <= CenterY(b.Original);
        var top = aBefore ? a : b;
        var bottom = aBefore ? b : a;
        var topRoom = Available(top, Axis.Vertical, negative: true, budget, keepInside);
        var bottomRoom = Available(bottom, Axis.Vertical, negative: false, budget, keepInside);
        Allocate(amount, topRoom, bottomRoom, out var topShift, out var bottomShift);
        if (topShift == 0 && bottomShift == 0)
        {
            return false;
        }

        top.Current = Translate(top.Current, 0, -topShift);
        bottom.Current = Translate(bottom.Current, 0, bottomShift);
        return true;
    }

    private static int Available(State state, Axis axis, bool negative, int budget, bool keepInside)
    {
        var rect = state.Current;
        var original = state.Original;
        var area = state.Window.WorkArea;
        if (axis == Axis.Horizontal)
        {
            var used = Math.Abs(rect.Left - original.Left);
            var room = Math.Max(0, budget - used);
            if (!keepInside || area.IsEmpty || rect.Width > area.Width)
            {
                return room;
            }
            return negative
                ? Math.Min(room, Math.Max(0, rect.Left - area.Left))
                : Math.Min(room, Math.Max(0, area.Right - rect.Right));
        }

        var usedY = Math.Abs(rect.Top - original.Top);
        var roomY = Math.Max(0, budget - usedY);
        if (!keepInside || area.IsEmpty || rect.Height > area.Height)
        {
            return roomY;
        }
        return negative
            ? Math.Min(roomY, Math.Max(0, rect.Top - area.Top))
            : Math.Min(roomY, Math.Max(0, area.Bottom - rect.Bottom));
    }

    private static void Allocate(int amount, int firstRoom, int secondRoom, out int first, out int second)
    {
        first = Math.Min(firstRoom, amount / 2);
        second = Math.Min(secondRoom, amount - first);
        var remaining = amount - first - second;
        if (remaining > 0)
        {
            var takeFirst = Math.Min(remaining, firstRoom - first);
            first += takeFirst;
            remaining -= takeFirst;
        }
        if (remaining > 0)
        {
            second += Math.Min(remaining, secondRoom - second);
        }
    }

    private static RectI FitInside(WindowSnapshot snapshot, RectI rect)
    {
        var area = snapshot.WorkArea;
        if (area.IsEmpty || rect.IsEmpty)
        {
            return rect;
        }

        var width = snapshot.IsResizable ? Math.Min(rect.Width, area.Width) : rect.Width;
        var height = snapshot.IsResizable ? Math.Min(rect.Height, area.Height) : rect.Height;
        var left = width <= area.Width ? Math.Clamp(rect.Left, area.Left, area.Right - width) : area.Left;
        var top = height <= area.Height ? Math.Clamp(rect.Top, area.Top, area.Bottom - height) : area.Top;
        return new RectI(left, top, width, height);
    }

    private static RectI Translate(RectI rect, int dx, int dy) =>
        new(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);

    private static bool Meaningful(RectI a, RectI b) =>
        Math.Abs(a.Left - b.Left) >= 2 || Math.Abs(a.Top - b.Top) >= 2 ||
        Math.Abs(a.Right - b.Right) >= 2 || Math.Abs(a.Bottom - b.Bottom) >= 2;

    private static double CenterX(RectI rect) => (rect.Left + rect.Right) / 2.0;
    private static double CenterY(RectI rect) => (rect.Top + rect.Bottom) / 2.0;

    private sealed class State(WindowSnapshot window, RectI current)
    {
        internal WindowSnapshot Window { get; } = window;
        internal RectI Original { get; } = window.VisualRect;
        internal RectI Current { get; set; } = current;
    }

    private enum Axis
    {
        Horizontal,
        Vertical,
    }

    private readonly record struct VerticalFillProfile(
        double MinimumHeightRatio,
        int EdgeRadius,
        double MaximumGrowthRatio);

    private readonly record struct OverlapProfile(
        double MaximumOriginalOverlapRatio,
        int MovementBudget);
}
