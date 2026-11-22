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
    [InlineData(5, 1, LayoutRoute.LightAssist)] [InlineData(5, 2, null)] [InlineData(5, 3, LayoutRoute.LocalOpening)]
    public void PublicModesAndLegacyValuesHaveDistinctTriggerContracts(int mode, int trigger, LayoutRoute? route) =>
        Assert.Equal(route, AutomaticLayoutPolicy.Route((AutomaticLayoutMode)mode, (LayoutTrigger)trigger));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(5)]
    public void ManualButtonIsAlwaysAvailable(int mode) =>
        Assert.Equal(LayoutRoute.ManualAlgorithm,
            AutomaticLayoutPolicy.Route((AutomaticLayoutMode)mode, LayoutTrigger.Manual));

    [Theory]
    [InlineData(AutomaticLayoutMode.Off, false)]
    [InlineData(AutomaticLayoutMode.Light, false)]
    [InlineData(AutomaticLayoutMode.LightAssist, false)]
    [InlineData(AutomaticLayoutMode.FullAssist, false)]
    [InlineData(AutomaticLayoutMode.AutoFullAssist, true)]
    public void OnlyFullGrantsAutomaticDismissal(AutomaticLayoutMode mode, bool permitted) =>
        Assert.Equal(permitted, AutomaticLayoutPolicy.AllowsAutomaticDismissal(mode));

    [Fact]
    public void LightRequiresAnIdleFirstForegroundVisitToANewIndependentWindow()
    {
        var old = W(1) with { IsForeground = false };
        var front = W(2, 300) with { IsForeground = true };
        WindowSnapshot[] desktop = [old, front];
        var history = new ForegroundHistory();
        history.Visit(old.Handle, old.ProcessId, 100);
        history.Shown(front.Handle, front.ProcessId, 1000);
        history.Visit(front.Handle, front.ProcessId, 1000);
        Assert.Null(history.NewTaskOpeningFor(desktop, 1010));

        history.Retire(front.Handle);
        history.Created(front.Handle, front.ProcessId, 1200);
        history.Visit(front.Handle, front.ProcessId, 1250);
        Assert.Null(history.NewTaskOpeningFor([old, front with { Owner = old.Handle }], 1260));
        var opening = Assert.IsType<ForegroundHistory.NewTaskOpening>(history.NewTaskOpeningFor(desktop, 1260));
        var scheduler = Scheduler(AutomaticLayoutMode.Light, old);
        scheduler.RequestCurrent(1260);
        Assert.Null(scheduler.Observe(desktop, 1260, false));
        Assert.False(scheduler.HasPending);
        Assert.Null(scheduler.Observe(desktop, 1260, true, newTaskOpeningReady: true));
        Assert.Equal(LayoutTrigger.NewTaskOpening, scheduler.Observe(desktop, 1260, false, newTaskOpeningReady: true)!.Trigger);
        Assert.False(history.ConsumeLocalOpening(opening, desktop, 1260, 1259));
        Assert.True(history.ConsumeLocalOpening(opening, desktop, 1260, 1249));
        Assert.Null(history.NewTaskOpeningFor(desktop, 1261));
    }

    [Theory]
    [InlineData(SmartTidyStrength.Gentle)]
    [InlineData(SmartTidyStrength.Balanced)]
    [InlineData(SmartTidyStrength.Assertive)]
    public void OpeningStrengthDoesNotAuthorizeMovesOrLayersForOtherWindows(SmartTidyStrength strength)
    {
        var front = W(1, 360);
        var resident = W(2, 620);
        WindowSnapshot[] desktop = [front, resident, W(3, 1700)];
        var task = new TaskLayoutContext(Focus: new(front.Handle, resident.Handle, 2), UseWorkingPairs: true,
            Attention: new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, 1, 0, true), [2] = new(1, 1, 1, 0, false) })
        {
            IsWorkspaceObservation = true,
            FirstPlacementWindow = front.Handle,
            LocalPlacementWindow = front.Handle,
            AllowGestureAssist = false,
        };
        var visible = new VisibilityAnalyzer().SelectVisibleWorkingSet(desktop, includeStackAccess: true);
        var plan = LayoutDispatch.Create(LayoutRoute.LocalOpening, visible, new(SmartStrength: strength), null, desktop, task);

        Assert.Empty(plan.Layers);
        Assert.All(plan.Moves, move => Assert.Equal(front.Handle, move.Window.Handle));
        if (strength == SmartTidyStrength.Assertive) Assert.Single(plan.Moves);
    }

    [Fact]
    public void ReadingAReferenceDoesNotQueueGeometryWhileANewWindowStillDoes()
    {
        var desktop = new[] { W(1), W(2, 1100) };
        var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, desktop);
        var history = new ForegroundHistory();
        foreach (long tick in new long[] { 1000, 1300, 7100, 15500, 81000 })
        {
            history.Interact(2, 2, tick);
            Assert.Null(s.Observe(desktop, tick, false));
            Assert.False(s.HasPending);
        }
        var popup = W(3, 100) with { IsForeground = false, IsManageable = false, IsTopmost = true,
            VisualRect = new(20, 30, 150, 45) };
        Assert.Null(s.Observe([.. desktop, popup], 81010, false));
        Assert.Null(s.Observe(desktop, 81090, false));
        Assert.False(s.HasPending);
        var opened = desktop.Append(W(3, 100)).ToArray();
        Assert.Null(s.Observe(opened, 81100, false));
        Assert.True(s.Observe(opened, 81600, false)!.Cause.HasFlag(WorkspaceChangeCause.Membership));
    }

    [Fact]
    public void FullAssistDoesNotTreatAnUnrelatedProgrammaticMoveAsAUserGesture()
    {
        var a = W(1); var s = Scheduler(AutomaticLayoutMode.FullAssist, a);
        var after = a with { VisualRect = a.VisualRect with { X = 300 } };
        Assert.Null(s.Observe([after], 100, false)); Assert.Null(s.Observe([after], 1000, false));
        var gesture = Gesture(after); s.GestureStarted(a.Handle); s.GestureCompleted(gesture, 1200);
        var ended = after with { VisualRect = gesture.EndRect };
        Assert.Equal(LayoutTrigger.AfterGesture, s.Observe([ended], 1200, false)!.Trigger);
        Assert.Null(s.Observe([ended], 1700, false));
    }

    [Theory]
    [InlineData(AutomaticLayoutMode.FullAssist)]
    [InlineData(AutomaticLayoutMode.AutoFullAssist)]
    public void FirstSafeObservationAfterTheNativeEndExecutesOnceEvenInsideTheWorkspaceCooldown(AutomaticLayoutMode mode)
    {
        var w = W(1);
        var s = Scheduler(mode, w);
        s.ApplicationRequested([w], new([], [], []), 100);
        s.GestureStarted(w.Handle);
        var gesture = Gesture(w) with { ReleasedTick = 160, ProcessId = w.ProcessId };
        var released = w with { VisualRect = gesture.EndRect };
        s.GestureCompleted(gesture, 160);
        Assert.Null(s.Observe([released], 160, busy: true));
        Assert.Null(s.Observe([released], 164, busy: true));
        Assert.Equal(LayoutTrigger.AfterGesture, s.Observe([released], 167, false)!.Trigger);
        Assert.Null(s.Observe([released], 168, false));
    }

    [Fact]
    public void LightAssistUsesTheSameNativeReleaseGateForAnImmediateOrDelayedButtonUp()
    {
        var w = W(1);
        var gesture = Gesture(w) with { ReleasedTick = 230, ProcessId = w.ProcessId };
        Assert.Equal(LayoutRoute.LightAssist, AutomaticLayoutPolicy.Route(AutomaticLayoutMode.LightAssist, LayoutTrigger.AfterGesture));
        Assert.Equal(GestureReleaseState.Wait, AutomaticLayoutPolicy.ReleaseState(gesture, w.Handle, w.ProcessId, 230, busy: true));
        Assert.Equal(GestureReleaseState.Ready, AutomaticLayoutPolicy.ReleaseState(gesture, w.Handle, w.ProcessId, 231, busy: false));
        Assert.Equal(GestureReleaseState.Discard, AutomaticLayoutPolicy.ReleaseState(gesture, w.Handle, w.ProcessId + 1, 231, busy: false));
        Assert.Equal(GestureReleaseState.Discard, AutomaticLayoutPolicy.ReleaseState(gesture, 0, w.ProcessId, 231, busy: false));
        Assert.Equal(GestureReleaseState.Discard, AutomaticLayoutPolicy.ReleaseState(gesture, w.Handle, w.ProcessId, 9000, busy: false));
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
        s.DeferWorkspaceWhileBusy(900); // native monitor avoids capturing the desktop while the pointer is occupied
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

    [Fact]
    public void SmallGeometryOscillationsDoNotStarveAnUnrelatedWindowOpening()
    {
        var anchor = W(1) with { IsManageable = false };
        var jittered = anchor with { VisualRect = anchor.VisualRect with { X = anchor.VisualRect.X + 1 } };
        var opened = W(2, 1100);
        var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, anchor);
        Assert.Null(s.Observe([jittered, opened], 100, false));
        Assert.Null(s.Observe([anchor, opened], 250, false));
        Assert.Null(s.Observe([jittered, opened], 400, false));
        Assert.Equal(LayoutTrigger.WorkspaceChange, s.Observe([anchor, opened], 550, false)!.Trigger);
        Assert.Null(s.Observe([jittered, opened], 1600, false));
        Assert.False(s.HasPending);
    }

    [Fact]
    public void ConsecutiveSmallProgrammaticMovesAccumulateIntoAWorkspaceChange()
    {
        var w = W(1);
        WindowSnapshot Moved(int dx) => w with { VisualRect = w.VisualRect with { X = w.VisualRect.X + dx } };
        var s = Scheduler(AutomaticLayoutMode.AutoFullAssist, w);
        Assert.Null(s.Observe([Moved(2)], 100, false));
        Assert.False(s.HasPending);
        Assert.Null(s.Observe([Moved(4)], 200, false));
        Assert.True(s.HasPending);
        Assert.Equal(LayoutTrigger.WorkspaceChange, s.Observe([Moved(5)], 650, false)!.Trigger);
    }

    [Fact]
    public void ARecordedOnePixelGestureStillRequestsAssistance()
    {
        var w = W(1);
        var gesture = Gesture(w) with { EndRect = w.VisualRect with { X = w.VisualRect.X + 1 } };
        var s = Scheduler(AutomaticLayoutMode.FullAssist, w);
        s.GestureStarted(w.Handle);
        s.GestureCompleted(gesture, 100);
        Assert.Equal(LayoutTrigger.AfterGesture, s.Observe([w with { VisualRect = gesture.EndRect }], 100, false)!.Trigger);
        Assert.Null(s.Observe([w with { VisualRect = gesture.EndRect }], 550, false));
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
        Assert.Equal(LayoutTrigger.AfterGesture, s.Observe([actual], 300, false, new HashSet<nint> { a.Handle })!.Trigger);
        Assert.Null(s.Observe([actual], 1100, false));
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
