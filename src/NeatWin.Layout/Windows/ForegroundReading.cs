using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using NeatWin.Core;

namespace NeatWin.Windows;

// All native handles stay local. Read the actual foreground input queue once per sample.
// A GUI caret owned by an unrelated window or stale queue must not protect a new task.
internal readonly record struct ForegroundReading(nint Root, nint Focus, uint Pid, bool Caret, uint? LastInput = null)
{
    // 有些编辑器打开时就有插入点，此时仍有机会完成首次摆位。求解期间若换了输入框、发生输入或新出现插入点，移动窗口会打断编辑。
    internal bool AllowsInitialPlacement(ForegroundReading live) =>
        Root != 0 && Focus != 0 && LastInput.HasValue && live.Root == Root && live.Focus == Focus &&
        live.Pid == Pid && live.LastInput == LastInput && (Caret || !live.Caret);

    // 输入时间与插入点描述操作状态；它们变化时仍可能是同一个 WPS 控件。
    internal bool SameControl(ForegroundReading other) =>
        Root != 0 && Focus != 0 && Root == other.Root && Focus == other.Focus && Pid == other.Pid;

    internal static ForegroundReading Read(WindowSnapshot? expected)
    {
        if (expected is not { IsForeground: true, IsManageable: true } ||
            NativeMethods.GetForegroundWindow() != expected.Handle) return default;
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(0, ref info) || (info.Flags & 0x1e) != 0 ||
            info.Active != expected.Handle || info.Focus == 0 ||
            NativeMethods.GetAncestor(info.Focus, NativeMethods.GaRoot) != expected.Handle) return default;
        bool caret = info.Caret != 0 && info.CaretRect.Bottom > info.CaretRect.Top &&
            NativeMethods.GetAncestor(info.Caret, NativeMethods.GaRoot) == expected.Handle &&
            (info.Focus == info.Caret || NativeMethods.IsChild(info.Focus, info.Caret) || NativeMethods.IsChild(info.Caret, info.Focus));
        var input = new NativeMethods.LastInput { Size = (uint)Marshal.SizeOf<NativeMethods.LastInput>() };
        uint? lastInput = NativeMethods.GetLastInputInfo(ref input) ? input.Time : null;
        return NativeMethods.GetForegroundWindow() == expected.Handle
            ? new(expected.Handle, info.Focus, expected.ProcessId, caret, lastInput) : default;
    }
}

// Shared WPS frames need a foreground accessibility query. Reserve at most one extra
// worker for another window when a provider stalls. A retired request keeps its slot
// until the COM call actually returns, and can never publish into a new window lifetime.
internal sealed class OfficeSurfaceProbe(Func<ForegroundReading, Task<FocusedOfficeSurface>>? query = null)
{
    private sealed record Request(ForegroundReading Target, Task<FocusedOfficeSurface> Work, long Started)
    {
        public bool Retired { get; set; }
    }

    private readonly List<Request> _requests = [];
    private ForegroundReading _cachedTarget;
    private FocusedOfficeSurface _cached;
    private long _lastRefresh;

    internal FocusedOfficeSurface Sample(ForegroundReading target, long now)
    {
        if (target.Root == 0) return default;
        // 同一个 WPS 外框内也会切换编辑控件。过期任务不得在切回旧 HWND 后覆盖新结果。
        if (!_cachedTarget.SameControl(target)) _cachedTarget = default;
        foreach (var pending in _requests)
            if (pending.Target.Root == target.Root && !pending.Target.SameControl(target)) pending.Retired = true;
        for (int i = _requests.Count - 1; i >= 0; i--)
        {
            var request = _requests[i];
            if (!request.Work.IsCompleted) continue;
            _requests.RemoveAt(i);
            if (!request.Retired && request.Target.SameControl(target))
            {
                _cachedTarget = target;
                _cached = request.Work.IsCompletedSuccessfully ? request.Work.Result : default;
                _lastRefresh = request.Started;
            }
            _ = request.Work.Exception;
        }
        bool same = _cachedTarget.SameControl(target);
        bool busy = _requests.Count >= 2 || _requests.Any(r =>
            (!r.Retired && r.Target.SameControl(target)) || now - r.Started < 1_500);
        if (!busy && (!same || now - _lastRefresh >= 2_000))
            _requests.Add(new(target, query?.Invoke(target) ?? Task.Run(() => Read(target)), now));
        return same ? _cached : default;
    }

    internal bool JustStarted(nint root, long now) =>
        _requests.Any(r => !r.Retired && r.Target.Root == root && !r.Target.SameControl(_cachedTarget) && now - r.Started < 200);

    internal void Retire(nint root)
    {
        if (_cachedTarget.Root == root) _cachedTarget = default;
        foreach (var request in _requests.Where(r => r.Target.Root == root)) request.Retired = true;
    }

    private static FocusedOfficeSurface Read(ForegroundReading target)
    {
        try
        {
            // A web start page or embedded article also exposes an accessible Document.
            var cls = new StringBuilder(128);
            NativeMethods.GetClassName(target.Focus, cls, cls.Capacity);
            string focusClass = cls.ToString();
            if (focusClass.StartsWith("Chrome_", StringComparison.Ordinal) || focusClass == "Internet Explorer_Server")
                return default;
            var id = typeof(IAccessible).GUID;
            if (NativeMethods.GetForegroundWindow() != target.Root ||
                NativeMethods.AccessibleObjectFromWindow(target.Focus, 0xfffffffc, ref id, out var control) < 0 || control == null)
                return default;
            var surface = FocusedOfficeSurface.Unknown;
            for (int depth = 0; depth < 6; depth++)
            {
                var focus = control.accFocus;
                var role = control.get_accRole(focus is int child ? child : 0);
                if (role is int value)
                {
                    surface = (AccessibleRole)value switch
                    {
                        AccessibleRole.Cell or AccessibleRole.Table => FocusedOfficeSurface.Grid,
                        AccessibleRole.Document when control.get_accState(focus is int childId ? childId : 0) is int state &&
                            (state & (int)AccessibleStates.ReadOnly) == 0 => FocusedOfficeSurface.Document,
                        AccessibleRole.Diagram => FocusedOfficeSurface.Canvas,
                        _ => surface
                    };
                }
                if (focus is not IAccessible next || ReferenceEquals(next, control)) break;
                control = next;
            }
            var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            return NativeMethods.GetForegroundWindow() == target.Root && NativeMethods.GetGUIThreadInfo(0, ref info) &&
                info.Focus == target.Focus ? surface : default;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotImplementedException)
        { return default; }
    }
}
