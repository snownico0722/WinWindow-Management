using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class VideoAspectPostProcessorTests
{
    private static readonly RectI WorkArea = new(0, 0, 1920, 1080);

    [Fact]
    public void VideoBlackBars_ShrinkTendency_ReducesBarDimensionOnly()
    {
        var browser = Window(1, new RectI(100, 100, 1200, 800), 0);
        var hint = new VideoBlackBarHint(browser.Handle, VideoBlackBarOrientation.Horizontal,
            2.0, new RectI(150, 190, 1100, 620), 0.95);
        var plan = VideoAspectPostProcessor.Refine([Visible(browser)], [], new TidyOptions(),
            new SmartBehaviorOptions(RemoveVideoBlackBars: true,
                VideoBlackBarTendency: VideoBlackBarTendency.Shrink), hint);
        var target = TargetFor(plan, browser);
        Assert.Equal(browser.VisualRect.Width, target.Width);
        Assert.Equal(730, target.Height);
    }

    [Fact]
    public void VideoBlackBars_ExpandTendency_GrowsOnlyEnoughToReachAspect()
    {
        var browser = Window(1, new RectI(100, 100, 1200, 800), 0);
        var hint = new VideoBlackBarHint(browser.Handle, VideoBlackBarOrientation.Horizontal,
            2.0, new RectI(150, 190, 1100, 620), 0.95);
        var plan = VideoAspectPostProcessor.Refine([Visible(browser)], [], new TidyOptions(),
            new SmartBehaviorOptions(RemoveVideoBlackBars: true,
                VideoBlackBarTendency: VideoBlackBarTendency.Expand), hint);
        var target = TargetFor(plan, browser);
        Assert.Equal(1340, target.Width);
        Assert.Equal(browser.VisualRect.Height, target.Height);
    }

    [Fact]
    public void VideoBlackBars_OverridesGenericFullHeightPlanInsteadOfExpandingFromIt()
    {
        var browser = Window(1, new RectI(100, 100, 1200, 800), 0);
        var genericFullHeightPlan = new[] { new TidyMove(browser, new RectI(100, 0, 1200, 1080)) };
        var hint = new VideoBlackBarHint(browser.Handle, VideoBlackBarOrientation.Horizontal,
            2.0, new RectI(150, 190, 1100, 620), 0.95);
        var plan = VideoAspectPostProcessor.Refine([Visible(browser)], genericFullHeightPlan,
            new TidyOptions(), new SmartBehaviorOptions(RemoveVideoBlackBars: true,
                VideoBlackBarTendency: VideoBlackBarTendency.Expand), hint);
        var target = TargetFor(plan, browser);
        Assert.Equal(1340, target.Width);
        Assert.Equal(800, target.Height);
        Assert.NotEqual(WorkArea.Height, target.Height);
    }

    [Fact]
    public void VideoBlackBars_ExpandTendency_FallsBackToShrinkWhenGrowthWouldOverlapNeighbor()
    {
        var browser = Window(1, new RectI(100, 100, 1200, 800), 0);
        var neighbor = Window(2, new RectI(1320, 100, 500, 800), 1);
        var hint = new VideoBlackBarHint(browser.Handle, VideoBlackBarOrientation.Horizontal,
            2.0, new RectI(150, 190, 1100, 620), 0.95);
        var plan = VideoAspectPostProcessor.Refine([Visible(browser), Visible(neighbor)], [],
            new TidyOptions(), new SmartBehaviorOptions(RemoveVideoBlackBars: true,
                VideoBlackBarTendency: VideoBlackBarTendency.Expand), hint);
        var target = TargetFor(plan, browser);
        Assert.Equal(browser.VisualRect.Width, target.Width);
        Assert.Equal(730, target.Height);
        Assert.Equal(0, OverlapArea(target, neighbor.VisualRect));
    }

    private static long OverlapArea(RectI a, RectI b) => a.Intersect(b).Area;

    private static WindowSnapshot Window(int id, RectI rect, int zOrder) =>
        new((nint)id, rect, rect, WorkArea, (nint)1, new FrameInsets(0, 0, 0, 0),
            IsResizable: true, IsForeground: zOrder == 0, IsManageable: true, ZOrder: zOrder);

    private static VisibleWindow Visible(WindowSnapshot window) =>
        new(window, window.VisualRect.Area, window.VisualRect.Area, 1.0);

    private static RectI TargetFor(IReadOnlyList<TidyMove> plan, WindowSnapshot window) =>
        plan.FirstOrDefault(move => move.Window.Handle == window.Handle)?.TargetVisualRect ?? window.VisualRect;
}
