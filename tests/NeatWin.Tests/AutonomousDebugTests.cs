using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class AutonomousDebugTests
{
    private static readonly RectI Area = new(0, 0, 2560, 1380);
    private static WindowSnapshot W(RectI rect) => new((nint)1, rect, rect, Area,
        (nint)1, default, true, true, true, 0, 120, 10);
    private static VisibleWindow V(WindowSnapshot w) => new(w, w.VisualRect.Area, w.VisualRect.Area, 1);

    [Theory]
    [InlineData(-3)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    public void VerifiedRoundingAnchorsTheObservedResultNotTheUnattainedRequest(int delta)
    {
        var before = W(new(100, 100, 800, 700));
        var target = before.VisualRect with { X = 200 };
        var plan = new IntentLayoutPlan([new(before, target)], [], [new([before.Handle], "local", 0, [])]);
        var actual = before with { VisualRect = target with { X = target.X + delta } };
        var now = DateTimeOffset.UtcNow;
        var session = new TaskLayoutSession();
        session.Requested([before], plan, now);
        session.Verify([actual]);
        Assert.True(session.Capture([actual], new(), [], null, now).VerifiedRepeat);
        // Do not let the tolerance authorize fresh movement after verification.
        Assert.False(session.Capture([actual with { VisualRect = actual.VisualRect with { X = actual.VisualRect.X + 1 } }],
            new(), [], null, now).VerifiedRepeat);
    }

    [Fact]
    public void ChangingResizeCapabilityInvalidatesTheVerifiedLayout()
    {
        var before = W(new(100, 100, 800, 700));
        var target = before.VisualRect with { X = 200 };
        var now = DateTimeOffset.UtcNow;
        var session = new TaskLayoutSession();
        session.Requested([before], new([new(before, target)], [], [new([before.Handle], "local", 0, [])]), now);
        var actual = before with { VisualRect = target };
        session.Verify([actual]);
        Assert.True(session.Capture([actual], new(), [], null, now).VerifiedRepeat);
        Assert.False(session.Capture([actual with { IsResizable = false }], new(), [], null, now).VerifiedRepeat);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OversizedNonResizingWindowCanRecoverItsVisibleEntrance(bool resizable, bool allowResize)
    {
        var w = W(new(-2800, -140, 3200, 1600)) with { IsResizable = resizable };
        var context = new TaskLayoutContext(Profile: new(AllowUsefulResize: allowResize));
        var plan = IntentLayoutPlanner.CreateDetailedPlan([V(w)], new(), desktop: [w], taskContext: context);
        var move = Assert.Single(plan.Moves);
        Assert.Equal(new RectI(0, 0, 3200, 1600), move.TargetVisualRect);
        Assert.True(move.TargetVisualRect.Intersect(Area).Area > w.VisualRect.Intersect(Area).Area);
    }
}
