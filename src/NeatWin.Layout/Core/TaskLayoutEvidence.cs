namespace NeatWin.Core;

public sealed record TaskEvidence(int First, int Second, double Joint, double Parked,
    double Integrated, double Uncertainty, bool SameColumn, string Source, double Affinity = 1);
// Deficits use five-percent bands so single-pixel changes do not trump continuity.
public readonly record struct AttentionDeficit(int Main, int Access, int Comfort, int Gesture = 0) : IComparable<AttentionDeficit>
{
    public int CompareTo(AttentionDeficit other) => (Gesture, Main, Access, Comfort).CompareTo((other.Gesture, other.Main, other.Access, other.Comfort));
}
public sealed record TaskCostBreakdown(double InformationLoss, double Switching,
    double Continuity, double Reflow, double PeripheralLoss, double Alignment,
    double Uncertainty, double Feedback, double PersonalPlacement = 0, double PairPlacement = 0, AttentionDeficit Goal = default)
{
    public double Total => InformationLoss + Switching + Continuity + Reflow +
        PeripheralLoss + Alignment + Uncertainty + Feedback + PersonalPlacement + PairPlacement;
}
public sealed record TaskGroupExplanation(TaskEvidence[] Relations, string ViewingModel,
    string[] Limits);
