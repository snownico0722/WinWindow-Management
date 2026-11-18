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
