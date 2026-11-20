using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class UndoStateTests
{
    private static WindowSnapshot W(int id) => new((nint)id, new(100 + id * 400, 100, 600, 500),
        new(100 + id * 400, 100, 600, 500), new(0, 0, 2560, 1380), (nint)1, default,
        true, id == 1, true, id, 120, (uint)id);

    [Theory]
    [InlineData("dpi")]
    [InlineData("topmost")]
    [InlineData("resize")]
    [InlineData("frame")]
    [InlineData("process")]
    [InlineData("monitor")]
    [InlineData("workarea")]
    [InlineData("unmanageable")]
    public void UndoDoesNotApplyOldGeometryToChangedWindowState(string change)
    {
        var w = W(1); var target = w.VisualRect with { X = 250 };
        var actual = w with { VisualRect = target };
        actual = change switch
        {
            "dpi" => actual with { Dpi = 144 },
            "topmost" => actual with { IsTopmost = true },
            "resize" => actual with { IsResizable = false },
            "frame" => actual with { FrameInsets = new(8, 0, 8, 8) },
            "process" => actual with { ProcessId = 999 },
            "monitor" => actual with { MonitorHandle = (nint)2 },
            "workarea" => actual with { WorkArea = actual.WorkArea with { Height = 1300 } },
            _ => actual with { IsManageable = false },
        };
        Assert.Empty(LayoutUndoPlanner.Create(new([new(w, target)], [], []), [actual]).Moves);
    }

    [Fact]
    public void RoundedWindowStillUndoesWithTheCurrentSnapshot()
    {
        var w = W(1); var target = w.VisualRect with { X = 250 };
        var actual = w with { VisualRect = target with { X = 252 } };
        var undo = LayoutUndoPlanner.Create(new([new(w, target)], [], []), [actual]);
        var move = Assert.Single(undo.Moves);
        Assert.Equal(actual, move.Window); Assert.Equal(w.VisualRect, move.TargetVisualRect);
    }

    [Fact]
    public void InvalidLayerMemberBlocksOnlyItsDependentGroup()
    {
        var a = W(1); var b = W(2); var c = W(3);
        var plan = new IntentLayoutPlan([new(a, a.VisualRect with { Y = 150 }), new(c, c.VisualRect with { Y = 160 })],
            [new([b, a])], []);
        WindowSnapshot[] actual = [a with { VisualRect = plan.Moves[0].TargetVisualRect, ZOrder = 2 },
            b with { ZOrder = 1, Dpi = 144 }, c with { VisualRect = plan.Moves[1].TargetVisualRect }];
        var undo = LayoutUndoPlanner.Create(plan, actual);
        Assert.Empty(undo.Layers); Assert.Equal(c.Handle, Assert.Single(undo.Moves).Window.Handle);
    }

    [Fact]
    public void ExternalInterleavingBlocksDependentGeometryUndo()
    {
        var a = W(1); var b = W(2); var unrelated = W(3) with { ZOrder = 2 };
        var target = a.VisualRect with { Y = 150 };
        var plan = new IntentLayoutPlan([new(a, target)], [new([b, a])], []);
        var undo = LayoutUndoPlanner.Create(plan, [b with { ZOrder = 1 }, unrelated,
            a with { VisualRect = target, ZOrder = 3 }]);
        Assert.Empty(undo.Layers); Assert.Empty(undo.Moves);
    }
}
