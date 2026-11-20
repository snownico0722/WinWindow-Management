using NeatWin.App;
using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class ReviewFixTests
{
    private static readonly RectI Area = new(0, 0, 2560, 1380);
    private static WindowSnapshot W(int id, RectI rect) => new((nint)id, rect, rect, Area,
        (nint)1, default, true, id == 1, true, id, 120, (uint)id);
    private static VisibleWindow V(WindowSnapshot w) => new(w, w.VisualRect.Area, w.VisualRect.Area, 1);
    private static RectI Target(WindowSnapshot w, IntentLayoutPlan plan) =>
        plan.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect;
    private static IntentLayoutPlan Plan(TaskLayoutContext context, params WindowSnapshot[] windows) =>
        IntentLayoutPlanner.CreateDetailedPlan(windows.Select(V).ToArray(), new(), desktop: windows, taskContext: context);

    [Fact]
    public void RayAngleAgreesWithHorizontalProjectionOnTheHorizontalMidline()
    {
        var calibration = new ViewingCalibration(Area, 120, 600, 700);
        var expected = Math.Abs(calibration.HorizontalAngle(800, Area) - calibration.HorizontalAngle(1700, Area));
        Assert.Equal(expected, calibration.AngularSeparation(800, 690, 1700, 690), 9);
    }

    [Fact]
    public void ViewingAngleHasVerticalSymmetryAndFiniteZeroAtTheSamePoint()
    {
        var calibration = new ViewingCalibration(Area, 120, 600, 700);
        var horizontal = calibration.AngularSeparation(1080, 690, 1480, 690);
        var vertical = calibration.AngularSeparation(1280, 490, 1280, 890);
        Assert.True(vertical > 0); Assert.Equal(horizontal, vertical, 9);
        Assert.Equal(vertical, calibration.AngularSeparation(1280, 890, 1280, 490), 9);
        Assert.Equal(0, calibration.AngularSeparation(900, 400, 900, 400), 9);
    }

    [Fact]
    public void PhysicalAngleIsIndependentOfDesktopCoordinateOrigin()
    {
        var first = new ViewingCalibration(Area, 120, 600, 700);
        var shifted = first with { WorkArea = Area with { X = -2560, Y = -200 } };
        Assert.Equal(first.AngularSeparation(900, 200, 1500, 900),
            shifted.AngularSeparation(900 - 2560, 200 - 200, 1500 - 2560, 900 - 200), 9);
    }

    [Fact]
    public void VerticalFillPreferenceActuallyOffersAndSelectsASingleWindowCandidate()
    {
        var w = W(1, new(450, 20, 1000, 1340));
        var plan = Plan(new(PreferReversibleVerticalFill: true), w);
        Assert.Equal(new RectI(450, 0, 1000, 1380), Target(w, plan));
        Assert.Contains(plan.Groups.SelectMany(g => g.Candidates), c => c.Kind == "task-vertical-fill" && c.Rejection is null);
    }

    [Fact]
    public void DisabledFillDoesNotTurnNearFullHeightIntoAMandate()
    {
        var w = W(1, new(450, 20, 1000, 1340));
        var plan = Plan(new(PreferReversibleVerticalFill: false), w);
        Assert.Equal(w.VisualRect, Target(w, plan));
        Assert.DoesNotContain(plan.Groups.SelectMany(g => g.Candidates), c => c.Kind == "task-vertical-fill");
    }

    [Fact]
    public void FillRespectsFixedSizesTopmostAndTheResizeOptOut()
    {
        var w = W(1, new(450, 20, 1000, 1340));
        foreach (var blocked in new[] { w with { IsResizable = false }, w with { IsTopmost = true } })
            Assert.Equal(blocked.VisualRect, Target(blocked, Plan(new(PreferReversibleVerticalFill: true), blocked)));
        var noResize = new TaskLayoutContext(Profile: new(AllowUsefulResize: false), PreferReversibleVerticalFill: true);
        Assert.Equal(w.VisualRect, Target(w, Plan(noResize, w)));
    }

    [Fact]
    public void FillDoesNotRecruitAnOrdinaryFloatingWindow()
    {
        var w = W(1, new(450, 250, 1000, 800));
        var plan = Plan(new(PreferReversibleVerticalFill: true), w);
        Assert.Equal(w.VisualRect, Target(w, plan));
        Assert.DoesNotContain(plan.Groups.SelectMany(g => g.Candidates), c => c.Kind == "task-vertical-fill");
    }

    [Fact]
    public void FillingAnAccessibleStackMustNotEraseItsBackWindow()
    {
        WindowSnapshot[] windows = [W(1, new(500, 50, 1000, 1330)), W(2, new(500, 0, 1000, 1330))];
        var plan = Plan(new(PreferReversibleVerticalFill: true), windows);
        var ranks = windows.ToDictionary(w => w.Handle, w => w.ZOrder);
        foreach (var layer in plan.Layers)
        {
            var slots = layer.FrontToBack.Select(w => ranks[w.Handle]).Order().ToArray();
            for (var i = 0; i < slots.Length; i++) ranks[layer.FrontToBack[i].Handle] = slots[i];
        }
        var front = new List<RectI>();
        foreach (var window in windows.OrderBy(w => ranks[w.Handle]))
        {
            var target = Target(window, plan);
            Assert.True(IntentLayoutPlanner.ScreenExposure(target, front, Area, 120).Visible > 0);
            front.Add(target);
        }
    }

    [Fact]
    public void SkippedUndoDoesNotCreateNegativeFeedback()
    {
        var w = W(1, new(100, 100, 800, 700));
        var session = new TaskLayoutSession(); var now = DateTimeOffset.UtcNow;
        session.Requested([w], new([new(w, w.VisualRect with { X = 120 })], [], [new([w.Handle], "local", 0, [])]), now);
        session.RejectExplicitly([]);
        Assert.Empty(session.Capture([w], new(), [], null, now).RejectedLayouts!);
    }

    [Fact]
    public void PartialUndoDoesNotLabelAnEntireTwoWindowArrangement()
    {
        WindowSnapshot[] windows = [W(1, new(100, 100, 800, 700)), W(2, new(900, 100, 800, 700))];
        var moves = windows.Select(w => new TidyMove(w, w.VisualRect with { Y = 110 })).ToArray();
        var session = new TaskLayoutSession(); var now = DateTimeOffset.UtcNow;
        session.Requested(windows, new(moves, [], [new([windows[0].Handle, windows[1].Handle], "columns", 0, [])]), now);
        session.RejectExplicitly([windows[0].Handle]);
        Assert.Empty(session.Capture(windows, new(), [], null, now).RejectedLayouts!);
    }

    [Fact]
    public void LayerOnlyUndoStillProducesOneLocalNegativeExample()
    {
        WindowSnapshot[] windows = [W(1, new(100, 100, 800, 700)), W(2, new(600, 100, 800, 700)), W(3, new(2100, 100, 300, 700))];
        var session = new TaskLayoutSession(); var now = DateTimeOffset.UtcNow;
        session.Requested(windows, new([], [new([windows[1], windows[0]])],
            [new([windows[0].Handle, windows[1].Handle], "restack", 0, []), new([windows[2].Handle], "keep", 0, [])]), now);
        session.RejectExplicitly([windows[0].Handle, windows[1].Handle]);
        Assert.Single(session.Capture(windows, new(), [], null, now).RejectedLayouts!);
    }

    [Fact]
    public void DisabledFollowHandHasNoPointerTimerOrMouseHook()
    {
        RunSta(() =>
        {
            using var manager = new AutoTidyManager();
            Assert.False(manager.Enabled);
            Assert.False(manager.PointerTrackingActive);
            Assert.True(manager.IsAvailable); // Geometry observation remains available.
        });
    }

    [Fact]
    public void TogglingFollowHandStartsAndStopsOnlyItsPointerTracking()
    {
        RunSta(() =>
        {
            using var manager = new AutoTidyManager();
            Assert.True(manager.IsAvailable);
            manager.Enabled = true;
            Assert.True(manager.PointerTrackingActive);
            manager.Enabled = true;
            Assert.True(manager.PointerTrackingActive);
            manager.Enabled = false;
            Assert.False(manager.PointerTrackingActive);
            Assert.True(manager.IsAvailable);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Native test thread did not complete.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
