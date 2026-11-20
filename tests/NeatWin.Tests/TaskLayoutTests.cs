using System.Text.Json;
using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class TaskLayoutTests
{
    private static readonly RectI Area = new(0, 0, 2560, 1380);
    private static WindowSnapshot W(int id, RectI rect, bool resize = true) =>
        new((nint)id, rect, rect, Area, (nint)1, default, resize, id == 1, true, id, 120, (uint)id);
    private static VisibleWindow V(WindowSnapshot w) => new(w, w.VisualRect.Area, w.VisualRect.Area, 1);
    private static TaskLayoutContext Context(TaskRelation relation = TaskRelation.Automatic) => new(
        Displays: [new((nint)1, Area, Area)], PairHints: [new((nint)1, (nint)2, relation)]);
    private static IntentLayoutPlan Plan(TaskLayoutContext context, params WindowSnapshot[] windows) =>
        IntentLayoutPlanner.CreateDetailedPlan(windows.Select(V).ToArray(), new(), desktop: windows, taskContext: context);
    private static RectI Target(WindowSnapshot w, IntentLayoutPlan p) =>
        p.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect;
    private static WindowSnapshot[] Placed(WindowSnapshot[] windows, IntentLayoutPlan p)
    {
        var ranks = windows.ToDictionary(w => w.Handle, w => w.ZOrder);
        foreach (var layer in p.Layers)
        {
            var slots = layer.FrontToBack.Select(w => ranks[w.Handle]).Order().ToArray();
            for (var i = 0; i < slots.Length; i++) ranks[layer.FrontToBack[i].Handle] = slots[i];
        }
        return windows.Select(w => w with { VisualRect = Target(w, p), ZOrder = ranks[w.Handle] }).ToArray();
    }

    [Fact]
    public void OffscreenPixelsNeverCountAsVisibleInformation()
    {
        var e = IntentLayoutPlanner.ScreenExposure(new(-100, 0, 1000, 800), [], Area, 120);
        Assert.Equal(.9, e.Visible, 6); Assert.InRange(e.AccessWidth, 0, 900);
    }

    [Fact]
    public void ScreenAndWindowOcclusionUseUnionNotDoubleCounting()
    {
        var r = new RectI(-100, 0, 1000, 800);
        var a = IntentLayoutPlanner.ScreenExposure(r, [new(-100, 0, 200, 800)], Area, 120);
        var b = IntentLayoutPlanner.ScreenExposure(r, [new(-100, 0, 200, 800), new(-100, 0, 200, 800)], Area, 120);
        Assert.Equal(a, b); Assert.Equal(.8, a.Visible, 6);
    }

    [Fact]
    public void SameGeometryCanCarryDifferentExplicitTaskHypotheses()
    {
        WindowSnapshot[] windows = [W(1, new(450, 260, 1050, 800)), W(2, new(400, 210, 1050, 800))];
        var joint = Plan(Context(TaskRelation.JointView), windows);
        var parked = Plan(Context(TaskRelation.Alternating), windows);
        Assert.Equal(1, joint.Groups[0].Task!.Relations[0].Joint);
        Assert.Equal(1, parked.Groups[0].Task!.Relations[0].Parked);
        Assert.Equal("explicit-task-hint", joint.Groups[0].Task!.Relations[0].Source);
        Assert.True(Target(windows[0], parked).Intersect(Target(windows[1], parked)).Area > 0);
    }

    [Fact]
    public void RoughJointViewHasEdgeAndResizeAlternativesNotJustPreserve()
    {
        var p = Plan(Context(TaskRelation.JointView), W(1, new(51, 80, 1500, 1100)), W(2, new(994, 160, 1409, 1000)));
        var accepted = p.Groups.SelectMany(g => g.Candidates).Where(c => c.Rejection is null).ToArray();
        Assert.Contains(accepted, c => c.Kind == "task-edge");
        Assert.Contains(accepted, c => c.Kind == "task-fit"); Assert.NotEmpty(p.Moves);
    }

    [Fact]
    public void SizeCanChangeWithoutAnyPriorUserResizeEvent()
    {
        WindowSnapshot[] windows = [W(1, new(60, 60, 1800, 1200)), W(2, new(660, 100, 1700, 1180))];
        var p = Plan(Context(TaskRelation.JointView), windows);
        Assert.Contains(windows, w => Target(w, p).Width != w.VisualRect.Width || Target(w, p).Height != w.VisualRect.Height);
    }

    [Fact]
    public void ResizeOptOutAndFixedWindowsAreRespected()
    {
        WindowSnapshot[] windows = [W(1, new(51, 80, 1500, 1100), false), W(2, new(994, 160, 1409, 1000))];
        var p = Plan(Context() with { Profile = new(AllowUsefulResize: false) }, windows);
        Assert.All(windows, w => Assert.Equal((w.VisualRect.Width, w.VisualRect.Height), (Target(w, p).Width, Target(w, p).Height)));
    }

    [Fact]
    public void SmallOutwardBleedIsARealCandidateWhenAuthorizedAndExterior()
    {
        var p = Plan(Context(TaskRelation.JointView), W(1, new(40, 50, 1500, 1100)), W(2, new(1000, 120, 1500, 1100)));
        Assert.Contains(p.Groups.SelectMany(g => g.Candidates), c => c.Kind == "task-bleed" && c.Rejection is null);
    }

    [Fact]
    public void BleedNeverCrossesIntoAnAdjacentMonitorOrVerticalTaskbar()
    {
        var rightArea = new RectI(2560, 0, 1920, 1080);
        var work = Area with { X = 60, Width = 2500 };
        var context = Context() with { Displays = [new((nint)1, work, Area), new((nint)2, rightArea, rightArea)] };
        WindowSnapshot[] windows = [W(1, new(80, 60, 1500, 1100)) with { WorkArea = work }, W(2, new(1000, 60, 1500, 1100)) with { WorkArea = work }];
        var p = Plan(context, windows);
        Assert.DoesNotContain(p.Groups.SelectMany(g => g.Candidates), c => c.Kind == "task-bleed" && c.Rejection is null);
        Assert.All(p.Moves, m => Assert.Equal(m.TargetVisualRect.Area, m.TargetVisualRect.Intersect(work).Area));
    }

    [Fact]
    public void ProtectingPeripheryDisablesItsBleed()
    {
        WindowSnapshot[] windows = [W(1, new(40, 50, 1500, 1100)), W(2, new(1000, 120, 1500, 1100))];
        var context = Context() with { WindowHints = [new((nint)1, ProtectPeriphery: true), new((nint)2, ProtectPeriphery: true)] };
        var p = Plan(context, windows);
        Assert.DoesNotContain(p.Groups.SelectMany(g => g.Candidates), c => c.Kind == "task-bleed" && c.Rejection is null);
    }

    [Fact]
    public void MissingMonitorTopologyDoesNotInventAnExteriorEdge()
    {
        var p = Plan(new(), W(1, new(40, 50, 1500, 1100)), W(2, new(1000, 120, 1500, 1100)));
        Assert.DoesNotContain(p.Groups.SelectMany(g => g.Candidates), c => c.Kind == "task-bleed");
    }

    [Fact]
    public void ARightSideDeckIsNotSprayedAcrossNewColumns()
    {
        WindowSnapshot[] windows = [W(1, new(20, 100, 1100, 1000)), W(2, new(1400, 250, 1000, 800)), W(3, new(1350, 200, 1000, 800))];
        var p = Plan(Context(), windows);
        var a = Target(windows[0], p); var b = Target(windows[1], p); var c = Target(windows[2], p);
        Assert.True(a.Left < b.Left && a.Left < c.Left);
        Assert.True(Math.Abs((b.X + b.Width / 2) - (c.X + c.Width / 2)) < Math.Min(b.Width, c.Width) * .40);
    }

    [Fact]
    public void ForegroundIsNotARequirementForVisualDemand()
    {
        var p = Plan(Context() with { WindowHints = [new((nint)2, PassiveVisual: true)] },
            W(1, new(20, 80, 1500, 1000)), W(2, new(1100, 140, 1100, 950)));
        Assert.True(p.Groups[0].Task!.Relations[0].Joint >= .88);
        Assert.Contains("passive-visual", p.Groups[0].Task!.Relations[0].Source);
    }

    [Fact]
    public void AutomaticInterpretationReportsUncertaintyRatherThanInventedLabels()
    {
        var p = Plan(Context(), W(1, new(50, 80, 1450, 1000)), W(2, new(1000, 100, 1350, 980)));
        Assert.All(p.Groups.SelectMany(g => g.Task!.Relations), r => Assert.True(r.Uncertainty > 0));
        Assert.Contains("hypothesis-weights-not-calibrated-probabilities", p.Groups[0].Task!.Limits);
    }

    [Fact]
    public void PhysicalCalibrationIsOptionalAndGeometrySpecific()
    {
        var c = new ViewingCalibration(Area, 120, 600, 700);
        Assert.True(c.Matches(Area, 120)); Assert.False(c.Matches(Area, 96));
        Assert.False(c.Matches(Area with { Width = 5120 }, 120));
        Assert.False((c with { DistanceMillimeters = double.NaN }).Matches(Area, 120));
        Assert.Equal(0, c.HorizontalAngle(1280, Area), 6);
        Assert.True(c.HorizontalAngle(2560, Area) > 0);
    }

    [Fact]
    public void CalibrationAppearsInTheDecisionTraceOnlyWhenMatched()
    {
        WindowSnapshot[] windows = [W(1, new(50, 80, 1450, 1000)), W(2, new(1000, 100, 1350, 980))];
        var p = Plan(Context() with { Calibration = new(Area, 120, 600, 700) }, windows);
        Assert.Equal("calibrated-flat-screen-angle", p.Groups[0].Task!.ViewingModel);
        Assert.Equal("normalized-distance-not-gaze", Plan(Context(), windows).Groups[0].Task!.ViewingModel);
    }

    [Fact]
    public void EveryAcceptedCandidateHasAuditableFiniteCostTerms()
    {
        var p = Plan(Context(), W(1, new(50, 80, 1450, 1000)), W(2, new(1000, 100, 1350, 980)));
        foreach (var c in p.Groups.SelectMany(g => g.Candidates).Where(c => c.Rejection is null))
        {
            Assert.NotNull(c.Breakdown); Assert.NotNull(c.Cost);
            Assert.True(double.IsFinite(c.Cost.Value)); Assert.Equal(c.Cost.Value, c.Breakdown.Total, 8);
        }
    }

    [Fact]
    public void ProfileCannotContainSessionNativeIdentityOrLogs()
    {
        var json = JsonSerializer.Serialize(new TaskLayoutProfile());
        Assert.DoesNotContain("Handle", json); Assert.DoesNotContain("Process", json);
        Assert.DoesNotContain("Session", json); Assert.DoesNotContain("Title", json);
        var bad = new TaskLayoutProfile(MaximumBleedDip: double.NaN, ComfortableWidthDip: -10);
        Assert.Equal(24, bad.Normalize().MaximumBleedDip); Assert.Equal(480, bad.Normalize().ComfortableWidthDip);
    }

    [Fact]
    public void RequestedButUnverifiedOutputDoesNotSuppressTheNextDecision()
    {
        var session = new TaskLayoutSession(); var now = DateTimeOffset.UtcNow;
        WindowSnapshot[] windows = [W(1, new(50, 80, 1450, 1000)), W(2, new(1000, 100, 1350, 980))];
        var p = Plan(Context(), windows); session.Requested(windows, p, now);
        Assert.False(session.Capture(Placed(windows, p), new(), [], null, now).VerifiedRepeat);
    }

    [Fact]
    public void VerifiedRepeatIsIdempotenceNotSatisfactionAndExpires()
    {
        var session = new TaskLayoutSession(); var now = DateTimeOffset.UtcNow;
        WindowSnapshot[] windows = [W(1, new(50, 80, 1450, 1000)), W(2, new(1000, 100, 1350, 980))];
        var p = Plan(Context(), windows); session.Requested(windows, p, now);
        var actual = Placed(windows, p); session.Verify(actual);
        var c = session.Capture(actual, new(), [], null, now);
        Assert.True(c.VerifiedRepeat); Assert.Empty(Plan(c, actual).Moves);
        session.Verify(actual);
        Assert.True(session.Capture(actual, new(), [], null, now).VerifiedRepeat);
        Assert.False(session.Capture(actual, new(), [], null, now.AddMinutes(6)).VerifiedRepeat);
        actual[0] = actual[0] with { ProcessId = 999 };
        Assert.False(session.Capture(actual, new(), [], null, now).VerifiedRepeat);
    }

    [Fact]
    public void ManualFollowupIsNotAutomaticallyARejection()
    {
        var session = new TaskLayoutSession(); var now = DateTimeOffset.UtcNow;
        var w = W(1, new(50, 80, 1400, 1000));
        var gesture = new ManualWindowGesture(w.Handle, w.VisualRect, w.VisualRect with { X = 80 }, Area,
            ManualGestureKind.Move, [], 400, 0);
        session.Gesture(gesture, now);
        var c = session.Capture([w], new(), [], null, now);
        Assert.NotNull(c.RecentGesture); Assert.Empty(c.RejectedLayouts!); Assert.False(c.VerifiedRepeat);
        Assert.Null(session.Capture([w], new(), [], null, now.AddMinutes(1)).RecentGesture);
    }

    [Fact]
    public void OnlyExplicitUndoProducesAContextLocalNegativeExample()
    {
        var session = new TaskLayoutSession(); var now = DateTimeOffset.UtcNow;
        WindowSnapshot[] windows = [W(1, new(50, 80, 1450, 1000)), W(2, new(1000, 100, 1350, 980))];
        var p = Plan(Context(), windows); session.Requested(windows, p, now); session.RejectExplicitly();
        var c = session.Capture(windows, new(), Context().Displays!, null, now);
        Assert.NotEmpty(c.RejectedLayouts!);
        Assert.Contains(Plan(c, windows).Groups.SelectMany(g => g.Candidates), t => t.Breakdown?.Feedback > 0);
    }

    [Fact]
    public void TopmostWindowRemainsAnAnchor()
    {
        var a = W(1, new(40, 60, 1400, 1000)) with { IsTopmost = true };
        var b = W(2, new(1000, 100, 1400, 1000));
        var p = Plan(Context(), a, b);
        Assert.Equal(a.VisualRect, Target(a, p)); Assert.Empty(p.Layers);
    }

    [Fact]
    public void PostprocessorCannotUndoChosenTaskGeometry()
    {
        WindowSnapshot[] windows = [W(1, new(50, 80, 1450, 1000)), W(2, new(1000, 100, 1350, 980))];
        var c = Context(); var p = Plan(c, windows);
        var bad = new[] { new TidyMove(windows[0], new(400, 400, 1500, 1100)) };
        Assert.False(IntentLayoutPlanner.TaskRefinementAcceptable(windows.Select(V).ToArray(), p, bad, new(), c, windows));
        Assert.True(IntentLayoutPlanner.TaskRefinementAcceptable(windows.Select(V).ToArray(), p, p.Moves, new(), c, windows));
    }
}
