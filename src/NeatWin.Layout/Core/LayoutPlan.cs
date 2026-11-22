namespace NeatWin.Core;

public sealed record WindowLayerOrder(WindowSnapshot[] FrontToBack);
public sealed record LayoutCandidateTrace(string Kind, double? Cost, string? Rejection, TaskCostBreakdown? Breakdown = null, int Generation = 0,
    double? InterventionWeight = null, double? GainThreshold = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? CenterTieCost = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    HeldLayoutAxes? Held = null);
public sealed record LayoutGroupTrace(nint[] Handles, string Selected, double StackEvidence,
    LayoutCandidateTrace[] Candidates, TaskGroupExplanation? Task = null, LayoutAxisCoverage[]? AxisCoverage = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SolverPortfolio? Portfolio = null);
public sealed record IntentLayoutPlan(IReadOnlyList<TidyMove> Moves,
    IReadOnlyList<WindowLayerOrder> Layers, IReadOnlyList<LayoutGroupTrace> Groups)
{
    // 成功或受保护而放弃的接缝请求，都没有顺便回收第三扇窗的授权。
    internal bool IsDividerGesture { get; init; }
    internal ActiveLayoutScene? Scene { get; init; }
}
