namespace NeatWin.Core;

/// <summary>
/// Small population human-factors metrics shared by Smart. These are deliberately deterministic
/// and dependency-free: personalization may tune around them, but does not need to relearn basic
/// motor/attention facts such as Fitts' law from one user's history.
/// </summary>
internal static class HumanFactorsMetrics
{
    /// <summary>
    /// Returns [0,1], where 1 means the rectangular target is easy to acquire from the current
    /// pointer position. Uses a rectangular effective target width along the movement direction
    /// and a Fitts-style index of difficulty. Being already inside the target is the easiest case.
    /// </summary>
    internal static double PointerAcquisitionQuality(PointI pointer, RectI target)
    {
        if (target.IsEmpty)
        {
            return 0;
        }

        if (pointer.X >= target.Left && pointer.X < target.Right &&
            pointer.Y >= target.Top && pointer.Y < target.Bottom)
        {
            return 1.0;
        }

        var nearestX = Math.Clamp(pointer.X, target.Left, target.Right);
        var nearestY = Math.Clamp(pointer.Y, target.Top, target.Bottom);
        var dx = nearestX - pointer.X;
        var dy = nearestY - pointer.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance <= 0.5)
        {
            return 1.0;
        }

        var centerX = (target.Left + target.Right) / 2.0;
        var centerY = (target.Top + target.Bottom) / 2.0;
        var directionX = centerX - pointer.X;
        var directionY = centerY - pointer.Y;
        var directionLength = Math.Sqrt((directionX * directionX) + (directionY * directionY));
        if (directionLength <= 0.5)
        {
            return 1.0;
        }

        var ux = Math.Abs(directionX / directionLength);
        var uy = Math.Abs(directionY / directionLength);
        // Projection of a rectangle onto the movement axis. This is a better effective width than
        // blindly using min(width,height) for ultrawide or very tall windows.
        var effectiveWidth = Math.Max(8.0, (target.Width * ux) + (target.Height * uy));
        var indexOfDifficulty = Math.Log2(1.0 + (distance / effectiveWidth));
        return Math.Clamp(Math.Exp(-0.72 * indexOfDifficulty), 0, 1);
    }

    /// <summary>
    /// A light visual-eccentricity prior. This is not eye tracking and must not be interpreted as
    /// literal gaze; it merely keeps highly attended content from being needlessly thrown far from
    /// the current interaction locus on very wide / multi-monitor desktops.
    /// </summary>
    internal static double VisualLocalityQuality(PointI pointer, RectI target, RectI workArea)
    {
        if (target.IsEmpty || workArea.IsEmpty)
        {
            return 0.5;
        }

        var centerX = (target.Left + target.Right) / 2.0;
        var centerY = (target.Top + target.Bottom) / 2.0;
        var dx = centerX - pointer.X;
        var dy = centerY - pointer.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var diagonal = Math.Sqrt((double)workArea.Width * workArea.Width + (double)workArea.Height * workArea.Height);
        return Math.Exp(-distance / Math.Max(120.0, diagonal * 0.28));
    }
}
