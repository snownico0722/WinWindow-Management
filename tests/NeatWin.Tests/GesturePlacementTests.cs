using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class GesturePlacementTests
{
    private static readonly RectI Area = new(100, 0, 1920, 1040);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(137, 100)]
    [InlineData(1380, 1420)]
    public void FullSmartCompletesAHorizontalDropTowardEitherEdge(int x, int expectedX)
    {
        var window = Window(1, new(x, 150, 600, 450));
        var gesture = Drag(window, window.VisualRect with { X = 700 });
        var (_, plan) = Arrange([window], gesture);
        Assert.Equal(window.VisualRect with { X = expectedX }, Assert.Single(plan.Moves).TargetVisualRect);
    }

    [Fact]
    public void CenterDropGrowsOnceAndTheAssistanceExpires()
    {
        var w = Window(1, new(794, 300, 480, 440));
        var gesture = Drag(w, w.VisualRect with { X = 220 });
        var (session, plan) = Arrange([w], gesture);
        var target = Assert.Single(plan.Moves).TargetVisualRect;
        Assert.Equal(Area.Left + Area.Width / 2, target.Left + target.Width / 2);
        Assert.InRange(target.Width, w.VisualRect.Width + 1, (int)Math.Ceiling(w.VisualRect.Width * 1.12));
        Assert.InRange(target.Height, w.VisualRect.Height + 1, (int)Math.Ceiling(w.VisualRect.Height * 1.12));
        session.Requested([w], plan, Now.AddSeconds(.5));
        var settled = TaskLayoutSession.Project([w], plan);
        session.Verify(settled);
        var repeated = session.Capture(settled, new(), [], null, Now.AddSeconds(1));
        Assert.Equal(w.Handle, repeated.GestureAnchor);
        Assert.True(repeated.VerifiedRepeat);
        Assert.Empty(Solve(settled, repeated).Moves);
        var later = session.Capture(settled, new(), [], null, Now.AddSeconds(5));
        Assert.Null(later.RecentGesture);
        Assert.False(later.VerifiedRepeat);
        var another = new[] { Window(2, new(1030, 90, 890, 770)) with { IsForeground = true },
            settled[0] with { IsForeground = false, ZOrder = 1 } };
        Assert.Null(session.Capture(another, new(), [], null, Now.AddSeconds(1)).RecentGesture);
    }

    [Fact]
    public void ReleasedPairSharesTheAvailableSpaceWithoutLockingTheUserSize()
    {
        var main = Window(1, new(144, 120, 570, 470));
        var peer = Window(2, new(740, 125, 640, 520));
        var (_, plan) = Arrange([main, peer], Drag(main, main.VisualRect with { X = 420 }));
        var final = TaskLayoutSession.Project([main, peer], plan);
        Assert.Equal(Area.Left, final[0].VisualRect.Left);
        Assert.InRange(final[0].VisualRect.Width, 571, 639);
        Assert.True(final[1].VisualRect.Width > peer.VisualRect.Width);
    }

    [Fact]
    public void ResizingSnapsOnlyTheEditedAxisAndExpiresOnTheNextTask()
    {
        var window = Window(1, new(330, 190, 950, 517));
        var gesture = Drag(window, window.VisualRect with { Width = 810 }) with { Kind = ManualGestureKind.Resize };
        var (session, plan) = Arrange([window], gesture);
        var target = Assert.Single(plan.Moves).TargetVisualRect;
        Assert.Equal(window.VisualRect with { Width = Area.Width / 2 }, target);
        var another = Window(2, new(1300, 100, 410, 500)) with { IsForeground = true };
        var switched = new[] { another, window with { VisualRect = target, IsForeground = false, ZOrder = 1 } };
        var fresh = session.Capture(switched, new(), [], null, Now.AddSeconds(1));
        Assert.Equal(0, fresh.GestureAnchor);
        Assert.Null(fresh.RecentGesture);
        Assert.Null(session.Capture([window], new(), [], null, Now.AddSeconds(2)).RecentGesture);
    }

    [Fact]
    public void ADeliberateSmallCorrectionAndAHighSpeedPassKeepTheReleaseFrame()
    {
        var w = Window(1, new(111, 100, 700, 450));
        var fine = Drag(w, w.VisualRect with { X = 104 });
        Assert.Empty(Arrange([w], fine).Plan.Moves);
        var pass = Drag(w, w.VisualRect with { X = 854 }) with { EndSpeedPixelsPerSecond = 6000 };
        Assert.Empty(Arrange([w], pass).Plan.Moves);
    }

    private static WindowSnapshot Window(int id, RectI rect) =>
        new(id, rect, rect, Area, 1, default, true, id == 1, true, id - 1, 96, (uint)id);

    private static ManualWindowGesture Drag(WindowSnapshot window, RectI from) =>
        new(window.Handle, from, window.VisualRect, Area, ManualGestureKind.Move, [], 500, 60);

    private static (TaskLayoutSession Session, IntentLayoutPlan Plan) Arrange(WindowSnapshot[] windows, ManualWindowGesture gesture)
    {
        var session = new TaskLayoutSession();
        session.Gesture(gesture, Now);
        var capture = session.Capture(windows, new(), [], null, Now.AddSeconds(.45));
        return (session, Solve(windows, capture));
    }

    private static IntentLayoutPlan Solve(WindowSnapshot[] windows, TaskLayoutContext capture)
    {
        var context = capture with { UseWorkingPairs = true, Focus = new(1, windows.Length > 1 ? 2 : 0, 3),
            Attention = new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, 1, 0, true) } };
        return LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(windows, true),
            new(), null, windows, context);
    }
}
