namespace NeatWin.Core;

public sealed record WindowLayerOrder(WindowSnapshot[] FrontToBack);
public sealed record LayoutCandidateTrace(string Kind, double? Cost, string? Rejection, TaskCostBreakdown? Breakdown = null, int Generation = 0,
    double? InterventionWeight = null, double? GainThreshold = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? CenterTieCost = null);
public sealed record LayoutGroupTrace(nint[] Handles, string Selected, double StackEvidence,
    LayoutCandidateTrace[] Candidates, TaskGroupExplanation? Task = null);
public sealed record IntentLayoutPlan(IReadOnlyList<TidyMove> Moves,
    IReadOnlyList<WindowLayerOrder> Layers, IReadOnlyList<LayoutGroupTrace> Groups);
