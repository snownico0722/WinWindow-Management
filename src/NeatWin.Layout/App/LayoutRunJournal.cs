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
    internal Action<LayoutRunObservation>? SnapshotObserver { get; set; }

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

    internal void Record(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan, string trigger, string[] notes,
        IntentLayoutPlan? baseline = null, IntentLayoutPlan? proposal = null, IntentLayoutPlan? scored = null,
        TaskLayoutContext? context = null, SmartTidyStrength? strength = null, bool? fullDesktop = null)
    {
        try
        {
            var frame = _recorder?.Capture(before) ?? _identity.Capture(before);
            var ids = before.ToDictionary(w => w.Handle, w => _identity.Id(w));
            var targets = plan.Moves.Select(m => new PlannedWindow(ids[m.Window.Handle], m.TargetVisualRect)).ToArray();
            var layers = plan.Layers.Select(l => l.FrontToBack.Select(w => ids[w.Handle]).ToArray()).ToArray();
            var groups = plan.Groups.Select(g => new PlannedGroup(g.Handles.Select(h => ids[h]).ToArray(),
                g.Selected, g.StackEvidence, g.Candidates, g.Task)).ToArray();
            ProjectedPlan? Project(IntentLayoutPlan? p) => p == null ? null : new(
                p.Moves.Select(m => new PlannedWindow(ids[m.Window.Handle], m.TargetVisualRect)).ToArray(),
                p.Layers.Select(l => l.FrontToBack.Select(w => ids[w.Handle]).ToArray()).ToArray(),
                p.Groups.Select(g => g.Selected).ToArray());
            var observation = new LayoutRunObservation(5, _session, Guid.NewGuid().ToString("N"), trigger,
                frame, targets, layers, groups, null, "requested-not-yet-verified", notes, Project(baseline), Project(proposal), Project(scored), context?.Rules,
                context == null ? null : RecordedTaskSignals.From(context, ids), strength, fullDesktop);
            SnapshotObserver?.Invoke(observation);
            var epoch = _recorder?.RecordingEpoch ?? 0;
            var recorded = TryAppend(observation, epoch);
            if (recorded && (plan.Moves.Count > 0 || trigger == "undo"))
                _recorder?.Applied(observation.RequestId, trigger == "undo" ? [] : plan.Moves.Select(m => m.Window.Handle));
            if (_pending.Count >= 8) _pending.RemoveAt(0);
            foreach (var old in _pending) old.Intervened = true;
            var movesById = plan.Moves.ToDictionary(m => ids[m.Window.Handle], m => (m.Window.Handle, m.Window.ProcessId, m.Window.Dpi, Before: m.Window.VisualRect, Target: m.TargetVisualRect));
            _pending.Add(new(observation, before.Select(w => w.Handle).ToHashSet(), Environment.TickCount64, epoch, recorded, movesById));
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
                var snapshot = _manager.Capture();
                var frame = _recorder?.Capture(snapshot) ?? _identity.Capture(snapshot);
                var requested = pending.Observation;
                var matches = requested.Targets.All(t => frame.Windows.Any(w => w.Id == t.Id && Near(w.Rect, t.Target))) &&
                    requested.Layers.All(order => frame.Windows.Where(w => order.Contains(w.Id)).OrderBy(w => w.ZOrder)
                        .Select(w => w.Id).SequenceEqual(order));
                bool intervened = pending.Intervened || NativeWindowActivity.IsMoving;
                if (!intervened && _recorder != null && pending.Epoch == _recorder.RecordingEpoch && pending.MovesById != null)
                {
                    var snapshotByHandle = snapshot.ToDictionary(w => w.Handle);
                    foreach (var (id, move) in pending.MovesById)
                    {
                        var actualWin = frame.Windows.FirstOrDefault(w => w.Id == id);
                        if (actualWin != null && snapshotByHandle.TryGetValue(move.Handle, out var snapWin) && snapWin.ProcessId == move.ProcessId && snapWin.Dpi == move.Dpi)
                            _recorder.SizeMemory.Observe(move.Before, move.Target, actualWin.Rect, move.Dpi, move.Handle, move.ProcessId, _recorder.TryReadAppKey(snapWin));
                    }
                }
                var actual = requested with { Actual = frame, Outcome = intervened ? "intervened" :
                    matches ? "observed-match" : "observed-difference" };
                // Pause/clear stops writes, not the runtime's placement verification. An old
                // completion must not resurrect cleared data or become a new recording session.
                if (pending.Recorded) TryAppend(actual, pending.Epoch);
                SnapshotObserver?.Invoke(actual);
                Completed?.Invoke(actual);
            }
            catch { }
        }
        if (_pending.Count == 0) _timer.Stop();
    }

    private static bool Near(RectI a, RectI b) => WindowStateRules.Near(a, b);
    public void Dispose() { _timer.Stop(); _timer.Dispose(); _pending.Clear(); }
    private sealed class Pending(LayoutRunObservation observation, HashSet<nint> handles, long tick, long epoch, bool recorded,
        Dictionary<int, (nint Handle, uint ProcessId, uint Dpi, RectI Before, RectI Target)>? movesById = null)
    {
        internal LayoutRunObservation Observation { get; } = observation;
        internal HashSet<nint> Handles { get; } = handles;
        internal long Tick { get; } = tick;
        internal long Epoch { get; } = epoch;
        internal bool Recorded { get; } = recorded;
        internal Dictionary<int, (nint Handle, uint ProcessId, uint Dpi, RectI Before, RectI Target)>? MovesById { get; } = movesById;
        internal bool Intervened { get; set; }
    }
}
