using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class LogicalLocalProposalTests
{
    private static VisibleWindow V(int id, RectI rect) => new(new((nint)id, rect, rect,
        new(0, 0, 2400, 1400), (nint)1, default, true, id == 1, true, id, 120, (uint)id), rect.Area, rect.Area, 1);

    [Fact]
    public void LocalProposalCompletesTranslationWithoutImplicitResizing()
    {
        VisibleWindow[] windows = [V(1, new(12, 80, 1000, 1000)), V(2, new(1030, 86, 1000, 950))];
        var moves = IntentLayoutPlanner.LogicalLocalPlan(windows, new());
        Assert.NotEmpty(moves);
        Assert.All(moves, m => Assert.Equal((m.Window.VisualRect.Width, m.Window.VisualRect.Height),
            (m.TargetVisualRect.Width, m.TargetVisualRect.Height)));
    }

    [Fact]
    public void LogicalLocalProposalUsesWorkAreaRelativeCoordinates()
    {
        VisibleWindow[] windows = [V(1, new(12, 80, 1000, 1000)), V(2, new(1030, 86, 1000, 950))];
        RectI Shift(RectI r) => r with { X = r.X - 2500, Y = r.Y - 170 };
        var shifted = windows.Select(w => w with { Window = w.Window with
            { VisualRect = Shift(w.Window.VisualRect), OuterRect = Shift(w.Window.OuterRect), WorkArea = Shift(w.Window.WorkArea) } }).ToArray();
        var a = IntentLayoutPlanner.LogicalLocalPlan(windows, new());
        var b = IntentLayoutPlanner.LogicalLocalPlan(shifted, new());
        Assert.Equal(a.Select(m => (m.Window.Handle, Shift(m.TargetVisualRect))),
            b.Select(m => (m.Window.Handle, m.TargetVisualRect)));
    }
}
