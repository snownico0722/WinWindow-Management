namespace NeatWin.Core;

public enum VideoBlackBarOrientation
{
    Horizontal,
    Vertical,
}

public sealed record VideoBlackBarHint(
    nint WindowHandle,
    VideoBlackBarOrientation Orientation,
    double ContentAspectRatio,
    RectI ViewportVisualRect,
    double Confidence);

/// <summary>
/// Applies a high-confidence video-content aspect hint to one browser window without touching the
/// browser/player itself. Zero black-bar geometry outranks generic Smart screen usage for that
/// browser; the resize tendency only chooses between aspect-correct solutions.
/// </summary>
public static class VideoAspectPostProcessor
{
    private const double MinimumConfidence = 0.82;
    private const double MaximumSingleAdjustmentRatio = 0.45;

    public static IReadOnlyList<TidyMove> Refine(
        IReadOnlyList<VisibleWindow> visibleWindows,
        IReadOnlyList<TidyMove> basePlan,
        TidyOptions tidyOptions,
        SmartBehaviorOptions behaviorOptions,
        VideoBlackBarHint? hint)
    {
        if (tidyOptions.AlgorithmMode != TidyAlgorithmMode.Smart ||
            !behaviorOptions.RemoveVideoBlackBars ||
            hint is null ||
            hint.Confidence < MinimumConfidence ||
            hint.ContentAspectRatio is < 0.55 or > 4.5)
        {
            return basePlan;
        }

        var visible = visibleWindows.FirstOrDefault(item => item.Window.Handle == hint.WindowHandle);
        if (visible is null || !visible.Window.IsResizable || !visible.Window.IsManageable)
        {
            return basePlan;
        }

        var snapshot = visible.Window;
        var original = snapshot.VisualRect;
        if (original.IsEmpty || hint.ViewportVisualRect.IsEmpty)
        {
            return basePlan;
        }

        var targetByHandle = basePlan.ToDictionary(
            move => move.Window.Handle,
            move => move.TargetVisualRect);
        var planned = targetByHandle.TryGetValue(snapshot.Handle, out var genericSmartTarget)
            ? genericSmartTarget
            : original;

        // Important priority rule: video geometry is solved from the user's current browser size,
        // not from a generic Smart target that may already have expanded or vertically filled it.
        // This prevents screen-utilization preferences from inflating the browser before aspect
        // correction and makes "no black bars" the actual objective.
        var geometryBaseline = original;
        var chromeX = Math.Max(0, original.Width - hint.ViewportVisualRect.Width);
        var chromeY = Math.Max(0, original.Height - hint.ViewportVisualRect.Height);
        var viewportWidth = Math.Max(1, geometryBaseline.Width - chromeX);
        var viewportHeight = Math.Max(1, geometryBaseline.Height - chromeY);

        var target = TryBuildTarget(
            geometryBaseline,
            snapshot.WorkArea,
            viewportWidth,
            viewportHeight,
            chromeX,
            chromeY,
            hint,
            behaviorOptions.VideoBlackBarTendency,
            tidyOptions.MinimumWidth,
            tidyOptions.MinimumHeight);

        if (target is not null &&
            WouldMateriallyIncreaseOverlap(
                visibleWindows,
                targetByHandle,
                snapshot.Handle,
                planned,
                target.Value))
        {
            target = null;
        }

        // Expand is only a tie-break preference among aspect-correct solutions. It is never a
        // request to occupy more of the monitor. If exact expansion conflicts with the layout,
        // choose the exact shrink solution instead.
        if (target is null && behaviorOptions.VideoBlackBarTendency == VideoBlackBarTendency.Expand)
        {
            target = TryBuildTarget(
                geometryBaseline,
                snapshot.WorkArea,
                viewportWidth,
                viewportHeight,
                chromeX,
                chromeY,
                hint,
                VideoBlackBarTendency.Shrink,
                tidyOptions.MinimumWidth,
                tidyOptions.MinimumHeight);

            if (target is not null &&
                WouldMateriallyIncreaseOverlap(
                    visibleWindows,
                    targetByHandle,
                    snapshot.Handle,
                    planned,
                    target.Value))
            {
                target = null;
            }
        }

        if (target is null)
        {
            return basePlan;
        }

        // Even when the aspect-correct target is close to the current geometry, it still owns this
        // browser's final rectangle: generic Smart filling must not replace it afterward.
        if (!HasMeaningfulChange(original, target.Value) && !HasMeaningfulChange(planned, target.Value))
        {
            return basePlan;
        }

        var result = basePlan
            .Where(move => move.Window.Handle != snapshot.Handle)
            .ToList();
        result.Add(new TidyMove(snapshot, target.Value));
        return result;
    }

    private static RectI? TryBuildTarget(
        RectI current,
        RectI workArea,
        int viewportWidth,
        int viewportHeight,
        int chromeX,
        int chromeY,
        VideoBlackBarHint hint,
        VideoBlackBarTendency tendency,
        int minimumWidth,
        int minimumHeight)
    {
        var size = ComputeTargetSize(
            current,
            workArea,
            viewportWidth,
            viewportHeight,
            chromeX,
            chromeY,
            hint.ContentAspectRatio,
            hint.Orientation,
            tendency,
            minimumWidth,
            minimumHeight);
        if (size is null)
        {
            return null;
        }

        var (targetWidth, targetHeight) = size.Value;
        var widthDeltaRatio = Math.Abs(targetWidth - current.Width) / (double)Math.Max(1, current.Width);
        var heightDeltaRatio = Math.Abs(targetHeight - current.Height) / (double)Math.Max(1, current.Height);
        if (Math.Max(widthDeltaRatio, heightDeltaRatio) > MaximumSingleAdjustmentRatio)
        {
            return null;
        }

        return ResizeAroundCenterAndFit(current, targetWidth, targetHeight, workArea);
    }

    private static (int Width, int Height)? ComputeTargetSize(
        RectI current,
        RectI workArea,
        int viewportWidth,
        int viewportHeight,
        int chromeX,
        int chromeY,
        double contentAspect,
        VideoBlackBarOrientation orientation,
        VideoBlackBarTendency tendency,
        int minimumWidth,
        int minimumHeight)
    {
        double width = current.Width;
        double height = current.Height;

        if (orientation == VideoBlackBarOrientation.Horizontal)
        {
            if (tendency == VideoBlackBarTendency.Shrink)
            {
                height = (viewportWidth / contentAspect) + chromeY;
            }
            else
            {
                width = (viewportHeight * contentAspect) + chromeX;
                if (!workArea.IsEmpty && width > workArea.Width)
                {
                    width = workArea.Width;
                    var fittedViewportWidth = Math.Max(1, width - chromeX);
                    height = (fittedViewportWidth / contentAspect) + chromeY;
                }
            }
        }
        else
        {
            if (tendency == VideoBlackBarTendency.Shrink)
            {
                width = (viewportHeight * contentAspect) + chromeX;
            }
            else
            {
                height = (viewportWidth / contentAspect) + chromeY;
                if (!workArea.IsEmpty && height > workArea.Height)
                {
                    height = workArea.Height;
                    var fittedViewportHeight = Math.Max(1, height - chromeY);
                    width = (fittedViewportHeight * contentAspect) + chromeX;
                }
            }
        }

        var widthFloor = Math.Min(current.Width, minimumWidth);
        var heightFloor = Math.Min(current.Height, minimumHeight);
        var targetWidth = Math.Max(widthFloor, (int)Math.Round(width));
        var targetHeight = Math.Max(heightFloor, (int)Math.Round(height));

        if (!workArea.IsEmpty)
        {
            targetWidth = Math.Min(targetWidth, workArea.Width);
            targetHeight = Math.Min(targetHeight, workArea.Height);
        }

        var effectiveViewportWidth = Math.Max(1, targetWidth - chromeX);
        var effectiveViewportHeight = Math.Max(1, targetHeight - chromeY);
        var achievedAspect = effectiveViewportWidth / (double)effectiveViewportHeight;
        var aspectError = Math.Abs(achievedAspect - contentAspect) / contentAspect;
        return aspectError <= 0.035
            ? (targetWidth, targetHeight)
            : null;
    }

    private static bool WouldMateriallyIncreaseOverlap(
        IReadOnlyList<VisibleWindow> visibleWindows,
        IReadOnlyDictionary<nint, RectI> targetByHandle,
        nint hwnd,
        RectI before,
        RectI after)
    {
        long beforeOverlap = 0;
        long afterOverlap = 0;

        foreach (var visible in visibleWindows)
        {
            if (visible.Window.Handle == hwnd)
            {
                continue;
            }

            var other = targetByHandle.TryGetValue(visible.Window.Handle, out var planned)
                ? planned
                : visible.Window.VisualRect;
            beforeOverlap += before.Intersect(other).Area;
            afterOverlap += after.Intersect(other).Area;
        }

        var allowance = Math.Max(12_000L, (long)Math.Round(after.Area * 0.015));
        return afterOverlap > beforeOverlap + allowance;
    }

    private static RectI ResizeAroundCenterAndFit(RectI current, int width, int height, RectI workArea)
    {
        var centerX = (current.Left + current.Right) / 2.0;
        var centerY = (current.Top + current.Bottom) / 2.0;
        var left = (int)Math.Round(centerX - (width / 2.0));
        var top = (int)Math.Round(centerY - (height / 2.0));

        if (!workArea.IsEmpty)
        {
            if (width <= workArea.Width)
            {
                left = Math.Clamp(left, workArea.Left, workArea.Right - width);
            }
            if (height <= workArea.Height)
            {
                top = Math.Clamp(top, workArea.Top, workArea.Bottom - height);
            }
        }

        return new RectI(left, top, width, height);
    }

    private static bool HasMeaningfulChange(RectI a, RectI b) =>
        Math.Abs(a.Left - b.Left) >= 2 ||
        Math.Abs(a.Top - b.Top) >= 2 ||
        Math.Abs(a.Right - b.Right) >= 2 ||
        Math.Abs(a.Bottom - b.Bottom) >= 2;
}
