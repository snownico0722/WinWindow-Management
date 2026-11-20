using System.Text.Json;
using NeatWin.Core;
using NeatWin.Reference;

namespace NeatWin.Tests;

public sealed class OcclusionIntentTests
{
    private static readonly RectI Area = new(0, 0, 2560, 1380);
    private static WindowSnapshot W(int id, RectI rect, RectI? area = null) =>
        new((nint)id, rect, rect, area ?? Area, (nint)1, default, true, id == 1, true, id, 120, (uint)id);
    private static VisibleWindow V(WindowSnapshot w) => new(w, w.VisualRect.Area, w.VisualRect.Area, 1);
    private static IntentLayoutPlan Plan(params WindowSnapshot[] windows) =>
        IntentLayoutPlanner.CreateDetailedPlan(windows.Select(V).ToArray(), new TidyOptions(), desktop: windows);
    private static RectI Target(WindowSnapshot w, IntentLayoutPlan p) =>
        p.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect;

    [Fact]
    public void WidthPressureCanUseEdgeOverlapWhenResizeIsDisabled()
    {
        var a = W(1, new(50, 80, 1400, 1120)); var b = W(2, new(920, 190, 1300, 1100));
        // v3 may choose useful resizing by default. This regression isolates the still-supported
        // no-resize alternative rather than forcing an obsolete family name for every task.
        var p = IntentLayoutPlanner.CreateDetailedPlan([V(a), V(b)], new(), desktop: [a, b],
            taskContext: new(Profile: new(AllowUsefulResize: false)));
        Assert.Contains(p.Groups.SelectMany(g => g.Candidates), c => c.Kind == "edge-row" && c.Rejection is null);
        Assert.NotEmpty(p.Moves);
        Assert.Equal(a.VisualRect.Width, Target(a, p).Width);
        Assert.Equal(b.VisualRect.Width, Target(b, p).Width);
        Assert.True(Target(a, p).Intersect(Target(b, p)).Area > 0);
        Assert.True(Target(a, p).Intersect(Target(b, p)).Area < a.VisualRect.Intersect(b.VisualRect).Area);
    }

    [Fact]
    public void PartiallyOccludedBackgroundWindowIsNotPromotedOverForeground()
    {
        var area = new RectI(0, 0, 2200, 1200);
        var a = W(1, new(0, 80, 1600, 950), area);
        var b = W(2, new(1240, 160, 800, 850), area);
        var visible = new[]
        {
            new VisibleWindow(a, a.VisualRect.Area, a.VisualRect.Area, 1),
            new VisibleWindow(b, (long)(b.VisualRect.Area * .42), (long)(b.VisualRect.Area * .42), .42),
        };
        var p = IntentLayoutPlanner.CreateDetailedPlan(visible, new(), desktop: [a, b],
            taskContext: new(Profile: new(AllowUsefulResize: false)));
        Assert.Contains(p.Groups.SelectMany(g => g.Candidates), c => c.Kind == "edge-row" && c.Rejection is null);
        Assert.Empty(p.Layers);
        Assert.True(a.IsForeground); Assert.False(b.IsForeground);
    }

    [Fact]
    public void CompactAccessibleStackIsNotForcedIntoFullscreenColumns()
    {
        var a = W(1, new(500, 260, 1000, 800)); var b = W(2, new(450, 210, 1000, 800));
        var p = Plan(a, b);
        Assert.True(Target(a, p).Intersect(Target(b, p)).Area > 0);
        Assert.All(new[] { a, b }, w => Assert.Equal(w.VisualRect.Width, Target(w, p).Width));
        Assert.All(p.Moves, m => Assert.InRange(Math.Abs(m.TargetVisualRect.X - m.Window.VisualRect.X), 0, 160));
    }

    [Fact]
    public void CrowdedThreeWindowGroupHasAnActualStackCandidate()
    {
        var p = Plan(W(1, new(1120, 240, 1320, 1090)), W(2, new(540, 100, 1280, 1190)), W(3, new(0, 80, 1380, 1200)));
        Assert.Contains(p.Groups.SelectMany(g => g.Candidates), c => c.Kind == "stack" && c.Rejection is null);
        Assert.Contains(p.Groups.SelectMany(g => g.Candidates), c => c.Kind is "columns" or "grid" && c.Rejection is null);
        Assert.NotEmpty(p.Moves);
    }

    [Fact]
    public void ExplicitSmartIncludesAnExposedTopBandButNotFullyCoveredWindows()
    {
        var a = W(1, new(450, 250, 1000, 800));
        var b = W(2, new(450, 210, 1000, 800));
        var hidden = W(3, a.VisualRect);
        var analyzer = new VisibilityAnalyzer();
        Assert.Single(analyzer.SelectVisibleWorkingSet([a, b, hidden]));
        var visible = analyzer.SelectVisibleWorkingSet([a, b, hidden], includeStackAccess: true);
        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(visible, v => v.Window.Handle == hidden.Handle);
    }

    [Fact]
    public void UnionOcclusionDoesNotDoubleCountIdenticalBlockers()
    {
        var rect = new RectI(0, 0, 1000, 800); var blocker = new RectI(800, 0, 300, 800);
        Assert.Equal(OcclusionMetrics.Measure(rect, [blocker], 96), OcclusionMetrics.Measure(rect, [blocker, blocker], 96));
        Assert.Equal(0.8, OcclusionMetrics.Measure(rect, [blocker], 96).Visible, 5);
    }

    [Fact]
    public void EmptyAndFullyCoveredWindowsHaveNoInventedAccessRegion()
    {
        Assert.Equal(0, OcclusionMetrics.Measure(default, [], 96).Visible);
        var rect = new RectI(0, 0, 1000, 800);
        var exposure = OcclusionMetrics.Measure(rect, [rect], 96);
        Assert.Equal(0, exposure.AccessWidth); Assert.Equal(0, exposure.UsefulWidth);
    }

    [Fact]
    public void LayerSafetyRejectsUnrelatedInterleavingAndTopmostBands()
    {
        var a = W(1, new(0, 50, 1300, 900)); var b = W(3, new(1100, 50, 1300, 900));
        var unrelated = W(2, new(2000, 0, 300, 400));
        Assert.False(WindowLayerSafety.CanReorder([b, a], [a, unrelated, b]));
        Assert.False(WindowLayerSafety.CanReorder([b, a], [a with { IsTopmost = true }, b]));
        Assert.False(WindowLayerSafety.CanReorder([b, a], [a with { ProcessId = 99 }, b]));
        Assert.True(WindowLayerSafety.CanReorder([b, a], [a, b]));
    }

    [Fact]
    public void ObserverRetiresDestroyedHandlesAndNeverPersistsNativeIdentity()
    {
        var tracker = new ObservationIdentityTracker(); var a = W(1, new(0, 0, 900, 700));
        var old = tracker.Id(a); tracker.Retire(a.Handle);
        Assert.NotEqual(old, tracker.Id(a));
        var json = JsonSerializer.Serialize(tracker.Capture([a]));
        Assert.DoesNotContain("Process", json); Assert.DoesNotContain("Handle", json); Assert.DoesNotContain("Title", json);
    }

    [Fact]
    public void ContextDistinguishesForegroundLayerAndGeometryChanges()
    {
        var tracker = new ObservationIdentityTracker(); var a = W(1, new(0, 0, 900, 700));
        var frame = tracker.Capture([a]);
        Assert.True(ObservationIdentityTracker.SameState(frame, tracker.Capture([a])));
        Assert.False(ObservationIdentityTracker.SameState(frame, tracker.Capture([a with { IsForeground = false }])));
        Assert.False(ObservationIdentityTracker.SameState(frame, tracker.Capture([a with { ZOrder = 5 }])));
    }

    [Fact]
    public void OverlappingGestureCanEnterNewReferenceButOldLogsCannotInventLayerHistory()
    {
        var observation = OverlapObservation();
        Assert.NotNull(IntentEvidence.ExtractRelation(observation));
        Assert.Null(IntentEvidence.ExtractRelation(observation with { Version = 1, StartContext = null }));
        Assert.Null(IntentEvidence.ExtractRelation(observation with { Outcome = "automation" }));
        Assert.Null(IntentEvidence.ExtractRelation(observation with { StartContext = observation.StartContext! with { Truncated = true } }));
    }

    [Fact]
    public void MovingNeighborIsNotAStationaryReference()
    {
        var o = OverlapObservation();
        var before = o.StartContext! with { Windows = o.StartContext.Windows.Select(w => w.Id == 2 ? w with { Rect = w.Rect with { X = 600 } } : w).ToArray() };
        Assert.Null(IntentEvidence.ExtractRelation(o with { StartContext = before }));
    }

    [Fact]
    public void NeighborMovingDuringSettlementDoesNotBecomePreference()
    {
        var o = OverlapObservation();
        var settled = o.SettledContext! with { Windows = o.SettledContext.Windows.Select(w => w.Id == 2 ? w with { Rect = w.Rect with { X = 600 } } : w).ToArray() };
        Assert.Null(IntentEvidence.ExtractRelation(o with { SettledContext = settled }));
    }

    [Fact]
    public void RelationReferenceRoundTripsWithoutReplacingExistingGapSamples()
    {
        var now = DateTimeOffset.UtcNow;
        var document = new IntentReferenceDocument(1, [new(now.AddMinutes(-1), "standard", 8, true)]);
        document = IntentEvidence.AddRelation(document, new(now, "standard", 2, "edge-overlap", 0.12), now);
        var roundtrip = IntentEvidence.Normalize(JsonSerializer.Deserialize<IntentReferenceDocument>(JsonSerializer.Serialize(document)), now);
        Assert.Single(roundtrip.Samples); Assert.Single(roundtrip.Relations!);
        Assert.Equal(0, IntentEvidence.Resolve(roundtrip, Area, 120, now).StackPreference);
    }

    [Fact]
    public void RandomSmallGroupsStayInTheirMonitorAndRetainFixedSizes()
    {
        var random = new Random(61423);
        for (var sample = 0; sample < 150; sample++)
        {
            var area = sample % 2 == 0 ? Area : Area with { X = -2560, Y = -100 };
            var windows = Enumerable.Range(1, random.Next(2, 6)).Select(i => W(i,
                new RectI(area.X + random.Next(0, 1600), area.Y + random.Next(0, 300), random.Next(600, 950), random.Next(700, 1050)), area)
                with { IsResizable = i % 2 == 0 }).ToArray();
            var p = Plan(windows);
            foreach (var w in windows)
            {
                var target = Target(w, p);
                Assert.Equal(target.Area, target.Intersect(area).Area);
                if (!w.IsResizable) { Assert.Equal(w.VisualRect.Width, target.Width); Assert.Equal(w.VisualRect.Height, target.Height); }
            }
        }
    }

    private static WindowAdjustmentObservation OverlapObservation()
    {
        var now = DateTimeOffset.UtcNow;
        var beforeRect = new RectI(200, 80, 1000, 850); var afterRect = new RectI(450, 80, 1000, 850);
        var peer = new ObservedWindow(2, new(1300, 80, 1000, 850), Area, 120, 1, false, false, true, 1);
        WorkspaceFrame Frame(RectI r) => new(now, [new(1, r, Area, 120, 0, true, false, true, 1), peer]);
        return new(now, "synthetic", 1, beforeRect, afterRect, Area, Area, 120, 600, [peer.Rect], "stable",
            2, Frame(beforeRect), Frame(afterRect), Frame(afterRect));
    }
}
