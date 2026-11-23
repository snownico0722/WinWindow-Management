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
            foreach (var covered in pieces) addition = RectRegion.Subtract(addition, covered, int.MaxValue);
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
            new(cells), LargestClearRectangle(pieces));
    }

    // 遮挡切出的碎片可能共用一条边。沿真实边界扫描它们的并集，阅读区域可跨过这条人为切缝。
    private static RectI LargestClearRectangle(List<RectI> pieces)
    {
        if (pieces.Count == 0) return default;
        var largest = pieces.MaxBy(r => r.Area);
        if (pieces.Count == 1) return largest;

        var xs = pieces.SelectMany(r => new[] { r.Left, r.Right }).Distinct().Order().ToArray();
        var events = new List<(int Y, int Left, int Right, int Delta)>(pieces.Count * 2);
        foreach (var piece in pieces)
        {
            int left = Array.BinarySearch(xs, piece.Left), right = Array.BinarySearch(xs, piece.Right);
            events.Add((piece.Top, left, right, 1));
            events.Add((piece.Bottom, left, right, -1));
        }
        events.Sort((a, b) => a.Y.CompareTo(b.Y));
        var coverage = new int[xs.Length];
        var heights = new int[xs.Length];
        var stack = new int[xs.Length];
        for (int i = 0; i < events.Count;)
        {
            int y = events[i].Y;
            while (i < events.Count && events[i].Y == y)
            {
                var edge = events[i++];
                coverage[edge.Left] += edge.Delta;
                coverage[edge.Right] -= edge.Delta;
            }
            if (i == events.Count) break;
            int bottom = events[i].Y, active = 0, count = 0;
            // 每列保存延伸到当前底边的无间断像素高度。单调栈以真实列宽计算最大矩形。
            for (int col = 0; col < xs.Length; col++)
            {
                active += coverage[col];
                heights[col] = active > 0 ? heights[col] + bottom - y : 0;
                while (count > 0 && heights[stack[count - 1]] > heights[col])
                {
                    int height = heights[stack[--count]];
                    int left = count == 0 ? xs[0] : xs[stack[count - 1] + 1];
                    if ((long)(xs[col] - left) * height > largest.Area)
                        largest = RectI.FromEdges(left, bottom - height, xs[col], bottom);
                }
                stack[count++] = col;
            }
        }
        return largest;
    }

}
public sealed record RecordedDisplay(RectI Bounds, RectI WorkArea);
