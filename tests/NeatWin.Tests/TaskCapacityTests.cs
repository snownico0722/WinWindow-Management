using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class TaskCapacityTests
{
    [Fact]
    public void ContentDemandCanJustifyGrowthWithoutManualResizeHistory()
    {
        var area = new RectI(0, 0, 2560, 1380);
        WindowSnapshot W(int id, int x) => new((nint)id, new(x, 140, 650, 600), new(x, 140, 650, 600),
            area, (nint)1, default, true, id == 1, true, id, 120, (uint)id);
        WindowSnapshot[] windows = [W(1, 500), W(2, 1260)];
        var context = new TaskLayoutContext(WindowHints:
            [new((nint)1, UsefulWidthDip: 800), new((nint)2, UsefulWidthDip: 800)]);
        var plan = IntentLayoutPlanner.CreateDetailedPlan(windows.Select(w => new VisibleWindow(w, w.VisualRect.Area, w.VisualRect.Area, 1)).ToArray(),
            new(), desktop: windows, taskContext: context);
        Assert.Contains(plan.Moves, m => m.TargetVisualRect.Width > m.Window.VisualRect.Width);
        Assert.All(plan.Moves, m => Assert.Equal(m.TargetVisualRect.Area, m.TargetVisualRect.Intersect(area).Area));
    }
}
