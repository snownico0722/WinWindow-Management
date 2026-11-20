using NeatWin.Core;

namespace NeatWin.Tests;

// Synthetic, behavioral regressions. No private recorder coordinates or target-layout labels.
public sealed class TaskRobustnessRegressionTests
{
    private static readonly RectI Area = new(0, 0, 2400, 1400);
    private static WindowSnapshot W(int id, RectI r, RectI? area = null, uint dpi = 120) =>
        new((nint)id, r, r, area ?? Area, (nint)1, default, true, id == 1, true, id, dpi, (uint)id);
    private static IntentLayoutPlan Plan(WindowSnapshot[] windows, TaskLayoutContext? context = null) =>
        IntentLayoutPlanner.CreateDetailedPlan(windows.Select(w => new VisibleWindow(w, w.VisualRect.Area,
            w.VisualRect.Area, 1)).ToArray(), new(), desktop: windows, taskContext: context);
    private static RectI Target(WindowSnapshot w, IntentLayoutPlan p) =>
        p.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect;
    private static int Difference(RectI a, RectI b) => new[] { Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y),
        Math.Abs(a.Width - b.Width), Math.Abs(a.Height - b.Height) }.Max();

    [Fact]
    public void CrossingTheOldGroupingRadiusDoesNotTurnACoherentPairIntoSingletons()
    {
        WindowSnapshot[] a = [W(1, new(831, 160, 1400, 920)), W(2, new(160, 220, 420, 800))];
        WindowSnapshot[] b = [a[0] with { VisualRect = a[0].VisualRect with { X = 834, Y = 157 } }, a[1]];
        var p = Plan(a); var q = Plan(b);
        Assert.Equal(p.Groups.Select(g => g.Handles.Length), q.Groups.Select(g => g.Handles.Length));
        for (var i = 0; i < a.Length; i++) Assert.InRange(Difference(Target(a[i], p), Target(b[i], q)), 0, 12);
    }

    [Fact]
    public void ExplicitTaskRelationshipIsNotDiscardedByTheLocalSearchRadius()
    {
        WindowSnapshot[] windows = [W(1, new(50, 100, 650, 700)), W(2, new(1650, 100, 650, 700))];
        var context = new TaskLayoutContext(PairHints: [new((nint)1, (nint)2, TaskRelation.Integrated)]);
        var p = Plan(windows, context);
        var relation = Assert.Single(Assert.Single(p.Groups).Task!.Relations);
        Assert.Equal(1, relation.Integrated);
        Assert.Equal("explicit-task-hint", relation.Source);
    }

    [Fact]
    public void EquivalentDipScenesKeepTheirWorkingGroupAcrossThePixelCap()
    {
        var area = new RectI(0, 0, 2000, 1100);
        WindowSnapshot[] a = [W(1, new(620, 100, 1280, 850), area), W(2, new(60, 140, 370, 780), area)];
        RectI Scale(RectI r) => new((int)(r.X * 1.5), (int)(r.Y * 1.5), (int)(r.Width * 1.5), (int)(r.Height * 1.5));
        var b = a.Select(w => w with { VisualRect = Scale(w.VisualRect), OuterRect = Scale(w.OuterRect),
            WorkArea = Scale(area), Dpi = 180 }).ToArray();
        var p = Plan(a); var q = Plan(b);
        Assert.Equal(p.Groups.Select(g => g.Handles.Length), q.Groups.Select(g => g.Handles.Length));
        for (var i = 0; i < a.Length; i++)
            Assert.InRange(Difference(Scale(Target(a[i], p)), Target(b[i], q)), 0, 4);
    }

    [Fact]
    public void ProtectPeripheryDoesNotTradeAnExposedLeftEdgeForMoreTotalArea()
    {
        var area = new RectI(0, 0, 2560, 1380);
        WindowSnapshot[] windows = [W(1, new(220, 60, 2080, 1220), area), W(2, new(0, 0, 2560, 1440), area)];
        var p = Plan(windows, new(PairHints: [new((nint)1, (nint)2, TaskRelation.JointView)],
            WindowHints: [new((nint)1, ProtectPeriphery: true), new((nint)2, ProtectPeriphery: true)]));
        var order = p.Layers.Count == 0 ? windows : p.Layers[0].FrontToBack;
        foreach (var w in windows)
        {
            var beforeBlockers = windows.TakeWhile(v => v.Handle != w.Handle).Select(v => v.VisualRect);
            var afterBlockers = order.TakeWhile(v => v.Handle != w.Handle).Select(v => Target(v, p));
            var a = w.VisualRect; var b = Target(w, p);
            var edgeA = new RectI(a.X, a.Y, (int)Math.Round(a.Width * .12), a.Height);
            var edgeB = new RectI(b.X, b.Y, (int)Math.Round(b.Width * .12), b.Height);
            var before = IntentLayoutPlanner.ScreenExposure(edgeA, beforeBlockers, area, w.Dpi).Visible;
            var after = IntentLayoutPlanner.ScreenExposure(edgeB, afterBlockers, area, w.Dpi).Visible;
            Assert.True(after + .005 >= before, $"Left edge regressed from {before} to {after}");
        }
        Assert.NotEmpty(p.Moves); // Protection must still permit useful improvement here.
    }
}
