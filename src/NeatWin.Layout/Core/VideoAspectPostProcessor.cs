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
    double Confidence, VideoBlackBarTendency Tendency = VideoBlackBarTendency.Shrink);

/// <summary>Compare both aspect-correct browser sizes on the projected desktop.</summary>
public static class VideoAspectPostProcessor
{
    private const double MaximumSingleAdjustmentRatio = 0.45;

    public static IReadOnlyList<TidyMove> Refine(IReadOnlyList<VisibleWindow> visibleWindows,
        IReadOnlyList<TidyMove> basePlan, TidyOptions tidyOptions, SmartBehaviorOptions behaviorOptions,
        VideoBlackBarHint? hint)
    {
        if (!behaviorOptions.RemoveVideoBlackBars || hint == null) return basePlan;
        var plan = new IntentLayoutPlan(basePlan, [], []);
        var desktop = visibleWindows.Select(v => v.Window).ToArray();
        return Select(visibleWindows, desktop, plan, tidyOptions,
            new(VideoHint: hint with { Tendency = behaviorOptions.VideoBlackBarTendency })).Moves;
    }

    internal static IntentLayoutPlan Select(IReadOnlyList<VisibleWindow> visible, IReadOnlyList<WindowSnapshot> desktop,
        IntentLayoutPlan plan, TidyOptions options, TaskLayoutContext context)
    {
        var hint = context.VideoHint;
        if (options.AlgorithmMode != TidyAlgorithmMode.Smart ||
            hint is not { Confidence: >= .82, ContentAspectRatio: >= .55 and <= 4.5 } ||
            context.GestureAnchor != 0 ||
            context.Focus?.OpeningResidentLeft != null || context.Focus?.Navigation is { Length: > 0 })
            return plan;
        var browser = visible.FirstOrDefault(v => v.Window.Handle == hint.WindowHandle)?.Window;
        if (browser is not { IsManageable: true, IsResizable: true, IsTopmost: false } || browser.SurfaceState == "fullscreen" ||
            context.WindowHints?.Any(h => h.Handle == browser.Handle && h.Anchor) == true || context.HoldsTextView(browser)) return plan;
        if (context.IsWorkspaceObservation && !desktop.Any(w => w.IsForeground && w.MonitorHandle == browser.MonitorHandle && w.IsManageable))
            return plan;

        var scene = context.Scene?.Monitor == browser.MonitorHandle ? context.Scene : null;
        if (!(scene?.Settings?.AllowResize ?? context.Preferences.AllowUsefulResize)) return plan;
        var frame = browser.VisualRect;
        var view = hint.ViewportVisualRect;
        if (view.IsEmpty || view.Intersect(frame) != view) return plan;
        var projected = TaskLayoutSession.Project(desktop, plan);
        // 检测到黑边能否决一个会盖住视频的摆法，但不能给其他窗的移动凭空增加收益。
        if (!IntentLayoutPlanner.DesktopRegionsPreserved(desktop, projected, context))
        {
            plan = new([], [], plan.Groups.Select(g => g with { Selected = "keep" }).ToArray());
            projected = desktop.ToArray();
        }
        var placed = projected.First(w => w.Handle == browser.Handle);
        var start = placed.VisualRect;
        var area = browser.WorkArea;
        var front = projected.Where(w => w.ZOrder < placed.ZOrder).Select(w => w.VisualRect).ToArray();
        var peers = visible.Where(v => v.VisibleArea > 0 && v.Window.Handle != browser.Handle)
            .Select(v => v.Window.Handle).ToHashSet();
        var neighbors = projected.Where(w => peers.Contains(w.Handle)).Select(w => w.VisualRect).ToArray();
        var group = plan.Groups.FirstOrDefault(g => g.Handles.Contains(browser.Handle));
        var floor = context.MinimumSize(browser.Handle);
        int chromeX = frame.Width - view.Width, chromeY = frame.Height - view.Height;
        double PicturePixels(RectI rect, IEnumerable<RectI> covers)
        {
            int vw = Math.Max(1, rect.Width - chromeX), vh = Math.Max(1, rect.Height - chromeY);
            int pw = Math.Max(1, (int)Math.Round(Math.Min(vw, vh * hint.ContentAspectRatio)));
            int ph = Math.Max(1, (int)Math.Round(Math.Min(vh, vw / hint.ContentAspectRatio)));
            var picture = new RectI(rect.X + view.X - frame.X + (vw - pw) / 2, rect.Y + view.Y - frame.Y + (vh - ph) / 2, pw, ph);
            return IntentLayoutPlanner.ScreenExposure(picture, covers, area, browser.Dpi).Visible * picture.Area;
        }

        double originalPixels = PicturePixels(frame, desktop.Where(w => w.ZOrder < browser.ZOrder).Select(w => w.VisualRect));
        double plannedPixels = PicturePixels(start, front);
        double referencePixels = Math.Max(originalPixels, plannedPixels);
        double AspectError(RectI rect) => Math.Abs(Math.Log(Math.Max(1, rect.Width - chromeX) /
            (double)Math.Max(1, rect.Height - chromeY) / hint.ContentAspectRatio));
        double baselineError = AspectError(start);
        var best = start;
        var visibleBrowser = visible.First(v => v.Window.Handle == browser.Handle);
        var browserHint = context.WindowHints?.FirstOrDefault(h => h.Handle == browser.Handle);
        // 视频拟合与一般布局共用同一感知成本。以本轮原始画面为基准，避免先移动、后拟合时漏算或重复收费。
        double Interruption(RectI box) => IntentLayoutPlanner.AttentionInterruption(visibleBrowser, box, context, browserHint)
            * .20 * (scene?.Settings?.Weights?.Interruption ?? 1);
        double bestCost = baselineError + Interruption(start);
        var audits = new List<LayoutCandidateTrace> { new("video-current", bestCost, null, null) };
        var videoAxes = context.TraceCandidateAxes ? new LayoutAxisEvidence() : null;
        videoAxes?.Observe([start], true);
        string selected = "video-current";
        foreach (var tendency in Enum.GetValues<VideoBlackBarTendency>())
        {
            string kind = tendency == VideoBlackBarTendency.Shrink ? "video-aspect-shrink" : "video-aspect-expand";
            // 从实测视口计算宽高，沿用普通布局已经确定的位置。上下黑边只收高度时，左右边界自然保持原位。
            var target = TryBuildTarget(frame, start, area, view.Width, view.Height, chromeX, chromeY, hint, tendency,
                Math.Max(floor.Width, (int)Math.Round(options.MinimumWidth * browser.Dpi / 96d)),
                Math.Max(floor.Height, (int)Math.Round(options.MinimumHeight * browser.Dpi / 96d)));
            if (target is not { } rect) continue;
            if (!HasMeaningfulChange(start, rect) || AspectError(rect) >= baselineError) continue;
            // 减少黑边可以缩小外框，但画面要保住。逐窗比较新增遮挡，避免一边腾出的面积抵消另一边刚被挡住的内容。
            double pixels = PicturePixels(rect, front);
            string? rejection = context.RepeatsReversal(browser, rect) ? "repeated-manual-reversal" :
                    context.LimitReturn(browser, rect) != rect ? "manual-return-amplitude" :
                scene?.Lane?.Allows(browser, rect) == false ? "attention-side-transfer" :
                !context.PreservesReleasedSide(browser, rect) ? "recent-manual-side" :
                neighbors.Any(r => rect.Intersect(r).Area > start.Intersect(r).Area) ? "increased-overlap" :
                pixels + Math.Max(view.Width, view.Height) < referencePixels ? "lost-video-picture" : null;
            var moves = plan.Moves.Where(m => m.Window.Handle != browser.Handle).ToList();
            if (rect != frame) moves.Add(new(browser, rect));
            var candidate = plan with { Moves = moves };
            var after = TaskLayoutSession.Project(desktop, candidate);
            if (rejection == null && scene?.References.FirstOrDefault(g =>
                g.Measure(after, w => w.VisualRect, w => w.ZOrder).Lost > g.Limit + 1e-9) is { } protectedView)
                rejection = "visible-reference:" + protectedView.Rule;
            if (rejection == null && (!IntentLayoutPlanner.TextViewAcceptsPlan(desktop, candidate, context) ||
                !IntentLayoutPlanner.DesktopRegionsPreserved(projected, after, context, desktop) ||
                context.RejectedLayouts?.Contains(TaskLayoutSession.LayoutKey(after.Where(w =>
                    group?.Handles.Contains(w.Handle) ?? w.Handle == browser.Handle))) == true))
                rejection = "protected-layout";
            // 去边与保留原状比较净收益。这里的单位是对数宽高比，感知项按相应尺度换算。
            double change = Math.Abs(rect.Width - start.Width) / (double)area.Width + Math.Abs(rect.Height - start.Height) / (double)area.Height;
            double cost = AspectError(rect) + Interruption(rect) + .04 * change - .015 * Math.Clamp((pixels - plannedPixels) / Math.Max(1, plannedPixels), 0, 1) +
                (tendency == hint.Tendency ? 0 : .01);
            context.ShadowProbe?.Observe([visibleBrowser], [rect], rejection == null ? 0 : double.PositiveInfinity);
            videoAxes?.Observe([rect], rejection == null);
            audits.Add(new(kind, cost, rejection, null));
            if (rejection == null && cost < bestCost) { best = rect; bestCost = cost; selected = kind; }
        }
        var output = plan.Moves.Where(m => m.Window.Handle != browser.Handle).ToList();
        if (best != frame) output.Add(new(browser, best));
        // 每扇窗只归一个组，避免用户撤回一次视频调整，却给学习器留下两份反悔样本。
        var groups = group == null ? plan.Groups.Append(new([browser.Handle], selected, 0, audits.ToArray(), AxisCoverage: videoAxes?.Export())) :
            plan.Groups.Select(g => g != group ? g : g with { Selected = best == start ? g.Selected : selected,
                Candidates = [.. g.Candidates, .. audits], AxisCoverage = videoAxes == null ? g.AxisCoverage :
                    [.. g.AxisCoverage ?? [], .. videoAxes.Export().Select(a => a with { WindowIndex = Array.IndexOf(g.Handles, browser.Handle) })] });
        return plan with { Moves = best == start ? plan.Moves : output, Groups = groups.ToArray() };
    }

    private static RectI? TryBuildTarget(
        RectI current,
        RectI planned,
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

        return ResizeInPlace(planned, targetWidth, targetHeight, workArea);
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

    private static RectI ResizeInPlace(RectI current, int width, int height, RectI workArea)
    {
        // 一栏贴着工作区外沿时向内收边。两边都贴住或都没贴住才保留中心，避免消除左右黑边后浏览器整窗向中间漂。
        static int Origin(int start, int size, int nextSize, int low, int high)
        {
            if (size == nextSize) return start;
            bool nearStart = Math.Abs(start - low) <= 2, nearEnd = Math.Abs(start + size - high) <= 2;
            int centered = start + (int)Math.Round((size - nextSize) / 2d);
            if (high - low < nextSize) return centered;
            int anchored = nearStart == nearEnd ? centered : nearStart ? start : start + size - nextSize;
            return Math.Clamp(anchored, low, high - nextSize);
        }
        return new(Origin(current.X, current.Width, width, workArea.Left, workArea.Right),
            Origin(current.Y, current.Height, height, workArea.Top, workArea.Bottom), width, height);
    }

    private static bool HasMeaningfulChange(RectI a, RectI b) =>
        Math.Abs(a.Left - b.Left) >= 2 ||
        Math.Abs(a.Top - b.Top) >= 2 ||
        Math.Abs(a.Right - b.Right) >= 2 ||
        Math.Abs(a.Bottom - b.Bottom) >= 2;
}
