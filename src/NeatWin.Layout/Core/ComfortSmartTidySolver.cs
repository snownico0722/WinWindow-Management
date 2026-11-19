namespace NeatWin.Core;

/// <summary>
/// Comfort-oriented Smart solver. It infers a small, fixed set of relationships from the user's
/// original layout, then solves only for window translations. Window size is deliberately not a
/// generic optimization variable: resizing is reserved for explicit behaviors such as offscreen
/// rescue, reversible vertical fill and video-aspect correction.
/// </summary>
internal static class ComfortSmartTidySolver
{
    private const double MinimumOrthogonalOverlap = 0.30;
    private const double ConstraintTolerance = 1.5;

    internal static IReadOnlyList<TidyMove> CreatePlan(
        IReadOnlyList<VisibleWindow> visibleWindows,
        TidyOptions options)
    {
        var result = new List<TidyMove>();
        var profile = ResolveProfile(options.SmartStrength);

        foreach (var monitorGroup in visibleWindows.GroupBy(static item => item.Window.MonitorHandle))
        {
            var states = monitorGroup
                .Select(item => new State(item, options.RescueOffscreenWindows
                    ? FitInsideWorkArea(item.Window, item.Window.VisualRect)
                    : item.Window.VisualRect))
                .ToList();

            if (states.Count == 0)
            {
                continue;
            }

            var x = SolveAxis(states, Axis.Horizontal, options, profile);
            var y = SolveAxis(states, Axis.Vertical, options, profile);

            for (var index = 0; index < states.Count; index++)
            {
                var state = states[index];
                var baseline = state.Baseline;
                var target = new RectI(
                    baseline.X + x[index],
                    baseline.Y + y[index],
                    baseline.Width,
                    baseline.Height);

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

    private static ComfortProfile ResolveProfile(SmartTidyStrength strength) => strength switch
    {
        SmartTidyStrength.Gentle => new ComfortProfile(
            NeighborRadius: 24,
            AlignmentRadius: 10,
            ScreenRadius: 14,
            MovementBudget: 48),
        SmartTidyStrength.Assertive => new ComfortProfile(
            NeighborRadius: 64,
            AlignmentRadius: 24,
            ScreenRadius: 36,
            MovementBudget: 104),
        _ => new ComfortProfile(
            NeighborRadius: 40,
            AlignmentRadius: 16,
            ScreenRadius: 24,
            MovementBudget: 72),
    };

    private static int[] SolveAxis(
        IReadOnlyList<State> states,
        Axis axis,
        TidyOptions options,
        ComfortProfile profile)
    {
        var count = states.Count;
        var union = new PotentialUnionFind(count);
        var candidates = InferPairConstraints(states, axis, profile)
            .OrderByDescending(static item => item.Priority)
            .ToArray();

        foreach (var candidate in candidates)
        {
            _ = union.TryConstrain(
                candidate.First,
                candidate.Second,
                candidate.SecondMinusFirst,
                ConstraintTolerance);
        }

        var components = Enumerable.Range(0, count)
            .GroupBy(union.Root)
            .ToArray();
        var translation = new int[count];

        foreach (var component in components)
        {
            var members = component.ToArray();
            var lower = double.NegativeInfinity;
            var upper = double.PositiveInfinity;
            var weightedPotential = 0.0;
            var totalImportance = 0.0;
            var screenAnchors = new List<ScreenAnchor>();

            foreach (var index in members)
            {
                var potential = union.Potential(index);
                var state = states[index];
                var budget = profile.MovementBudget;

                lower = Math.Max(lower, -budget - potential);
                upper = Math.Min(upper, budget - potential);

                if (options.RescueOffscreenWindows)
                {
                    GetWorkAreaTranslationRange(state, axis, out var minimumShift, out var maximumShift);
                    lower = Math.Max(lower, minimumShift - potential);
                    upper = Math.Min(upper, maximumShift - potential);
                }

                weightedPotential += potential * state.Importance;
                totalImportance += state.Importance;

                if (TryGetScreenAnchor(state, axis, profile.ScreenRadius, out var anchor))
                {
                    screenAnchors.Add(anchor with
                    {
                        RootTarget = anchor.WindowShift - potential,
                        Score = anchor.Score + (state.Snapshot.IsForeground ? 0.18 : 0),
                    });
                }
            }

            // A chain of otherwise reasonable pair constraints can occasionally become impossible
            // to fit inside the work area. In that case fail conservatively: keep the original
            // component geometry and only allow each window's own unambiguous screen snap.
            if (lower > upper)
            {
                foreach (var index in members)
                {
                    translation[index] = TryGetScreenAnchor(
                        states[index], axis, profile.ScreenRadius, out var anchor)
                        ? ClampIndividualShift(states[index], axis, anchor.WindowShift, options, profile)
                        : 0;
                }
                continue;
            }

            // For an unconstrained component this is the exact least-squares minimum of total
            // weighted movement. Foreground / highly visible windows therefore move less without
            // needing a separate post-processing rule.
            var rootTarget = totalImportance > 0
                ? -weightedPotential / totalImportance
                : 0;

            // Screen edges are discrete anchors, not a continuous "use more space" force. A
            // component uses at most one compatible screen anchor per axis; pair relationships stay
            // exact and the other outer edge may remain slightly inset rather than resizing.
            var bestAnchor = screenAnchors
                .Where(anchor => anchor.RootTarget >= lower - 0.5 && anchor.RootTarget <= upper + 0.5)
                .OrderByDescending(static anchor => anchor.Score)
                .FirstOrDefault();
            if (bestAnchor.IsValid)
            {
                rootTarget = bestAnchor.RootTarget;
            }

            rootTarget = Math.Clamp(rootTarget, lower, upper);
            foreach (var index in members)
            {
                translation[index] = (int)Math.Round(rootTarget + union.Potential(index));
            }
        }

        return translation;
    }

    private static IEnumerable<PairConstraint> InferPairConstraints(
        IReadOnlyList<State> states,
        Axis axis,
        ComfortProfile profile)
    {
        var neighborRadius = profile.NeighborRadius;
        var alignmentRadius = profile.AlignmentRadius;

        for (var first = 0; first < states.Count; first++)
        {
            for (var second = first + 1; second < states.Count; second++)
            {
                var a = states[first].Baseline;
                var b = states[second].Baseline;

                if (axis == Axis.Horizontal)
                {
                    var verticalOverlap = a.VerticalOverlapRatio(b);
                    if (verticalOverlap >= MinimumOrthogonalOverlap)
                    {
                        var aBeforeB = CenterX(a) <= CenterX(b);
                        var leftIndex = aBeforeB ? first : second;
                        var rightIndex = aBeforeB ? second : first;
                        var left = states[leftIndex].Baseline;
                        var right = states[rightIndex].Baseline;
                        var gap = right.Left - left.Right;
                        if (Math.Abs(gap) <= neighborRadius)
                        {
                            var proximity = Proximity(Math.Abs(gap), neighborRadius);
                            yield return new PairConstraint(
                                leftIndex,
                                rightIndex,
                                -gap,
                                400 + (proximity * 100) + (verticalOverlap * 35));
                        }
                    }

                    if (IsVerticallyRelated(a, b, neighborRadius))
                    {
                        var leftError = b.Left - a.Left;
                        var rightError = b.Right - a.Right;
                        var selected = Math.Abs(leftError) <= Math.Abs(rightError)
                            ? leftError
                            : rightError;
                        if (Math.Abs(selected) <= alignmentRadius)
                        {
                            var proximity = Proximity(Math.Abs(selected), alignmentRadius);
                            yield return new PairConstraint(
                                first,
                                second,
                                -selected,
                                210 + (proximity * 70));
                        }
                    }
                }
                else
                {
                    var horizontalOverlap = a.HorizontalOverlapRatio(b);
                    if (horizontalOverlap >= MinimumOrthogonalOverlap)
                    {
                        var aBeforeB = CenterY(a) <= CenterY(b);
                        var topIndex = aBeforeB ? first : second;
                        var bottomIndex = aBeforeB ? second : first;
                        var top = states[topIndex].Baseline;
                        var bottom = states[bottomIndex].Baseline;
                        var gap = bottom.Top - top.Bottom;
                        if (Math.Abs(gap) <= neighborRadius)
                        {
                            var proximity = Proximity(Math.Abs(gap), neighborRadius);
                            yield return new PairConstraint(
                                topIndex,
                                bottomIndex,
                                -gap,
                                400 + (proximity * 100) + (horizontalOverlap * 35));
                        }
                    }

                    if (IsHorizontallyRelated(a, b, neighborRadius))
                    {
                        var topError = b.Top - a.Top;
                        var bottomError = b.Bottom - a.Bottom;
                        var selected = Math.Abs(topError) <= Math.Abs(bottomError)
                            ? topError
                            : bottomError;
                        if (Math.Abs(selected) <= alignmentRadius)
                        {
                            var proximity = Proximity(Math.Abs(selected), alignmentRadius);
                            yield return new PairConstraint(
                                first,
                                second,
                                -selected,
                                210 + (proximity * 70));
                        }
                    }
                }
            }
        }
    }

    private static bool IsVerticallyRelated(RectI a, RectI b, int radius) =>
        a.HorizontalOverlapRatio(b) >= MinimumOrthogonalOverlap &&
        RectI.IntervalGap(a.Top, a.Bottom, b.Top, b.Bottom) <= Math.Max(12, radius * 2);

    private static bool IsHorizontallyRelated(RectI a, RectI b, int radius) =>
        a.VerticalOverlapRatio(b) >= MinimumOrthogonalOverlap &&
        RectI.IntervalGap(a.Left, a.Right, b.Left, b.Right) <= Math.Max(12, radius * 2);

    private static bool TryGetScreenAnchor(
        State state,
        Axis axis,
        int radius,
        out ScreenAnchor anchor)
    {
        anchor = default;
        if (radius <= 0 || state.Snapshot.WorkArea.IsEmpty)
        {
            return false;
        }

        var rect = state.Baseline;
        var workArea = state.Snapshot.WorkArea;
        var startShift = axis == Axis.Horizontal
            ? workArea.Left - rect.Left
            : workArea.Top - rect.Top;
        var endShift = axis == Axis.Horizontal
            ? workArea.Right - rect.Right
            : workArea.Bottom - rect.Bottom;
        var startNear = Math.Abs(startShift) <= radius;
        var endNear = Math.Abs(endShift) <= radius;

        if (!startNear && !endNear)
        {
            return false;
        }

        if (startNear && endNear)
        {
            if (Math.Abs(startShift) <= 1)
            {
                anchor = NewScreenAnchor(startShift, radius);
                return true;
            }
            if (Math.Abs(endShift) <= 1)
            {
                anchor = NewScreenAnchor(endShift, radius);
                return true;
            }

            // Nearly symmetric margins are ambiguous. Do not arbitrarily drag a near-full-size
            // window to one side and, crucially, do not stretch it to satisfy both edges.
            var ambiguity = Math.Max(2.0, radius * 0.15);
            if (Math.Abs(Math.Abs(startShift) - Math.Abs(endShift)) <= ambiguity)
            {
                return false;
            }
        }

        var selected = !endNear || (startNear && Math.Abs(startShift) < Math.Abs(endShift))
            ? startShift
            : endShift;
        anchor = NewScreenAnchor(selected, radius);
        return true;
    }

    private static ScreenAnchor NewScreenAnchor(int shift, int radius)
    {
        var proximity = Proximity(Math.Abs(shift), radius);
        return new ScreenAnchor(
            IsValid: true,
            WindowShift: shift,
            RootTarget: shift,
            Score: 300 + (proximity * 150));
    }

    private static int ClampIndividualShift(
        State state,
        Axis axis,
        int desired,
        TidyOptions options,
        ComfortProfile profile)
    {
        var minimum = -profile.MovementBudget;
        var maximum = profile.MovementBudget;
        if (options.RescueOffscreenWindows)
        {
            GetWorkAreaTranslationRange(state, axis, out var workMinimum, out var workMaximum);
            minimum = Math.Max(minimum, workMinimum);
            maximum = Math.Min(maximum, workMaximum);
        }
        return minimum <= maximum
            ? Math.Clamp(desired, minimum, maximum)
            : 0;
    }

    private static void GetWorkAreaTranslationRange(
        State state,
        Axis axis,
        out int minimum,
        out int maximum)
    {
        var rect = state.Baseline;
        var workArea = state.Snapshot.WorkArea;
        if (workArea.IsEmpty)
        {
            minimum = int.MinValue / 4;
            maximum = int.MaxValue / 4;
            return;
        }

        if (axis == Axis.Horizontal)
        {
            minimum = workArea.Left - rect.Left;
            maximum = workArea.Right - rect.Right;
        }
        else
        {
            minimum = workArea.Top - rect.Top;
            maximum = workArea.Bottom - rect.Bottom;
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

    private static double Proximity(double distance, double radius) =>
        radius <= 0 ? 0 : Math.Clamp(1.0 - (distance / radius), 0, 1);

    private static double CenterX(RectI rect) => (rect.Left + rect.Right) / 2.0;
    private static double CenterY(RectI rect) => (rect.Top + rect.Bottom) / 2.0;

    private static bool HasMeaningfulChange(RectI original, RectI target) =>
        Math.Abs(original.Left - target.Left) >= 2 ||
        Math.Abs(original.Top - target.Top) >= 2 ||
        Math.Abs(original.Right - target.Right) >= 2 ||
        Math.Abs(original.Bottom - target.Bottom) >= 2;

    private sealed class State(VisibleWindow visible, RectI baseline)
    {
        internal WindowSnapshot Snapshot { get; } = visible.Window;
        internal RectI Baseline { get; } = baseline;
        internal double Importance { get; } =
            1.0 + (0.55 * Math.Clamp(visible.VisibleRatio, 0, 1)) +
            (visible.Window.IsForeground ? 3.0 : 0.0);
    }

    private sealed class PotentialUnionFind
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;
        // _offset[x] = value(x) - value(parent(x)).
        private readonly double[] _offset;

        internal PotentialUnionFind(int count)
        {
            _parent = Enumerable.Range(0, count).ToArray();
            _rank = new byte[count];
            _offset = new double[count];
        }

        internal int Root(int item)
        {
            Find(item);
            return _parent[item];
        }

        internal double Potential(int item)
        {
            Find(item);
            return _offset[item];
        }

        internal bool TryConstrain(int first, int second, double secondMinusFirst, double tolerance)
        {
            var firstRoot = Find(first);
            var firstPotential = _offset[first];
            var secondRoot = Find(second);
            var secondPotential = _offset[second];

            if (firstRoot == secondRoot)
            {
                var implied = secondPotential - firstPotential;
                return Math.Abs(implied - secondMinusFirst) <= tolerance;
            }

            // value(secondRoot) - value(firstRoot)
            var rootDelta = secondMinusFirst + firstPotential - secondPotential;
            if (_rank[firstRoot] < _rank[secondRoot])
            {
                _parent[firstRoot] = secondRoot;
                _offset[firstRoot] = -rootDelta;
            }
            else
            {
                _parent[secondRoot] = firstRoot;
                _offset[secondRoot] = rootDelta;
                if (_rank[firstRoot] == _rank[secondRoot])
                {
                    _rank[firstRoot]++;
                }
            }
            return true;
        }

        private int Find(int item)
        {
            if (_parent[item] == item)
            {
                return item;
            }

            var parent = _parent[item];
            var root = Find(parent);
            _offset[item] += _offset[parent];
            _parent[item] = root;
            return root;
        }
    }

    private enum Axis
    {
        Horizontal,
        Vertical,
    }

    private readonly record struct ComfortProfile(
        int NeighborRadius,
        int AlignmentRadius,
        int ScreenRadius,
        int MovementBudget);

    private readonly record struct PairConstraint(
        int First,
        int Second,
        double SecondMinusFirst,
        double Priority);

    private readonly record struct ScreenAnchor(
        bool IsValid,
        int WindowShift,
        double RootTarget,
        double Score);
}
