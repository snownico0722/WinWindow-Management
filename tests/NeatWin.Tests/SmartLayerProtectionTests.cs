using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class SmartLayerProtectionTests
{
    [Theory]
    [InlineData(.20)]
    [InlineData(.64)]
    [InlineData(.80)]
    [InlineData(1.0)]
    public void ClickingOurUiDoesNotGrantPermissionToSurfaceTheCoveredWindow(double reportedVisibility)
    {
        var area = new RectI(0, 0, 2200, 1200);
        // Own settings/toolbar has focus, so neither external window is IsForeground.
        var a = new WindowSnapshot((nint)1, new(0, 80, 1600, 950), new(0, 80, 1600, 950),
            area, (nint)1, default, true, false, true, 0, 120, 1);
        var b = a with { Handle = (nint)2, ProcessId = 2, ZOrder = 1,
            VisualRect = new(1240, 160, 800, 850), OuterRect = new(1240, 160, 800, 850) };
        var visible = new[] { new VisibleWindow(a, a.VisualRect.Area, a.VisualRect.Area, 1),
            new VisibleWindow(b, (long)(b.VisualRect.Area * reportedVisibility), b.VisualRect.Area, reportedVisibility) };
        var plan = IntentLayoutPlanner.CreateDetailedPlan(visible, new(), desktop: [a, b],
            taskContext: new(Profile: new(AllowUsefulResize: false)));
        Assert.Empty(plan.Layers);
        Assert.Contains(plan.Groups.SelectMany(g => g.Candidates), c => c.Rejection == "layer-preserve-foreground-or-occluded");
        Assert.NotEmpty(plan.Moves); // Geometry is still allowed to improve without raising b.
    }

    [Theory]
    [InlineData(SmartTidyStrength.Gentle)]
    [InlineData(SmartTidyStrength.Balanced)]
    [InlineData(SmartTidyStrength.Assertive)]
    public void CompletelyCoveredWindowDoesNotEnterTheSmartWorkingSet(SmartTidyStrength strength)
    {
        var area = new RectI(0, 0, 2560, 1380);
        var a = new WindowSnapshot((nint)1, new(80, 80, 1300, 1100), new(80, 80, 1300, 1100),
            area, (nint)1, default, true, true, true, 0, 120, 1);
        var b = a with { Handle = (nint)2, ProcessId = 2, ZOrder = 1, IsForeground = false };
        var visible = new VisibilityAnalyzer().SelectVisibleWorkingSet([a, b], includeStackAccess: true);
        Assert.Single(visible);
        var plan = IntentLayoutPlanner.CreateDetailedPlan(visible, new(SmartStrength: strength), desktop: [a, b]);
        Assert.DoesNotContain(plan.Moves, m => m.Window.Handle == b.Handle);
        Assert.DoesNotContain(plan.Layers.SelectMany(l => l.FrontToBack), w => w.Handle == b.Handle);
    }
}
