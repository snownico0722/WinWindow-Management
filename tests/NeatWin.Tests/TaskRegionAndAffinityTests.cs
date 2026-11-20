using System.Text.Json;
using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class TaskRegionAndAffinityTests
{
    private static readonly RectI Area = new(0, 0, 2000, 1200);
    private static WindowSnapshot W(int id, RectI r, uint dpi = 96) =>
        new((nint)id, r, r, Area, (nint)1, default, true, id == 1, true, id, dpi, (uint)id);

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public void AffinityFadesContinuouslyAndIsDipEquivalent(int dpi)
    {
        var previous = 1.0; var scale = dpi / 96.0;
        RectI Scale(RectI r) => new((int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale),
            (int)Math.Round(r.Width * scale), (int)Math.Round(r.Height * scale));
        for (var gap = 0; gap <= 400; gap++)
        {
            var a = W(1, new(0, 100, 600, 800)); var b = W(2, new(600 + gap, 100, 600, 800));
            var current = IntentLayoutPlanner.TaskAffinity(a, b, new());
            Assert.InRange(current, 0, previous + 1e-9); Assert.InRange(previous - current, 0, .02);
            var x = a with { VisualRect = Scale(a.VisualRect), WorkArea = Scale(Area), Dpi = (uint)dpi };
            var y = b with { VisualRect = Scale(b.VisualRect), WorkArea = Scale(Area), Dpi = (uint)dpi };
            // Scale() rounds to actual pixels. Non-integral gaps are not exactly equivalent.
            // SmoothStep has maximum derivative 1.5; bound only the measurable gap rounding
            // error. Exactly representable scenes must still match to numeric precision.
            var radiusDip = Math.Clamp(Math.Min(Area.Width, Area.Height) * .18, 100, 260);
            var gapErrorDip = Math.Abs((y.VisualRect.Left - x.VisualRect.Right) / scale - gap);
            var quantizationBound = 1.5 / (radiusDip * .35) * gapErrorDip;
            Assert.InRange(Math.Abs(current - IntentLayoutPlanner.TaskAffinity(x, y, new())),
                0, quantizationBound + 1e-9);
            previous = current;
        }
        Assert.Equal(0, previous);
    }

    [Fact]
    public void DeclaredPairStillCannotMergeAcrossDisplays()
    {
        var a = W(1, new(0, 100, 600, 800)); var b = W(2, new(700, 100, 600, 800)) with { MonitorHandle = (nint)2 };
        Assert.Equal(0, IntentLayoutPlanner.TaskAffinity(a, b,
            new(PairHints: [new(a.Handle, b.Handle, TaskRelation.JointView)])));
    }

    [Fact]
    public void ProtectedRegionUsesOcclusionUnionAndCountsOffscreenLoss()
    {
        var window = new RectI(-100, 0, 1000, 800); var region = new TaskContentRegion(0, 0, .2, 1);
        Assert.Equal(.5, IntentLayoutPlanner.RegionVisibility(window, region, [], Area), 6);
        var blocker = new RectI(0, 0, 50, 800);
        Assert.Equal(.25, IntentLayoutPlanner.RegionVisibility(window, region, [blocker, blocker], Area), 6);
    }

    [Fact]
    public void ProtectingOneSideDoesNotProtectUnrelatedPixels()
    {
        var window = new RectI(0, 0, 1000, 800); var region = new TaskContentRegion(0, 0, .12, 1);
        Assert.True(IntentLayoutPlanner.RegionsPreserved(window, window, [], [new(900, 0, 100, 800)], Area, [region]));
        Assert.False(IntentLayoutPlanner.RegionsPreserved(window, window, [], [new(0, 0, 100, 800)], Area, [region]));
    }

    [Fact]
    public void AlreadyOccludedRegionCanImproveWithoutAnImpossibleFloor()
    {
        var window = new RectI(0, 0, 1000, 800); var region = new TaskContentRegion(0, 0, .2, 1, .95);
        Assert.True(IntentLayoutPlanner.RegionsPreserved(window, window,
            [new(0, 0, 150, 800)], [new(0, 0, 100, 800)], Area, [region]));
        Assert.False(IntentLayoutPlanner.RegionsPreserved(window, window,
            [new(0, 0, 100, 800)], [new(0, 0, 150, 800)], Area, [region]));
    }

    [Fact]
    public void ProtectedStationaryNeighborIsCheckedAsWellAsMovedWindows()
    {
        var front = W(1, new(900, 0, 100, 800)); var back = W(2, new(0, 0, 1000, 800));
        var context = new TaskLayoutContext(WindowHints: [new(back.Handle, ProtectedRegions: [new(0, 0, .12, 1)])]);
        Assert.False(IntentLayoutPlanner.DesktopRegionsPreserved([front, back],
            [front with { VisualRect = new(0, 0, 100, 800) }, back], context));
    }

    [Fact]
    public void RegionProtectionFollowsTheProposedLayerOrder()
    {
        var a = W(1, new(0, 0, 1000, 800)); var b = W(2, new(0, 0, 100, 800));
        var context = new TaskLayoutContext(WindowHints: [new(a.Handle, ProtectedRegions: [new(0, 0, .12, 1)])]);
        Assert.False(IntentLayoutPlanner.DesktopRegionsPreserved([a, b], [a with { ZOrder = 2 }, b with { ZOrder = 1 }], context));
    }

    [Fact]
    public void RegionProtectionIsOptInAndProfileRoundTrips()
    {
        Assert.False(new TaskLayoutProfile().ProtectWindowEdges);
        var profile = JsonSerializer.Deserialize<TaskLayoutProfile>(JsonSerializer.Serialize(new TaskLayoutProfile(ProtectWindowEdges: true)));
        Assert.True(profile!.ProtectWindowEdges);
        Assert.False(new TaskContentRegion(double.NaN, 0, 1, 1).IsValid);
    }

    [Fact]
    public void LookaheadIsBoundedAndEveryAcceptedScoreRemainsAuditable()
    {
        WindowSnapshot[] windows = [W(1, new(60, 60, 1400, 1000)), W(2, new(600, 100, 1400, 1000))];
        var plan = IntentLayoutPlanner.CreateDetailedPlan(windows.Select(w => new VisibleWindow(w, w.VisualRect.Area, w.VisualRect.Area, 1)).ToArray(),
            new(), desktop: windows, taskContext: new(PairHints: [new((nint)1, (nint)2, TaskRelation.JointView)]));
        Assert.NotEmpty(plan.Moves);
        foreach (var c in plan.Groups.SelectMany(g => g.Candidates))
        {
            Assert.InRange(c.Generation, 0, 2);
            if (c.Rejection is not null) continue;
            Assert.NotNull(c.Cost); Assert.NotNull(c.Breakdown);
            Assert.True(double.IsFinite(c.Cost.Value)); Assert.Equal(c.Cost.Value, c.Breakdown.Total, 9);
        }
    }
}
