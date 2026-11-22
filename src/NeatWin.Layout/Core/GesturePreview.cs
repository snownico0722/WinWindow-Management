namespace NeatWin.Core;

// Only geometry crosses the worker boundary. Attention, exclusions, visibility, gesture speed,
// and the identity of the current task partner are resolved by the normal mouse-up planner.
internal sealed class GesturePreview
{
    private readonly Dictionary<nint, IReadOnlyList<FollowHandAssistant.Candidate>> _choices = [];
    private readonly Dictionary<nint, WindowSnapshot> _windows;
    private readonly ManualWindowGesture _frame;
    private readonly SmartTidyStrength _strength;
    private readonly int _gap, _drift;
    private readonly WindowSnapshot _moved;
    private readonly (bool Left, bool Right, bool Center) _directions;
    internal bool Used { get; private set; }
    internal long SampledAt { get; }

    internal GesturePreview(ManualWindowGesture frame, Dictionary<nint, WindowSnapshot> windows,
        WindowSnapshot moved, SmartTidyStrength strength, int gap, long sampledAt)
    {
        _frame = frame; _moved = moved; _strength = strength; _gap = gap; SampledAt = sampledAt; _windows = windows;
        var scale = moved.Dpi == 0 ? 1 : Math.Max(.5, moved.Dpi / 96d);
        int radius = FollowHandAssistant.SnapRadius(strength, scale);
        _drift = (int)Math.Ceiling(16 * scale);
        _directions = GesturePlacement.Directions(frame, scale);
        _choices[0] = FollowHandAssistant.Geometry(frame, moved, null, radius, gap, scale);
        foreach (var other in _windows.Values)
        {
            if (other.Handle == moved.Handle || !other.IsManageable) continue;
            // An edge must be near another edge for every peer snap (including gap and resize).
            // Expand by allowable release drift so a newly reached target is never filtered out.
            var a = frame.EndRect;
            var b = other.VisualRect;
            int range = radius * 2 + gap + _drift;
            static bool Near(int v, int x, int y, int bound) => Math.Abs(v - x) <= bound || Math.Abs(v - y) <= bound;
            if (Near(a.Left, b.Left, b.Right, range) || Near(a.Right, b.Left, b.Right, range) ||
                Near(a.Top, b.Top, b.Bottom, range) || Near(a.Bottom, b.Top, b.Bottom, range))
                _choices[other.Handle] = FollowHandAssistant.Geometry(frame, moved, b, radius, gap, scale);
        }
    }

    internal bool Matches(ManualWindowGesture release, IReadOnlyList<WindowSnapshot> live, nint foreground, long now, long? lastSample = null)
    {
        if (now - (lastSample ?? SampledAt) is < 0 or > 450 || release.WindowHandle != _moved.Handle ||
            release.ProcessId != _moved.ProcessId || foreground != _moved.Handle ||
            release.StartRect != _frame.StartRect || release.WorkArea != _frame.WorkArea ||
            release.Kind != _frame.Kind || !Within(_frame.EndRect, release.EndRect, _drift)) return false;
        int members = 0;
        foreach (var w in live)
        {
            if (w.MonitorHandle != _moved.MonitorHandle) continue;
            members++;
            if (!_windows.TryGetValue(w.Handle, out var prior)) return false;
            if (w.Handle == _moved.Handle)
            {
                if (w.VisualRect != release.EndRect || w with { VisualRect = prior.VisualRect, OuterRect = prior.OuterRect } != prior)
                    return false;
            }
            else if (w != prior) return false;
        }
        return members == _windows.Count;
    }

    internal bool CoversSample(ManualWindowGesture gesture) => gesture.WindowHandle == _frame.WindowHandle &&
        gesture.ProcessId == _frame.ProcessId && gesture.StartRect == _frame.StartRect && gesture.WorkArea == _frame.WorkArea &&
        gesture.Kind == _frame.Kind && Within(_frame.EndRect, gesture.EndRect, 3);

    internal bool Exact(ManualWindowGesture gesture) => _frame.EndRect == gesture.EndRect;

    internal (bool Left, bool Right, bool Center) Directions()
    {
        Used = true;
        return _directions;
    }

    internal IReadOnlyList<FollowHandAssistant.Candidate>? For(ManualWindowGesture gesture, nint source,
        SmartTidyStrength strength, int gap)
    {
        if (_strength != strength || _gap != gap) return null;
        Used = true;
        if (!_choices.TryGetValue(source, out var choices)) return [];
        // A small final movement needs fresh coordinates. Only the screen and potential
        // neighbors are rebuilt. Scoring still sees every real visible occluder.
        return Exact(gesture) ? choices : null;
    }

    internal static bool Within(RectI a, RectI b, int amount) =>
        Math.Abs(a.Left - b.Left) <= amount && Math.Abs(a.Right - b.Right) <= amount &&
        Math.Abs(a.Top - b.Top) <= amount && Math.Abs(a.Bottom - b.Bottom) <= amount;
}

// A gesture has one immutable monitor membership and one preferred gap. Build both once on
// the worker, including on the first drag after startup when no earlier plan exists.
internal sealed class GesturePreviewDesktop(IReadOnlyList<WindowSnapshot> windows, IntentReferenceDocument? reference)
{
    private WindowSnapshot? _moved;
    private Dictionary<nint, WindowSnapshot>? _monitor;
    private int _gap;

    internal GesturePreview? Prepare(ManualWindowGesture frame, SmartTidyStrength strength, long sampledAt)
    {
        if (_moved == null)
        {
            _moved = windows.FirstOrDefault(w => w.Handle == frame.WindowHandle && w.IsForeground && w.IsManageable && !w.IsTopmost);
            if (_moved == null) return null;
            _monitor = windows.Where(w => w.MonitorHandle == _moved.MonitorHandle).ToDictionary(w => w.Handle);
            _gap = Math.Clamp(IntentEvidence.Resolve(reference, _moved.WorkArea, _moved.Dpi, DateTimeOffset.UtcNow).GapPixels, 0, 48);
        }
        if (_moved.Handle != frame.WindowHandle || _moved.ProcessId == 0 || _moved.ProcessId != frame.ProcessId ||
            _moved.WorkArea != frame.WorkArea) return null;
        return new(frame, _monitor!, _moved, strength, _gap, sampledAt);
    }
}

// One worker for all gestures, one replaceable pending frame and one result.
// No completion callback into the UI, and no native window placement API on this path.
internal sealed class GesturePreviewCache
{
    private sealed record Session(Lazy<GesturePreviewDesktop> Desktop, SmartTidyStrength Strength);
    private sealed record Request(Session Session, ManualWindowGesture Frame, long Tick);
    private readonly object _gate = new();
    private Session? _session;
    private Request? _pending;
    private GesturePreview? _ready;
    private Task? _worker;
    private long _lastSample;

    internal void Begin(SmartTidyStrength strength, Func<GesturePreviewDesktop> capture)
    {
        lock (_gate)
        {
            _ready = null; _pending = null;
            _session = new(new(capture), strength);
        }
    }

    internal Task Offer(ManualWindowGesture frame, long now)
    {
        lock (_gate)
        {
            if (_session == null) return Task.CompletedTask;
            if (_worker == null && _ready?.CoversSample(frame) == true)
            {
                _lastSample = now;
                return Task.CompletedTask;
            }
            // 拖动时鼠标事件可能快过求解。只保留最新的待算位置，避免松手后还要等后台算完一串已经经过的坐标。
            _pending = new(_session, frame, now);
            return _worker ??= Task.Run(Work);
        }
    }

    private void Work()
    {
        while (true)
        {
            Request work;
            lock (_gate)
            {
                if (_pending == null) { _worker = null; return; }
                work = _pending; _pending = null;
                if (_ready?.CoversSample(work.Frame) == true)
                {
                    _lastSample = work.Tick;
                    continue;
                }
            }
            GesturePreview? candidate = null;
            try
            {
                var desktop = work.Session.Desktop.Value;
                candidate = desktop.Prepare(work.Frame, work.Session.Strength, work.Tick);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Gesture preview unavailable: {ex.Message}");
            }
            lock (_gate)
            {
                if (!ReferenceEquals(work.Session, _session) || _pending != null) continue;
                _ready = candidate;
                _lastSample = work.Tick;
            }
        }
    }

    internal GesturePreview? Take(ManualWindowGesture gesture, IReadOnlyList<WindowSnapshot> live, nint foreground, long now)
    {
        GesturePreview? ready;
        long lastSample;
        lock (_gate)
        {
            lastSample = _lastSample;
            ready = _ready; _ready = null; _session = null; _pending = null;
        }
        return ready?.Matches(gesture, live, foreground, now, lastSample) == true ? ready : null;
    }

    internal void Clear()
    {
        lock (_gate) { _session = null; _ready = null; _pending = null; }
    }
}
