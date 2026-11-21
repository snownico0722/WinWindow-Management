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
    private readonly Action<LayoutRunObservation>? _snapshotObserver;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 600 };
    private readonly List<Pending> _pending = [];
    internal event Action<LayoutRunObservation>? Completed;
    internal event Action? ReplanAroundBlockedWindow;
    internal WindowPlacementFeedback Feedback { get; } = new();
    internal LayoutRunJournal(WindowManager manager, IntentReferenceStore store, RecorderSession? recorder = null,
        Action<LayoutRunObservation>? snapshotObserver = null)
    {
        _manager = manager; _store = store; _recorder = recorder;
        _snapshotObserver = snapshotObserver;
        _identity = recorder?.Identity ?? new ObservationIdentityTracker();
        _session = recorder?.SessionId ?? Guid.NewGuid().ToString("N");
        _timer.Tick += (_, _) => CompleteDue();
    }

    internal void MarkGesture(nint handle)
    {
        Feedback.Forget(handle);
        foreach (var pending in _pending)
            if (pending.Handles.Contains(handle)) pending.Intervened = true;
    }

    internal string? Record(IReadOnlyList<WindowSnapshot> before, LayoutApplication application, string trigger,
        IntentLayoutPlan? baseline = null, IntentLayoutPlan? proposal = null, IntentLayoutPlan? scored = null,
        TaskLayoutContext? context = null, SmartTidyStrength? strength = null, bool? fullDesktop = null, bool allowReplan = false, string? undoOfRequest = null,
        ManualWindowGesture? releaseInput = null, bool reusedPreview = false)
    {
        try
        {
            var plan = application.Plan;
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
            var refused = application.Failures.Select(f => new RejectedWindowMove(ids[f.Move.Window.Handle],
                f.Move.TargetVisualRect, f.Win32Error)).ToArray();
            RecordedGestureTiming? timing = null;
            if (releaseInput?.ReleasedTick is long release && ids.TryGetValue(releaseInput.WindowHandle, out int windowId) &&
                before.Any(w => w.Handle == releaseInput.WindowHandle && w.ProcessId == releaseInput.ProcessId))
            {
                long? Elapsed(long? tick) => tick >= release ? tick - release : null;
                timing = new(windowId, reusedPreview, Elapsed(application.FirstGeometrySubmittedTick), Elapsed(application.GestureWindowSubmittedTick));
            }
            var observation = new LayoutRunObservation(6, _session, Guid.NewGuid().ToString("N"), trigger,
                frame, targets, layers, groups, null, refused.Length > 0 ? "submission-failed" : "requested-not-yet-verified",
                application.Notes, Project(baseline), Project(proposal), Project(scored), context?.Rules,
                context == null ? null : RecordedTaskSignals.From(context, ids, before), strength, fullDesktop, refused.Length == 0 ? null : refused,
                application.MinimizeRequested is { } minimized ? ids[minimized.Handle] : null, UndoOfRequest: undoOfRequest, GestureTiming: timing);
            _snapshotObserver?.Invoke(observation);
            var epoch = _recorder?.RecordingEpoch ?? 0;
            var recorded = TryAppend(observation, epoch);
            if (recorded && (plan.Moves.Count > 0 || trigger == "undo"))
                _recorder?.Applied(observation.RequestId, trigger, application);
            if (_pending.Count >= 8) _pending.RemoveAt(0);
            foreach (var old in _pending) old.Intervened = true;
            _pending.Add(new(observation, before.Select(w => w.Handle).ToHashSet(), Environment.TickCount64, epoch,
                recorded, plan.Moves, application.Failures, allowReplan, application.MinimizeRequested) { Intervened = application.Interrupted });
            _timer.Start();
            return observation.RequestId;
        }
        catch { return null; } // Diagnostics must not prevent a layout or an undo.
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
            try
            {
                var snapshot = _manager.Capture();
                var current = snapshot.ToDictionary(w => w.Handle);
                var frame = _recorder?.Capture(snapshot) ?? _identity.Capture(snapshot);
                var requested = pending.Observation;
                bool intervened = pending.Intervened || NativeWindowActivity.IsMoving;
                bool blocked = false;
                bool smart = requested.Trigger is "explicit" or "automatic-gesture" or "automatic-workspace";
                if (smart && !intervened)
                {
                    // A successful asynchronous call may still be queued. Check it twice before
                    // treating a completely unchanged rect as an obstacle for the next plan.
                    var unchanged = pending.Moves.Where(m => current.TryGetValue(m.Window.Handle, out var w) &&
                        WindowStateRules.Matches(m.Window, w, m.Window.VisualRect) &&
                        !Near(w.VisualRect, m.TargetVisualRect)).ToArray();
                    if (unchanged.Length > 0 && pending.UnchangedOnFirstRead == null)
                    {
                        pending.UnchangedOnFirstRead = unchanged.Select(m => m.Window.Handle).ToHashSet();
                        continue;
                    }
                    var candidates = unchanged.Where(m => pending.UnchangedOnFirstRead?.Contains(m.Window.Handle) == true)
                        .Concat(pending.Failures.Select(f => f.Move));
                    foreach (var move in candidates)
                        if (current.TryGetValue(move.Window.Handle, out var observed))
                            blocked |= Feedback.BlockUnchanged(move, observed, Environment.TickCount64);
                }

                var matches = requested.Targets.All(t => frame.Windows.Any(w => w.Id == t.Id && Near(w.Rect, t.Target))) &&
                    requested.Layers.All(order => frame.Windows.Where(w => order.Contains(w.Id)).OrderBy(w => w.ZOrder)
                        .Select(w => w.Id).SequenceEqual(order));
                bool? minimized = null;
                if (pending.Minimize is { } leaving)
                {
                    NativeMethods.GetWindowThreadProcessId(leaving.Handle, out var pid);
                    minimized = pid == leaving.ProcessId && NativeMethods.IsIconic(leaving.Handle) &&
                        _identity.Id(leaving) == requested.MinimizeTarget;
                    matches &= minimized.Value;
                }
                if (!intervened && _recorder != null && pending.Epoch == _recorder.RecordingEpoch)
                {
                    foreach (var move in pending.Moves)
                    {
                        var old = move.Window;
                        if (current.TryGetValue(old.Handle, out var w) && old.ProcessId == w.ProcessId && old.Dpi == w.Dpi)
                            _recorder.SizeMemory.Observe(old.VisualRect, move.TargetVisualRect, w.VisualRect,
                                w.Dpi, w.Handle, w.ProcessId, _recorder.TryReadAppKey(w));
                    }
                }
                var actual = requested with { Actual = frame, MinimizeVerified = minimized, Outcome = intervened ? "intervened" :
                    pending.Failures.Count > 0 ? "submission-failed" : matches ? "observed-match" : "observed-difference" };
                // Pause/clear stops writes, not the runtime's placement verification. An old
                // completion must not resurrect cleared data or become a new recording session.
                if (pending.Recorded) TryAppend(actual, pending.Epoch);
                _snapshotObserver?.Invoke(actual);
                Completed?.Invoke(actual);
                if (blocked && pending.AllowReplan) ReplanAroundBlockedWindow?.Invoke();
            }
            catch { }
            _pending.Remove(pending);
        }
        if (_pending.Count == 0) _timer.Stop();
    }

    private static bool Near(RectI a, RectI b) => WindowStateRules.Near(a, b);
    public void Dispose() { _timer.Stop(); _timer.Dispose(); _pending.Clear(); }
    private sealed class Pending(LayoutRunObservation observation, HashSet<nint> handles, long tick, long epoch, bool recorded,
        IReadOnlyList<TidyMove> moves, IReadOnlyList<WindowMoveFailure> failures, bool allowReplan, WindowSnapshot? minimize)
    {
        internal LayoutRunObservation Observation { get; } = observation;
        internal HashSet<nint> Handles { get; } = handles;
        internal long Tick { get; } = tick;
        internal long Epoch { get; } = epoch;
        internal bool Recorded { get; } = recorded;
        internal IReadOnlyList<TidyMove> Moves { get; } = moves;
        internal IReadOnlyList<WindowMoveFailure> Failures { get; } = failures;
        internal bool AllowReplan { get; } = allowReplan;
        internal WindowSnapshot? Minimize { get; } = minimize;
        internal HashSet<nint>? UnchangedOnFirstRead { get; set; }
        internal bool Intervened { get; set; }
    }
}
