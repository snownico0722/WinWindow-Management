namespace NeatWin.Core;

// Coordinates are physical pixels. A submitted native target is a request, not a verified frame.
public sealed record RecordedPlacementLink(string RequestId, long ElapsedMs, RectI Before, RectI Submitted);
internal sealed record SubmittedPlacement(int Instance, string Request, long Tick, RectI Before, RectI Submitted)
{
    internal RecordedPlacementLink? At(int instance, long start) =>
        instance == Instance && start >= Tick ? new(Request, start - Tick, Before, Submitted) : null;
}

// 一个回拖可以表达方向，即使手没有精确回到原像素。可信度描述的是这次改动的反向幅度，只作用于这一轴。
public sealed record AxisCorrection(int Submitted, int Released, double Confidence)
{
    internal static AxisCorrection? Observe(int before, int submitted, int released, uint dpi)
    {
        double travel = (double)submitted - before;
        if (Math.Abs(travel) <= 3) return null;
        double reversal = (submitted - (double)released) / travel;
        if (reversal <= 0 || Math.Abs(released - (double)before) >= Math.Abs(travel)) return null;
        // 极小的系统误差和随手抖动提供的证据很弱；反向大幅移动无需精准命中旧位置。
        double confidence = Math.Min(1, reversal) * Math.Min(1, Math.Abs(travel) / (32 * Math.Max(96u, dpi) / 96d));
        return new(submitted, released, confidence);
    }

    internal double Cost(int current, int candidate) => Confidence * Math.Clamp(
        (Math.Abs(current - (double)Submitted) - Math.Abs(candidate - (double)Submitted)) /
        Math.Max(1, Math.Abs(Released - (double)Submitted)), 0, 1);
}

public sealed record PlacementCorrection(AxisCorrection? X, AxisCorrection? Y, AxisCorrection? Width, AxisCorrection? Height)
{
    internal static PlacementCorrection? Observe(RectI start, RectI end, RectI? before, RectI? submitted,
        uint dpi, PlacementCorrection? previous = null)
    {
        bool linked = submitted.HasValue && before.HasValue && WindowStateRules.Near(start, submitted.Value);
        AxisCorrection? Axis(int from, int to, int? old, int? target, AxisCorrection? prior) =>
            from == to ? prior : linked ? AxisCorrection.Observe(old!.Value, target!.Value, to, dpi) : null;
        var result = new PlacementCorrection(Axis(start.X, end.X, before?.X, submitted?.X, previous?.X),
            Axis(start.Y, end.Y, before?.Y, submitted?.Y, previous?.Y),
            Axis(start.Width, end.Width, before?.Width, submitted?.Width, previous?.Width),
            Axis(start.Height, end.Height, before?.Height, submitted?.Height, previous?.Height));
        return result is { X: null, Y: null, Width: null, Height: null } ? null : result;
    }

    internal bool RepeatsRejectedAxis(RectI from, RectI to) =>
        X?.Cost(from.X, to.X) >= .5 || Y?.Cost(from.Y, to.Y) >= .5 ||
        Width?.Cost(from.Width, to.Width) >= .5 || Height?.Cost(from.Height, to.Height) >= .5;

    internal double Cost(RectI current, RectI candidate) => (X?.Cost(current.X, candidate.X) ?? 0) +
        (Y?.Cost(current.Y, candidate.Y) ?? 0) + (Width?.Cost(current.Width, candidate.Width) ?? 0) +
        (Height?.Cost(current.Height, candidate.Height) ?? 0);
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

// 两次从系统落点往同方向拖回，短时间内已有足够证据停止重试这一轴。每轴独立计时；输入不会续期。
internal sealed class RecentReversals(WindowSnapshot window)
{
    private sealed record Vote(AxisCorrection Correction, long Tick, bool Repeated);
    private readonly Vote?[] _axes = new Vote?[4];
    private const int LifetimeMs = 120_000;
    internal bool Matches(WindowSnapshot live) => WindowStateRules.Matches(window, live, live.VisualRect);

    internal void Observe(RectI start, RectI end, ManualGestureKind kind, PlacementCorrection? reversal, long tick)
    {
        // 从左边框拉宽时 X 也会改变。此次推翻的是宽度，上一轮主动摆放的位置证据仍然有效。
        if (kind == ManualGestureKind.Move)
        {
            Update(0, start.X != end.X, reversal?.X);
            Update(1, start.Y != end.Y, reversal?.Y);
        }
        else
        {
            Update(2, start.Width != end.Width, reversal?.Width);
            Update(3, start.Height != end.Height, reversal?.Height);
        }

        void Update(int axis, bool changed, AxisCorrection? evidence)
        {
            if (!changed) return;
            var prior = _axes[axis];
            // 用户后来在该轴选择了不同摆法，就以新的动作重新起算，不需要点撤销。
            bool same = evidence is { Confidence: >= .65 } && prior != null && tick - prior.Tick is >= 0 and <= LifetimeMs &&
                Math.Sign((long)evidence.Released - evidence.Submitted) == Math.Sign((long)prior.Correction.Released - prior.Correction.Submitted);
            _axes[axis] = evidence is { Confidence: >= .65 } ? new(evidence, tick, same) : null;
        }
    }

    internal PlacementCorrection? Active(long now)
    {
        AxisCorrection? Read(int i) => _axes[i] is { Repeated: true } v && now - v.Tick is >= 0 and <= LifetimeMs ? v.Correction : null;
        var result = new PlacementCorrection(Read(0), Read(1), Read(2), Read(3));
        return result is { X: null, Y: null, Width: null, Height: null } ? null : result;
    }
}
