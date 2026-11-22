namespace NeatWin.Core;

// 顺序号只在本次进程会话内有效。Tick 是事件发生的单调时刻；窗口属性在通知送达时取得。
internal sealed record DesktopInteraction(string Session, long Sequence, long Tick, string Kind, int WindowId,
    uint ProcessId, string? Handle, string? ClassName = null, RectI? Frame = null, string? Owner = null,
    string? Foreground = null, PointI? Point = null, int? WheelDelta = null, string? Hit = null,
    string? Detail = null, RawWindowIdentity? Identity = null, RectI? CaretClientRect = null, long? DeliveredTick = null, DateTimeOffset? DeliveredAt = null, int Version = 1);

public sealed record ReadingInteraction(double Scrolling, double CaretMotion, long? ScrollEvent, long? CaretEvent, bool RepeatedScroll = false);

internal readonly record struct PointerObservation(string Kind, nint Target, uint TargetPid, nint Hit, nint Foreground,
    PointI Point, int? Delta, long Tick);

internal sealed record ContextPress(nint Root, uint Pid, PointI Point, long Tick, long Sequence);

// 每次求解只读取当时已发生的输入。静默后权重自动衰减，无定时排布，也不把系统挪窗当作用户偏好。
internal sealed class DesktopInteractionHistory
{
    private sealed class Use
    {
        internal (long Tick, long Sequence)? Scroll, Caret;
        internal long? PreviousCaret, PreviousScroll;
        internal RectI? CaretRect;
        internal long Last => Math.Max(Scroll?.Tick ?? 0, Caret?.Tick ?? 0);
    }
    private readonly Dictionary<(nint, uint), Use> _uses = [];
    private ContextPress? _context;

    internal void Pointer(string kind, nint root, uint pid, long tick, long sequence, PointI point)
    {
        if (kind == "right-down") _context = new(root, pid, point, tick, sequence);
        if (kind == "right-up" && _context is { } down)
        {
            // 长按后松开也可能打开菜单，右键拖动游戏视角则不能遗留菜单意图。
            _context = down.Root == root && down.Pid == pid && Math.Abs((long)point.X - down.Point.X) <= 8 &&
                Math.Abs((long)point.Y - down.Point.Y) <= 8 ? down with { Tick = tick, Sequence = sequence } : null;
        }
        if (kind == "left-down" || kind == "middle-down") _context = null;
        if (kind is "wheel" or "horizontal-wheel")
        {
            var use = Get(root, pid);
            use.PreviousScroll = use.Scroll?.Tick;
            use.Scroll = (tick, sequence);
        }
    }

    internal void CaretMoved(nint root, uint pid, long tick, long sequence, RectI clientRect)
    {
        var use = Get(root, pid);
        if (use.CaretRect == clientRect) return;
        use.CaretRect = clientRect;
        use.PreviousCaret = use.Caret?.Tick;
        use.Caret = (tick, sequence);
    }

    private Use Get(nint root, uint pid)
    {
        var key = (root, pid);
        if (_uses.TryGetValue(key, out var use)) return use;
        if (_uses.Count >= 256) _uses.Remove(_uses.MinBy(e => e.Value.Last).Key);
        return _uses[key] = new();
    }

    internal Dictionary<nint, ReadingInteraction> Read(IReadOnlyList<WindowSnapshot> windows, long now)
    {
        var result = new Dictionary<nint, ReadingInteraction>();
        foreach (var w in windows)
        {
            if (!_uses.TryGetValue((w.Handle, w.ProcessId), out var use)) continue;
            // 滚轮有明确目标；插入点首次出现也可能只是获得焦点，至少两次位置事件才作为连续操作的弱证据。
            double scroll = Strength(use.Scroll?.Tick, now, 1800);
            double caret = use.Caret?.Tick - use.PreviousCaret is > 0 and <= 1100 && w.IsForeground
                ? Strength(use.Caret?.Tick, now, 1400) : 0;
            if (scroll + caret > 0)
                result[w.Handle] = new(scroll, caret, scroll > 0 ? use.Scroll?.Sequence : null, caret > 0 ? use.Caret?.Sequence : null,
                    scroll > 0 && use.Scroll?.Tick - use.PreviousScroll is > 0 and <= 1600);
        }
        return result;
    }
    private static double Strength(long? then, long now, int duration) =>
        then is long t && now >= t ? Math.Max(0, 1 - (now - t) / (double)duration) : 0;

    internal ContextPress? ContextAt(long now) =>
        _context is { } press && now - press.Tick is >= 0 and <= 900 ? press : null;

    internal void Retire(nint root)
    {
        foreach (var k in _uses.Keys.Where(k => k.Item1 == root).ToArray()) _uses.Remove(k);
        if (_context?.Root == root) _context = null;
    }
    internal void Clear() { _uses.Clear(); _context = null; }
}
