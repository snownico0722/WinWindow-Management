namespace NeatWin.Core;

public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => IsEmpty ? 0L : (long)Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectI FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public RectI Intersect(RectI other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return FromEdges(left, top, right, bottom);
    }

    public double VerticalOverlapRatio(RectI other)
    {
        var overlap = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
        var denominator = Math.Min(Height, other.Height);
        return denominator <= 0 ? 0 : (double)overlap / denominator;
    }

    public double HorizontalOverlapRatio(RectI other)
    {
        var overlap = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left));
        var denominator = Math.Min(Width, other.Width);
        return denominator <= 0 ? 0 : (double)overlap / denominator;
    }

    public static int IntervalGap(int aStart, int aEnd, int bStart, int bEnd)
    {
        if (aEnd < bStart)
        {
            return bStart - aEnd;
        }

        if (bEnd < aStart)
        {
            return aStart - bEnd;
        }

        return 0;
    }
}

public static class RectRegion
{
    internal static IReadOnlyList<RectI> FreeRectangles(RectI area, IEnumerable<RectI> occupied,
        int minWidth = 1, int minHeight = 1)
    {
        minWidth = Math.Max(1, minWidth);
        minHeight = Math.Max(1, minHeight);
        if (area.Width < minWidth || area.Height < minHeight) return [];
        var free = new List<RectI> { area };
        foreach (var obstacle in occupied)
        {
            if (free.Count == 0) break;
            if (!free.Any(r => !r.Intersect(obstacle).IsEmpty)) continue;
            var split = new List<RectI>();
            foreach (var rect in free)
            {
                var cut = rect.Intersect(obstacle);
                if (cut.IsEmpty) { split.Add(rect); continue; }

                // Retain full-span strips. Their overlaps allow a placement to cross a
                // partition seam while still avoiding every occupied pixel.
                split.Add(RectI.FromEdges(rect.Left, rect.Top, cut.Left, rect.Bottom));
                split.Add(RectI.FromEdges(cut.Right, rect.Top, rect.Right, rect.Bottom));
                split.Add(RectI.FromEdges(rect.Left, rect.Top, rect.Right, cut.Top));
                split.Add(RectI.FromEdges(rect.Left, cut.Bottom, rect.Right, rect.Bottom));
            }
            free.Clear();
            foreach (var rect in split.Where(r => r.Width >= minWidth && r.Height >= minHeight)
                .OrderByDescending(r => r.Area).ThenBy(r => r.Left).ThenBy(r => r.Top))
            {
                if (free.Any(other => other.Intersect(rect) == rect)) continue;
                free.Add(rect);
                if (free.Count == 64) break;
            }
        }
        return free;
    }

    public static List<RectI> Subtract(IReadOnlyList<RectI> pieces, RectI cut, int maxFragments = 256)
    {
        if (pieces.Count == 0 || cut.IsEmpty)
        {
            return pieces.ToList();
        }

        var next = new List<RectI>(Math.Min(maxFragments, pieces.Count * 2));
        foreach (var piece in pieces)
        {
            next.AddRange(Subtract(piece, cut));
        }

        if (next.Count <= maxFragments)
        {
            return next;
        }

        return next
            .OrderByDescending(static rect => rect.Area)
            .Take(maxFragments)
            .ToList();
    }

    public static IReadOnlyList<RectI> Subtract(RectI source, RectI cut)
    {
        var intersection = source.Intersect(cut);
        if (intersection.IsEmpty)
        {
            return [source];
        }

        var result = new List<RectI>(4);
        AddIfNotEmpty(result, RectI.FromEdges(source.Left, source.Top, source.Right, intersection.Top));
        AddIfNotEmpty(result, RectI.FromEdges(source.Left, intersection.Bottom, source.Right, source.Bottom));
        AddIfNotEmpty(result, RectI.FromEdges(source.Left, intersection.Top, intersection.Left, intersection.Bottom));
        AddIfNotEmpty(result, RectI.FromEdges(intersection.Right, intersection.Top, source.Right, intersection.Bottom));
        return result;
    }

    private static void AddIfNotEmpty(List<RectI> target, RectI rect)
    {
        if (!rect.IsEmpty)
        {
            target.Add(rect);
        }
    }
}
