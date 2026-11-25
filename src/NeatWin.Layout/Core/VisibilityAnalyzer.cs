namespace NeatWin.Core;

public sealed record VisibilityOptions(
    double MinimumVisibleRatio = 0.12,
    long MinimumLargestVisibleFragmentArea = 40_000);

public sealed class VisibilityAnalyzer
{
    public IReadOnlyList<VisibleWindow> SelectVisibleWorkingSet(
        IReadOnlyList<WindowSnapshot> windows,
        VisibilityOptions? options = null, bool includeStackAccess = false) =>
        Select(windows, options ?? new(), includeStackAccess, includeCoveredWindows: false, rescueOffscreenWindows: false);

    public IReadOnlyList<VisibleWindow> SelectForManualArrange(IReadOnlyList<WindowSnapshot> windows,
        bool rescueOffscreenWindows) =>
        Select(windows, new(), includeStackAccess: false, includeCoveredWindows: true, rescueOffscreenWindows);

    private static IReadOnlyList<VisibleWindow> Select(IReadOnlyList<WindowSnapshot> windows, VisibilityOptions options,
        bool includeStackAccess, bool includeCoveredWindows, bool rescueOffscreenWindows)
    {
        var result = new List<VisibleWindow>();

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!window.IsManageable || window.WorkArea.IsEmpty || window.VisualRect.IsEmpty) continue;
            var baseRect = window.VisualRect.Intersect(window.WorkArea);
            if (baseRect.IsEmpty)
            {
                if (rescueOffscreenWindows) result.Add(new(window, 0, 0, 0));
                continue;
            }

            var fragments = new List<RectI> { baseRect };
            for (var frontIndex = 0; frontIndex < i && fragments.Count > 0; frontIndex++)
            {
                var front = windows[frontIndex];
                // 自遮挡免疫：归属于当前窗口自身的非托管下拉菜单、模态对话框或受抑局部表面，不削减宿主自身的可视曝光率。
                if (front.IsLocalTo(window))
                {
                    continue;
                }

                var occluder = front.VisualRect.Intersect(window.WorkArea);
                if (occluder.IsEmpty)
                {
                    continue;
                }

                fragments = RectRegion.Subtract(fragments, occluder);
            }

            var visibleArea = fragments.Sum(static fragment => fragment.Area);
            var largestFragment = fragments.Count == 0 ? 0 : fragments.Max(static fragment => fragment.Area);
            var visibleRatio = baseRect.Area == 0 ? 0 : (double)visibleArea / baseRect.Area;

            var scale = Math.Clamp(window.Dpi / 96.0, 0.5, 4);
            var topBand = new RectI(window.VisualRect.X, window.VisualRect.Y, window.VisualRect.Width,
                Math.Min(window.VisualRect.Height, (int)Math.Round(32 * scale)));
            var stackAccess = includeStackAccess && fragments.Select(f => f.Intersect(topBand)).Any(f =>
                f.Width >= Math.Min(window.VisualRect.Width * 0.5, 240 * scale) && f.Height >= 18 * scale);

            var isVisuallyPresent =
                visibleRatio >= options.MinimumVisibleRatio &&
                largestFragment >= options.MinimumLargestVisibleFragmentArea;

            // An explicit arrange can move the ordinary windows hiding this one. A fixed
            // foreground surface (for example a maximized app) must still shield its background.
            var available = visibleArea > 0;
            if (includeCoveredWindows && !available)
            {
                var uncoveredByFixed = new List<RectI> { baseRect };
                for (var j = 0; j < i && uncoveredByFixed.Count > 0; j++)
                    if (!windows[j].IsManageable && !windows[j].IsLocalTo(window))
                        uncoveredByFixed = RectRegion.Subtract(uncoveredByFixed, windows[j].VisualRect);
                available = uncoveredByFixed.Count > 0;
            }
            if (available && (includeCoveredWindows || window.IsForeground || isVisuallyPresent || stackAccess))
            {
                result.Add(new VisibleWindow(window, visibleArea, largestFragment, visibleRatio));
            }
        }

        return result;
    }
}
