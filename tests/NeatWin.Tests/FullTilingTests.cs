using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class FullTilingTests
{
    private static WindowSnapshot W(int id, RectI area, RectI? rect = null) => new((nint)id,
        rect ?? new(area.X + 100, area.Y + 100, 800, 700), rect ?? new(area.X + 100, area.Y + 100, 800, 700),
        area, 1, default, true, id == 1, true, id, 96, (uint)id);
    private static RectI Target(WindowSnapshot w, IntentLayoutPlan plan) =>
        plan.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect;

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)] [InlineData(7)]
    public void TilingIncludesOccludedWindowsAndNeverOverlaps(int count)
    {
        var area = new RectI(-2560, -100, 2560, 1380);
        var windows = Enumerable.Range(1, count).Select(i => W(i, area)).ToArray();
        var plan = FullTilingPlanner.Create(windows);
        Assert.Equal(count, plan.Moves.Count);
        Assert.Empty(plan.Layers);
        var rects = windows.Select(w => Target(w, plan)).ToArray();
        Assert.All(rects, r => Assert.Equal(r.Area, r.Intersect(area).Area));
        for (var i = 0; i < count; i++) for (var j = i + 1; j < count; j++) Assert.Equal(0, rects[i].Intersect(rects[j]).Area);
        Assert.True(rects.Sum(r => r.Area) > area.Area * .94);
        var after = windows.Select(w => w with { VisualRect = Target(w, plan) }).ToArray();
        Assert.Empty(FullTilingPlanner.Create(after).Moves);
        var undo = LayoutUndoPlanner.Create(plan, after);
        Assert.Equal(windows.Select(w => w.VisualRect), after.Select(w => Target(w, undo)));
    }

    [Theory]
    [InlineData("fixed")] [InlineData("topmost")] [InlineData("dialog")]
    public void FixedAndProtectedWindowsAreAnchorsNotTilingVictims(string kind)
    {
        var area = new RectI(0, 0, 2560, 1380);
        var anchor = W(1, area, new(0, 0, 550, 1380));
        anchor = kind switch { "fixed" => anchor with { IsResizable = false },
            "topmost" => anchor with { IsTopmost = true }, _ => anchor with { IsManageable = false } };
        WindowSnapshot[] windows = [anchor, W(2, area), W(3, area)];
        var p = FullTilingPlanner.Create(windows);
        Assert.DoesNotContain(p.Moves, m => m.Window.Handle == anchor.Handle);
        Assert.NotEmpty(p.Moves);
        Assert.All(p.Moves, m => Assert.Equal(0, m.TargetVisualRect.Intersect(anchor.VisualRect).Area));
    }

    [Fact]
    public void TilingDoesNotResizePastTheReadableFloorWhenSpaceIsInsufficient()
    {
        var area = new RectI(0, 0, 600, 400);
        var p = FullTilingPlanner.Create([W(1, area), W(2, area), W(3, area)]);
        Assert.Empty(p.Moves);
        Assert.Equal("tiling-insufficient-space", Assert.Single(p.Groups).Selected);
    }

    [Fact]
    public void TilingUsesEachMonitorsOwnWorkAreaWithoutCrossingDisplays()
    {
        var a = new RectI(-1920, 0, 1920, 1040); var b = new RectI(0, 0, 2560, 1380);
        WindowSnapshot[] windows = [W(1, a), W(2, a), W(3, b) with { MonitorHandle = 2, Dpi = 144 }];
        var p = FullTilingPlanner.Create(windows);
        Assert.Equal(2, p.Groups.Count);
        Assert.All(p.Moves, m => Assert.Equal(m.TargetVisualRect.Area, m.TargetVisualRect.Intersect(m.Window.WorkArea).Area));
    }

    [Theory]
    [InlineData(null, false, AutomaticLayoutMode.Off)]
    [InlineData(null, true, AutomaticLayoutMode.LightAssist)]
    [InlineData(3, false, AutomaticLayoutMode.AutoFullAssist)]
    [InlineData(4, false, AutomaticLayoutMode.AutoFullAssist)]
    [InlineData(0, true, AutomaticLayoutMode.Off)]
    [InlineData(999, true, AutomaticLayoutMode.Off)]
    public void SettingsMigrationDoesNotSilentlyEnableAggressiveAutomation(int? mode, bool old, AutomaticLayoutMode expected) =>
        Assert.Equal(expected, AutomaticLayoutPolicy.Read(mode, old));

}
