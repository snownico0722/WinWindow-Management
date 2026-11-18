using NeatWin.Core;
using NeatWin.Reference;
using NeatWin.Recording;
using NeatWin.Windows;

namespace NeatWin.App;

/// <summary>Requested targets are not evidence of successful placement.</summary>
internal sealed class LayoutRunJournal : IDisposable
{
    private readonly WindowManager _manager;
    private readonly IntentReferenceStore _store;
    private readonly ObservationIdentityTracker _identity;
    private readonly string _session;
    private readonly RecorderSession? _recorder;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 600 };
    private readonly List<Pending> _pending = [];
    internal event Action<LayoutRunObservation>? Completed;

    internal LayoutRunJournal(WindowManager manager, IntentReferenceStore store, RecorderSession? recorder = null)
    {
        _manager = manager; _store = store; _recorder = recorder;
        _identity = recorder?.Identity ?? new ObservationIdentityTracker();
        _session = recorder?.SessionId ?? Guid.NewGuid().ToString("N");
        _timer.Tick += (_, _) => CompleteDue();
    }

    internal void MarkGesture(nint handle)
    {
        foreach (var pending in _pending)
            if (pending.Handles.Contains(handle)) pending.Intervened = true;
    }

    internal void Record(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan, string trigger, string[] notes)
    {
        try
        {
            var frame = _identity.Capture(before);
            var ids = before.ToDictionary(w => w.Handle, w => _identity.Id(w));
            var targets = plan.Moves.Select(m => new PlannedWindow(ids[m.Window.Handle], m.TargetVisualRect)).ToArray();
            var layers = plan.Layers.Select(l => l.FrontToBack.Select(w => ids[w.Handle]).ToArray()).ToArray();
            var groups = plan.Groups.Select(g => new PlannedGroup(g.Handles.Select(h => ids[h]).ToArray(),
                g.Selected, g.StackEvidence, g.Candidates, g.Task)).ToArray();
            var observation = new LayoutRunObservation(2, _session, Guid.NewGuid().ToString("N"), trigger,
                frame, targets, layers, groups, null, "requested-not-yet-verified", notes);
            var epoch = _recorder?.RecordingEpoch ?? 0;
            var recorded = TryAppend(observation, epoch);
            if (_pending.Count >= 8) _pending.RemoveAt(0);
            foreach (var old in _pending) old.Intervened = true;
            _pending.Add(new(observation, before.Select(w => w.Handle).ToHashSet(), Environment.TickCount64, epoch, recorded));
            _timer.Start();
        }
        catch { } // Diagnostics must not prevent a layout or an undo.
    }

    private bool TryAppend(LayoutRunObservation observation, long epoch)
    {
        if (_recorder is not null && (!_recorder.CanWriteDiagnostics || _recorder.RecordingEpoch != epoch)) return false;
        try { _store.AppendPlan(observation); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning($"Layout diagnostic write failed: {ex.Message}"); return false; }
    }

    private void CompleteDue()
    {
        foreach (var pending in _pending.Where(p => Environment.TickCount64 - p.Tick >= 600).ToArray())
        {
            _pending.Remove(pending);
            try
            {
                var frame = _identity.Capture(_manager.Capture());
                var requested = pending.Observation;
                var matches = requested.Targets.All(t => frame.Windows.Any(w => w.Id == t.Id && Near(w.Rect, t.Target))) &&
                    requested.Layers.All(order => frame.Windows.Where(w => order.Contains(w.Id)).OrderBy(w => w.ZOrder)
                        .Select(w => w.Id).SequenceEqual(order));
                var actual = requested with { Actual = frame, Outcome = pending.Intervened || NativeWindowActivity.IsMoving ? "intervened" :
                    matches ? "observed-match" : "observed-difference" };
                // Pause/clear stops writes, not the runtime's placement verification. An old
                // completion must not resurrect cleared data or become a new recording session.
                if (pending.Recorded) TryAppend(actual, pending.Epoch);
                Completed?.Invoke(actual);
            }
            catch { }
        }
        if (_pending.Count == 0) _timer.Stop();
    }

    private static bool Near(RectI a, RectI b) => WindowStateRules.Near(a, b);
    public void Dispose() { _timer.Stop(); _timer.Dispose(); _pending.Clear(); }
    private sealed class Pending(LayoutRunObservation observation, HashSet<nint> handles, long tick, long epoch, bool recorded)
    {
        internal LayoutRunObservation Observation { get; } = observation;
        internal HashSet<nint> Handles { get; } = handles;
        internal long Tick { get; } = tick;
        internal long Epoch { get; } = epoch;
        internal bool Recorded { get; } = recorded;
        internal bool Intervened { get; set; }
    }
}
