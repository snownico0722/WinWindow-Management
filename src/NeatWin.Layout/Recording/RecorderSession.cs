using static NeatWin.App.LayoutStrings;
using NeatWin.Core;
using NeatWin.Reference;
using NeatWin.Windows;

namespace NeatWin.Recording;

internal sealed record RecorderStatus(
    bool IsPaused,
    bool IsAvailable,
    int RecordedGestures,
    int ContextRecords,
    int Excluded,
    string? Error)
{
    internal string StateText => !IsAvailable ? LayoutText.Get("Unavailable") : IsPaused ? LayoutText.Get("Paused") : LayoutText.Get("Recording");
}

/// <summary>
/// Shared recorder engine used by the main application and the legacy standalone recorder UI.
/// It records window geometry, input observations, original window identities and optional change masks.
/// </summary>
internal sealed partial class RecorderSession : IDisposable
{
    private readonly DesktopActivity _activity;
    private readonly RecordedUsage _usage = new();
    private ActivitySample? _latest;
    private int _sampleTextFocus;
    internal Func<IReadOnlyList<WindowSnapshot>, long, LayoutFocus>? ReadPlacementFocus { get; init; }
    internal bool PointerCaptureAvailable { get; set; }
    internal void RecordPointer(nint handle, uint pid)
    {
        if (CanWriteDiagnostics) _usage.Input(_identity.Id(handle, pid));
    }
    private long _sampleTick;
    private WorkspaceFrame? _savedGeometry;
    private long _geometryTick;
    private readonly Dictionary<nint, SubmittedPlacement> _lastPlan = [];
    private readonly List<GestureEvent> _gestureEvents = [];
    internal Action<string, GestureEvent>? GestureObserver { get; init; }
    internal Func<bool>? SnapshotWantsGestures { get; init; }
    private bool ObserveGestures => CanWriteDiagnostics || _ownsSingleton && SnapshotWantsGestures?.Invoke() == true;
    private int _gestureSequence, _droppedGestureEvents;
    private long _senseUntil;
    private bool LiveSensing => _senseUntil > Environment.TickCount64;
    internal WindowSizeMemory SizeMemory { get; } = new();

    internal void SetLiveSensing(AutomaticLayoutMode mode, bool manualRequest = false)
    {
        if (_disposed || !_ownsSingleton) return;
        _senseUntil = AutomaticLayoutPolicy.UsesFullPlanner(mode) ? long.MaxValue :
            manualRequest ? Environment.TickCount64 + 20_000 : 0;
        _timer.Enabled = !_paused || LiveSensing;
        if (!_timer.Enabled) ResetActivity();
    }
    private bool _visualSampling;
    internal bool VisualSampling
    {
        get => _visualSampling;
        set
        {
            if (_visualSampling == value) return;
            _visualSampling = value;
            _latest = null;
            _activity.ResetVisual();
        }
    }
    private readonly WindowManager _manager = new();
    private readonly IntentReferenceStore _store;
    private readonly ObservationIdentityTracker _identity = new();
    private readonly List<Pending> _pending = [];
    private LayoutHabit[] _instanceHabits = [];
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Mutex _singleton;
    private readonly bool _ownsSingleton;
    private readonly NativeMethods.WinEventProc _moveCallback;
    private readonly NativeMethods.WinEventProc _contextCallback;
    private readonly nint _moveHook;
    private readonly nint _contextHook;
    private readonly nint _foregroundHook, _menuHook;
    private readonly System.Windows.Forms.Timer _timer;
    private WorkspaceFrame? _lastContext;
    private Active? _active;
    private bool _contextDirty;
    private string _contextCause = "state-change-unknown";
    private bool _paused;
    private bool _disposed;
    private bool _handlingEvent;
    private int _recorded;
    private int _contextRecords;
    private int _excluded;
    private string? _error;
    private long _lastMaintenanceTick = long.MinValue;

    private const int MaxObservedSurfaces = 512;
    private readonly Dictionary<(nint Child, uint ChildPid, nint Host, uint HostPid), SurfaceEvent> _observedSurfaces = [];
    internal Action<SurfaceEvent>? SurfaceObserver { get; init; }
    internal Func<bool>? SnapshotWantsSurfaces { get; init; }

    internal void ResetSurfaceObservations() => _observedSurfaces.Clear();

    internal void SurfaceChanged(SurfaceMatch match, string action)
    {
        var key = (match.Child.Handle, match.Child.ProcessId, match.Host.Handle, match.Host.ProcessId);
        _observedSurfaces.Remove(key, out var suppression);
        bool writeDisk = CanWriteDiagnostics;
        bool snapshot = _ownsSingleton && !_disposed && SnapshotWantsSurfaces?.Invoke() == true;
        if (!writeDisk && !snapshot) return;

        long tick = Environment.TickCount64;
        var time = DateTimeOffset.UtcNow;
        bool starting = action != "release";
        // 打开时记录本次选择（局部或独立）及身份。解除可能由销毁或句柄重用触发，不能重新查询系统去猜测原窗口。
        // 暂停长期记录时仍可为快照读取身份；兼容旧规则只需读取已有的匹配键。
        var item = suppression is { } prior && !starting
            ? prior with { Action = action, Tick = tick, Time = time, ReleaseReason = match.ReleaseReason,
                ReleasedChildRect = match.ReleasedChildRect, ReleasedHostRect = match.ReleasedHostRect }
            : new SurfaceEvent(1, _session, tick, time, action, match.Rule,
                starting ? _identity.Id(match.Child) : null, starting ? _identity.Id(match.Host) : null,
                starting ? _activity.AppKey(match.Child, writeDisk) : null,
                starting ? _activity.AppKey(match.Host, writeDisk) : null,
                match.Child.VisualRect, match.Host.VisualRect, match.Child.Owner == match.Host.Handle,
                match.Child.NativeUtility, match.Child.IndependentFrame, match.Child.IsResizable,
                match.ReleaseReason, match.ReleasedChildRect, match.ReleasedHostRect, match.ContextPress?.Sequence,
                starting ? _activity.RawIdentity(match.Child.Handle, match.Child.ProcessId, true) : null,
                starting ? _activity.RawIdentity(match.Host.Handle, match.Host.ProcessId, true) : null, match.ContextPress?.ReleaseSequence);
        if (action == "suppress")
        {
            if (_observedSurfaces.Count >= MaxObservedSurfaces)
                _observedSurfaces.Remove(_observedSurfaces.MinBy(pair => pair.Value.Tick).Key);
            _observedSurfaces[key] = item;
        }
        if (snapshot) SurfaceObserver?.Invoke(item);
        if (writeDisk)
        {
            try { _store.AppendSurface(item); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _error = e.Message; }
        }
    }

    internal string SessionId => _session;
    internal ObservationIdentityTracker Identity => _identity;
    internal Action<nint>? WindowRetired { get; set; }
    internal long RecordingEpoch { get; private set; }
    internal bool CanWriteDiagnostics => _ownsSingleton && !_paused && !_disposed;

    internal RecorderSession(IntentReferenceStore store, bool recordingEnabled)
    {
        _store = store;
        _activity = new(store.Placement);
        _paused = !recordingEnabled;
        var directoryBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(store.DirectoryPath).ToUpperInvariant());
        var directoryId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(directoryBytes));
        _singleton = new Mutex(true, @"Local\WinWindow.Layout.Recorder." + directoryId, out _ownsSingleton);
        _moveCallback = OnMoveEvent;
        _contextCallback = OnContextEvent;

        if (!_ownsSingleton)
        {
            _paused = true;
            _error = LayoutText.Get("RecorderInUse");
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            PublishStatus();
            return;
        }

        _moveHook = NativeMethods.SetWinEventHook(NativeMethods.EventSystemMoveSizeStart,
            NativeMethods.EventSystemMoveSizeEnd, nint.Zero, _moveCallback, 0, 0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
        _foregroundHook = NativeMethods.SetWinEventHook(0x0003, 0x0003, nint.Zero, _contextCallback, 0, 0,
            NativeMethods.WinEventOutOfContext);
        _menuHook = NativeMethods.SetWinEventHook(4, 7, nint.Zero, _contextCallback, 0, 0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
        _contextHook = NativeMethods.SetWinEventHook(0x8000, 0x800C, nint.Zero, _contextCallback, 0, 0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);

        try { if (!_paused) _lastContext = Capture(_manager.Capture()); }
        catch (Exception ex) { _error = ex.Message; }
        if (_contextHook == nint.Zero || _foregroundHook == nint.Zero || _menuHook == nint.Zero)
            _error = LayoutText.Get("MonitorPartial");
        if (_moveHook == nint.Zero)
        {
            _paused = true;
            _error = LayoutText.Get("MonitorFailed");
        }

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += OnTimerTick;
        if (!_paused) _timer.Start();
        PublishStatus();
    }

    internal event Action<RecorderStatus>? StatusChanged;

    internal RecorderStatus Status => new(_paused, _ownsSingleton && _moveHook != nint.Zero,
        _recorded, _contextRecords, _excluded, _error);

    internal void TogglePause()
    {
        if (!_ownsSingleton || _moveHook == nint.Zero)
        {
            PublishStatus();
            return;
        }
        if (!_paused)
        {
            EndUnobservedGesture("paused", Environment.TickCount64);
            RecordFocusBoundary("pause");
            FlushInteractions();
            FlushGestureEvents();
        }
        _paused = !_paused;
        _timer.Enabled = !_paused || LiveSensing;
        RecordingEpoch++;
        _active = null;
        _pending.Clear();
        _instanceHabits = [];
        ResetActivity();
        _lastContext = null;
        _contextDirty = !_paused;
        PublishStatus();
    }

    internal void OpenDataDirectory()
    {
        Directory.CreateDirectory(_store.DirectoryPath);
        _store.Rules.EnsureTemplate();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_store.DirectoryPath)
        {
            UseShellExecute = true,
        });
    }

    internal void ExportRecords(string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RecordFocusBoundary("export");
            FlushInteractions();
            FlushGestureEvents();
            if (_interactionBatch.Count > 0 || _gestureEvents.Count > 0) throw new IOException(_error);
            _store.ExportRecords(temporary);
            File.Move(temporary, destination, overwrite: true);
            _error = null;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            PublishStatus();
        }
    }

    internal void Clear()
    {
        RecordingEpoch++;
        if (!_ownsSingleton) throw new InvalidOperationException(LayoutText.Get("RecorderInUse"));
        _gestureEvents.Clear();
        _droppedGestureEvents = 0;
        _interactionBatch.Clear(); _droppedInteractions = 0; InputHistory.Clear();
        _store.Clear();
        SizeMemory.Clear();
        _instanceHabits = [];
        ResetActivity();
        _pending.Clear();
        _active = null;
        _lastContext = null;
        _recorded = 0;
        _contextRecords = 0;
        _excluded = 0;
        _error = null;
        _contextDirty = !_paused;
        PublishStatus();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || !_ownsSingleton) return;
        if (_paused && !LiveSensing) { _timer.Stop(); ResetActivity(); return; }
        if (CanWriteDiagnostics && (_lastMaintenanceTick == long.MinValue || Environment.TickCount64 - _lastMaintenanceTick >= 3_600_000))
        {
            _lastMaintenanceTick = Environment.TickCount64;
            try { _store.Prune(); }
            catch (Exception ex) { _error = LayoutText.Format("RecordCleanupFailed", ex.Message); }
        }
        if (CanWriteDiagnostics)
        {
            if (_interactionBatch.Count >= 128 || Environment.TickCount64 - _lastInteractionFlush >= 1000) FlushInteractions();
            FlushGestureEvents();
        }
        SampleActivity();
        FlushPending();
        CaptureContextChange();
    }

    private void OnMoveEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || (!ObserveGestures && !(_paused && LiveSensing && _store.Enabled)) || _handlingEvent || hwnd == nint.Zero || objectId != 0 || childId != 0 ||
            AutomationGuard.IsUtility(hwnd)) return;
        _handlingEvent = true;
        try
        {
            if (eventType == NativeMethods.EventSystemMoveSizeStart)
            {
                EndUnobservedGesture("superseded-start", EventTick(time));
                _active = null;
                var snapshot = _manager.Capture();
                var start = snapshot.FirstOrDefault(w => w.Handle == hwnd && w.IsManageable);
                if (start is null) return;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                var tick = EventTick(time);
                int instance = _identity.Id(start);
                var prior = _lastPlan.GetValueOrDefault(hwnd)?.At(instance, tick);
                var context = CanWriteDiagnostics || LiveSensing && _store.Enabled ? Capture(snapshot) : null;
                var previous = _pending.LastOrDefault(p => p.Observation.WindowId == instance && !p.Superseded);
                int batch = _gestureSequence + 1;
                if (previous != null && tick - previous.Tick is >= 0 and < AdjustmentSettleMilliseconds)
                {
                    batch = previous.Batch;
                    previous.Superseded = true;
                    // 新手势开始前核实上次松手。后续如果改了另一轴，这个端点仍能提供原轴的证据。
                    previous.Continuation = previous.Observation with
                    {
                        Outcome = AdjustmentOutcome(previous, start),
                        SettledContext = context,
                    };
                }
                // 用事件发生前的采样，避开鼠标拖动导致的焦点变化和松手后的辅助摆位。
                var usage = context == null ? null : ObservePlacementUse(snapshot, tick - 1);
                _active = new(start, pid, tick, context, AutomationGuard.Stamp(hwnd), ++_gestureSequence, instance, batch, prior, usage);
                if (ObserveGestures)
                    QueueGesture(new(_active.Number, instance, "start", tick, start.VisualRect, start.WorkArea, start.Dpi,
                        AutomationGuard.IsMarked(hwnd), prior));
            }
            else if (eventType == NativeMethods.EventSystemMoveSizeEnd)
            {
                var active = _active;
                if (active is null || active.Window.Handle != hwnd) { _excluded++; PublishStatus(); return; }
                var captured = _manager.Capture();
                var end = captured.FirstOrDefault(w => w.Handle == hwnd && w.IsManageable);
                long released = EventTick(time);
                if (end is null) { EndUnobservedGesture("unavailable", released); return; }
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != active.ProcessId || _identity.Id(end) != active.Instance)
                { EndUnobservedGesture("identity-changed", released); return; }
                _active = null;
                bool automated = active.AutomationStamp != AutomationGuard.Stamp(hwnd);
                if (ObserveGestures)
                    QueueGesture(new(active.Number, active.Instance, "release", released, end.VisualRect, end.WorkArea, end.Dpi,
                        automated || AutomationGuard.IsMarked(hwnd), active.Prior, OuterPath: active.Path.ToArray(), OmittedPathPoints: active.Omitted,
                        Change: RecordedGeometryChange.From(active.Window.VisualRect, end.VisualRect, active.Prior), StartTick: active.Tick));
                if (active.Context == null || Near(active.Window.VisualRect, end.VisualRect, 3)) return;
                var neighbors = new VisibilityAnalyzer().SelectVisibleWorkingSet(captured)
                    .Where(w => w.Window.Handle != hwnd && w.Window.MonitorHandle == end.MonitorHandle)
                    .Take(24).Select(w => w.Window.VisualRect).ToArray();
                var id = _identity.Id(end);
                var context = Capture(captured);
                var observation = new WindowAdjustmentObservation(DateTimeOffset.UtcNow, _session, id,
                    active.Window.VisualRect, end.VisualRect, active.Window.WorkArea, end.WorkArea,
                    end.Dpi, Math.Max(0, released - active.Tick), neighbors, "unconfirmed",
                    Version: 6, StartContext: active.Context, EndContext: context,
                    AfterRequest: active.Prior is { ElapsedMs: <= 30_000 } recent ? recent.RequestId : null,
                    GestureId: active.Number, PriorPlacement: active.Prior, TrialFirstGestureId: active.Batch, StartUsage: active.Usage);
                _pending.Add(new(hwnd, pid, end.MonitorHandle, observation, released, active.Batch)
                {
                    Automated = automated, ManualRelease = !automated,
                    AutomationStamp = AutomationGuard.Stamp(hwnd),
                });
                _contextDirty = true;
            }
        }
        catch (Exception ex) { _error = LayoutText.Format("CaptureFailed", ex.Message); PublishStatus(); }
        finally { _handlingEvent = false; }
    }

    private const int AdjustmentSettleMilliseconds = 2000;

    private void FlushPending()
    {
        if (_disposed || _pending.Count == 0) return;
        try
        {
            long now = Environment.TickCount64;
            var due = _pending.GroupBy(p => p.Batch).Where(g =>
                _active?.Batch != g.Key &&
                now - g.Last().Tick >= AdjustmentSettleMilliseconds).ToArray();
            if (due.Length == 0) return;
            var snapshot = _manager.Capture();
            var current = snapshot.ToDictionary(w => w.Handle);
            var settledContext = Capture(snapshot);
            foreach (var sequence in due)
            {
                var pending = sequence.ToArray();
                var candidates = pending.Select(item => item.Continuation ?? item.Observation with
                {
                    Outcome = AdjustmentOutcome(item, current.GetValueOrDefault(item.Handle)),
                    SettledContext = settledContext,
                }).ToArray();
                var selected = LayoutHabits.Coalesce(candidates);
                for (int i = 0; i < pending.Length; i++)
                {
                    var item = pending[i];
                    _pending.Remove(item);
                    var candidate = candidates[i];
                    try
                    {
                        var eligible = selected[i] != HabitAxes.None;
                        var durable = CanWriteDiagnostics && eligible ? LayoutHabits.Observe(candidate, false, selected[i]) : null;
                        if (CanWriteDiagnostics)
                        {
                            // 原始手势照录；允许成为长期证据的轴另记，供离线模型复核试摆链的归因。
                            var axes = new List<string>(3);
                            if (durable?.Edited?.HasFlag(HorizontalEdit.Position) == true) axes.Add("position");
                            if (durable?.Edited?.HasFlag(HorizontalEdit.Width) == true) axes.Add("width");
                            if (durable?.Height != null) axes.Add("height");
                            _store.AppendAdjustment(item.Observation with
                            {
                                EligiblePlacementAxes = axes.ToArray(),
                                EligiblePlacementRegion = durable is { RevealedCell: >= 0 } ? durable.RevealedCell : null,
                                Outcome = item.Superseded && candidate.Outcome != "cross-monitor" ? "superseded" : candidate.Outcome,
                                SettledContext = settledContext,
                            });
                            _recorded++;
                        }
                        if (_store.Enabled && eligible && LayoutHabits.Observe(candidate with { Outcome = "manual-release" }, byInstance: true, selected[i]) is { } live)
                            _instanceHabits = LayoutHabits.Add(_instanceHabits, live, sessionOnly: true);
                        if (durable != null) _store.Placement.Learn(durable);
                        // 整体间隙和搭档关系无法按轴拆开。只从单次稳定且没有布局回拖的选择学习。
                        if (CanWriteDiagnostics && pending.Length == 1 && !item.Superseded)
                            _store.LearnGeometry(candidate);
                        _error = null;
                    }
                    catch (Exception ex) { _error = LayoutText.Format("RecordWriteFailed", ex.Message); }
                }
            }
        }
        catch (Exception ex) { _error = LayoutText.Format("ConfirmationFailed", ex.Message); }
        PublishStatus();
    }

    private string AdjustmentOutcome(Pending item, WindowSnapshot? window)
    {
        if (item.Observation.StartWorkArea != item.Observation.WorkArea) return "cross-monitor";
        if (window == null || window.ProcessId != item.ProcessId || !window.IsManageable) return "unavailable";
        if (_identity.Id(window) != item.Observation.WindowId) return "identity-changed";
        if (window.MonitorHandle != item.Monitor || window.WorkArea != item.Observation.WorkArea) return "monitor-changed";
        if (item.ManualRelease && item.AssistedTarget is { } target && Near(window.VisualRect, target, 3))
            return "assisted-release";
        if (item.Automated || item.AutomationStamp != AutomationGuard.Stamp(item.Handle)) return "automation";
        return Near(window.VisualRect, item.Observation.End, 3) ? "stable" : "changed-after-release";
    }

    private void OnContextEvent(nint hook, uint type, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed) return;
        try
        {
            ObserveNativeInteraction(type, hwnd, objectId, childId, time);
            if (objectId != 0 || childId != 0 || type is >= 4 and <= 7 or 0x800C) return;
            if (type == 0x8001)
            {
                if (_active?.Window.Handle == hwnd) EndUnobservedGesture("destroy-observed", EventTick(time));
                InputHistory.Retire(hwnd);
                _identity.Retire(hwnd); _activity.Retire(hwnd); SizeMemory.Retire(hwnd); _lastPlan.Remove(hwnd); _contextDirty = true;
                WindowRetired?.Invoke(hwnd);
                return;
            }
            if (_paused && !(type == 0x800B && _active?.Window.Handle == hwnd && ObserveGestures)) return;
            if (type == 0x0003)
            {
                _usage.Foreground(EventTick(time), RecordingId(hwnd));
            }
            if (type == 0x800B && _active is { } active && hwnd == active.Window.Handle)
            {
                long tick = EventTick(time);
                if (tick >= active.Tick && (active.LastSample == null || tick - active.LastSample >= 100))
                {
                    active.LastSample = tick;
                    if (WindowManager.TryGetRect(hwnd, out var rect) && !rect.IsEmpty)
                    {
                        var point = new GesturePoint(tick - active.Tick, rect.X, rect.Y, rect.Width, rect.Height);
                        if (active.Path.Count < 96) active.Path.Add(point);
                        else { active.Path[^1] = point; active.Omitted++; }
                    }
                }
            }
            if (hwnd == nint.Zero || AutomationGuard.IsUtility(hwnd)) return;
            if (type is not 0x0003 and not 0x8002 and not 0x8003 and not 0x8004 and not 0x800B) return;
            _contextDirty = true;
            _contextCause = type == 0x0003 ? "foreground-observed" : "state-change-unknown";
        }
        catch { }
    }

    private static long EventTick(uint time)
    {
        long now = Environment.TickCount64;
        return now - unchecked((uint)now - time);
    }

    private void RecordFocusBoundary(string reason, bool readForeground = true)
    {
        if (!CanWriteDiagnostics || !_usage.Started) return;
        try
        {
            int? id = readForeground ? RecordingId(NativeMethods.GetForegroundWindow()) : null;
            var coverage = _usage.Snapshot(Environment.TickCount64, id, PointerCaptureAvailable, _foregroundHook != 0);
            _store.AppendFocusBoundary(_session, reason, coverage);
        }
        catch (Exception ex) { _error = LayoutText.Format("RecordWriteFailed", ex.Message); }
    }

    private void EndUnobservedGesture(string stage, long tick)
    {
        if (_active is not { } active) return;
        if (ObserveGestures)
            QueueGesture(new(active.Number, active.Instance, stage, tick, null, null, null,
                active.AutomationStamp != AutomationGuard.Stamp(active.Window.Handle), active.Prior, active.Path.ToArray(), active.Omitted, StartTick: active.Tick));
        _active = null;
    }

    private void QueueGesture(GestureEvent item)
    {
        GestureObserver?.Invoke(_session, item);
        if (!CanWriteDiagnostics) return;
        if (_gestureEvents.Count < 256) _gestureEvents.Add(item);
        else _droppedGestureEvents++;
    }

    private void FlushGestureEvents()
    {
        if (_gestureEvents.Count == 0) return;
        try
        {
            _store.AppendGestures(new(1, _session, DateTimeOffset.UtcNow, _gestureEvents.ToArray(), _droppedGestureEvents));
            _gestureEvents.Clear();
            _droppedGestureEvents = 0;
        }
        catch (Exception ex) { _error = LayoutText.Format("RecordWriteFailed", ex.Message); }
    }

    private void CaptureContextChange()
    {
        if (_disposed || _paused || !_contextDirty || _active is not null) return;
        _contextDirty = false;
        try
        {
            var snapshot = _manager.CaptureCoalesced();
            var after = Capture(snapshot);
            if (!ObservationIdentityTracker.SameState(_lastContext, after))
            {
                var automated = snapshot.Any(w => AutomationGuard.IsMarked(w.Handle));
                _store.AppendTransition(new(3, _session, automated ? "neatwin-intervention" : _contextCause, _lastContext, after));
                _contextRecords++;
            }
            _lastContext = after;
        }
        catch (Exception ex) { _error = LayoutText.Format("RecordStateFailed", ex.Message); }
        PublishStatus();
    }

    private int RecordingId(nint hwnd)
    {
        if (hwnd == 0 || hwnd == NativeMethods.GetShellWindow() || AutomationGuard.IsUtility(hwnd)) return 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 || pid == Environment.ProcessId || WindowManager.IsSystemSurface(hwnd) ? 0 : _identity.Id(hwnd, pid);
    }

    private void ResetActivity()
    {
        ResetSurfaceObservations();
        _latest = null; _sampleTextFocus = 0; _savedGeometry = null; _sampleTick = 0; _lastPlan.Clear(); _activity.Reset(); _usage.Reset();
    }

    internal Func<WindowSnapshot, bool>? IsLocalSurface { get; set; }

    internal WorkspaceFrame Capture(IReadOnlyList<WindowSnapshot> windows) =>
        _identity.Capture(windows, DesktopActivity.Displays(), CanWriteDiagnostics ? w => _activity.AppKey(w, true) : null, IsLocalSurface,
            CanWriteDiagnostics || SnapshotInput ? w => _activity.RawIdentity(w.Handle, w.ProcessId, true) : null);

    private void SampleActivity()
    {
        long now = Environment.TickCount64;
        bool writeDisk = CanWriteDiagnostics;
        if ((!writeDisk && !LiveSensing) || _disposed || !_ownsSingleton || now - _sampleTick < 5000) return;
        _sampleTick = now;
        FocusCoverage? coverage = null;
        try
        {
            if (!DesktopActivity.Interactive())
            {
                EndUnobservedGesture("desktop-unavailable", now);
                RecordFocusBoundary("desktop-unavailable", false);
                ResetActivity();
                return;
            }
            var snapshot = _manager.CaptureCoalesced();
            var frame = Capture(snapshot);
            var previous = _latest?.Frame;
            var usage = writeDisk ? _usage.Sample(now, RecordingId(NativeMethods.GetForegroundWindow()), PointerCaptureAvailable,
                _foregroundHook != 0, out coverage) : null;
            _sampleTextFocus = 0;
            if (snapshot.FirstOrDefault(w => w.IsForeground && w.IsManageable) is { } active)
            {
                var reading = ForegroundReading.Read(active);
                _activity.SemanticRole(active, reading);
                if (reading.Root == active.Handle && reading.Pid == active.ProcessId && reading.Caret)
                    _sampleTextFocus = _identity.Id(active);
            }
            _latest = _activity.Sample(_session, now, frame, snapshot, _identity, VisualSampling, _store.Rules.Read(), persistKeys: writeDisk);
            if (!writeDisk) return;
            _latest = _latest with { FocusCoverage = coverage };
            if (usage != null && _latest.ObservedMilliseconds > 0)
            {
                var foreground = usage.Foreground.GroupBy(span => span.Id).ToDictionary(g => g.Key, g => g.Sum(span => span.DurationMs));
                _latest = _latest with
                {
                    Usage = usage, CoUse = RecordedUsage.CoUse(usage, previous, frame),
                    Windows = _latest.Windows.Select(w => w with { FocusEventMs = foreground.GetValueOrDefault(w.Id) }).ToArray(),
                };
            }
            bool keyframe = _latest.ObservedMilliseconds == 0 || now - _geometryTick >= 60_000 ||
                !ObservationIdentityTracker.SameState(_savedGeometry, frame);
            _store.AppendActivity(_latest, keyframe ? frame : null, keyframe ? now : _geometryTick);
            if (keyframe) { _geometryTick = now; _savedGeometry = frame; }
        }
        catch (Exception ex)
        {
            if (writeDisk)
            {
                // The usage interval may already have advanced before process sampling or the activity write failed.
                if (coverage != null)
                {
                    try { _store.AppendFocusBoundary(_session, "activity-write-incomplete", coverage); }
                    catch (Exception writeError) { System.Diagnostics.Trace.TraceWarning(writeError.Message); }
                }
                else RecordFocusBoundary("sample-failed", false);
                _error = LayoutText.Format("RecordStateFailed", ex.Message);
            }
            _latest = null;
            _usage.Reset();
        }
    }

    internal void Applied(string request, string trigger, LayoutApplication application, long tick)
    {
        if (!ObserveGestures && !(LiveSensing && _store.Enabled)) return;
        if (trigger == "undo")
        {
            _lastPlan.Clear();
            foreach (var pending in _pending) pending.AssistedTarget = null;
            return;
        }
        foreach (var move in application.Plan.Moves)
        {
            if (application.Failures.Any(f => f.Move.Window.Handle == move.Window.Handle)) continue;
            if (_lastPlan.Count >= 1024 && !_lastPlan.ContainsKey(move.Window.Handle))
                _lastPlan.Remove(_lastPlan.MinBy(p => p.Value.Tick).Key);
            _lastPlan[move.Window.Handle] = new(_identity.Id(move.Window), request, tick, move.Window.VisualRect, move.TargetVisualRect);
            var pending = _pending.LastOrDefault(p => p.Handle == move.Window.Handle && !p.Superseded);
            if (pending == null) continue;
            // 松手后的辅助吸附会改变实测终点。只关联同一进程、同一落点的手势请求，才能把原始手势与辅助结果分别记录。
            pending.AssistedTarget = (trigger is "automatic-gesture" or "follow-hand") && !application.Interrupted &&
                pending.ProcessId == move.Window.ProcessId && pending.Observation.End == move.Window.VisualRect &&
                application.Failures.All(f => f.Move.Window.Handle != move.Window.Handle) ? move.TargetVisualRect : null;
        }
    }

    internal IReadOnlyDictionary<nint, string> LiveActivities(IReadOnlyList<WindowSnapshot> snapshot)
    {
        if (_disposed || _latest == null || Environment.TickCount64 - _latest.Tick > 8000)
            return new Dictionary<nint, string>();
        var map = new Dictionary<nint, string>();
        foreach (var w in snapshot.Where(w => w.IsManageable))
        {
            var id = _identity.Id(w);
            if (_latest.Frame.Windows.Any(s => s.Id == id && s.Rect == w.VisualRect &&
                s.Foreground == w.IsForeground && s.VisibleRatio > 0) &&
                _latest.Windows.FirstOrDefault(a => a.Id == id) is { } ev)
                map[w.Handle] = ev.ActivityHint;
        }
        return map;
    }

    internal TaskWindowHint[] LiveHints(IReadOnlyList<WindowSnapshot> snapshot)
    {
        if (_disposed || _latest == null || Environment.TickCount64 - _latest.Tick > 8000) return [];
        var result = new List<TaskWindowHint>();
        foreach (var w in snapshot.Where(w => w.IsManageable))
        {
            var id = _identity.Id(w);
            var evidence = _latest.Windows.FirstOrDefault(a => a.Id == id);
            if (w.IsForeground || evidence == null || !_latest.Frame.Windows.Any(a => a.Id == id &&
                a.Rect == w.VisualRect && a.VisibleRatio > 0)) continue;
            var hint = ActivityHints.ToLayoutHint(w.Handle, w.VisualRect, evidence.ActivityHint, evidence.Fragment);
            if (hint?.Anchor == true && evidence.Fragment is { } patch)
            {
                // A new covering window can invalidate a sampled corner without moving its owner.
                if (snapshot.Any(front => front.ZOrder < w.ZOrder && !front.VisualRect.Intersect(patch).IsEmpty)) continue;
            }
            if (hint != null) result.Add(hint);
        }
        return result.ToArray();
    }

    internal WorkingPairRule[] BindRules(CompiledLayoutRules rules, IReadOnlyList<WindowSnapshot> windows,
        IReadOnlyList<TaskWindowHint> hints, IReadOnlyDictionary<nint, string> activities, LayoutFocus? focus) =>
        (rules.Pairs is { Length: > 0 } || rules.RolePairs is { Length: > 0 })
            ? rules.Bind(windows.Where(w => w.IsManageable).Select(w => (w, _activity.AppKey(w, false))),
                hints, activities, focus)
            : [];

    internal PlacementPreference[] BindSlots(CompiledLayoutRules rules, IReadOnlyList<WindowSnapshot> windows, LayoutFocus? focus) =>
        rules.BindSlots(windows.Select(w => (w, _activity.AppKey(w, false))), focus);

    internal WindowSizeFloor[] BindSizeFloors(IReadOnlyList<WindowSnapshot> windows) =>
        SizeMemory.Bind(windows.Where(w => w.IsManageable).Select(w => (w, _activity.AppKey(w, false))));

    internal bool CaptureOverlay(WindowSnapshot window) => _activity.CaptureOverlay(window);
    internal bool AwaitingOfficeSurface(nint handle) => _activity.AwaitingOfficeSurface(handle);

    internal WindowUseRole SemanticRole(WindowSnapshot window, ForegroundReading reading) => _activity.SemanticRole(window, reading);

    internal string? TryReadAppKey(WindowSnapshot window) => _activity.AppKey(window, false);

    private PlacementUseObservation? ObservePlacementUse(IReadOnlyList<WindowSnapshot> windows, long tick, LayoutFocus? focus = null)
    {
        // 只用八秒内、仍属于同一窗口及前后台状态的测量。未采样到或共享进程的媒体状态保持未知。
        var current = windows.Where(w => w.IsManageable && IsLocalSurface?.Invoke(w) != true).ToDictionary(w => _identity.Id(w));
        var foreground = current.Values.FirstOrDefault(w => w.IsForeground);
        if (foreground == null) return null;
        int frontId = _identity.Id(foreground);
        var input = InputHistory.Read(current.Values.ToArray(), tick);
        var scrolling = current.Where(pair => pair.Value.MonitorHandle == foreground.MonitorHandle &&
            input.GetValueOrDefault(pair.Value.Handle)?.RepeatedScroll == true).Select(pair => pair.Key).ToArray();
        bool caret = input.GetValueOrDefault(foreground.Handle)?.CaretMotion > 0;
        var sample = _latest;
        if (sample == null || tick - sample.Tick is < 0 or > 8_000 || !sample.Frame.Windows.Any(w => w.Id == frontId && w.Foreground))
            return scrolling.Length > 0 || caret ? new(frontId, 0, caret, [], scrolling) : null;
        focus ??= ReadPlacementFocus?.Invoke(windows, tick);
        int joint = focus is { Evidence: "recent-switch-pair", Switches: >= 2 } && focus.Main == foreground.Handle
            ? current.FirstOrDefault(p => p.Value.Handle == focus.Companion).Key : 0;
        var playing = sample.Windows.Where(a => a.Playing && current.TryGetValue(a.Id, out var live) &&
            live.MonitorHandle == foreground.MonitorHandle && live.WorkArea == foreground.WorkArea &&
            sample.Frame.Windows.Any(w => w.Id == a.Id && w.Foreground == live.IsForeground))
            .Select(a => a.Id).ToArray();
        bool text = caret || _sampleTextFocus == frontId;
        if (joint == 0 && !text && playing.Length == 0 && scrolling.Length == 0) return null;
        return new(frontId, joint, text, playing, scrolling);
    }

    internal PlacementPreference[] Preferences(IReadOnlyList<WindowSnapshot> windows, LayoutFocus focus, LayoutFocus observedFocus)
    {
        if (!_store.Enabled) return [];
        var stored = _store.Placement.Read();
        if (stored.Length == 0 && _instanceHabits.Length == 0) return [];
        var now = DateTimeOffset.UtcNow;
        var entries = windows.Where(w => w.IsManageable).Select(w =>
            (Window: w, App: stored.Length == 0 ? null : _activity.AppKey(w, false),
             Instance: _instanceHabits.Length == 0 ? string.Empty : _identity.Id(w).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var usage = ObservePlacementUse(windows, Environment.TickCount64, observedFocus);
        var ids = usage == null ? null : entries.ToDictionary(e => e.Window.Handle, e => _identity.Id(e.Window));
        return PlacementPreferences.Bind(entries, focus, stored, _instanceHabits, now,
            ids == null ? null : (window, peer) => usage!.For(ids.GetValueOrDefault(window), ids.GetValueOrDefault(peer)),
            ids == null ? null : window => usage!.UseOf(ids.GetValueOrDefault(window)));
    }

    private void PublishStatus() => StatusChanged?.Invoke(Status);

    private static bool Near(RectI a, RectI b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;

    public void Dispose()
    {
        if (_disposed) return;
        EndUnobservedGesture("exit", Environment.TickCount64);
        RecordFocusBoundary("exit");
        FlushInteractions();
        FlushGestureEvents();
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Dispose();
        if (_moveHook != nint.Zero) _ = NativeMethods.UnhookWinEvent(_moveHook);
        if (_contextHook != nint.Zero) _ = NativeMethods.UnhookWinEvent(_contextHook);
        if (_foregroundHook != nint.Zero) _ = NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_menuHook != nint.Zero) _ = NativeMethods.UnhookWinEvent(_menuHook);
        _pending.Clear();
        if (_ownsSingleton)
        {
            try { _singleton.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _singleton.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record Active(WindowSnapshot Window, uint ProcessId, long Tick,
        WorkspaceFrame? Context, nint AutomationStamp, int Number, int Instance, int Batch, RecordedPlacementLink? Prior, PlacementUseObservation? Usage)
    {
        internal List<GesturePoint> Path { get; } = [];
        internal long? LastSample { get; set; }
        internal int Omitted { get; set; }
    }

    private sealed class Pending(nint handle, uint processId, nint monitor, WindowAdjustmentObservation observation, long tick, int batch)
    {
        internal nint Handle { get; } = handle;
        internal uint ProcessId { get; } = processId;
        internal nint Monitor { get; } = monitor;
        internal WindowAdjustmentObservation Observation { get; } = observation;
        internal long Tick { get; } = tick;
        internal int Batch { get; } = batch;
        internal bool Superseded { get; set; }
        internal WindowAdjustmentObservation? Continuation { get; set; }
        internal bool Automated { get; init; }
        internal bool ManualRelease { get; init; }
        internal RectI? AssistedTarget { get; set; }
        internal nint AutomationStamp { get; init; }
    }
}
