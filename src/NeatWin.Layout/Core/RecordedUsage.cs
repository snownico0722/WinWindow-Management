namespace NeatWin.Core;

// Offsets are milliseconds from the beginning of this interval. Window 0 is an
// untracked foreground (shell, WinWindow, or another surface absent from the frame).
internal sealed record ForegroundSpan(int Id, int OffsetMs, int DurationMs);
internal sealed record WindowInputCount(int Id, int Count);
internal sealed record UsageInterval(ForegroundSpan[] Foreground, WindowInputCount[]? PointerActions);
internal sealed record CoUseSample(int ForegroundId, int VisiblePeerId, int ForegroundMsWithPeerVisibleAtBothEnds);

internal sealed class RecordedUsage
{
    private readonly List<(long Tick, int Id)> _changes = [];
    private readonly Dictionary<int, int> _inputs = [];
    private long _start;
    private int _first;
    private bool _started, _overflow;

    internal void Reset()
    {
        _started = false; _overflow = false;
        _changes.Clear(); _inputs.Clear();
    }

    internal void Foreground(long tick, int id)
    {
        if (!_started || tick < _start || (_changes.Count == 0 ? _first : _changes[^1].Id) == id) return;
        if (_changes.Count >= 256) { _overflow = true; return; }
        long last = _changes.Count == 0 ? _start : _changes[^1].Tick;
        _changes.Add((Math.Max(tick, last), id));
    }

    internal void Input(int id)
    {
        if (_started && id != 0 && (_inputs.Count < 128 || _inputs.ContainsKey(id)))
            _inputs[id] = _inputs.GetValueOrDefault(id) + 1;
    }

    internal UsageInterval? Sample(long now, int actualForeground, bool inputTracking)
    {
        UsageInterval? result = null;
        if (_started && !_overflow && now - _start is > 0 and <= 8000 &&
            actualForeground == (_changes.Count == 0 ? _first : _changes[^1].Id))
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
            result = new(spans.ToArray(), inputTracking ? _inputs.Select(p => new WindowInputCount(p.Key, p.Value)).ToArray() : null);
        }
        _start = now; _first = actualForeground; _started = true; _overflow = false;
        _changes.Clear(); _inputs.Clear();
        return result;
    }

    internal static CoUseSample[]? CoUse(UsageInterval interval, WorkspaceFrame? previous, WorkspaceFrame current)
    {
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
