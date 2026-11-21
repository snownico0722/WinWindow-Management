using NeatWin.Core;
using NeatWin.Windows;
using Microsoft.Win32;

namespace NeatWin.App;

/// <summary>Cheap native notifications; capture/plan/apply only on the owning UI thread.</summary>
internal sealed class WorkspaceAutoTidyMonitor : NativeWindow, IDisposable
{
    private const int WorkspacePollMilliseconds = 150;
    private const int ChangedMessage = 0x8000 + 79;
    private readonly Func<IReadOnlyList<WindowSnapshot>> _capture;
    private readonly Func<bool> _busy;
    private readonly ForegroundHistory _focus;
    private readonly AutomaticLayoutScheduler _scheduler = new();
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private readonly Dictionary<nint, nint> _observedStamps = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = WorkspacePollMilliseconds };
    private int _messageQueued;
    private bool _disposed;
    internal event Action<AutomaticLayoutRequest>? Requested;
    internal event Action<Exception>? Failed;
    internal bool WatchingWorkspace => _hooks.Count != 0;
    internal AutomaticLayoutMode Mode => _scheduler.Mode;

    internal WorkspaceAutoTidyMonitor(Func<IReadOnlyList<WindowSnapshot>> capture, Func<bool> busy, ForegroundHistory focus)
    {
        _capture = capture; _busy = busy; _focus = focus; _callback = OnWinEvent;
        CreateHandle(new CreateParams { Caption = "NeatWin.AutomaticLayout", Parent = NativeMethods.HwndMessage });
        _timer.Tick += OnTick;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
    }

    internal bool SetMode(AutomaticLayoutMode mode, bool arrangeNow = false)
    {
        _timer.Stop(); _timer.Interval = WorkspacePollMilliseconds;
        RemoveHooks();
        var baseline = _capture();
        _scheduler.Reset(mode, baseline, Environment.TickCount64);
        _observedStamps.Clear();
        foreach (var window in baseline) _observedStamps[window.Handle] = AutomationGuard.Stamp(window.Handle);
        if (AutomaticLayoutPolicy.WatchesWorkspace(mode))
        {
            // Top-level create/destroy/show/hide/reorder; state/location; foreground; minimize.
            foreach (var (first, last) in new (uint, uint)[] { (0x8000, 0x8004), (0x800A, 0x800B), (3, 3), (0x16, 0x17) })
            {
                var hook = NativeMethods.SetWinEventHook(first, last, 0, _callback, 0, 0,
                    NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
                if (hook == 0)
                {
                    RemoveHooks(); _scheduler.Reset(AutomaticLayoutMode.Off, _capture(), Environment.TickCount64);
                    return false;
                }
                _hooks.Add(hook);
            }
            if (arrangeNow) { _scheduler.RequestCurrent(Environment.TickCount64); _timer.Start(); }
        }
        return true;
    }

    internal void GestureStarted(nint handle) => _scheduler.GestureStarted(handle);
    internal void GestureCompleted(ManualWindowGesture gesture, IReadOnlyList<WindowSnapshot> releasedDesktop)
    {
        if (_disposed || !AutomaticLayoutPolicy.UsesFullPlanner(Mode)) return;
        _scheduler.GestureCompleted(gesture, Environment.TickCount64);
        if (_scheduler.PendingRelease != null) ObserveAndDispatch(releasedDesktop);
        else if (_scheduler.HasPending) ScheduleNextObservation();
    }
    internal void PointerReleased()
    {
        if (!_disposed && _scheduler.PendingRelease != null) ObserveAndDispatch();
    }

    internal void ApplicationRequested(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan) =>
        _scheduler.ApplicationRequested(before, plan, Environment.TickCount64);

    internal void RequestCurrent()
    {
        if (_disposed || !AutomaticLayoutPolicy.WatchesWorkspace(Mode)) return;
        if (!_scheduler.HasPending) _scheduler.RequestCurrent(Environment.TickCount64);
        _timer.Start();
    }

    internal bool TaskInput(nint handle, uint pid, long now)
    {
        if (_disposed || !_scheduler.TaskInput(handle, pid, now)) return false;
        _timer.Start();
        return true;
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Destruction can invalidate HWND before delivery, so membership changes are confirmed
        // by Capture instead of requiring IsWindow here. Child-control/caret events are noise.
        if (_disposed || hwnd == 0 || eventType >= 0x8000 && (idObject != 0 || idChild != 0)) return;
        if (AutomationGuard.IsUtility(hwnd)) return;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return;
        Signal();
    }
    private void OnDisplayChanged(object? sender, EventArgs e) => Signal();
    private void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => Signal();
    private void Signal()
    {
        if (_disposed || !AutomaticLayoutPolicy.WatchesWorkspace(Mode) || Interlocked.Exchange(ref _messageQueued, 1) != 0) return;
        if (!NativeMethods.PostMessage(Handle, ChangedMessage, 0, 0)) Interlocked.Exchange(ref _messageQueued, 0);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ChangedMessage)
        {
            Interlocked.Exchange(ref _messageQueued, 0);
            if (!_disposed && AutomaticLayoutPolicy.WatchesWorkspace(Mode)) _timer.Start();
            return;
        }
        base.WndProc(ref m);
    }
    private void OnTick(object? sender, EventArgs e) => ObserveAndDispatch();

    private void ScheduleNextObservation()
    {
        _timer.Interval = _scheduler.PendingRelease != null ? AutomaticLayoutScheduler.ReleaseRetryMilliseconds : WorkspacePollMilliseconds;
        _timer.Start();
    }

    private void ObserveAndDispatch(IReadOnlyList<WindowSnapshot>? releasedDesktop = null)
    {
        try
        {
            if (NativeWindowActivity.IsShellForeground(NativeMethods.GetForegroundWindow()))
            {
                _scheduler.Observe([], Environment.TickCount64, false, desktopFocused: true);
                _timer.Stop();
                return;
            }
            if (releasedDesktop == null && _scheduler.PendingRelease == null && (_busy() || NativeWindowActivity.PointerBusy))
            {
                // Leave the UI thread to the live gesture. Workspace membership and geometry
                // are captured together after release, including independent window changes.
                _scheduler.DeferWorkspaceWhileBusy(Environment.TickCount64);
                return;
            }
            // While Windows still owns the pointer, retry the inexpensive release guard.
            // A fresh snapshot and normal planning run only when the gesture can execute.
            if (releasedDesktop == null && _scheduler.PendingRelease is { } pending)
            {
                var foreground = NativeMethods.GetForegroundWindow();
                NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
                if (AutomaticLayoutPolicy.ReleaseState(pending, foreground, pid, Environment.TickCount64,
                    _busy() || NativeWindowActivity.PointerBusy) == GestureReleaseState.Wait)
                {
                    ScheduleNextObservation();
                    return;
                }
            }
            var snapshot = releasedDesktop ?? _capture();
            // An unexpired property can belong to an earlier layout, including one before a
            // mode switch. Only a NEW stamp attributes an otherwise unknown change to NeatWin;
            // known requested targets are tracked separately by the scheduler through settling.
            var marked = new HashSet<nint>();
            foreach (var window in snapshot)
            {
                var stamp = AutomationGuard.Stamp(window.Handle);
                if (stamp != _observedStamps.GetValueOrDefault(window.Handle) && AutomationGuard.IsMarked(window.Handle))
                    marked.Add(window.Handle);
                _observedStamps[window.Handle] = stamp;
            }
            var present = snapshot.Select(w => w.Handle).ToHashSet();
            foreach (var handle in _observedStamps.Keys.Where(h => !present.Contains(h)).ToArray()) _observedStamps.Remove(handle);
            long tick = Environment.TickCount64;
            // Navigation quiet time only gates workspace changes. If a release loses focus,
            // the scheduler defers that new task to its own normal workspace observation.
            var navigation = _scheduler.PendingRelease == null ? _focus.NavigationFor(snapshot, tick) : null;
            long quiet = navigation?.SettleUntil ?? 0;
            if (navigation != null && quiet > 0 && tick <= quiet + 200 && NativeMethods.GetCursorPos(out var pointer) &&
                snapshot.FirstOrDefault(w => w.Handle == navigation.Main) is { } endpoint)
                quiet = _focus.PointerQuietUntil(navigation, new(pointer.X, pointer.Y), endpoint, tick);
            var request = _scheduler.Observe(snapshot, tick, _busy() || NativeWindowActivity.PointerBusy, marked,
                navigationSettleUntil: quiet);
            if (_scheduler.HasPending) ScheduleNextObservation();
            else { _timer.Stop(); _timer.Interval = WorkspacePollMilliseconds; }
            if (request is not null) Requested?.Invoke(request);
        }
        catch (Exception ex) { _timer.Stop(); Failed?.Invoke(ex); }
    }
    private void RemoveHooks()
    {
        foreach (var hook in _hooks) _ = NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer.Stop(); _timer.Dispose(); RemoveHooks();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferencesChanged;
        DestroyHandle(); GC.SuppressFinalize(this);
    }
}
