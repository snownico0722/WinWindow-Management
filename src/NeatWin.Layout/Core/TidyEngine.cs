namespace NeatWin.Core;

public enum TidyAlgorithmMode
{
    Smart,
    Classic,
}

public enum SmartTidyStrength
{
    Gentle,
    Balanced,
    Assertive,
}

// Legacy Smart knobs are intentionally retained in the settings model so older settings files and
// callers remain compatible. Smart no longer resolves behavior from these fields; one coherent
// SmartStrength profile controls relation reach, resize willingness, screen anchoring and movement.
public enum SmartHitTendency
{
    Cautious,
    Balanced,
    Sensitive,
}

public enum SmartSizeTendency
{
    Preserve,
    Balanced,
    Expand,
}

public sealed record TidyOptions(
    TidyAlgorithmMode AlgorithmMode = TidyAlgorithmMode.Smart,
    SmartTidyStrength SmartStrength = SmartTidyStrength.Balanced,
    SmartHitTendency SmartHitTendency = SmartHitTendency.Balanced,
    SmartSizeTendency SmartSizeTendency = SmartSizeTendency.Balanced,
    double PreserveLayoutWeight = 1.0,
    double ResizeResistanceWeight = 0.1,
    double OrderlinessWeight = 1.8,
    double SpaceUsageWeight = 1.0,
    int SmartIterations = 36,
    int NeighborSnapDistance = 72,
    int AlignmentSnapDistance = 24,
    int ScreenSnapDistance = 96,
    int MaximumEdgeAdjustment = 96,
    double MaximumSizeChangeRatio = 0.12,
    bool RescueOffscreenWindows = true,
    int MinimumWidth = 320,
    int MinimumHeight = 220,
    double MinimumNeighborOverlapRatio = 0.30,
    int Passes = 2);

public sealed class TidyEngine
{
    public IReadOnlyList<TidyMove> CreatePlan(
        IReadOnlyList<VisibleWindow> visibleWindows,
        TidyOptions? options = null)
    {
        options ??= new TidyOptions();

        return options.AlgorithmMode == TidyAlgorithmMode.Smart
            ? SmartTidySolver.CreatePlan(visibleWindows, ResolveSmartProfile(options))
            : CreateClassicPlan(visibleWindows, options);
    }

    private static TidyOptions ResolveSmartProfile(TidyOptions options)
    {
        // Smart is one coordinated temperament rather than several independent sliders. Reach is
        // kept modest so distant windows are not pulled toward edges/groups, while admitted snap
        // relations remain strong enough to finish cleanly instead of stopping a few pixels short.
        return options.SmartStrength switch
        {
            SmartTidyStrength.Gentle => options with
            {
                PreserveLayoutWeight = 1.45,
                ResizeResistanceWeight = 0.35,
                OrderlinessWeight = 1.35,
                SpaceUsageWeight = 0.90,
                SmartIterations = 28,
                NeighborSnapDistance = 56,
                AlignmentSnapDistance = 18,
                ScreenSnapDistance = 48,
                MaximumEdgeAdjustment = 64,
                MaximumSizeChangeRatio = 0.08,
            },
            SmartTidyStrength.Assertive => options with
            {
                PreserveLayoutWeight = 0.78,
                ResizeResistanceWeight = 0.06,
                OrderlinessWeight = 2.35,
                SpaceUsageWeight = 1.30,
                SmartIterations = 52,
                NeighborSnapDistance = 104,
                AlignmentSnapDistance = 32,
                ScreenSnapDistance = 96,
                MaximumEdgeAdjustment = 144,
                MaximumSizeChangeRatio = 0.20,
            },
            _ => options with
            {
                PreserveLayoutWeight = 1.00,
                ResizeResistanceWeight = 0.12,
                OrderlinessWeight = 1.85,
                SpaceUsageWeight = 1.25,
                SmartIterations = 40,
                NeighborSnapDistance = 76,
                AlignmentSnapDistance = 24,
                ScreenSnapDistance = 72,
                MaximumEdgeAdjustment = 104,
                MaximumSizeChangeRatio = 0.14,
            },
        };
    }

    private static IReadOnlyList<TidyMove> CreateClassicPlan(
        IReadOnlyList<VisibleWindow> visibleWindows,
        TidyOptions options)
    {
        var result = new List<TidyMove>();

        foreach (var monitorGroup in visibleWindows.GroupBy(static item => item.Window.MonitorHandle))
        {
            var working = monitorGroup
                .Select(static item => new WorkingWindow(item.Window))
                .ToList();

            if (working.Count == 0)
            {
                continue;
            }

            if (options.RescueOffscreenWindows)
            {
                RescueOffscreenWindows(working);
            }

            for (var pass = 0; pass < Math.Max(1, options.Passes); pass++)
            {
                SnapNearScreenEdges(working, options);
                CloseSmallNeighborGapsAndOverlaps(working, options);
                AlignNearlyMatchingEdges(working, options);
            }

            foreach (var item in working)
            {
                var target = ClampToBudget(item.Snapshot, item.Current, options);
                if (options.RescueOffscreenWindows)
                {
                    target = FitInsideWorkArea(item.Snapshot, target);
                }

                if (HasMeaningfulChange(item.Original, target))
                {
                    result.Add(new TidyMove(item.Snapshot, target));
                }
            }
        }

        return result;
    }

    private static void RescueOffscreenWindows(List<WorkingWindow> windows)
    {
        foreach (var window in windows)
        {
            window.SetRect(FitInsideWorkArea(window.Snapshot, window.Current));
        }
    }

    private static RectI FitInsideWorkArea(WindowSnapshot snapshot, RectI rect)
    {
        var workArea = snapshot.WorkArea;
        if (workArea.IsEmpty || rect.IsEmpty)
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

        return new RectI(left, top, width, height);
    }

    private static void SnapNearScreenEdges(List<WorkingWindow> windows, TidyOptions options)
    {
        foreach (var window in windows)
        {
            var workArea = window.Snapshot.WorkArea;

            if (Math.Abs(window.Left - workArea.Left) <= options.ScreenSnapDistance)
            {
                window.SetLeft(workArea.Left, window.Snapshot.IsResizable);
            }

            if (Math.Abs(window.Right - workArea.Right) <= options.ScreenSnapDistance)
            {
                window.SetRight(workArea.Right, window.Snapshot.IsResizable);
            }

            if (Math.Abs(window.Top - workArea.Top) <= options.ScreenSnapDistance)
            {
                window.SetTop(workArea.Top, window.Snapshot.IsResizable);
            }

            if (Math.Abs(window.Bottom - workArea.Bottom) <= options.ScreenSnapDistance)
            {
                window.SetBottom(workArea.Bottom, window.Snapshot.IsResizable);
            }
        }
    }

    private static void CloseSmallNeighborGapsAndOverlaps(List<WorkingWindow> windows, TidyOptions options)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            for (var j = i + 1; j < windows.Count; j++)
            {
                var a = windows[i];
                var b = windows[j];

                if (a.Current.VerticalOverlapRatio(b.Current) >= options.MinimumNeighborOverlapRatio)
                {
                    var left = a.Left <= b.Left ? a : b;
                    var right = ReferenceEquals(left, a) ? b : a;
                    var gap = right.Left - left.Right;
                    if (Math.Abs(gap) <= options.NeighborSnapDistance)
                    {
                        var boundary = (int)Math.Round((left.Right + right.Left) / 2.0);
                        left.SetRight(boundary, left.Snapshot.IsResizable);
                        right.SetLeft(boundary, right.Snapshot.IsResizable);
                    }
                }

                if (a.Current.HorizontalOverlapRatio(b.Current) >= options.MinimumNeighborOverlapRatio)
                {
                    var top = a.Top <= b.Top ? a : b;
                    var bottom = ReferenceEquals(top, a) ? b : a;
                    var gap = bottom.Top - top.Bottom;
                    if (Math.Abs(gap) <= options.NeighborSnapDistance)
                    {
                        var boundary = (int)Math.Round((top.Bottom + bottom.Top) / 2.0);
                        top.SetBottom(boundary, top.Snapshot.IsResizable);
                        bottom.SetTop(boundary, bottom.Snapshot.IsResizable);
                    }
                }
            }
        }
    }

    private static void AlignNearlyMatchingEdges(List<WorkingWindow> windows, TidyOptions options)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            for (var j = i + 1; j < windows.Count; j++)
            {
                var a = windows[i];
                var b = windows[j];

                if (AreVerticallyRelated(a.Current, b.Current, options))
                {
                    AlignXEdge(a, b, static window => window.Left, options.AlignmentSnapDistance);
                    AlignXEdge(a, b, static window => window.Right, options.AlignmentSnapDistance);
                }

                if (AreHorizontallyRelated(a.Current, b.Current, options))
                {
                    AlignYEdge(a, b, static window => window.Top, options.AlignmentSnapDistance);
                    AlignYEdge(a, b, static window => window.Bottom, options.AlignmentSnapDistance);
                }
            }
        }
    }

    private static bool AreVerticallyRelated(RectI a, RectI b, TidyOptions options) =>
        a.VerticalOverlapRatio(b) > 0 ||
        RectI.IntervalGap(a.Top, a.Bottom, b.Top, b.Bottom) <= options.NeighborSnapDistance * 2;

    private static bool AreHorizontallyRelated(RectI a, RectI b, TidyOptions options) =>
        a.HorizontalOverlapRatio(b) > 0 ||
        RectI.IntervalGap(a.Left, a.Right, b.Left, b.Right) <= options.NeighborSnapDistance * 2;

    private static void AlignXEdge(
        WorkingWindow a,
        WorkingWindow b,
        Func<WorkingWindow, int> edgeSelector,
        int threshold)
    {
        var edgeA = edgeSelector(a);
        var edgeB = edgeSelector(b);
        if (Math.Abs(edgeA - edgeB) > threshold)
        {
            return;
        }

        var target = (int)Math.Round((edgeA + edgeB) / 2.0);
        a.TranslateX(target - edgeA);
        b.TranslateX(target - edgeB);
    }

    private static void AlignYEdge(
        WorkingWindow a,
        WorkingWindow b,
        Func<WorkingWindow, int> edgeSelector,
        int threshold)
    {
        var edgeA = edgeSelector(a);
        var edgeB = edgeSelector(b);
        if (Math.Abs(edgeA - edgeB) > threshold)
        {
            return;
        }

        var target = (int)Math.Round((edgeA + edgeB) / 2.0);
        a.TranslateY(target - edgeA);
        b.TranslateY(target - edgeB);
    }

    private static RectI ClampToBudget(WindowSnapshot snapshot, RectI proposed, TidyOptions options)
    {
        var original = snapshot.VisualRect;
        var maxEdge = Math.Max(0, options.MaximumEdgeAdjustment);

        if (!snapshot.IsResizable)
        {
            var x = original.X + Math.Clamp(proposed.X - original.X, -maxEdge, maxEdge);
            var y = original.Y + Math.Clamp(proposed.Y - original.Y, -maxEdge, maxEdge);
            return new RectI(x, y, original.Width, original.Height);
        }

        var left = Math.Clamp(proposed.Left, original.Left - maxEdge, original.Left + maxEdge);
        var right = Math.Clamp(proposed.Right, original.Right - maxEdge, original.Right + maxEdge);
        var top = Math.Clamp(proposed.Top, original.Top - maxEdge, original.Top + maxEdge);
        var bottom = Math.Clamp(proposed.Bottom, original.Bottom - maxEdge, original.Bottom + maxEdge);

        var resizeRatio = Math.Clamp(options.MaximumSizeChangeRatio, 0, 1);
        var minWidthFloor = Math.Min(original.Width, options.MinimumWidth);
        var minHeightFloor = Math.Min(original.Height, options.MinimumHeight);
        var minWidth = Math.Max(minWidthFloor, (int)Math.Floor(original.Width * (1 - resizeRatio)));
        var maxWidth = Math.Max(minWidth, (int)Math.Ceiling(original.Width * (1 + resizeRatio)));
        var minHeight = Math.Max(minHeightFloor, (int)Math.Floor(original.Height * (1 - resizeRatio)));
        var maxHeight = Math.Max(minHeight, (int)Math.Ceiling(original.Height * (1 + resizeRatio)));

        var width = Math.Clamp(Math.Max(1, right - left), minWidth, maxWidth);
        var height = Math.Clamp(Math.Max(1, bottom - top), minHeight, maxHeight);
        var centerX = (left + right) / 2.0;
        var centerY = (top + bottom) / 2.0;

        left = (int)Math.Round(centerX - width / 2.0);
        top = (int)Math.Round(centerY - height / 2.0);

        var minLeft = Math.Max(original.Left - maxEdge, original.Right - maxEdge - width);
        var maxLeft = Math.Min(original.Left + maxEdge, original.Right + maxEdge - width);
        if (minLeft <= maxLeft)
        {
            left = Math.Clamp(left, minLeft, maxLeft);
        }
        else
        {
            left = original.Left;
        }

        var minTop = Math.Max(original.Top - maxEdge, original.Bottom - maxEdge - height);
        var maxTop = Math.Min(original.Top + maxEdge, original.Bottom + maxEdge - height);
        if (minTop <= maxTop)
        {
            top = Math.Clamp(top, minTop, maxTop);
        }
        else
        {
            top = original.Top;
        }

        return new RectI(left, top, width, height);
    }

    private static bool HasMeaningfulChange(RectI original, RectI target) =>
        Math.Abs(original.Left - target.Left) >= 2 ||
        Math.Abs(original.Top - target.Top) >= 2 ||
        Math.Abs(original.Right - target.Right) >= 2 ||
        Math.Abs(original.Bottom - target.Bottom) >= 2;

    private sealed class WorkingWindow(WindowSnapshot snapshot)
    {
        private int _left = snapshot.VisualRect.Left;
        private int _top = snapshot.VisualRect.Top;
        private int _right = snapshot.VisualRect.Right;
        private int _bottom = snapshot.VisualRect.Bottom;

        public WindowSnapshot Snapshot { get; } = snapshot;
        public RectI Original { get; } = snapshot.VisualRect;
        public int Left => _left;
        public int Top => _top;
        public int Right => _right;
        public int Bottom => _bottom;
        public RectI Current => RectI.FromEdges(_left, _top, _right, _bottom);

        public void SetRect(RectI rect)
        {
            _left = rect.Left;
            _top = rect.Top;
            _right = rect.Right;
            _bottom = rect.Bottom;
        }

        public void TranslateX(int delta)
        {
            _left += delta;
            _right += delta;
        }

        public void TranslateY(int delta)
        {
            _top += delta;
            _bottom += delta;
        }

        public void SetLeft(int target, bool resize)
        {
            if (resize)
            {
                _left = target;
            }
            else
            {
                TranslateX(target - _left);
            }
        }

        public void SetRight(int target, bool resize)
        {
            if (resize)
            {
                _right = target;
            }
            else
            {
                TranslateX(target - _right);
            }
        }

        public void SetTop(int target, bool resize)
        {
            if (resize)
            {
                _top = target;
            }
            else
            {
                TranslateY(target - _top);
            }
        }

        public void SetBottom(int target, bool resize)
        {
            if (resize)
            {
                _bottom = target;
            }
            else
            {
                TranslateY(target - _bottom);
            }
        }
    }
}
