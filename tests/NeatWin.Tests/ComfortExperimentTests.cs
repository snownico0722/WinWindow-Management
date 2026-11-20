using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class ComfortExperimentTests
{
    [Fact]
    public void HistoricalExperimentRetainsDimensionsWithoutReplacingTaskPlanner()
    {
        var area = new RectI(0, 0, 2560, 1380);
        WindowSnapshot W(int id, RectI rect) => new((nint)id, rect, rect, area, (nint)1,
            default, true, id == 1, true, id, 120, (uint)id);
        var windows = new[] { W(1, new(100, 80, 1000, 900)), W(2, new(1120, 85, 1000, 900)) };
        var visible = windows.Select(w => new VisibleWindow(w, w.VisualRect.Area, w.VisualRect.Area, 1)).ToArray();
        var plan = ComfortSmartTidySolver.CreatePlan(visible, new());
        Assert.All(plan, m =>
        {
            Assert.Equal(m.Window.VisualRect.Width, m.TargetVisualRect.Width);
            Assert.Equal(m.Window.VisualRect.Height, m.TargetVisualRect.Height);
            Assert.Equal(m.TargetVisualRect.Area, m.TargetVisualRect.Intersect(area).Area);
        });
        var taskPlan = IntentLayoutPlanner.CreateDetailedPlan(visible, new(), desktop: windows);
        Assert.All(taskPlan.Groups, g => Assert.NotNull(g.Task));
    }
}
