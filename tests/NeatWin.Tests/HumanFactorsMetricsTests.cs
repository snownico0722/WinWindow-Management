using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class HumanFactorsMetricsTests
{
    [Fact]
    public void PointerAcquisition_CurrentWindowIsEasiest()
    {
        var target = new RectI(300, 200, 600, 500);

        var inside = HumanFactorsMetrics.PointerAcquisitionQuality(new PointI(450, 350), target);
        var nearby = HumanFactorsMetrics.PointerAcquisitionQuality(new PointI(220, 350), target);
        var far = HumanFactorsMetrics.PointerAcquisitionQuality(new PointI(1700, 350), target);

        Assert.Equal(1.0, inside);
        Assert.True(nearby > far);
    }

    [Fact]
    public void PointerAcquisition_LargerTargetIsEasierAtSameDistance()
    {
        var pointer = new PointI(100, 400);
        var small = new RectI(900, 350, 220, 120);
        var large = new RectI(900, 250, 700, 500);

        var smallQuality = HumanFactorsMetrics.PointerAcquisitionQuality(pointer, small);
        var largeQuality = HumanFactorsMetrics.PointerAcquisitionQuality(pointer, large);

        Assert.True(largeQuality > smallQuality);
    }

    [Fact]
    public void VisualLocality_PrefersContentCloserToCurrentInteractionLocus()
    {
        var workArea = new RectI(0, 0, 5120, 1440);
        var pointer = new PointI(900, 700);
        var near = new RectI(500, 250, 1100, 900);
        var far = new RectI(3900, 250, 1100, 900);

        Assert.True(
            HumanFactorsMetrics.VisualLocalityQuality(pointer, near, workArea) >
            HumanFactorsMetrics.VisualLocalityQuality(pointer, far, workArea));
    }
}
