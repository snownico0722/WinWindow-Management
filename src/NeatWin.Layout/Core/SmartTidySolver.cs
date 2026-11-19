namespace NeatWin.Core;

/// <summary>
/// Geometry-only smart solver. It infers a sparse relationship graph from the current floating
/// arrangement, then approximately minimizes a weighted quadratic energy using damped Jacobi
/// relaxation. Hard usability constraints are projected after every iteration.
/// </summary>
internal static class SmartTidySolver
{
    private const double MinimumWeight = 0.05;
    private const double Relaxation = 0.46;

    // Once a relation has passed geometric inference it should behave close to a hard constraint
    // at the default user weights. The user-facing Preserve/Orderliness/Screen weights still scale
    // these values, so users can deliberately make the optimizer softer or more assertive.
    private const double NeighborConstraintScale = 40.0;
    private const double AlignmentConstraintScale = 14.0;
    private const double ScreenConstraintScale = 120.0;
    private const double SizePreservationConstraintScale = 8.0;

    internal static IReadOnlyList<TidyMove> CreatePlan(
        IReadOnlyList<VisibleWindow> visibleWindows,
        TidyOptions options)
    {
        var result = new List<TidyMove>();

        foreach (var monitorGroup in visibleWindows.GroupBy(static item => item.Window.MonitorHandle))
        {
            var states = monitorGroup
                .Select(item => new SmartWindow(item, options))
                .ToList();

            if (states.Count == 0)
            {
                continue;
            }

            var constraints = InferConstraints(states, options);
            var iterations = Math.Clamp(options.SmartIterations, 4, 128);

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                Relax(states, constraints, options);
            }

            foreach (var state in states)
            {
                var target = state.Current.ToRectI();
                if (options.RescueOffscreenWindows)
                {
                    target = FitInsideWorkArea(state.Snapshot, target);
                }

                if (HasMeaningfulChange(state.Snapshot.VisualRect, target))
                {
                    result.Add(new TidyMove(state.Snapshot, target));
                }
            }
        }

        return result;
    }

    private static List<EdgeConstraint> InferConstraints(
        IReadOnlyList<SmartWindow> windows,
        TidyOptions options)
    {
        var result = new List<EdgeConstraint>();
        var orderliness = Math.Max(0, options.OrderlinessWeight);
        var spaceUsage = Math.Max(0, options.SpaceUsageWeight);
        var resizeResistance = Math.Max(0, options.ResizeResistanceWeight);

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            AddScreenAnchor(result, i, Edge.Left, window.Baseline.Left, window.Snapshot.WorkArea.Left,
                options.ScreenSnapDistance, ScreenConstraintScale * spaceUsage);
            AddScreenAnchor(result, i, Edge.Right, window.Baseline.Right, window.Snapshot.WorkArea.Right,
                options.ScreenSnapDistance, ScreenConstraintScale * spaceUsage);
            AddScreenAnchor(result, i, Edge.Top, window.Baseline.Top, window.Snapshot.WorkArea.Top,
                options.ScreenSnapDistance, ScreenConstraintScale * spaceUsage);
            AddScreenAnchor(result, i, Edge.Bottom, window.Baseline.Bottom, window.Snapshot.WorkArea.Bottom,
                options.ScreenSnapDistance, ScreenConstraintScale * spaceUsage);

            if (window.Snapshot.IsResizable && resizeResistance > 0)
            {
                var sizeWeight = SizePreservationConstraintScale * resizeResistance * window.Importance;
                result.Add(EdgeConstraint.Pair(
                    i,
                    Edge.Right,
                    i,
                    Edge.Left,
                    window.Baseline.Width,
                    sizeWeight));
                result.Add(EdgeConstraint.Pair(
                    i,
                    Edge.Bottom,
                    i,
                    Edge.Top,
                    window.Baseline.Height,
                    sizeWeight));
            }
        }

        for (var i = 0; i < windows.Count; i++)
        {
            for (var j = i + 1; j < windows.Count; j++)
            {
                var a = windows[i].Baseline;
                var b = windows[j].Baseline;
                var verticalOverlap = VerticalOverlapRatio(a, b);
                var horizontalOverlap = HorizontalOverlapRatio(a, b);

                if (verticalOverlap >= options.MinimumNeighborOverlapRatio)
                {
                    var aBeforeB = CenterX(a) <= CenterX(b);
                    var leftIndex = aBeforeB ? i : j;
                    var rightIndex = aBeforeB ? j : i;
                    var leftRect = windows[leftIndex].Baseline;
                    var rightRect = windows[rightIndex].Baseline;
                    var gap = rightRect.Left - leftRect.Right;
                    if (Math.Abs(gap) <= options.NeighborSnapDistance)
                    {
                        var confidence = GaussianConfidence(
                            Math.Abs(gap),
                            Math.Max(8, options.NeighborSnapDistance * 0.58)) *
                            (0.35 + 0.65 * verticalOverlap);

                        result.Add(EdgeConstraint.Pair(
                            leftIndex,
                            Edge.Right,
                            rightIndex,
                            Edge.Left,
                            0,
                            NeighborConstraintScale * orderliness * confidence));
                    }

                    AddPairAlignment(result, i, Edge.Top, j, Edge.Top, a.Top, b.Top,
                        options.AlignmentSnapDistance, AlignmentConstraintScale * orderliness * verticalOverlap);
                    AddPairAlignment(result, i, Edge.Bottom, j, Edge.Bottom, a.Bottom, b.Bottom,
                        options.AlignmentSnapDistance, AlignmentConstraintScale * orderliness * verticalOverlap);
                }

                if (horizontalOverlap >= options.MinimumNeighborOverlapRatio)
                {
                    var aBeforeB = CenterY(a) <= CenterY(b);
                    var topIndex = aBeforeB ? i : j;
                    var bottomIndex = aBeforeB ? j : i;
                    var topRect = windows[topIndex].Baseline;
                    var bottomRect = windows[bottomIndex].Baseline;
                    var gap = bottomRect.Top - topRect.Bottom;
                    if (Math.Abs(gap) <= options.NeighborSnapDistance)
                    {
                        var confidence = GaussianConfidence(
                            Math.Abs(gap),
                            Math.Max(8, options.NeighborSnapDistance * 0.58)) *
                            (0.35 + 0.65 * horizontalOverlap);

                        result.Add(EdgeConstraint.Pair(
                            topIndex,
                            Edge.Bottom,
                            bottomIndex,
                            Edge.Top,
                            0,
                            NeighborConstraintScale * orderliness * confidence));
                    }

                    AddPairAlignment(result, i, Edge.Left, j, Edge.Left, a.Left, b.Left,
                        options.AlignmentSnapDistance, AlignmentConstraintScale * orderliness * horizontalOverlap);
                    AddPairAlignment(result, i, Edge.Right, j, Edge.Right, a.Right, b.Right,
                        options.AlignmentSnapDistance, AlignmentConstraintScale * orderliness * horizontalOverlap);
                }
            }
        }

        return result.Where(static constraint => constraint.Weight >= MinimumWeight).ToList();
    }

    private static void AddScreenAnchor(
        List<EdgeConstraint> constraints,
        int windowIndex,
        Edge edge,
        double current,
        double target,
        int threshold,
        double baseWeight)
    {
        var distance = Math.Abs(current - target);
        if (distance > threshold || threshold <= 0)
        {
            return;
        }

        var confidence = GaussianConfidence(distance, Math.Max(6, threshold * 0.58));
        constraints.Add(EdgeConstraint.Anchor(windowIndex, edge, target, baseWeight * confidence));
    }

    private static void AddPairAlignment(
        List<EdgeConstraint> constraints,
        int firstWindow,
        Edge firstEdge,
        int secondWindow,
        Edge secondEdge,
        double firstValue,
        double secondValue,
        int threshold,
        double baseWeight)
    {
        var distance = Math.Abs(firstValue - secondValue);
        if (distance > threshold || threshold <= 0)
        {
            return;
        }

        var confidence = GaussianConfidence(distance, Math.Max(4, threshold * 0.55));
        constraints.Add(EdgeConstraint.Pair(
            firstWindow,
            firstEdge,
            secondWindow,
            secondEdge,
            0,
            baseWeight * confidence));
    }

    private static void Relax(
        IReadOnlyList<SmartWindow> windows,
        IReadOnlyList<EdgeConstraint> constraints,
        TidyOptions options)
    {
        var accumulators = new EdgeAccumulator[windows.Count];
        var preserve = Math.Max(MinimumWeight, options.PreserveLayoutWeight);

        for (var i = 0; i < windows.Count; i++)
        {
            var state = windows[i];
            var weight = preserve * state.Importance;
            accumulators[i].Add(Edge.Left, state.Baseline.Left, weight);
            accumulators[i].Add(Edge.Right, state.Baseline.Right, weight);
            accumulators[i].Add(Edge.Top, state.Baseline.Top, weight);
            accumulators[i].Add(Edge.Bottom, state.Baseline.Bottom, weight);
        }

        foreach (var constraint in constraints)
        {
            if (constraint.OtherWindowIndex is int otherIndex)
            {
                var firstCurrent = windows[constraint.WindowIndex].Current.Get(constraint.Edge);
                var otherCurrent = windows[otherIndex].Current.Get(constraint.OtherEdge);

                accumulators[constraint.WindowIndex].Add(
                    constraint.Edge,
                    otherCurrent + constraint.Delta,
                    constraint.Weight);
                accumulators[otherIndex].Add(
                    constraint.OtherEdge,
                    firstCurrent - constraint.Delta,
                    constraint.Weight);
            }
            else
            {
                accumulators[constraint.WindowIndex].Add(constraint.Edge, constraint.AnchorValue, constraint.Weight);
            }
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var state = windows[i];
            var current = state.Current;
            var proposed = new RectD(
                Lerp(current.Left, accumulators[i].Target(Edge.Left, current.Left), Relaxation),
                Lerp(current.Top, accumulators[i].Target(Edge.Top, current.Top), Relaxation),
                Lerp(current.Right, accumulators[i].Target(Edge.Right, current.Right), Relaxation),
                Lerp(current.Bottom, accumulators[i].Target(Edge.Bottom, current.Bottom), Relaxation));

            state.Current = Project(state, proposed, options);
        }
    }

    private static RectD Project(SmartWindow state, RectD proposed, TidyOptions options)
    {
        var baseline = state.Baseline;
        var maxEdge = Math.Max(0, options.MaximumEdgeAdjustment);

        if (!state.Snapshot.IsResizable)
        {
            var targetCenterX = (proposed.Left + proposed.Right) / 2.0;
            var targetCenterY = (proposed.Top + proposed.Bottom) / 2.0;
            var baselineCenterX = CenterX(baseline);
            var baselineCenterY = CenterY(baseline);
            var centerX = Math.Clamp(targetCenterX, baselineCenterX - maxEdge, baselineCenterX + maxEdge);
            var centerY = Math.Clamp(targetCenterY, baselineCenterY - maxEdge, baselineCenterY + maxEdge);
            var rect = RectD.FromCenter(centerX, centerY, baseline.Width, baseline.Height);
            return options.RescueOffscreenWindows ? FitInsideWorkArea(state.Snapshot, rect) : rect;
        }

        var left = Math.Clamp(proposed.Left, baseline.Left - maxEdge, baseline.Left + maxEdge);
        var right = Math.Clamp(proposed.Right, baseline.Right - maxEdge, baseline.Right + maxEdge);
        var top = Math.Clamp(proposed.Top, baseline.Top - maxEdge, baseline.Top + maxEdge);
        var bottom = Math.Clamp(proposed.Bottom, baseline.Bottom - maxEdge, baseline.Bottom + maxEdge);

        var resizeRatio = Math.Clamp(options.MaximumSizeChangeRatio, 0, 1);
        var minWidthFloor = Math.Min(baseline.Width, options.MinimumWidth);
        var minHeightFloor = Math.Min(baseline.Height, options.MinimumHeight);
        var minWidth = Math.Max(minWidthFloor, baseline.Width * (1 - resizeRatio));
        var maxWidth = Math.Max(minWidth, baseline.Width * (1 + resizeRatio));
        var minHeight = Math.Max(minHeightFloor, baseline.Height * (1 - resizeRatio));
        var maxHeight = Math.Max(minHeight, baseline.Height * (1 + resizeRatio));

        var width = Math.Clamp(Math.Max(1, right - left), minWidth, maxWidth);
        var height = Math.Clamp(Math.Max(1, bottom - top), minHeight, maxHeight);
        var rectResult = RectD.FromCenter((left + right) / 2.0, (top + bottom) / 2.0, width, height);
        return options.RescueOffscreenWindows ? FitInsideWorkArea(state.Snapshot, rectResult) : rectResult;
    }

    private static RectI FitInsideWorkArea(WindowSnapshot snapshot, RectI rect) =>
        FitInsideWorkArea(snapshot, RectD.FromRectI(rect)).ToRectI();

    private static RectD FitInsideWorkArea(WindowSnapshot snapshot, RectD rect)
    {
        var workArea = snapshot.WorkArea;
        if (workArea.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return rect;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (snapshot.IsResizable)
        {
            width = Math.Min(width, workArea.Width);
            height = Math.Min(height, workArea.Height);
        }

        var left = width <= workArea.Width
            ? Math.Clamp(rect.Left, workArea.Left, workArea.Right - width)
            : workArea.Left;
        var top = height <= workArea.Height
            ? Math.Clamp(rect.Top, workArea.Top, workArea.Bottom - height)
            : workArea.Top;

        return new RectD(left, top, left + width, top + height);
    }

    private static bool HasMeaningfulChange(RectI original, RectI target) =>
        Math.Abs(original.Left - target.Left) >= 2 ||
        Math.Abs(original.Top - target.Top) >= 2 ||
        Math.Abs(original.Right - target.Right) >= 2 ||
        Math.Abs(original.Bottom - target.Bottom) >= 2;

    private static double GaussianConfidence(double distance, double sigma)
    {
        if (sigma <= 0)
        {
            return distance <= 0 ? 1 : 0;
        }

        var normalized = distance / sigma;
        return Math.Exp(-0.5 * normalized * normalized);
    }

    private static double VerticalOverlapRatio(RectD a, RectD b)
    {
        var overlap = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var denominator = Math.Min(a.Height, b.Height);
        return denominator <= 0 ? 0 : overlap / denominator;
    }

    private static double HorizontalOverlapRatio(RectD a, RectD b)
    {
        var overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var denominator = Math.Min(a.Width, b.Width);
        return denominator <= 0 ? 0 : overlap / denominator;
    }

    private static double CenterX(RectD rect) => (rect.Left + rect.Right) / 2.0;
    private static double CenterY(RectD rect) => (rect.Top + rect.Bottom) / 2.0;
    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private sealed class SmartWindow
    {
        internal SmartWindow(VisibleWindow visibleWindow, TidyOptions options)
        {
            Visible = visibleWindow;
            Snapshot = visibleWindow.Window;
            var original = RectD.FromRectI(Snapshot.VisualRect);
            Baseline = options.RescueOffscreenWindows
                ? FitInsideWorkArea(Snapshot, original)
                : original;
            Current = Baseline;

            // The actively focused window is a stronger anchor. A partially occluded window is
            // deliberately a little easier to move because its current geometry is a weaker signal
            // of user intent than the window the user is actively working in.
            Importance = 0.80 + (0.45 * Math.Clamp(visibleWindow.VisibleRatio, 0, 1));
            if (Snapshot.IsForeground)
            {
                Importance += 0.95;
            }
        }

        internal VisibleWindow Visible { get; }
        internal WindowSnapshot Snapshot { get; }
        internal RectD Baseline { get; }
        internal RectD Current { get; set; }
        internal double Importance { get; }
    }

    private enum Edge
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    private readonly record struct EdgeConstraint(
        int WindowIndex,
        Edge Edge,
        int? OtherWindowIndex,
        Edge OtherEdge,
        double Delta,
        double AnchorValue,
        double Weight)
    {
        internal static EdgeConstraint Pair(
            int firstWindow,
            Edge firstEdge,
            int secondWindow,
            Edge secondEdge,
            double delta,
            double weight) =>
            new(firstWindow, firstEdge, secondWindow, secondEdge, delta, 0, weight);

        internal static EdgeConstraint Anchor(
            int window,
            Edge edge,
            double anchor,
            double weight) =>
            new(window, edge, null, edge, 0, anchor, weight);
    }

    private struct EdgeAccumulator
    {
        private double _leftSum;
        private double _leftWeight;
        private double _topSum;
        private double _topWeight;
        private double _rightSum;
        private double _rightWeight;
        private double _bottomSum;
        private double _bottomWeight;

        internal void Add(Edge edge, double value, double weight)
        {
            if (weight <= 0)
            {
                return;
            }

            switch (edge)
            {
                case Edge.Left:
                    _leftSum += value * weight;
                    _leftWeight += weight;
                    break;
                case Edge.Top:
                    _topSum += value * weight;
                    _topWeight += weight;
                    break;
                case Edge.Right:
                    _rightSum += value * weight;
                    _rightWeight += weight;
                    break;
                case Edge.Bottom:
                    _bottomSum += value * weight;
                    _bottomWeight += weight;
                    break;
            }
        }

        internal double Target(Edge edge, double fallback) => edge switch
        {
            Edge.Left => _leftWeight > 0 ? _leftSum / _leftWeight : fallback,
            Edge.Top => _topWeight > 0 ? _topSum / _topWeight : fallback,
            Edge.Right => _rightWeight > 0 ? _rightSum / _rightWeight : fallback,
            Edge.Bottom => _bottomWeight > 0 ? _bottomSum / _bottomWeight : fallback,
            _ => fallback,
        };
    }

    private readonly record struct RectD(double Left, double Top, double Right, double Bottom)
    {
        internal double Width => Math.Max(0, Right - Left);
        internal double Height => Math.Max(0, Bottom - Top);

        internal double Get(Edge edge) => edge switch
        {
            Edge.Left => Left,
            Edge.Top => Top,
            Edge.Right => Right,
            Edge.Bottom => Bottom,
            _ => 0,
        };

        internal RectI ToRectI() => RectI.FromEdges(
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            (int)Math.Round(Right),
            (int)Math.Round(Bottom));

        internal static RectD FromRectI(RectI rect) =>
            new(rect.Left, rect.Top, rect.Right, rect.Bottom);

        internal static RectD FromCenter(double centerX, double centerY, double width, double height) =>
            new(
                centerX - (width / 2.0),
                centerY - (height / 2.0),
                centerX + (width / 2.0),
                centerY + (height / 2.0));
    }
}
