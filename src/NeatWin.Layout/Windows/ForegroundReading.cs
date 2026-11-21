using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using NeatWin.Core;

namespace NeatWin.Windows;

// All native handles stay local. Read the actual foreground input queue once per sample.
// A GUI caret owned by an unrelated window or stale queue must not protect a new task.
internal readonly record struct ForegroundReading(nint Root, nint Focus, uint Pid, bool Caret, uint LastInput = 0)
{
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
            (info.Focus == info.Caret || IsChild(info.Focus, info.Caret) || IsChild(info.Caret, info.Focus));
        var input = new NativeMethods.LastInput { Size = (uint)Marshal.SizeOf<NativeMethods.LastInput>() };
        NativeMethods.GetLastInputInfo(ref input);
        return NativeMethods.GetForegroundWindow() == expected.Handle
            ? new(expected.Handle, info.Focus, expected.ProcessId, caret, input.Time) : default;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);
}

// WPS can use the same Qt top-level class for a start page, grid, document and slide
// canvas. Ask only its foreground accessibility provider for control roles. A blocked
// provider can occupy one worker, never the arranging thread or a queue of retries.
internal sealed class OfficeSurfaceProbe(Func<ForegroundReading, Task<FocusedOfficeSurface>>? query = null)
{
    private Task<FocusedOfficeSurface>? _pending;
    private ForegroundReading _queryTarget, _cachedTarget;
    private FocusedOfficeSurface _cached;
    private long _started;

    internal FocusedOfficeSurface Sample(ForegroundReading target, long now)
    {
        if (target.Root == 0) return default;
        if (_pending is { IsCompleted: true })
        {
            _cachedTarget = _queryTarget;
            _cached = _pending.IsCompletedSuccessfully ? _pending.Result : default;
            _pending = null;
        }
        bool same = _cachedTarget == target;
        if (_pending == null && (!same || now - _started >= 2_000))
        {
            _queryTarget = target;
            _started = now;
            _pending = query?.Invoke(target) ?? Task.Run(() => Read(target));
        }
        // A completed sample remains evidence at long intervals when both the native
        // input target and last user input are identical. Refresh it in the background.
        return same ? _cached : default;
    }

    internal bool JustStarted(nint root, long now) =>
        _pending != null && _queryTarget.Root == root &&
        _queryTarget != _cachedTarget && now - _started < 200;

    internal void Retire(nint root)
    {
        if (_cachedTarget.Root == root) _cachedTarget = default;
        if (_queryTarget.Root == root) _queryTarget = default;
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
                AccessibleObjectFromWindow(target.Focus, 0xfffffffc, ref id, out var control) < 0 || control == null)
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

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint window, uint objectId, ref Guid id,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
}
