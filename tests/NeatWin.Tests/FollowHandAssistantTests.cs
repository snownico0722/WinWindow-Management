using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class FollowHandAssistantTests
{
    private static readonly RectI WorkArea = new(0, 0, 1920, 1080);

    [Fact]
    public void AutoPersonalization_PairwiseCorrectionMovesBiasTowardCorrectedArchetype()
    {
        var chosen = new SmartLearningSnapshot(
            AutoLayoutArchetype.EqualColumns,
            Enumerable.Repeat(0.0, SmartPersonalizationState.AutoFeatureCount).ToArray(),
            new Dictionary<nint, RectI>());
        var correctedFeatures = Enumerable.Repeat(0.0, SmartPersonalizationState.AutoFeatureCount).ToArray();
        correctedFeatures[6] = 0.8;
        correctedFeatures[7] = 0.9;

        var learned = SmartPersonalizationLearner.LearnAutoCorrection(
            SmartPersonalizationState.Default,
            chosen,
            AutoLayoutArchetype.FocusLeft,
            correctedFeatures,
            confidence: 1.0);

        Assert.True(learned.AutoArchetypeBias[(int)AutoLayoutArchetype.FocusLeft] > 0);
        Assert.True(learned.AutoArchetypeBias[(int)AutoLayoutArchetype.EqualColumns] < 0);
        Assert.True(learned.AutoFeatureWeights[6] > 0);
        Assert.True(learned.AutoFeatureWeights[7] > 0);
        Assert.Equal(1, learned.AutoCorrectionSamples);
    }

    [Fact]
    public void FollowHand_SlowReleaseNearScreenEdgeFinishesTheLastPixels()
    {
        var moved = Window(1, new RectI(13, 180, 600, 520), 0, foreground: true);
        var gesture = Gesture(
            moved.Handle,
            new RectI(240, 180, 600, 520),
            moved.VisualRect,
            ManualGestureKind.Move,
            endSpeed: 45,
            [
                new PointerMotionSample(new PointI(300, 240), 0),
                new PointerMotionSample(new PointI(95, 240), 240),
                new PointerMotionSample(new PointI(28, 240), 400),
                new PointerMotionSample(new PointI(18, 240), 500),
            ]);

        var decision = FollowHandAssistant.Decide(
            gesture,
            [Visible(moved)],
            new TidyOptions(SmartStrength: SmartTidyStrength.Balanced),
            SmartInteractionContext.Empty,
            SmartPersonalizationState.Default);

        Assert.True(decision.ShouldApply);
        Assert.Equal(FollowHandTargetKind.ScreenEdge, decision.TargetKind);
        Assert.Equal(0, decision.TargetRect.Left);
        Assert.Equal(moved.VisualRect.Width, decision.TargetRect.Width);
        Assert.Equal(moved.VisualRect.Height, decision.TargetRect.Height);
    }

    [Fact]
    public void FollowHand_FastFlyThroughDoesNotMagnetizeToScreenEdge()
    {
        var moved = Window(1, new RectI(13, 180, 600, 520), 0, foreground: true);
        var gesture = Gesture(
            moved.Handle,
            new RectI(900, 180, 600, 520),
            moved.VisualRect,
            ManualGestureKind.Move,
            endSpeed: 6500,
            [
                new PointerMotionSample(new PointI(1200, 240), 0),
                new PointerMotionSample(new PointI(600, 240), 35),
                new PointerMotionSample(new PointI(30, 240), 70),
            ]);

        var decision = FollowHandAssistant.Decide(
            gesture,
            [Visible(moved)],
            new TidyOptions(SmartStrength: SmartTidyStrength.Balanced),
            SmartInteractionContext.Empty,
            SmartPersonalizationState.Default);

        Assert.False(decision.ShouldApply);
        Assert.Equal(moved.VisualRect, decision.TargetRect);
    }

    [Fact]
    public void FollowHand_LearnsPreferredGapFromRawMouseUpNotAssistedTarget()
    {
        var moved = Window(1, new RectI(186, 200, 600, 500), 0, foreground: true);
        var neighbor = Window(2, new RectI(800, 200, 600, 500), 1);
        var gesture = Gesture(
            moved.Handle,
            new RectI(130, 200, 600, 500),
            moved.VisualRect,
            ManualGestureKind.Move,
            endSpeed: 80,
            []);

        var learned = FollowHandAssistant.LearnFromRawGesture(
            SmartPersonalizationState.Default,
            gesture,
            [Visible(moved), Visible(neighbor)]);

        Assert.True(learned.PreferredGapPixels > SmartPersonalizationState.Default.PreferredGapPixels);
        Assert.True(learned.PreferredGapPixels < 14.1);
        Assert.True(learned.FollowTargetBias[(int)FollowHandTargetKind.NeighborGap] > 0);
        Assert.Equal(1, learned.FollowGestureSamples);
    }

    [Fact]
    public void FollowHand_PersonalGapChangesThePredictedEndpoint()
    {
        var moved = Window(1, new RectI(174, 200, 600, 500), 0, foreground: true);
        var neighbor = Window(2, new RectI(800, 200, 600, 500), 1);
        var gesture = Gesture(
            moved.Handle,
            new RectI(130, 200, 600, 500),
            moved.VisualRect,
            ManualGestureKind.Move,
            endSpeed: 60,
            [
                new PointerMotionSample(new PointI(300, 250), 0),
                new PointerMotionSample(new PointI(230, 250), 180),
                new PointerMotionSample(new PointI(180, 250), 360),
            ]);
        var personal = SmartPersonalizationState.Default with
        {
            PreferredGapPixels = 20,
            FollowGestureSamples = 80,
            FollowTargetBias = Bias(FollowHandTargetKind.NeighborGap, 0.8),
        };

        var decision = FollowHandAssistant.Decide(
            gesture,
            [Visible(moved), Visible(neighbor)],
            new TidyOptions(SmartStrength: SmartTidyStrength.Balanced),
            SmartInteractionContext.Empty,
            personal);

        Assert.True(decision.ShouldApply);
        Assert.Equal(FollowHandTargetKind.NeighborGap, decision.TargetKind);
        Assert.Equal(20, neighbor.VisualRect.Left - decision.TargetRect.Right);
    }

    private static ManualWindowGesture Gesture(
        nint handle,
        RectI start,
        RectI end,
        ManualGestureKind kind,
        double endSpeed,
        IReadOnlyList<PointerMotionSample> samples) =>
        new(handle, start, end, WorkArea, kind, samples, 500, endSpeed);

    private static double[] Bias(FollowHandTargetKind kind, double value)
    {
        var result = new double[SmartPersonalizationState.FollowTargetCount];
        result[(int)kind] = value;
        return result;
    }

    private static WindowSnapshot Window(
        int id,
        RectI rect,
        int zOrder,
        bool foreground = false) =>
        new(
            (nint)id,
            rect,
            rect,
            WorkArea,
            (nint)1,
            new FrameInsets(0, 0, 0, 0),
            IsResizable: true,
            IsForeground: foreground,
            IsManageable: true,
            ZOrder: zOrder);

    private static VisibleWindow Visible(WindowSnapshot window) =>
        new(window, window.VisualRect.Area, window.VisualRect.Area, 1.0);

    private static RectI TargetFor(IReadOnlyList<TidyMove> plan, WindowSnapshot window) =>
        plan.FirstOrDefault(move => move.Window.Handle == window.Handle)?.TargetVisualRect ?? window.VisualRect;
}
