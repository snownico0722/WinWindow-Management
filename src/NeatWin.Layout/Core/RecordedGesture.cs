namespace NeatWin.Core;

// Coordinates are physical pixels. A submitted native target is a request, not a verified frame.
public sealed record RecordedPlacementLink(string RequestId, long ElapsedMs, RectI Before, RectI Submitted);
internal sealed record SubmittedPlacement(int Instance, string Request, long Tick, RectI Before, RectI Submitted)
{
    internal RecordedPlacementLink? At(int instance, long start) =>
        instance == Instance && start >= Tick ? new(Request, start - Tick, Before, Submitted) : null;
}

public sealed record RecordedGeometryChange(int Dx, int Dy, int DWidth, int DHeight,
    bool? StartedAtSubmitted, string[]? ReturnedAxes)
{
    internal static RecordedGeometryChange From(RectI start, RectI end, RecordedPlacementLink? prior)
    {
        // 缺少前一次排布时无法判断用户撤回了哪一轴。空数组专用于已有参照、但这次没有任何轴回到原值的情况。
        List<string>? returned = null;
        if (prior != null)
        {
            returned = [];
            void Axis(string name, int before, int submitted, int from, int to)
            {
                if (Math.Abs((long)before - submitted) > 3 && Math.Abs((long)from - before) > 3 &&
                    Math.Abs((long)to - before) <= 3) returned.Add(name);
            }
            var a = prior.Before;
            var b = prior.Submitted;
            Axis("x", a.X, b.X, start.X, end.X);
            Axis("y", a.Y, b.Y, start.Y, end.Y);
            Axis("width", a.Width, b.Width, start.Width, end.Width);
            Axis("height", a.Height, b.Height, start.Height, end.Height);
        }
        return new(end.X - start.X, end.Y - start.Y, end.Width - start.Width, end.Height - start.Height,
            prior == null ? null : WindowStateRules.Near(start, prior.Submitted, 3), returned?.ToArray());
    }
}

internal sealed record GesturePoint(long OffsetMs, int X, int Y, int Width, int Height);
internal sealed record GestureEvent(int GestureId, int WindowId, string Stage, long Tick, RectI? Frame,
    RectI? WorkArea, uint? Dpi, bool AutomationMarked, RecordedPlacementLink? Prior = null,
    GesturePoint[]? OuterPath = null, int OmittedPathPoints = 0, RecordedGeometryChange? Change = null, long? StartTick = null)
{
    // Snapshot export may begin in the middle of a gesture or after the linked layout.
    internal GestureEvent WithinLookback(long earliest)
    {
        long start = StartTick ?? Tick;
        var prior = Prior != null && start - Prior.ElapsedMs >= earliest ? Prior : null;
        var change = start < earliest ? null : Change;
        if (prior == null && change != null) change = change with { StartedAtSubmitted = null, ReturnedAxes = null };
        return this with { Prior = prior, Change = change,
            OuterPath = OuterPath?.Where(p => start + p.OffsetMs >= earliest).ToArray() };
    }
}

internal sealed record GestureBatch(int Version, string Session, DateTimeOffset Time, GestureEvent[] Events, int DroppedEvents);
