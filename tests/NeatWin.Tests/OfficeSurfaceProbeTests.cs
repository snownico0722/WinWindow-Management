using NeatWin.Core;
using NeatWin.Windows;
using Xunit;

namespace NeatWin.Tests;

public sealed class OfficeSurfaceProbeTests
{
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
