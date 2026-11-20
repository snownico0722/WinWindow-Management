using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class SmartStrengthTaskTests
{
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
