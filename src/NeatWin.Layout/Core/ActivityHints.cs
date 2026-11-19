namespace NeatWin.Core;

/// <summary>Weak descriptions of observed behavior. They do not establish app identity or attention.</summary>
internal static class ActivityHints
{
    internal static string Classify(bool foreground, double visible, double workspaceFraction,
        int consecutiveMotionSamples, bool mediaPlaying, double? processCores)
    {
        if (visible <= 0) return "covered";
        if (consecutiveMotionSamples >= 2)
        {
            if (mediaPlaying) return "media-with-motion";
            if (!foreground) return visible < .25 ? "peripheral-motion" : "background-motion";
            if (workspaceFraction >= .65 && processCores >= .5) return "game-like-interaction";
        }
        return foreground ? "foreground" : "visible-background";
    }
}
