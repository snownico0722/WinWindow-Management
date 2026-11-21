using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class ForegroundHistoryTests
{
    private static readonly RectI WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void FirstSettledTransitionKeepsTheSourceAccessibleAndExpires()
    {
        var windows = new[] { Window(1) with { IsForeground = false }, Window(2) with { IsForeground = true } };
        var history = new ForegroundHistory();
        history.Visit(1, 1, 1_000);
        history.Visit(2, 2, 700_000); // A document may have been open for longer than the history horizon.

        Assert.Equal((nint)0, history.Resolve(windows, 700_100).Companion);
        Assert.Equal(new LayoutFocus(2, 1, 1, "handoff"), history.Resolve(windows, 700_600));
        Assert.Equal((nint)0, history.Resolve(windows, 720_000).Companion);
    }

    [Fact]
    public void BriefForegroundBounceDoesNotEstablishWorkingPair()
    {
        var windows = new[] { Window(1), Window(2) };
        var history = Visits(1, 2);
        history.Visit(1, 1, 2_150);

        Assert.Equal((nint)0, history.Resolve(windows, 3_000).Companion);
    }

    [Fact]
    public void IndependentDocumentUsesItsOwnerWhileModalDialogSuspendsLayout()
    {
        var document = Window(2) with { Owner = 1, IsForeground = true };
        var owner = Window(1) with { IsForeground = false };
        var history = Visits(2);

        Assert.Equal(new LayoutFocus(2, 1, 0, "handoff"), history.Resolve([owner, document], 1_700));
        Assert.Equal((nint)0, history.Resolve([owner, document with { IsManageable = false }], 1_700).Main);
        Assert.False(WindowLayerSafety.CanReorder([owner, document], [document with { ZOrder = 0 }, owner]));
    }

    [Fact]
    public void RepeatedDirectSwitchesOutweighAnOccasionalInterruption()
    {
        var windows = new[] { Window(1), Window(2), Window(3) };
        var history = Visits(1, 2, 1, 2, 1, 3, 1, 2, 1);

        Assert.Equal(new LayoutFocus(1, 2, 6), history.Resolve(windows, 9_100));
    }

    [Fact]
    public void ComparableCompanionsAreLeftUnresolved()
    {
        var windows = new[] { Window(1), Window(2), Window(3) };
        var history = Visits(1, 2, 1, 3, 1, 2, 1, 3, 1);

        Assert.Equal((nint)0, history.Resolve(windows, 9_100).Companion);
    }

    [Fact]
    public void FrequentSwitchingToADifferentTasksHubDoesNotMakeItACompanion()
    {
        var windows = new[] { Window(1), Window(2), Window(3), Window(4) };
        var history = Visits(1, 2, 3, 2, 4, 2, 3, 2, 1);

        Assert.Equal((nint)0, history.Resolve(windows, 9_100).Companion);
    }

    [Fact]
    public void TwoOldSwitchesDoNotEstablishACurrentCompanion()
    {
        var windows = new[] { Window(1), Window(2) };
        var history = Visits(1, 2, 1);

        Assert.Equal((nint)0, history.Resolve(windows, 240_000).Companion);
    }

    [Fact]
    public void CurrentPairCanOutweighMoreSwitchesFromAnEarlierTask()
    {
        var windows = new[] { Window(1), Window(2), Window(3) };
        var history = Visits(1, 2, 1, 2, 1, 2, 1);
        history.Visit(3, 3, 300_000);
        history.Visit(1, 1, 301_000);
        history.Visit(3, 3, 302_000);
        history.Visit(1, 1, 303_000);

        Assert.Equal(new LayoutFocus(1, 3, 3), history.Resolve(windows, 303_100));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcludedWindowDoesNotInventDirectSwitches(bool retired)
    {
        var windows = new[] { Window(1), Window(2), Window(3) with { IsManageable = retired } };
        var history = Visits(1, 3, 2, 3, 1);
        if (retired) history.Retire(3);

        Assert.Equal((nint)0, history.Resolve(windows, 5_100).Companion);
    }

    private static ForegroundHistory Visits(params int[] handles)
    {
        var history = new ForegroundHistory();
        for (var i = 0; i < handles.Length; i++)
            history.Visit(handles[i], (uint)handles[i], (i + 1) * 1000);
        return history;
    }

    private static WindowSnapshot Window(int id) =>
        new(id, new(id * 100, 100, 800, 600), new(id * 100, 100, 800, 600),
            WorkArea, 1, default, true, id == 1, true, id, 96, (uint)id);
}
