using NeatWin.Core;
using NeatWin.Windows;
using Microsoft.Win32;

namespace NeatWin.App;

/// <summary>Cheap native notifications; capture/plan/apply only on the owning UI thread.</summary>
internal sealed class WorkspaceAutoTidyMonitor : NativeWindow, IDisposable
{
    private const int ChangedMessage = 0x8000 + 79;
    private readonly Func<IReadOnlyList<WindowSnapshot>> _capture;
    private readonly Func<bool> _busy;
    private readonly AutomaticLayoutScheduler _scheduler = new();
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private readonly Dictionary<nint, nint> _observedStamps = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private int _messageQueued;
    private bool _disposed;
    internal event Action<AutomaticLayoutRequest>? Requested;
    internal event Action<Exception>? Failed;
    internal bool WatchingWorkspace => _hooks.Count != 0;
    internal AutomaticLayoutMode Mode => _scheduler.Mode;

    internal WorkspaceAutoTidyMonitor(Func<IReadOnlyList<WindowSnapshot>> capture, Func<bool> busy)
    {
        _capture = capture; _busy = busy; _callback = OnWinEvent;
        CreateHandle(new CreateParams { Caption = "NeatWin.AutomaticLayout", Parent = NativeMethods.HwndMessage });
        _timer.Tick += OnTick;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
    }

    internal bool SetMode(AutomaticLayoutMode mode, bool arrangeNow = false)
    {
        _timer.Stop(); RemoveHooks();
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
    internal void GestureCompleted(ManualWindowGesture gesture)
    {
        _scheduler.GestureCompleted(gesture, Environment.TickCount64);
        if (_scheduler.HasPending) _timer.Start();
    }
    internal void ApplicationRequested(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan) =>
        _scheduler.ApplicationRequested(before, plan, Environment.TickCount64);

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
    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            var snapshot = _capture();
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
            var request = _scheduler.Observe(snapshot, Environment.TickCount64, _busy(), marked);
            if (!_scheduler.HasPending) _timer.Stop();
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
