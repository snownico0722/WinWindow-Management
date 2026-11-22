namespace NeatWin.Core;

public sealed record TaskEvidence(int First, int Second, double Joint, double Parked,
    double Integrated, double Uncertainty, bool SameColumn, string Source, double Affinity = 1,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Purpose = null);
// 主窗和返回入口不足时收益更陡；达到舒适尺寸前仍连续计分。分数是经验尺度，不代表注视概率。
public readonly record struct AttentionDeficit(double Main, double Access, double Comfort, int Gesture = 0)
{
    public double DemandCost => .48 * Main + .39 * Access + .12 * Comfort + 1.5 * Gesture;
}
public sealed record TaskCostBreakdown(double InformationLoss, double Switching,
    double Continuity, double Reflow, double PeripheralLoss, double Alignment,
    double Uncertainty, double Feedback, double PersonalPlacement = 0, double PairPlacement = 0,
    AttentionDeficit Goal = default, double Interruption = 0)
{
    public double Total => InformationLoss + Switching + Continuity + Reflow + PeripheralLoss +
        Alignment + Uncertainty + Feedback + PersonalPlacement + PairPlacement + Goal.DemandCost + Interruption;
}
public sealed record TaskGroupExplanation(TaskEvidence[] Relations, string ViewingModel,
    string[] Limits,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    WindowLayoutObjective[]? Objectives = null);
