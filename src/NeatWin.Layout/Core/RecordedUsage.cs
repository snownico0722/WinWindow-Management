namespace NeatWin.Core;

// Offsets are milliseconds from the beginning of this interval. Window 0 is an
// untracked foreground (shell, WinWindow, or another surface absent from the frame).
internal sealed record ForegroundSpan(int Id, int OffsetMs, int DurationMs);
internal sealed record WindowInputCount(int Id, int Count);
internal sealed record UsageInterval(ForegroundSpan[] Foreground, WindowInputCount[]? PointerActions);
internal sealed record CoUseSample(int ForegroundId, int VisiblePeerId, int ForegroundMsWithPeerVisibleAtBothEnds);

internal sealed record FocusChange(long Tick, int Id);
internal sealed record FocusCoverage(long? FromTick, long ToTick, int? InitialId, int? ObservedEndId,
    string Status, FocusChange[] Events, int OmittedEvents, WindowInputCount[]? PointerActions, int? OmittedPointerActions);

internal sealed class RecordedUsage
{
    private readonly List<(long Tick, int Id)> _changes = [];
    private readonly Dictionary<int, int> _inputs = [];
    private long _start;
    private int _first, _lastEvent;
    private bool _started;
    private int _omitted, _omittedInputs;
    internal bool Started => _started;

    internal void Reset()
    {
        _started = false; _omitted = _omittedInputs = 0;
        _changes.Clear(); _inputs.Clear();
    }

    internal void Foreground(long tick, int id)
    {
        if (!_started || tick < _start || _lastEvent == id) return;
        _lastEvent = id;
        if (_changes.Count >= 256) { _omitted++; return; }
        long last = _changes.Count == 0 ? _start : _changes[^1].Tick;
        _changes.Add((Math.Max(tick, last), id));
    }

    internal void Input(int id)
    {
        if (!_started || id == 0) return;
        if (_inputs.Count < 128 || _inputs.ContainsKey(id)) _inputs[id] = _inputs.GetValueOrDefault(id) + 1;
        else _omittedInputs++;
    }

    internal UsageInterval? Sample(long now, int actualForeground, bool inputTracking)
        => Sample(now, actualForeground, inputTracking, true, out _);

    internal UsageInterval? Sample(long now, int actualForeground, bool inputTracking, bool foregroundHook, out FocusCoverage coverage)
    {
        coverage = Snapshot(now, actualForeground, inputTracking, foregroundHook);
        UsageInterval? result = null;
        if (coverage.Status == "observed")
        {
            var spans = new List<ForegroundSpan>(_changes.Count + 1);
            long previous = _start;
            int current = _first;
            foreach (var (tick, id) in _changes)
            {
                long end = Math.Min(tick, now);
                if (end > previous) spans.Add(new(current, (int)(previous - _start), (int)(end - previous)));
                previous = end; current = id;
            }
            if (now > previous) spans.Add(new(current, (int)(previous - _start), (int)(now - previous)));
            result = new(spans.ToArray(), _omittedInputs == 0 ? coverage.PointerActions : null);
        }
        _start = now; _lastEvent = _first = actualForeground; _started = true; _omitted = _omittedInputs = 0;
        _changes.Clear(); _inputs.Clear();
        return result;
    }

    internal FocusCoverage Snapshot(long now, int? actualForeground, bool inputTracking, bool foregroundHook)
    {
        string status = !foregroundHook ? "hook-unavailable" : !_started ? "initial" :
            _omitted > 0 ? "overflow" : now - _start is <= 0 or > 8000 ? "sampling-gap" :
            actualForeground == null ? "end-unknown" : actualForeground != _lastEvent ? "unmatched-foreground" : "observed";
        var inputs = inputTracking && _started ? _inputs.Select(p => new WindowInputCount(p.Key, p.Value)).ToArray() : null;
        return new(_started ? _start : null, now, _started ? _first : null, actualForeground, status,
            _changes.Select(c => new FocusChange(c.Tick, c.Id)).ToArray(), _omitted, inputs, inputTracking && _started ? _omittedInputs : null);
    }

    internal static CoUseSample[]? CoUse(UsageInterval interval, WorkspaceFrame? previous, WorkspaceFrame current)
    {
        // 共现时长来自相邻两帧都可见的窗口。截断帧可能漏掉其中一扇，此时应保留缺测，避免把它计作从未同用。
        if (previous == null || previous.Truncated || current.Truncated) return null;
        var stable = previous.Windows.Where(w => w.VisibleRatio > 0 && (w.Manageable || w.SurfaceState != null)).ToDictionary(w => w.Id);
        var peers = current.Windows.Where(w => w.VisibleRatio > 0 && stable.TryGetValue(w.Id, out var before) &&
            w.Rect == before.Rect && w.WorkArea == before.WorkArea).Select(w => w.Id).ToArray();
        var captured = current.Windows.Concat(previous.Windows).Where(w => w.Manageable || w.SurfaceState != null).Select(w => w.Id).ToHashSet();
        return interval.Foreground.Where(s => s.Id != 0 && captured.Contains(s.Id)).GroupBy(s => s.Id)
            .SelectMany(group => peers.Where(id => id != group.Key).Select(id => new CoUseSample(group.Key, id, group.Sum(s => s.DurationMs))))
            .ToArray();
    }
}
