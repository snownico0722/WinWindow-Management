using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class SmartStrengthTaskTests
{
    private static readonly RectI Area = new(0, 0, 1800, 980);

    [Fact]
    public void AssertiveFitsACrowdedMixedSizeDesktopBeyondTheLocalSearch()
    {
        WindowSnapshot[] windows = [
            Window(1, new(230, 150, 800, 600)) with { IsResizable = false },
            Window(2, new(250, 175, 780, 550)),
            Window(3, new(260, 200, 700, 510)),
        ];
        var balanced = Arranged(windows, SmartTidyStrength.Balanced);
        var assertive = Arranged(windows, SmartTidyStrength.Assertive);

        Assert.Equal(Size(windows[0]), Size(assertive[0]));
        Assert.True(Overlap(assertive) < Overlap(balanced));
    }

    [Fact]
    public void GentleRestoresAccessWhileBalancedCanSeparateTheWholePair()
    {
        WindowSnapshot[] windows = [Window(1, new(400, 160, 740, 580)), Window(2, new(400, 160, 740, 580))];
        var gentle = Arranged(windows, SmartTidyStrength.Gentle);
        var balanced = Arranged(windows, SmartTidyStrength.Balanced);
        Assert.Equal(windows.Select(Size), gentle.Select(Size));
        Assert.InRange(Overlap(gentle), 1, Overlap(windows) - 1);
        Assert.Equal(0, Overlap(balanced));
    }

    [Theory]
    [InlineData(SmartTidyStrength.Gentle)]
    [InlineData(SmartTidyStrength.Balanced)]
    [InlineData(SmartTidyStrength.Assertive)]
    public void AnAlignedDeskAndAnUnobstructedLoneWindowDoNotNeedAnotherLayout(SmartTidyStrength strength)
    {
        var first = Window(1, new(90, 80, 650, 580));
        WindowSnapshot[] windows = [first, Window(2, new(748, 80, 650, 580))];
        Assert.Equal(windows, Arranged(windows, strength));
        Assert.Equal(new[] { first }, Arranged([first], strength));
    }

    [Fact]
    public void SameStackKeepsDifferentLayoutsWhenTaskEvidenceChanges()
    {
        WindowSnapshot[] windows = [Window(1, new(480, 200, 720, 540)), Window(2, new(450, 140, 720, 540))];
        TaskLayoutContext Task(TaskRelation relation) => new(ExplicitArrange: true, PairHints: [new(1, 2, relation)]);
        var parked = Arranged(windows, SmartTidyStrength.Balanced, Task(TaskRelation.Alternating));
        var reading = Arranged(windows, SmartTidyStrength.Balanced, Task(TaskRelation.JointView));
        Assert.True(Overlap(parked) > 0);
        Assert.Equal(0, Overlap(reading));
    }

    [Fact]
    public void BothFullDesktopTriggersCanRevealAnOrdinaryCoveredWindow()
    {
        WindowSnapshot[] windows = [Window(1, new(400, 160, 740, 580)), Window(2, new(400, 160, 740, 580))];
        var manual = Arranged(windows, SmartTidyStrength.Balanced, new(ExplicitArrange: true, VerifiedRepeat: true));
        var automatic = Arranged(windows, SmartTidyStrength.Balanced, new(UseWorkingPairs: true));
        Assert.Equal(0, Overlap(manual));
        Assert.Equal(0, Overlap(automatic));
        var undo = LayoutUndoPlanner.Create(Plan(windows, SmartTidyStrength.Balanced, new(ExplicitArrange: true)), manual);
        Assert.Equal(windows, TaskLayoutSession.Project(manual, undo));
    }

    private static WindowSnapshot Window(int id, RectI rect) =>
        new(id, rect, rect, Area, 1, default, true, id == 1, true, id);

    private static (int Width, int Height) Size(WindowSnapshot w) => (w.VisualRect.Width, w.VisualRect.Height);

    private static long Overlap(WindowSnapshot[] windows) =>
        windows.SelectMany((a, i) => windows.Skip(i + 1).Select(b => a.VisualRect.Intersect(b.VisualRect).Area)).Sum();

    private static IntentLayoutPlan Plan(WindowSnapshot[] windows, SmartTidyStrength strength, TaskLayoutContext context) =>
        IntentLayoutPlanner.CreateDetailedPlan(new VisibilityAnalyzer().SelectForManualArrange(windows, true),
            new(SmartStrength: strength), desktop: windows, taskContext: context);

    private static WindowSnapshot[] Arranged(WindowSnapshot[] windows, SmartTidyStrength strength, TaskLayoutContext? context = null) =>
        TaskLayoutSession.Project(windows, Plan(windows, strength, context ?? new(ExplicitArrange: true)));

    [Fact]
    public void StrongerGeometryDoesNotEraseExplicitParkedTaskResistance()
    {
        var adaptation = new TaskCostBreakdown(0, 0, .6, .4, 0, 0, 0, 0);
        var joint = new[] { new TaskEvidence(0, 1, 1, 0, 0, 0, false, "explicit-task-hint") };
        var parked = new[] { new TaskEvidence(0, 1, 0, 1, 0, 0, false, "explicit-task-hint") };

        Assert.True(IntentLayoutPlanner.TaskScore(adaptation, SmartTidyStrength.Balanced, joint) <
            IntentLayoutPlanner.TaskScore(adaptation, SmartTidyStrength.Balanced, parked));
        Assert.True(IntentLayoutPlanner.SelectionThreshold(SmartTidyStrength.Balanced, joint) <
            IntentLayoutPlanner.SelectionThreshold(SmartTidyStrength.Balanced, parked));
        Assert.Equal(.065, IntentLayoutPlanner.SelectionThreshold(SmartTidyStrength.Balanced, parked), 6);
    }

    [Fact]
    public void UncertainGeometryDoesNotPretendToBeAConfidentParkedTask()
    {
        var adaptation = new TaskCostBreakdown(0, 0, .5, .5, 0, 0, 0, 0);
        var uncertain = new[] { new TaskEvidence(0, 1, .15, .85, 0, .40, false, "geometry-hypotheses") };
        var concurrent = new[] { new TaskEvidence(0, 1, .85, .15, 0, .25, false, "geometry-hypotheses") };

        Assert.Equal(.03, IntentLayoutPlanner.SelectionThreshold(SmartTidyStrength.Balanced, uncertain), 6);
        Assert.Equal(IntentLayoutPlanner.TaskScore(adaptation, SmartTidyStrength.Balanced, concurrent),
            IntentLayoutPlanner.TaskScore(adaptation, SmartTidyStrength.Balanced, uncertain), 6);
    }
}
