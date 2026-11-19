namespace NeatWin.Core;

public sealed record TaskEvidence(int First, int Second, double Joint, double Parked,
    double Integrated, double Uncertainty, bool SameColumn, string Source, double Affinity = 1);
public sealed record TaskCostBreakdown(double InformationLoss, double Switching,
    double Continuity, double Reflow, double PeripheralLoss, double Alignment,
    double Uncertainty, double Feedback)
{
    public double Total => InformationLoss + Switching + Continuity + Reflow +
        PeripheralLoss + Alignment + Uncertainty + Feedback;
}
public sealed record TaskGroupExplanation(TaskEvidence[] Relations, string ViewingModel,
    string[] Limits);
