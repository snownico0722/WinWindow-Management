namespace NeatWin.Core;

// Each hex digit stores the visible fraction (0..15) of one window-relative cell, row-major.
public sealed record WindowExposure(double Visible, double Offscreen, string Cells, RectI LargestFragment)
{
    public static RectI Cell(RectI r, int index)
    {
        int col = index % 4, row = index / 4;
        return RectI.FromEdges(r.X + r.Width * col / 4, r.Y + r.Height * row / 4,
            r.X + r.Width * (col + 1) / 4, r.Y + r.Height * (row + 1) / 4);
    }

    internal static WindowExposure Measure(RectI window, IEnumerable<RectI> screens, IEnumerable<RectI> blockers)
    {
        if (window.IsEmpty) return new(0, 1, new string('0', 16), default);
        var pieces = new List<RectI>();
        foreach (var display in screens.Distinct())
        {
            var intersection = window.Intersect(display);
            if (intersection.IsEmpty) continue;
            var addition = new List<RectI> { intersection };
            foreach (var covered in pieces) addition = RectRegion.Subtract(addition, covered);
            pieces.AddRange(addition);
        }
        var onScreen = pieces.Sum(r => r.Area);
        foreach (var blocker in blockers)
        {
            if (pieces.Count == 0) break;
            if (!window.Intersect(blocker).IsEmpty) pieces = RectRegion.Subtract(pieces, blocker, int.MaxValue);
        }
        var cells = new char[16];
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = Cell(window, i);
            var fraction = pieces.Sum(r => r.Intersect(cell).Area) / (double)Math.Max(1, cell.Area);
            cells[i] = "0123456789ABCDEF"[(int)Math.Floor(15 * fraction)];
        }
        return new(pieces.Sum(r => r.Area) / (double)window.Area, 1 - onScreen / (double)window.Area,
            new(cells), pieces.DefaultIfEmpty().MaxBy(r => r.Area));
    }
}
public sealed record RecordedDisplay(RectI Bounds, RectI WorkArea);
