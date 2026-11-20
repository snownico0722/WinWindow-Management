using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class ForegroundHistoryTests
{
    private static readonly RectI WorkArea = new(0, 0, 1920, 1040);

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
