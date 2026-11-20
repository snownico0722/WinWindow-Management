using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class LayoutStabilityTests
{
    [Fact]
    public void UnequalDeckDoesNotDriftOnRepeatedTidy()
    {
        var area = new RectI(0, 0, 2560, 1380);
        WindowSnapshot W(int id, RectI rect) => new((nint)id, rect, rect, area, (nint)1,
            default, true, id == 1, true, id, 120, (uint)id);
        IntentLayoutPlan Plan(WindowSnapshot[] windows) => IntentLayoutPlanner.CreateDetailedPlan(
            windows.Select(w => new VisibleWindow(w, w.VisualRect.Area, w.VisualRect.Area, 1)).ToArray(),
            new TidyOptions(), desktop: windows);
        var windows = new[] { W(1, new(1120, 240, 1320, 1090)), W(2, new(540, 100, 1280, 1190)), W(3, new(0, 80, 1380, 1200)) };
        var first = Plan(windows);
        var order = first.Layers.SelectMany(l => l.FrontToBack.Select((w, i) => (w.Handle, Index: i)))
            .ToDictionary(p => p.Handle, p => p.Index);
        var placed = windows.Select(w =>
        {
            var target = first.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect;
            return w with { VisualRect = target, OuterRect = target, ZOrder = order.GetValueOrDefault(w.Handle, w.ZOrder) };
        }).OrderBy(w => w.ZOrder).ToArray();
        var second = Plan(placed);
        Assert.Empty(second.Moves);
        Assert.Empty(second.Layers);
    }
}
