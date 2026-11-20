using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class AutomaticLayoutTests
{
    private static readonly RectI Area = new(0, 0, 2560, 1380);
    private static WindowSnapshot W(int id, int x = 100) => new((nint)id, new(x, 100, 1000, 800),
        new(x, 100, 1000, 800), Area, (nint)1, default, true, id == 1, true, id, 120, (uint)id);
    private static ManualWindowGesture Gesture(WindowSnapshot w) => new(w.Handle, w.VisualRect,
        w.VisualRect with { X = w.VisualRect.X + 50 }, w.WorkArea, ManualGestureKind.Move, [], 300, 0);
    private static AutomaticLayoutScheduler Scheduler(AutomaticLayoutMode mode, params WindowSnapshot[] before)
    {
        var s = new AutomaticLayoutScheduler(); s.Reset(mode, before, 0); return s;
    }

    [Theory]
    [InlineData(0, 1, null)] [InlineData(0, 2, null)]
    [InlineData(1, 1, LayoutRoute.LightAssist)] [InlineData(1, 2, null)]
    [InlineData(2, 1, LayoutRoute.Smart)] [InlineData(2, 2, null)]
    [InlineData(3, 1, LayoutRoute.Smart)] [InlineData(3, 2, LayoutRoute.Smart)]
    public void FourModesHaveDistinctTriggerContracts(int mode, int trigger, LayoutRoute? route) =>
        Assert.Equal(route, AutomaticLayoutPolicy.Route((AutomaticLayoutMode)mode, (LayoutTrigger)trigger));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ManualButtonIsAlwaysAvailable(int mode) =>
        Assert.Equal(LayoutRoute.ManualAlgorithm,
            AutomaticLayoutPolicy.Route((AutomaticLayoutMode)mode, LayoutTrigger.Manual));

    [Fact]
    public void FullAssistDoesNotTreatAnUnrelatedProgrammaticMoveAsAUserGesture()
    {
        var a = W(1); var s = Scheduler(AutomaticLayoutMode.FullAssist, a);
        var after = a with { VisualRect = a.VisualRect with { X = 300 } };
        Assert.Null(s.Observe([after], 100, false)); Assert.Null(s.Observe([after], 1000, false));
        var gesture = Gesture(after); s.GestureStarted(a.Handle); s.GestureCompleted(gesture, 1200);
        var ended = after with { VisualRect = gesture.EndRect };
        Assert.Null(s.Observe([ended], 1250, false));
        Assert.Equal(LayoutTrigger.AfterGesture, s.Observe([ended], 1700, false)!.Trigger);
    }

    [Fact]
    public void CancelledDragDoesNotRunCompleteSmart()
    {
        var a = W(1); var s = Scheduler(AutomaticLayoutMode.FullAssist, a);
        s.GestureCompleted(Gesture(a) with { EndRect = a.VisualRect }, 100);
        Assert.Null(s.Observe([a], 1000, false));
    }

    [Fact]
    public void ExternalChangesCoalesceAndWaitUntilTheRealDragEnds()
    {
        var a = W(1); var b = W(2, 1100); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a);
        Assert.Null(s.Observe([a, b], 100, false));
        Assert.Null(s.Observe([a, b], 550, true));
        Assert.Null(s.Observe([a, b], 900, true));
        Assert.Null(s.Observe([a, b], 1100, false));
        Assert.NotNull(s.Observe([a, b], 1350, false));
        Assert.Null(s.Observe([a, b], 5000, false));
    }

    [Fact]
    public void NewExternalEventsDuringCooldownAreDeferredNotLost()
    {
        var a = W(1); var b = W(2, 1100); var c = W(3, 1300);
        var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a);
        s.Observe([a, b], 100, false); Assert.NotNull(s.Observe([a, b], 550, false));
        s.Observe([a, b, c], 650, false);
        Assert.Null(s.Observe([a, b, c], 1100, false));
        Assert.NotNull(s.Observe([a, b, c], 1550, false));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(-4)]
    public void OwnPlacementAndNativeRoundingDoNotTriggerAnotherRun(int offset)
    {
        var a = W(1); var b = W(2, 1100);
        var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a, b);
        var target = a.VisualRect with { X = 0 };
        s.ApplicationRequested([a, b], new([new(a, target)], [], []), 100);
        var actual = a with { VisualRect = target with { X = offset } };
        Assert.Null(s.Observe([actual, b], 200, false));
        Assert.Null(s.Observe([actual, b], 700, false));
        Assert.Null(s.Observe([actual, b], 3000, false));
    }

    [Fact]
    public void ExternalChangeOnSameWindowAfterSettlingIsNotSwallowedByOwnStamp()
    {
        var a = W(1); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a);
        var target = a.VisualRect with { X = 0 };
        s.ApplicationRequested([a], new([new(a, target)], [], []), 100);
        s.Observe([a with { VisualRect = target }], 250, false);
        var external = a with { VisualRect = target with { Width = 1200 } };
        s.Observe([external], 1000, false, new HashSet<nint> { a.Handle });
        Assert.NotNull(s.Observe([external], 1450, false));
    }

    [Fact]
    public void UnrelatedWindowOpeningWhileOurLayoutSettlesStillTriggers()
    {
        var a = W(1); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a);
        var target = a.VisualRect with { X = 0 };
        s.ApplicationRequested([a], new([new(a, target)], [], []), 100);
        var b = W(2, 1300); var actual = a with { VisualRect = target };
        s.Observe([actual, b], 200, false);
        Assert.NotNull(s.Observe([actual, b], 1100, false));
    }

    [Fact]
    public void ImmediateManualCorrectionOverridesOurOwnEchoSuppression()
    {
        var a = W(1); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a);
        var target = a.VisualRect with { X = 0 };
        s.ApplicationRequested([a], new([new(a, target)], [], []), 100);
        var settled = a with { VisualRect = target }; s.Observe([settled], 150, false);
        s.GestureStarted(a.Handle); var gesture = Gesture(settled); s.GestureCompleted(gesture, 300);
        var actual = settled with { VisualRect = gesture.EndRect };
        s.Observe([actual], 350, false, new HashSet<nint> { a.Handle });
        Assert.Equal(LayoutTrigger.AfterGesture, s.Observe([actual], 1100, false)!.Trigger);
    }

    [Fact]
    public void OwnRestackDoesNotTurnRenumberedNeighborRanksIntoExternalEvents()
    {
        var a = W(1); var b = W(2, 1100); var c = W(3);
        var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a, b, c);
        s.ApplicationRequested([a, b, c], new([], [new([b, a])], []), 100);
        WindowSnapshot[] after = [b with { ZOrder = 1 }, a with { ZOrder = 2 }, c];
        Assert.Null(s.Observe(after, 200, false)); Assert.Null(s.Observe(after, 1100, false));
        Assert.Null(s.Observe(after, 3000, false));
    }

    [Fact]
    public void ForegroundChangesTriggerButActivatingExcludedSettingsDoesNot()
    {
        var a = W(1); var b = W(2, 1100); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a, b);
        Assert.Null(s.Observe([a with { IsForeground = false }, b], 100, false));
        Assert.Null(s.Observe([a, b], 1000, false));
        WindowSnapshot[] next = [a with { IsForeground = false }, b with { IsForeground = true }];
        s.Observe(next, 1100, false); Assert.NotNull(s.Observe(next, 1550, false));
    }

    [Fact]
    public void RawZRankOffsetsFromExcludedUtilityAreIgnored()
    {
        var a = W(1); var b = W(2); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a, b);
        Assert.Null(s.Observe([a with { ZOrder = 12 }, b with { ZOrder = 20 }], 100, false));
        Assert.False(s.HasPending);
    }

    [Fact]
    public void OwnUndoClearsPendingAndDoesNotImmediatelyReapply()
    {
        var a = W(1); var placed = a with { VisualRect = a.VisualRect with { X = 0 } };
        var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, placed);
        var other = W(2, 1300); s.Observe([placed, other], 100, false);
        s.ApplicationRequested([placed, other], new([new(placed, a.VisualRect)], [], []), 200);
        Assert.Null(s.Observe([a, other], 300, false));
        Assert.Null(s.Observe([a, other], 2500, false));
        s.Observe([a], 2600, false); Assert.NotNull(s.Observe([a], 3050, false));
    }

    [Fact]
    public void SwitchingOffCancelsAlreadyQueuedWork()
    {
        var a = W(1); var b = W(2); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a);
        s.Observe([a, b], 100, false); s.Reset(AutomaticLayoutMode.Off, [a, b], 200);
        Assert.Null(s.Observe([a], 1000, false)); Assert.False(s.HasPending);
    }

    [Theory]
    [InlineData("process")] [InlineData("dpi")] [InlineData("monitor")] [InlineData("pin")] [InlineData("maximize")]
    public void IdentityAndStateChangesAreNeverOwnGeometryEchoes(string kind)
    {
        var a = W(1); var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, a);
        s.ApplicationRequested([a], new([new(a, a.VisualRect)], [], []), 100);
        var after = kind switch
        {
            "process" => a with { ProcessId = 999 }, "dpi" => a with { Dpi = 144 },
            "monitor" => a with { MonitorHandle = 2 }, "pin" => a with { IsTopmost = true },
            _ => a with { IsManageable = false },
        };
        s.Observe([after], 200, false); Assert.NotNull(s.Observe([after], 1100, false));
    }

    [Fact]
    public void CompleteAutomaticPathIsTheSameSmartPlannerAsTheManualPath()
    {
        WindowSnapshot[] windows = [W(1), W(2, 900)];
        var visible = windows.Select(w => new VisibleWindow(w, w.VisualRect.Area, w.VisualRect.Area, 1)).ToArray();
        var context = new TaskLayoutContext(Profile: new(AllowUsefulResize: true),
            PairHints: [new(1, 2, TaskRelation.JointView)]);
        var expected = LayoutDispatch.Create(LayoutRoute.ManualAlgorithm, visible, new(), null, windows, context);
        var actual = LayoutDispatch.Create(LayoutRoute.Smart, visible, new(AlgorithmMode: TidyAlgorithmMode.Classic), null, windows, context);
        Assert.Equal(expected.Moves.ToArray(), actual.Moves.ToArray());
        Assert.Equal(expected.Groups.Select(g => g.Selected), actual.Groups.Select(g => g.Selected));
        Assert.Equal(expected.Groups.SelectMany(g => g.Candidates).Select(c => c.Cost), actual.Groups.SelectMany(g => g.Candidates).Select(c => c.Cost));
        Assert.Equal(expected.Layers.SelectMany(l => l.FrontToBack), actual.Layers.SelectMany(l => l.FrontToBack));
    }
}
