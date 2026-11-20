using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class IntentLayoutTests
{
    private static readonly RectI Area = new(0, 0, 1920, 1080);

    [Fact]
    public void Balanced_ClosesModerateGapsAndAlignsWithoutFullScreenTiling()
    {
        var a = W(1, new(200, 160, 660, 640), true);
        var b = W(2, new(960, 240, 650, 600));
        var targets = Arrange([a, b]);
        Assert.InRange(targets[1].Left - targets[0].Right, 0, 16);
        Assert.True(Math.Min(Math.Abs(targets[0].Top - targets[1].Top), Math.Abs(targets[0].Bottom - targets[1].Bottom)) <= 3);
        Assert.InRange(targets[0].Width, 600, 700);
        Assert.InRange(targets[1].Width, 600, 700);
        Assert.True(targets[0].Height < Area.Height);
    }

    [Fact]
    public void Balanced_ExplicitTidyCanResolveDeepOverlap()
    {
        var a = W(1, new(200, 180, 820, 620), true);
        var b = W(2, new(550, 250, 820, 620));
        var target = Arrange([a, b]);
        Assert.Equal(0, target[0].Intersect(target[1]).Area);
        Assert.True(target[0].Left < target[1].Left);
    }

    [Fact]
    public void DistantFloatingWindowsAreNotRecruitedIntoOneLayout()
    {
        var windows = new[] { W(1, new(180, 190, 520, 500), true), W(2, new(1160, 260, 500, 480)) };
        Assert.Equal(windows.Select(w => w.VisualRect), Arrange(windows));
    }

    [Fact]
    public void Ultrawide_LocalWorkingGroupDoesNotGrowToFill5120Pixels()
    {
        var area = new RectI(0, 0, 5120, 1440);
        var windows = new[] { W(1, new(300, 200, 1100, 900), true, area), W(2, new(1520, 260, 1100, 860), area: area), W(3, new(2730, 220, 1080, 900), area: area) };
        var targets = Arrange(windows);
        Assert.True(targets.Max(r => r.Right) - targets.Min(r => r.Left) < 3700);
        for (var i = 0; i < targets.Length; i++) Assert.InRange(targets[i].Width, 1000, 1200);
        Assert.True(targets[0].Left < targets[1].Left && targets[1].Left < targets[2].Left);
        Assert.Equal(0, targets[0].Intersect(targets[1]).Area);
    }

    [Fact]
    public void FixedSizeWindowIsNeverResized()
    {
        var windows = new[] { W(1, new(150, 180, 880, 620), true) with { IsResizable = false }, W(2, new(600, 250, 900, 650)) };
        var targets = Arrange(windows);
        Assert.Equal(windows[0].VisualRect.Width, targets[0].Width);
        Assert.Equal(windows[0].VisualRect.Height, targets[0].Height);
    }

    [Fact]
    public void NegativeCoordinateMonitorIsRespected()
    {
        var area = new RectI(-1920, -120, 1920, 1080);
        var windows = new[] { W(1, new(-1750, 0, 750, 620), true, area), W(2, new(-900, 50, 750, 600), area: area) };
        foreach (var target in Arrange(windows)) Assert.Equal(target.Area, target.Intersect(area).Area);
    }

    [Fact]
    public void DifferentMonitorsNeverJoinTheSameGroup()
    {
        var first = W(1, new(1100, 160, 700, 600));
        var second = W(2, new(1930, 190, 750, 620), area: new RectI(1920, 0, 1920, 1080)) with { MonitorHandle = (nint)2 };
        var targets = Arrange([first, second]);
        Assert.Equal(targets[0].Area, targets[0].Intersect(first.WorkArea).Area);
        Assert.Equal(targets[1].Area, targets[1].Intersect(second.WorkArea).Area);
    }

    [Fact]
    public void OffscreenRescueStillWorksForSingleWindow()
    {
        var window = W(1, new(-300, 160, 600, 700), true);
        var target = Arrange([window])[0];
        Assert.Equal(0, target.Left);
        Assert.Equal(600, target.Width);
    }

    [Fact]
    public void NormalTidyIsIdempotentForLocalRow()
    {
        var windows = new[] { W(1, new(200, 160, 660, 640), true), W(2, new(960, 240, 650, 600)) };
        var once = Arrange(windows);
        var updated = windows.Select((w, i) => w with { VisualRect = once[i], OuterRect = once[i] }).ToArray();
        Assert.Equal(once, Arrange(updated));
    }

    [Fact]
    public void ExtremeReferenceCannotVetoOverlapRemoval()
    {
        var windows = new[] { W(1, new(200, 180, 820, 620), true), W(2, new(550, 250, 820, 620)) };
        var now = DateTimeOffset.UtcNow;
        var reference = new IntentReferenceDocument(1, Enumerable.Range(0, 100)
            .Select(i => new IntentReferenceSample(now.AddMinutes(-i - 1), "standard", 48, false)).ToArray());
        var targets = Arrange(windows, reference);
        Assert.Equal(0, targets[0].Intersect(targets[1]).Area);
        Assert.InRange(targets[1].Left - targets[0].Right, 0, 16);
    }

    [Fact]
    public void SmallWorkAreaNeverProducesInvalidRectangles()
    {
        var area = new RectI(0, 0, 640, 400);
        var windows = new[] { W(1, new(0, 0, 400, 300), area: area), W(2, new(180, 80, 430, 300), area: area) };
        foreach (var target in Arrange(windows)) { Assert.False(target.IsEmpty); Assert.Equal(target.Area, target.Intersect(area).Area); }
    }

    [Fact]
    public void ClassicStillUsesTheOriginalEngine()
    {
        var windows = new[] { V(W(1, new(8, 10, 700, 800))) };
        var options = new TidyOptions(AlgorithmMode: TidyAlgorithmMode.Classic);
        Assert.Equal(new TidyEngine().CreatePlan(windows, options), IntentLayoutPlanner.CreatePlan(windows, options));
    }

    private static RectI[] Arrange(WindowSnapshot[] windows, IntentReferenceDocument? reference = null)
    {
        var moves = IntentLayoutPlanner.CreatePlan(windows.Select(V).ToArray(), new TidyOptions(), reference)
            .ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        return windows.Select(w => moves.GetValueOrDefault(w.Handle, w.VisualRect)).ToArray();
    }
    private static VisibleWindow V(WindowSnapshot window) => new(window, window.VisualRect.Area, window.VisualRect.Area, 1);
    private static WindowSnapshot W(int handle, RectI rect, bool foreground = false, RectI? area = null) =>
        new((nint)handle, rect, rect, area ?? Area, (nint)1, default, true, foreground, true, handle);
}
