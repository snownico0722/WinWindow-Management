namespace NeatWin.Core;

public readonly record struct PointI(int X, int Y);

public enum ManualGestureKind
{
    Move,
    Resize,
    MoveAndResize,
}

public enum AutoLayoutArchetype
{
    Preserve,
    CleanCurrent,
    EqualColumns,
    EqualRows,
    Grid,
    FocusLeft,
    FocusRight,
    FocusTop,
    FocusBottom,
}

public enum FollowHandTargetKind
{
    None,
    ScreenEdge,
    NeighborGap,
    EdgeAlignment,
    SizeMatch,
    WorkAreaRatio,
}

public sealed record PointerMotionSample(
    PointI Point,
    long ElapsedMilliseconds);

public sealed record ManualWindowGesture(
    nint WindowHandle,
    RectI StartRect,
    RectI EndRect,
    RectI WorkArea,
    ManualGestureKind Kind,
    IReadOnlyList<PointerMotionSample> PointerSamples,
    long DurationMilliseconds,
    double EndSpeedPixelsPerSecond);

public sealed record WindowAttentionSignal(
    nint WindowHandle,
    double Attention,
    double RecentClick,
    double Dwell,
    double RecentManipulation,
    double CursorProximity);

public sealed record SmartInteractionContext(
    nint ForegroundWindow,
    PointI Cursor,
    IReadOnlyList<WindowAttentionSignal> WindowSignals,
    ManualWindowGesture? LastGesture = null)
{
    public static SmartInteractionContext Empty { get; } =
        new(nint.Zero, default, Array.Empty<WindowAttentionSignal>());

    public double AttentionFor(nint handle)
    {
        var signal = WindowSignals.FirstOrDefault(item => item.WindowHandle == handle);
        var score = signal?.Attention ?? 0;
        if (ForegroundWindow == handle)
        {
            score += 0.85;
        }

        return Math.Clamp(score, 0, 3.0);
    }
}

public sealed record SmartPersonalizationState(
    double[] AutoFeatureWeights,
    double[] AutoArchetypeBias,
    double[] FollowTargetBias,
    double PreferredGapPixels,
    double PreferredMainRatio,
    int AutoCorrectionSamples,
    int FollowGestureSamples)
{
    public const int AutoFeatureCount = 12;
    public const int AutoArchetypeCount = 9;
    public const int FollowTargetCount = 6;

    public static SmartPersonalizationState Default { get; } = new(
        new double[AutoFeatureCount],
        new double[AutoArchetypeCount],
        new double[FollowTargetCount],
        PreferredGapPixels: 8.0,
        PreferredMainRatio: 0.62,
        AutoCorrectionSamples: 0,
        FollowGestureSamples: 0);

    public SmartPersonalizationState Normalize()
    {
        var autoWeights = NormalizeArray(AutoFeatureWeights, AutoFeatureCount);
        var archetypeBias = NormalizeArray(AutoArchetypeBias, AutoArchetypeCount);
        var followBias = NormalizeArray(FollowTargetBias, FollowTargetCount);
        return this with
        {
            AutoFeatureWeights = autoWeights,
            AutoArchetypeBias = archetypeBias,
            FollowTargetBias = followBias,
            PreferredGapPixels = Math.Clamp(double.IsFinite(PreferredGapPixels) ? PreferredGapPixels : 8.0, 0, 48),
            PreferredMainRatio = Math.Clamp(double.IsFinite(PreferredMainRatio) ? PreferredMainRatio : 0.62, 0.50, 0.78),
            AutoCorrectionSamples = Math.Max(0, AutoCorrectionSamples),
            FollowGestureSamples = Math.Max(0, FollowGestureSamples),
        };
    }

    private static double[] NormalizeArray(double[]? source, int length)
    {
        var result = new double[length];
        if (source is null)
        {
            return result;
        }

        var count = Math.Min(source.Length, result.Length);
        for (var index = 0; index < count; index++)
        {
            result[index] = double.IsFinite(source[index])
                ? Math.Clamp(source[index], -4.0, 4.0)
                : 0;
        }

        return result;
    }
}

public sealed record SmartLearningSnapshot(
    AutoLayoutArchetype Archetype,
    double[] Features,
    IReadOnlyDictionary<nint, RectI> TargetLayout);

public sealed record SmartPlanResult(
    IReadOnlyList<TidyMove> Moves,
    SmartLearningSnapshot? Learning);

public sealed record FollowHandDecision(
    bool ShouldApply,
    RectI TargetRect,
    FollowHandTargetKind TargetKind,
    double Confidence);
