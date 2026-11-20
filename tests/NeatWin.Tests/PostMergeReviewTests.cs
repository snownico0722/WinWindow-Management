using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class PostMergeReviewTests
{
    private static readonly RectI Area = new(0, 0, 2560, 1380);
    private static WindowSnapshot W(int id, RectI rect) => new((nint)id, rect, rect, Area,
        (nint)1, default, true, id == 1, true, id, 120, (uint)id);
    private static VisibleWindow V(WindowSnapshot w) => new(w, w.VisualRect.Area, w.VisualRect.Area, 1);

    [Fact]
    public void PhysicalViewingModelMustNotEraseVerticalSwitchingDistance()
    {
        WindowSnapshot[] windows = [W(1, new(500, 50, 600, 450)), W(2, new(500, 700, 600, 450))];
        var context = new TaskLayoutContext(Calibration: new(Area, 120, 600, 700),
            PairHints: [new((nint)1, (nint)2, TaskRelation.Integrated)]);
        var plan = IntentLayoutPlanner.CreateDetailedPlan(windows.Select(V).ToArray(), new(),
            desktop: windows, taskContext: context);
        var keep = Assert.Single(plan.Groups).Candidates.Single(c => c.Kind == "keep");
        Assert.True(keep.Breakdown!.Switching > 0, "Vertically separated content has a non-zero viewing angle.");
    }

    [Fact]
    public void UndoMustNotRejectAnUnchangedWorkingGroup()
    {
        var first = W(1, new(50, 80, 1000, 800));
        var untouched = W(2, new(1800, 80, 500, 600));
        var plan = new IntentLayoutPlan([new(first, first.VisualRect with { X = 80 })], [],
            [new([first.Handle], "local", 0, []), new([untouched.Handle], "keep", 0, [])]);
        var session = new TaskLayoutSession();
        var now = DateTimeOffset.UtcNow;
        session.Requested([first, untouched], plan, now);
        session.RejectExplicitly();
        var context = session.Capture([first, untouched], new(), [], null, now);
        Assert.Single(context.RejectedLayouts!);
    }
}
