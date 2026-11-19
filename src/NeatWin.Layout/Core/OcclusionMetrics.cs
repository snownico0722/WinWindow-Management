namespace NeatWin.Core;

/// <summary>
/// A geometry-only prior, NOT an estimate of semantic importance or gaze. Count the union of
/// foreground occluders so three-way overlap is not charged twice. Access and useful exposed
/// width are distinct from total exposed area. All distances below are in screen pixels.
/// </summary>
internal static class OcclusionMetrics
{
    internal sealed record Exposure(double Visible, double CenterVisible, int AccessWidth, int UsefulWidth);

    internal static Exposure Measure(RectI rect, IEnumerable<RectI> occluders, uint dpi)
    {
        if (rect.IsEmpty) return new(0, 0, 0, 0);
        var blockers = occluders.ToArray();
        var pieces = VisiblePieces(rect, blockers);
        var insetX = (int)Math.Round(rect.Width * 0.18);
        var insetY = (int)Math.Round(rect.Height * 0.08);
        var center = RectI.FromEdges(rect.Left + insetX, rect.Top + insetY,
            rect.Right - insetX, rect.Bottom - insetY);
        var band = new RectI(rect.X, rect.Y, rect.Width,
            Math.Min(rect.Height, Math.Max(1, (int)Math.Round(28 * dpi / 96.0))));
        var access = VisiblePieces(band, blockers).Where(r => r.Height >= band.Height * 0.65)
            .Select(r => r.Width).DefaultIfEmpty(0).Max();
        var useful = pieces.Where(r => r.Height >= rect.Height * 0.45)
            .Select(r => r.Width).DefaultIfEmpty(0).Max();
        return new(pieces.Sum(r => r.Area) / (double)rect.Area,
            VisiblePieces(center, blockers).Sum(r => r.Area) / (double)Math.Max(1, center.Area), access, useful);
    }

    private static List<RectI> VisiblePieces(RectI rect, RectI[] blockers)
    {
        var pieces = new List<RectI> { rect };
        foreach (var blocker in blockers)
            pieces = RectRegion.Subtract(pieces, blocker, maxFragments: 512);
        return pieces;
    }
}
