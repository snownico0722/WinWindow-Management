using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class GesturePlacementTests
{
    private static readonly RectI Area = new(100, 0, 1920, 1040);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(137, 100)]
    [InlineData(1380, 1420)]
    [InlineData(240, 100)]
    [InlineData(1280, 1420)]
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
        Assert.InRange(target.Width, (int)(Area.Width * .50), (int)(Area.Width * .72));
        Assert.InRange(target.Height, (int)(Area.Height * .60), (int)Math.Ceiling(Area.Height * .74));
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
        Assert.InRange(final[0].VisualRect.Width, main.VisualRect.Width + 1, Area.Width / 2);
        Assert.True(final[1].VisualRect.Width > peer.VisualRect.Width);
    }

    [Fact]
    public void ResizingKeepsTheReleasedSizeAndFinishesTheApproachedScreenEdgeOnce()
    {
        var window = Window(1, new(1058, 190, 950, 517));
        var gesture = Drag(window, window.VisualRect with { Width = 810 }) with { Kind = ManualGestureKind.Resize };
        var (session, plan) = Arrange([window], gesture);
        var target = Assert.Single(plan.Moves).TargetVisualRect;
        Assert.Equal(window.VisualRect with { X = Area.Right - window.VisualRect.Width }, target);
        Assert.Equal(target, Light([window], gesture).TargetRect);
        session.Requested([window], plan, Now.AddSeconds(.5));
        var settled = TaskLayoutSession.Project([window], plan);
        session.Verify(settled);
        Assert.Empty(Solve(settled, session.Capture(settled, new(), [], null, Now.AddSeconds(.9))).Moves);
        var another = Window(2, new(1300, 100, 410, 500)) with { IsForeground = true };
        var switched = new[] { another, window with { VisualRect = target, IsForeground = false, ZOrder = 1 } };
        var fresh = session.Capture(switched, new(), [], null, Now.AddSeconds(1));
        Assert.Equal(0, fresh.GestureAnchor);
        Assert.Null(fresh.RecentGesture);
        Assert.Null(session.Capture([window], new(), [], null, Now.AddSeconds(2)).RecentGesture);
    }

    [Fact]
    public void ResizingTowardACompanionClosesTheGapWithOneTranslation()
    {
        var main = Window(1, new(200, 140, 705, 470));
        var peer = Window(2, new(925, 155, 590, 590)) with { IsResizable = false };
        var gesture = Drag(main, main.VisualRect with { Width = 604 }) with { Kind = ManualGestureKind.Resize };
        var projected = TaskLayoutSession.Project([main, peer], Arrange([main, peer], gesture).Plan);
        var desiredX = peer.VisualRect.Left - (int)SmartPersonalizationState.Default.PreferredGapPixels - main.VisualRect.Width;
        Assert.Equal(main.VisualRect with { X = desiredX }, projected[0].VisualRect);
        var light = Light([main, peer], gesture);
        Assert.Equal(FollowHandTargetKind.NeighborGap, light.TargetKind);
        Assert.Equal(projected[0].VisualRect, light.TargetRect);

        var reference = new IntentReferenceDocument(1, Enumerable.Range(1, 32)
            .Select(i => new IntentReferenceSample(Now.AddMinutes(-6 * i), IntentEvidence.ContextFor(Area), 32, true)).ToArray());
        var expected = IntentEvidence.Resolve(reference, Area, main.Dpi, Now).GapPixels;
        Assert.True(expected > SmartPersonalizationState.Default.PreferredGapPixels);
        var context = new TaskLayoutContext(Focus: new(main.Handle, peer.Handle, 3),
            GestureAnchor: main.Handle, RecentGesture: gesture);
        var visible = new VisibilityAnalyzer().SelectVisibleWorkingSet([main, peer]);
        var withHabit = GesturePlacement.Resolve(context, visible, new(), reference);
        Assert.Equal(expected, peer.VisualRect.Left - withHabit.GestureTarget!.Value.Right);
        var noPeer = GesturePlacement.Resolve(context with { Focus = new(main.Handle, 0, 0) }, visible, new(), reference);
        Assert.Null(noPeer.GestureTarget);
    }

    [Fact]
    public void ResizingTowardTwoScreenEdgesCompletesTheCornerInOneTranslation()
    {
        var main = Window(1, new(1215, 470, 790, 558));
        var gesture = Drag(main, main.VisualRect with { Width = 680, Height = 460 }) with { Kind = ManualGestureKind.Resize };
        var corner = main.VisualRect with { X = Area.Right - main.VisualRect.Width, Y = Area.Bottom - main.VisualRect.Height };
        Assert.Equal(corner, Light([main], gesture).TargetRect);
        Assert.Equal(corner, Assert.Single(Arrange([main], gesture).Plan.Moves).TargetVisualRect);
    }

    [Fact]
    public void ResizingOneSideOfACenteredWindowCanPreserveItsCenter()
    {
        var main = Window(1, new(800, 160, 554, 450));
        var gesture = Drag(main, main.VisualRect with { Width = 520 }) with { Kind = ManualGestureKind.Resize };
        var target = Assert.Single(Arrange([main], gesture).Plan.Moves).TargetVisualRect;
        Assert.Equal(main.VisualRect with { X = 783 }, target);
        Assert.Equal(target, Light([main], gesture).TargetRect);
    }

    [Theory]
    [InlineData(330, 810, true)]   // A popular width ratio alone has no positional meaning.
    [InlineData(1058, 946, true)]  // A fine tune near an edge belongs to the user.
    [InlineData(1058, 810, false)] // Shape-constrained surfaces do not take part in resize assistance.
    public void ResizingWithoutAPositionalIntentKeepsTheExactRelease(int x, int startWidth, bool resizable)
    {
        var main = Window(1, new(x, 190, 950, 517)) with { IsResizable = resizable };
        var gesture = Drag(main, main.VisualRect with { Width = startWidth }) with { Kind = ManualGestureKind.Resize };
        Assert.Empty(Arrange([main], gesture).Plan.Moves);
        Assert.False(Light([main], gesture).ShouldApply);
    }

    private static FollowHandDecision Light(WindowSnapshot[] windows, ManualWindowGesture gesture) =>
        FollowHandAssistant.Decide(gesture, new VisibilityAnalyzer().SelectVisibleWorkingSet(windows),
            new(), SmartInteractionContext.Empty, SmartPersonalizationState.Default);

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
