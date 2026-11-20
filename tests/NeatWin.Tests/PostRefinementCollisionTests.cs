using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class PostRefinementCollisionTests
{
    [Fact]
    public void VideoRefinementChecksOtherGroupsAtTheirPlannedPositions()
    {
        var area = new RectI(0, 0, 2560, 1380);
        WindowSnapshot W(int id, RectI rect) => new((nint)id, rect, rect, area,
            (nint)1, default, true, id == 1, true, id, 120, (uint)id);
        var video = W(1, new(100, 100, 600, 800));
        var other = W(2, new(1300, 100, 400, 600));
        var otherMove = new TidyMove(other, other.VisualRect with { X = 800 });
        var plan = new IntentLayoutPlan([otherMove], [],
            [new([video.Handle], "keep", 0, []), new([other.Handle], "local", 0, [])]);
        var refined = new[] { otherMove, new TidyMove(video, video.VisualRect with { Width = 800 }) };
        var context = new TaskLayoutContext(VideoHint: new(video.Handle,
            VideoBlackBarOrientation.Vertical, 1, video.VisualRect, .95));
        var visible = new[] { video, other }.Select(w => new VisibleWindow(w, w.VisualRect.Area, w.VisualRect.Area, 1)).ToArray();
        Assert.False(IntentLayoutPlanner.TaskRefinementAcceptable(visible, plan, refined, new(), context, [video, other]));
    }
}
