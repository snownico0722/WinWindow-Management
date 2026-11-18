namespace NeatWin.Core;

public enum SmartOverlapAvoidance
{
    Gentle,
    Balanced,
    Strong,
}

public enum VideoBlackBarTendency
{
    Shrink,
    Expand,
}

public sealed record SmartBehaviorOptions(
    bool PreferReversibleVerticalFill = true,
    SmartOverlapAvoidance OverlapAvoidance = SmartOverlapAvoidance.Balanced,
    bool RemoveVideoBlackBars = false,
    VideoBlackBarTendency VideoBlackBarTendency = VideoBlackBarTendency.Shrink);
