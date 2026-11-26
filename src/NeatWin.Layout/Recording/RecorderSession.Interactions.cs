using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.Recording;

internal sealed partial class RecorderSession
{
    internal DesktopInteractionHistory InputHistory { get; } = new();
    internal Action<DesktopInteraction>? InteractionObserver { get; init; }
    private readonly List<DesktopInteraction> _interactionBatch = [];
    private long _sequence, _lastInteractionFlush;
    private readonly Dictionary<(nint Handle, uint Pid), (long Tick, RectI Rect)> _geometryEvents = [];

    internal void ReportInteractionCoverage(bool pointer)
    {
        if (!TraceInput) return;
        PublishInteraction(new(_session, ++_sequence, Environment.TickCount64, "coverage", 0, 0, null,
            Detail: $"pointer={pointer};window={_contextHook != 0};foreground={_foregroundHook != 0};menu={_menuHook != 0}"));
    }
    private int _droppedInteractions;
    private bool SnapshotInput => InteractionObserver != null && SnapshotWantsSurfaces?.Invoke() == true;
    private bool TraceInput => _ownsSingleton && !_disposed && (CanWriteDiagnostics || SnapshotInput);
    private static string? H(nint h) => h == 0 ? null : $"0x{h:X}";

    internal void PointerObserved(PointerObservation pointer)
    {
        var sequence = ++_sequence;
        if (pointer.Target != 0 && pointer.TargetPid != 0 && !AutomationGuard.IsUtility(pointer.Target))
            InputHistory.Pointer(pointer.Kind, pointer.Target, pointer.TargetPid, pointer.Tick, sequence, pointer.Point);
        if (!TraceInput) return;
        PublishInteraction(new(_session, sequence, pointer.Tick, pointer.Kind, EventWindowId(pointer.Target, pointer.TargetPid),
            pointer.TargetPid, H(pointer.Target), Foreground: H(pointer.Foreground), Point: pointer.Point,
            WheelDelta: pointer.Delta, Hit: H(pointer.Hit), Identity: _activity.RawIdentity(pointer.Target, pointer.TargetPid, false)));
    }

    internal bool TracksLayerReturns => TraceInput;
    internal void RecordLayerReturn(WindowSnapshot window, LayerReturnEvidence evidence)
    {
        if (TraceInput)
            PublishInteraction(new DesktopInteraction(_session, ++_sequence, Environment.TickCount64, "layer-return",
                _identity.Id(window), window.ProcessId, H(window.Handle)) { LayerReturn = evidence });
    }

    internal long? WorkspaceChanged(WindowSnapshot window, string detail)
    {
        if (!TraceInput) return null;
        var seq = ++_sequence;
        PublishInteraction(new(_session, seq, Environment.TickCount64, "workspace-difference",
            detail == "removed" ? 0 : _identity.Id(window), window.ProcessId, H(window.Handle),
            Frame: window.VisualRect, Owner: H(window.Owner), Detail: detail,
            Identity: detail == "removed" ? null : _activity.RawIdentity(window.Handle, window.ProcessId, false)));
        return seq;
    }

    private int EventWindowId(nint handle, uint pid) => handle == 0 || pid == 0 ? 0 : _identity.Id(handle, pid);

    private void ObserveNativeInteraction(uint type, nint hwnd, int obj, int child, uint time)
    {
        if (hwnd == 0 || _disposed || !_ownsSingleton) return;
        bool caret = obj == -8 && type == 0x800B;
        bool menu = type is >= 4 and <= 7;
        if (!caret && !menu && (obj != 0 || child != 0)) return;
        string? kind = type switch
        {
            3 => "foreground", 4 => "menu-start", 5 => "menu-end", 6 => "popup-start", 7 => "popup-end",
            0x8000 => "create", 0x8001 => "destroy", 0x8002 => "show", 0x8003 => "hide", 0x800C => "name-change",
            0x800B => caret ? "caret-location" : "window-location", _ => null
        };
        if (kind == null || !TraceInput && !(caret && LiveSensing)) return;
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        if (!caret && !menu && root != 0 && root != hwnd) return;
        if (caret && (root == 0 || root != NativeMethods.GetForegroundWindow())) return;
        var target = caret ? root : hwnd;
        NativeMethods.GetWindowThreadProcessId(target, out var pid);
        if (pid == Environment.ProcessId) return;
        var tick = EventTick(time);
        RectI? bounds = null;
        if (kind == "window-location")
        {
            if (pid == 0 || !WindowManager.TryGetRect(hwnd, out var frame)) return;
            var key = (hwnd, pid);
            if (_geometryEvents.TryGetValue(key, out var prior) && (tick - prior.Tick < 100 || prior.Rect == frame)) return;
            if (_geometryEvents.Count >= 1024) _geometryEvents.Clear();
            _geometryEvents[key] = (tick, frame); bounds = frame;
        }
        else if (kind == "destroy")
            foreach (var key in _geometryEvents.Keys.Where(k => k.Handle == hwnd).ToArray()) _geometryEvents.Remove(key);

        var seq = ++_sequence;
        var caretRect = caret ? NativeWindowActivity.CaretClientRect(root) : null;
        if (caretRect is { } rect && pid != 0) InputHistory.CaretMoved(target, pid, tick, seq, rect);
        if (!TraceInput) return;
        // 销毁通知可能晚于 HWND 失效。保留原句柄和时钟；未知身份不回填成新窗口的身份。
        bool present = pid != 0 && type != 0x8001;
        var identity = present ? _activity.RawIdentity(target, pid, !caret && bounds == null) : null;
        PublishInteraction(new(_session, seq, tick, kind, present ? EventWindowId(target, pid) : 0, present ? pid : 0,
            H(target), identity?.ClassName,
            bounds ?? (present && !caret && WindowManager.TryGetRect(target, out var outer) ? outer : null),
            present && !caret ? H(NativeMethods.GetWindow(target, NativeMethods.GwOwner)) : null, H(NativeMethods.GetForegroundWindow()),
            Identity: identity, CaretClientRect: caretRect));
    }

    private void PublishInteraction(DesktopInteraction item)
    {
        item = item with { DeliveredTick = Environment.TickCount64, DeliveredAt = DateTimeOffset.UtcNow };
        if (SnapshotInput) InteractionObserver?.Invoke(item);
        if (!CanWriteDiagnostics) return;
        // 消息回调只写有界内存。磁盘延迟和日志轮换不应阻塞鼠标派发。
        if (_interactionBatch.Count < 4096) _interactionBatch.Add(item);
        else _droppedInteractions++;
    }

    private void FlushInteractions()
    {
        _lastInteractionFlush = Environment.TickCount64;
        if (_interactionBatch.Count == 0) return;
        try
        {
            _store.AppendInteractions(_session, _interactionBatch, _droppedInteractions);
            _interactionBatch.Clear(); _droppedInteractions = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _error = ex.Message; }
    }
}
