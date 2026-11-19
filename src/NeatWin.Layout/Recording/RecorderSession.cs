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
    internal string StateText => !IsAvailable ? "不可用" : IsPaused ? "已暂停" : "正在记录";
}

/// <summary>
/// Shared recorder engine used by the main application and the legacy standalone recorder UI.
/// It records geometry/context only; no titles, process names, keystrokes, screenshots or pointer paths.
/// </summary>
internal sealed class RecorderSession : IDisposable
{
    private readonly WindowManager _manager = new();
    private readonly IntentReferenceStore _store;
    private readonly ObservationIdentityTracker _identity = new();
    private readonly List<Pending> _pending = [];
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Mutex _singleton;
    private readonly bool _ownsSingleton;
    private readonly NativeMethods.WinEventProc _moveCallback;
    private readonly NativeMethods.WinEventProc _contextCallback;
    private readonly nint _moveHook;
    private readonly nint _contextHook;
    private readonly nint _foregroundHook;
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

    internal string SessionId => _session;
    internal ObservationIdentityTracker Identity => _identity;
    internal long RecordingEpoch { get; private set; }
    internal bool CanWriteDiagnostics => _ownsSingleton && !_paused && !_disposed;

    internal RecorderSession(IntentReferenceStore store, bool recordingEnabled)
    {
        _store = store;
        _paused = !recordingEnabled;
        var directoryBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(store.DirectoryPath).ToUpperInvariant());
        var directoryId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(directoryBytes));
        _singleton = new Mutex(true, @"Local\WinWindow.Layout.Recorder." + directoryId, out _ownsSingleton);
        _moveCallback = OnMoveEvent;
        _contextCallback = OnContextEvent;

        if (!_ownsSingleton)
        {
            _paused = true;
            _error = "当前数据目录已有记录器运行；当前实例不会重复记录。";
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            PublishStatus();
            return;
        }

        _moveHook = NativeMethods.SetWinEventHook(NativeMethods.EventSystemMoveSizeStart,
            NativeMethods.EventSystemMoveSizeEnd, nint.Zero, _moveCallback, 0, 0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
        _foregroundHook = NativeMethods.SetWinEventHook(0x0003, 0x0003, nint.Zero, _contextCallback, 0, 0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
        _contextHook = NativeMethods.SetWinEventHook(0x8000, 0x800B, nint.Zero, _contextCallback, 0, 0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);

        try { _lastContext = _identity.Capture(_manager.Capture()); }
        catch (Exception ex) { _error = ex.Message; }
        if (_contextHook == nint.Zero || _foregroundHook == nint.Zero)
            _error = "层级／前台监听不可用，仍可记录拖动；请重启检查。";
        if (_moveHook == nint.Zero)
        {
            _paused = true;
            _error = "窗口事件监听失败，未开始记录。请重新启动 NeatWin。";
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
        _paused = !_paused;
        _timer.Enabled = !_paused;
        RecordingEpoch++;
        _active = null;
        _pending.Clear();
        _lastContext = null;
        _contextDirty = !_paused;
        PublishStatus();
    }

    internal void OpenDataDirectory()
    {
        Directory.CreateDirectory(_store.DirectoryPath);
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
        _store.Clear();
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
        if (_lastMaintenanceTick == long.MinValue || Environment.TickCount64 - _lastMaintenanceTick >= 3_600_000)
        {
            _lastMaintenanceTick = Environment.TickCount64;
            try { _store.Prune(); }
            catch (Exception ex) { _error = $"清理旧记录失败：{ex.Message}"; }
        }
        FlushPending();
        CaptureContextChange();
    }

    private void OnMoveEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || _paused || _handlingEvent || hwnd == nint.Zero || objectId != 0 || childId != 0 ||
            AutomationGuard.IsUtility(hwnd)) return;
        _handlingEvent = true;
        try
        {
            if (eventType == NativeMethods.EventSystemMoveSizeStart)
            {
                foreach (var pending in _pending) pending.Superseded = true;
                _active = null;
                var snapshot = _manager.Capture();
                var start = snapshot.FirstOrDefault(w => w.Handle == hwnd && w.IsManageable);
                if (start is null) return;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                _active = new(start, pid, Environment.TickCount64, _identity.Capture(snapshot),
                    AutomationGuard.Stamp(hwnd), AutomationGuard.IsMarked(hwnd));
            }
            else if (eventType == NativeMethods.EventSystemMoveSizeEnd)
            {
                var active = _active;
                _active = null;
                if (active is null || active.Window.Handle != hwnd) { _excluded++; PublishStatus(); return; }
                var captured = _manager.Capture();
                var end = captured.FirstOrDefault(w => w.Handle == hwnd && w.IsManageable);
                if (end is null || Near(active.Window.VisualRect, end.VisualRect, 3)) return;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != active.ProcessId) return;
                var neighbors = new VisibilityAnalyzer().SelectVisibleWorkingSet(captured)
                    .Where(w => w.Window.Handle != hwnd && w.Window.MonitorHandle == end.MonitorHandle)
                    .Take(24).Select(w => w.Window.VisualRect).ToArray();
                var id = _identity.Id(end);
                var context = _identity.Capture(captured);
                var observation = new WindowAdjustmentObservation(DateTimeOffset.UtcNow, _session, id,
                    active.Window.VisualRect, end.VisualRect, active.Window.WorkArea, end.WorkArea,
                    end.Dpi, Math.Max(0, Environment.TickCount64 - active.Tick), neighbors, "unconfirmed",
                    Version: 2, StartContext: active.Context, EndContext: context);
                if (_pending.Count >= 32) { _pending.RemoveAt(0); _excluded++; }
                _pending.Add(new(hwnd, pid, end.MonitorHandle, observation, Environment.TickCount64)
                {
                    Automated = active.Automated || active.AutomationStamp != AutomationGuard.Stamp(hwnd) || AutomationGuard.IsMarked(hwnd),
                    AutomationStamp = AutomationGuard.Stamp(hwnd),
                });
                _contextDirty = true;
            }
        }
        catch (Exception ex) { _error = $"捕获失败：{ex.Message}"; PublishStatus(); }
        finally { _handlingEvent = false; }
    }

    private void FlushPending()
    {
        if (_disposed || _paused || _pending.Count == 0) return;
        try
        {
            foreach (var item in _pending)
                item.Automated |= AutomationGuard.IsMarked(item.Handle) || item.AutomationStamp != AutomationGuard.Stamp(item.Handle);
            var due = _pending.Where(p => Environment.TickCount64 - p.Tick >= 2000).ToArray();
            if (due.Length == 0) return;
            var snapshot = _manager.Capture();
            var current = snapshot.ToDictionary(w => w.Handle);
            var settledContext = _identity.Capture(snapshot);
            foreach (var pending in due)
            {
                _pending.Remove(pending);
                var outcome = "stable";
                NativeMethods.GetWindowThreadProcessId(pending.Handle, out var pid);
                if (pending.Superseded) outcome = "superseded";
                else if (pending.Automated || AutomationGuard.IsMarked(pending.Handle)) outcome = "automation";
                else if (pid != pending.ProcessId || !current.TryGetValue(pending.Handle, out var window) || !window.IsManageable)
                    outcome = "unavailable";
                else if (_identity.Id(window) != pending.Observation.WindowId) outcome = "identity-changed";
                else if (window.MonitorHandle != pending.Monitor || window.WorkArea != pending.Observation.WorkArea)
                    outcome = "monitor-changed";
                else if (!Near(window.VisualRect, pending.Observation.End, 3)) outcome = "changed-after-release";
                if (pending.Observation.StartWorkArea != pending.Observation.WorkArea) outcome = "cross-monitor";
                try
                {
                    _store.Append(pending.Observation with { Outcome = outcome, SettledContext = settledContext });
                    _recorded++;
                    _error = null;
                }
                catch (Exception ex) { _error = $"写入失败（本条未计入）：{ex.Message}"; }
            }
        }
        catch (Exception ex) { _error = $"确认窗口状态失败：{ex.Message}"; }
        PublishStatus();
    }

    private void OnContextEvent(nint hook, uint type, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || objectId != 0 || childId != 0) return;
        try
        {
            if (type == 0x8001) { _identity.Retire(hwnd); _contextDirty = true; return; }
            if (_paused || hwnd == nint.Zero || AutomationGuard.IsUtility(hwnd)) return;
            if (type is not 0x0003 and not 0x8002 and not 0x8003 and not 0x8004 and not 0x800B) return;
            _contextDirty = true;
            _contextCause = type == 0x0003 ? "foreground-observed" : "state-change-unknown";
        }
        catch { }
    }

    private void CaptureContextChange()
    {
        if (_disposed || _paused || !_contextDirty || _active is not null) return;
        _contextDirty = false;
        try
        {
            var snapshot = _manager.Capture();
            var after = _identity.Capture(snapshot);
            if (!ObservationIdentityTracker.SameState(_lastContext, after))
            {
                var automated = snapshot.Any(w => AutomationGuard.IsMarked(w.Handle));
                _store.AppendTransition(new(2, _session, automated ? "neatwin-intervention" : _contextCause, _lastContext, after));
                _contextRecords++;
            }
            _lastContext = after;
        }
        catch (Exception ex) { _error = $"状态记录失败：{ex.Message}"; }
        PublishStatus();
    }

    private void PublishStatus() => StatusChanged?.Invoke(Status);

    private static bool Near(RectI a, RectI b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance && Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Width - b.Width) <= tolerance && Math.Abs(a.Height - b.Height) <= tolerance;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Dispose();
        if (_moveHook != nint.Zero) _ = NativeMethods.UnhookWinEvent(_moveHook);
        if (_contextHook != nint.Zero) _ = NativeMethods.UnhookWinEvent(_contextHook);
        if (_foregroundHook != nint.Zero) _ = NativeMethods.UnhookWinEvent(_foregroundHook);
        _pending.Clear();
        if (_ownsSingleton)
        {
            try { _singleton.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _singleton.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record Active(WindowSnapshot Window, uint ProcessId, long Tick,
        WorkspaceFrame Context, nint AutomationStamp, bool Automated);

    private sealed class Pending(nint handle, uint processId, nint monitor, WindowAdjustmentObservation observation, long tick)
    {
        internal nint Handle { get; } = handle;
        internal uint ProcessId { get; } = processId;
        internal nint Monitor { get; } = monitor;
        internal WindowAdjustmentObservation Observation { get; } = observation;
        internal long Tick { get; } = tick;
        internal bool Superseded { get; set; }
        internal bool Automated { get; set; }
        internal nint AutomationStamp { get; init; }
    }
}
