namespace NeatWin.Core;

public sealed record VisibilityOptions(
    double MinimumVisibleRatio = 0.12,
    long MinimumLargestVisibleFragmentArea = 40_000);

public sealed class VisibilityAnalyzer
{
    public IReadOnlyList<VisibleWindow> SelectVisibleWorkingSet(
        IReadOnlyList<WindowSnapshot> windows,
        VisibilityOptions? options = null, bool includeStackAccess = false)
    {
        options ??= new VisibilityOptions();
        var result = new List<VisibleWindow>();

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var baseRect = window.VisualRect.Intersect(window.WorkArea);
            if (baseRect.IsEmpty)
            {
                continue;
            }

            var fragments = new List<RectI> { baseRect };
            for (var frontIndex = 0; frontIndex < i && fragments.Count > 0; frontIndex++)
            {
                var occluder = windows[frontIndex].VisualRect.Intersect(window.WorkArea);
                if (occluder.IsEmpty)
                {
                    continue;
                }

                fragments = RectRegion.Subtract(fragments, occluder);
            }

            var visibleArea = fragments.Sum(static fragment => fragment.Area);
            var largestFragment = fragments.Count == 0 ? 0 : fragments.Max(static fragment => fragment.Area);
            var visibleRatio = baseRect.Area == 0 ? 0 : (double)visibleArea / baseRect.Area;

            if (!window.IsManageable || visibleArea == 0)
            {
                continue;
            }

            var scale = Math.Clamp(window.Dpi / 96.0, 0.5, 4);
            var topBand = new RectI(window.VisualRect.X, window.VisualRect.Y, window.VisualRect.Width,
                Math.Min(window.VisualRect.Height, (int)Math.Round(32 * scale)));
            var stackAccess = includeStackAccess && fragments.Select(f => f.Intersect(topBand)).Any(f =>
                f.Width >= Math.Min(window.VisualRect.Width * 0.5, 240 * scale) && f.Height >= 18 * scale);

            var isVisuallyPresent =
                visibleRatio >= options.MinimumVisibleRatio &&
                largestFragment >= options.MinimumLargestVisibleFragmentArea;

            if (window.IsForeground || isVisuallyPresent || stackAccess)
            {
                result.Add(new VisibleWindow(window, visibleArea, largestFragment, visibleRatio));
            }
        }

        return result;
    }
}
