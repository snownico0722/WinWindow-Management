using NeatWin.Core;
using NeatWin.Windows;
using Xunit;

namespace NeatWin.Tests;

public sealed class OfficeSurfaceProbeTests
{
    [Fact]
    public void StalledProviderLeavesOneSlotForAnotherWindowAndCannotPublishAfterRetirement()
    {
        var queries = new List<TaskCompletionSource<FocusedOfficeSurface>>();
        var probe = new OfficeSurfaceProbe(_ =>
        {
            var work = new TaskCompletionSource<FocusedOfficeSurface>();
            queries.Add(work);
            return work.Task;
        });
        ForegroundReading first = new(1, 2, 10, false, 55), second = new(3, 4, 20, false, 65);
        probe.Sample(first, 100);
        probe.Sample(second, 200);
        Assert.Single(queries);
        probe.Sample(second, 1700);
        probe.Sample(second with { Root = 7 }, 3500);
        Assert.Equal(2, queries.Count);
        queries[1].SetResult(FocusedOfficeSurface.Grid);
        Assert.Equal(FocusedOfficeSurface.Grid, probe.Sample(second, 3600));
        probe.Retire(first.Root);
        queries[0].SetResult(FocusedOfficeSurface.Canvas);
        Assert.Equal(FocusedOfficeSurface.Unknown, probe.Sample(first, 3720));
    }

    [Fact]
    public void OnlyAnUnchangedInitialInputSurfaceAllowsFirstPlacement()
    {
        ForegroundReading start = new(1, 2, 3, true, 42);
        Assert.True(start.AllowsInitialPlacement(start));
        Assert.False(start.AllowsInitialPlacement(start with { LastInput = 43 }));
        Assert.False(start.AllowsInitialPlacement(start with { Focus = 5 }));
        Assert.False((start with { Caret = false }).AllowsInitialPlacement(start));
        Assert.False(start.AllowsInitialPlacement(start with { LastInput = null }));
    }

    [Fact]
    public void SparseSamplesReuseTheSameInputTargetButNeverCarryItsSurfaceAcrossInputOrRetirement()
    {
        var queries = new List<TaskCompletionSource<FocusedOfficeSurface>>();
        var probe = new OfficeSurfaceProbe(_ =>
        {
            var next = new TaskCompletionSource<FocusedOfficeSurface>();
            queries.Add(next);
            return next.Task;
        });
        ForegroundReading first = new(7, 12, 66, false, 42);
        Assert.Equal(FocusedOfficeSurface.Unknown, probe.Sample(first, 100));
        Assert.True(probe.JustStarted(first.Root, 150));
        Assert.False(probe.JustStarted(first.Root, 1000));
        queries[0].SetResult(FocusedOfficeSurface.Grid);
        Assert.Equal(FocusedOfficeSurface.Grid, probe.Sample(first, 5100));
        Assert.Equal(2, queries.Count);
        var nextInput = first with { LastInput = 44 };
        Assert.Equal(FocusedOfficeSurface.Unknown, probe.Sample(nextInput, 5110));
        queries[1].SetResult(FocusedOfficeSurface.Grid);
        Assert.Equal(FocusedOfficeSurface.Unknown, probe.Sample(nextInput, 5200));
        queries[2].SetResult(FocusedOfficeSurface.Canvas);
        Assert.Equal(FocusedOfficeSurface.Canvas, probe.Sample(nextInput, 5300));
        probe.Retire(first.Root);
        Assert.Equal(FocusedOfficeSurface.Unknown, probe.Sample(nextInput, 5400));
    }
}
