namespace NeatWin.Core;

/// <summary>Weak descriptions of observed behavior. They do not establish app identity or attention.</summary>
internal static class ActivityHints
{
    internal static bool Equivalent(TaskWindowHint[] previous, TaskWindowHint[] current) =>
        previous.Length == current.Length && previous.Zip(current).All(pair =>
            pair.First with { ProtectedRegions = null } == pair.Second with { ProtectedRegions = null } &&
            (pair.First.ProtectedRegions ?? []).SequenceEqual(pair.Second.ProtectedRegions ?? []));

    internal static RectI? FragmentPatch(RectI visible)
    {
        if (visible.Width < 12 || visible.Height < 12) return null;
        int width = Math.Min(160, visible.Width), height = Math.Min(160, visible.Height);
        return new RectI(visible.X + (visible.Width - width) / 2, visible.Y + (visible.Height - height) / 2, width, height);
    }

    internal static TaskWindowHint? ToLayoutHint(nint handle, RectI window, string activity, RectI? patch)
    {
        if (activity == "peripheral-motion" && patch is { } r && !r.IsEmpty && r.Intersect(window) == r)
        {
            var region = new TaskContentRegion((r.Left - window.Left) / (double)window.Width,
                (r.Top - window.Top) / (double)window.Height, r.Width / (double)window.Width,
                r.Height / (double)window.Height, .95);
            return new(handle, PassiveVisual: true, ProtectedRegions: [region], Anchor: true);
        }
        if (activity is "media-with-motion" or "audible-with-motion" or "audible-media-session")
            return new(handle, PassiveVisual: true, MediaCompanion: true);
        return activity == "background-motion" ? new(handle, PassiveVisual: true) : null;
    }

    internal static string Classify(bool foreground, double visible, double workspaceFraction,
        int consecutiveMotionSamples, bool mediaPlaying, double? processCores, bool audible = false, int fragmentStreak = 0, int processWindowCount = 0)
    {
        if (visible <= 0) return "covered";
        if (!foreground && visible < .25 && fragmentStreak >= 2) return "peripheral-motion";
        if (!foreground && mediaPlaying && audible && processWindowCount == 1) return "audible-media-session";
        if (consecutiveMotionSamples >= 2)
        {
            if (mediaPlaying) return "media-with-motion";
            if (audible && !foreground) return "audible-with-motion";
            if (!foreground) return visible < .25 ? "peripheral-motion" : "background-motion";
            if (workspaceFraction >= .65 && processCores >= .5) return "game-like-interaction";
        }
        return foreground ? "foreground" : "visible-background";
    }

    internal static bool MatchesRole(string role, WindowSnapshot window, TaskWindowHint? hint, string? activity) =>
        role switch
        {
            "any" => true,
            "game" => activity == "game-like-interaction" ||
                (window.IsForeground && !window.IsResizable && window.VisualRect.Area >= window.WorkArea.Area * .42),
            "media" => hint?.MediaCompanion == true ||
                activity is "media-with-motion" or "audible-with-motion" or "audible-media-session",
            "passive" => hint?.PassiveVisual == true || activity == "background-motion",
            "foreground" => window.IsForeground || activity == "foreground",
            "companion" => !window.IsForeground && hint?.Anchor != true,
            _ => false,
        };
}
