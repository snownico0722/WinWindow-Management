namespace NeatWin.Core;

/// <summary>
/// A geometry-only prior, NOT an estimate of semantic importance or gaze. Count the union of
/// foreground occluders so three-way overlap is not charged twice. Access and useful exposed
/// width are distinct from total exposed area. All distances below are in screen pixels.
/// </summary>
internal static class OcclusionMetrics
{
    internal const double HorizontalInsetFraction = .18;

    internal sealed record Exposure(double Visible, double CenterVisible, int AccessWidth, int UsefulWidth)
    {
        internal double UsefulHeight(RectI frame) =>
            Math.Min(frame.Height, frame.Area * Visible / Math.Max(1, UsefulWidth));
    }

    internal static Exposure Measure(RectI rect, IEnumerable<RectI> occluders, uint dpi)
    {
        if (rect.IsEmpty) return new(0, 0, 0, 0);
        var blockers = occluders.Where(r => !rect.Intersect(r).IsEmpty).OrderBy(r => r.Left).ToArray();
        if (blockers.Length == 0) return new(1, 1, rect.Width, rect.Width);
        var pieces = VisiblePieces(rect, blockers);
        var insetX = (int)Math.Round(rect.Width * HorizontalInsetFraction);
        var insetY = (int)Math.Round(rect.Height * 0.08);
        var center = RectI.FromEdges(rect.Left + insetX, rect.Top + insetY,
            rect.Right - insetX, rect.Bottom - insetY);
        var band = new RectI(rect.X, rect.Y, rect.Width,
            Math.Min(rect.Height, Math.Max(1, (int)Math.Round(28 * dpi / 96.0))));
        int access = ClearWidth(band, blockers, .65);
        int useful = ClearWidth(rect, blockers, .45);
        return new(pieces.Sum(r => r.Area) / (double)rect.Area,
            VisiblePieces(center, blockers).Sum(r => r.Area) / (double)Math.Max(1, center.Area), access, useful);
    }

    // A readable rectangle can span the seams of the disjoint pieces used for area.
    // Slide a strip of the required height up until it touches the frame or a blocker.
    // For each such position, measure horizontal gaps in the union of blocked intervals.
    private static int ClearWidth(RectI frame, RectI[] blockers, double heightFraction)
    {
        int height = (int)Math.Ceiling(frame.Height * heightFraction);
        int best = GapAt(frame.Top);
        foreach (var blocker in blockers)
        {
            if (best == frame.Width) break;
            int y = blocker.Bottom;
            if (y > frame.Top && y <= frame.Bottom - height) best = Math.Max(best, GapAt(y));
        }
        return best;

        int GapAt(int top)
        {
            int x = frame.Left, widest = 0;
            foreach (var blocker in blockers)
            {
                if (blocker.Top >= top + height || blocker.Bottom <= top || blocker.Right <= x) continue;
                if (blocker.Left >= frame.Right) break;
                widest = Math.Max(widest, blocker.Left - x);
                x = Math.Max(x, blocker.Right);
                if (x >= frame.Right) return widest;
            }
            return Math.Max(widest, frame.Right - x);
        }
    }

    private static List<RectI> VisiblePieces(RectI rect, RectI[] blockers)
    {
        var pieces = new List<RectI> { rect };
        foreach (var blocker in blockers)
            pieces = RectRegion.Subtract(pieces, blocker, maxFragments: 512);
        return pieces;
    }
}
