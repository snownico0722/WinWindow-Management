using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NeatWin.Core;

namespace NeatWin.Windows;

public sealed partial class WindowManager
{
    private IReadOnlyList<WindowSnapshot>? _cachedSnapshot;
    private long _cachedTick = long.MinValue;

    internal void InvalidateCache()
    {
        _cachedSnapshot = null;
        _cachedTick = long.MinValue;
    }

    internal IReadOnlyList<WindowSnapshot> CaptureCoalesced(int maxAgeMilliseconds = 25)
    {
        var now = Environment.TickCount64;
        if (_cachedSnapshot is { } cached && now - _cachedTick is >= 0 && now - _cachedTick <= maxAgeMilliseconds)
            return cached;
        return Capture();
    }
    private const int SwShowMaximized = 3;
    private static readonly HashSet<string> SystemClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    public IReadOnlyList<WindowSnapshot> Capture()
    {
        var result = new List<WindowSnapshot>();
        var foreground = NativeMethods.GetForegroundWindow();
        var shell = NativeMethods.GetShellWindow();
        var ownProcessId = (uint)Environment.ProcessId;
        var zOrder = 0;

        var success = NativeMethods.EnumWindows((hwnd, data) =>
        {
            if (hwnd == shell || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            if (NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot) != hwnd)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcessId || AutomationGuard.IsUtility(hwnd))
            {
                return true;
            }

            if (IsCloaked(hwnd) || IsSystemSurface(hwnd))
            {
                return true;
            }

            if (!TryGetRect(hwnd, out var outerRect) || outerRect.IsEmpty)
            {
                return true;
            }

            var visualRect = TryGetExtendedFrameRect(hwnd, out var extendedFrame)
                ? extendedFrame
                : outerRect;
            if (visualRect.IsEmpty)
            {
                return true;
            }

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
            var monitorInfo = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            {
                return true;
            }

            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            if ((style & NativeMethods.WsChild) != 0 ||
                ((exStyle & NativeMethods.WsExLayered) != 0 &&
                 NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags) &&
                 (flags & 2) != 0 && alpha == 0))
            {
                return true;
            }

            var maximized = IsMaximized(hwnd);
            var owner = VisibleOwner(hwnd);
            var independent = owner == 0 || IsIndependentOwnedWindow(hwnd, owner, style, exStyle);
            var isToolWindow = (exStyle & NativeMethods.WsExToolWindow) != 0;
            var noActivate = (exStyle & NativeMethods.WsExNoActivate) != 0;
            var isResizable = (style & NativeMethods.WsThickFrame) != 0 && !maximized;
            var isTopmost = (exStyle & NativeMethods.WsExTopmost) != 0;
            var clickThrough = (exStyle & NativeMethods.WsExTransparent) != 0;
            var fullScreen = visualRect == ToRectI(monitorInfo.Monitor);
            var isManageable = NativeMethods.IsWindowEnabled(hwnd) && !isTopmost && !clickThrough && !fullScreen &&
                !maximized &&
                independent &&
                !isToolWindow &&
                !noActivate &&
                visualRect.Area >= 10_000;

            var workArea = ToRectI(monitorInfo.Work);
            var insets = new FrameInsets(
                Math.Max(0, visualRect.Left - outerRect.Left),
                Math.Max(0, visualRect.Top - outerRect.Top),
                Math.Max(0, outerRect.Right - visualRect.Right),
                Math.Max(0, outerRect.Bottom - visualRect.Bottom));

            result.Add(new WindowSnapshot(
                hwnd,
                visualRect,
                outerRect,
                workArea,
                monitor,
                insets,
                isResizable,
                hwnd == foreground,
                isManageable,
                zOrder++,
                Math.Max(96u, NativeMethods.GetDpiForWindow(hwnd)),
                processId,
                isTopmost,
                independent && !isToolWindow && !noActivate && visualRect.Area >= 10_000
                    ? (fullScreen ? "fullscreen" : maximized ? "maximized" : "windowed") : null) { Owner = owner });

            return true;
        }, nint.Zero);

        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows failed.");
        }

        _cachedSnapshot = result;
        _cachedTick = Environment.TickCount64;
        return result;
    }

    public void Apply(IReadOnlyList<TidyMove> plan) => SubmitMoves(plan, []);

    private IReadOnlyList<WindowMoveFailure> SubmitMoves(IReadOnlyList<TidyMove> plan, List<string> notes)
    {
        if (plan.Count == 0) return [];

        InvalidateCache();
        foreach (var move in plan) AutomationGuard.Mark(move.Window.Handle);
        const uint flags = NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpAsyncWindowPos;

        if (plan.Count > 1)
        {
            var stage = "BeginDeferWindowPos";
            var deferred = NativeMethods.BeginDeferWindowPos(plan.Count);
            if (deferred != 0)
            {
                stage = "DeferWindowPos";
                foreach (var move in plan)
                {
                    var outer = ToOuterRect(move.Window, move.TargetVisualRect);
                    deferred = NativeMethods.DeferWindowPos(deferred, move.Window.Handle, 0,
                        outer.X, outer.Y, outer.Width, outer.Height, flags);
                    if (deferred == 0) break;
                }
                if (deferred != 0)
                {
                    stage = "EndDeferWindowPos";
                    if (NativeMethods.EndDeferWindowPos(deferred)) return [];
                }
            }
            notes.Add($"{stage}: Win32 {Marshal.GetLastPInvokeError()}. Retrying individually.");
        }

        var failures = new List<WindowMoveFailure>();
        foreach (var move in plan)
        {
            var outer = ToOuterRect(move.Window, move.TargetVisualRect);
            if (!NativeMethods.SetWindowPos(move.Window.Handle, 0, outer.X, outer.Y, outer.Width, outer.Height, flags))
                failures.Add(new(move, Marshal.GetLastPInvokeError()));
        }
        return failures;
    }

    private static RectI ToOuterRect(WindowSnapshot window, RectI visualRect)
    {
        var insets = window.FrameInsets;
        return RectI.FromEdges(
            visualRect.Left - insets.Left,
            visualRect.Top - insets.Top,
            visualRect.Right + insets.Right,
            visualRect.Bottom + insets.Bottom);
    }

    private static bool IsCloaked(nint hwnd) =>
        NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaCloaked,
            out int cloaked,
            sizeof(int)) == 0 && cloaked != 0;

    private static bool TryGetExtendedFrameRect(nint hwnd, out RectI rect)
    {
        var status = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaExtendedFrameBounds,
            out NativeMethods.Rect nativeRect,
            Marshal.SizeOf<NativeMethods.Rect>());

        rect = status == 0 ? ToRectI(nativeRect) : default;
        return status == 0;
    }

    private static bool TryGetRect(nint hwnd, out RectI rect)
    {
        if (NativeMethods.GetWindowRect(hwnd, out var nativeRect))
        {
            rect = ToRectI(nativeRect);
            return true;
        }

        rect = default;
        return false;
    }

    internal static bool IsSystemSurface(nint hwnd)
    {
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassName(hwnd, className, className.Capacity);
        return SystemClasses.Contains(className.ToString());
    }

    private static nint VisibleOwner(nint hwnd)
    {
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner);
        return owner != 0 && NativeMethods.IsWindowVisible(owner) &&
            TryGetRect(owner, out var rect) && !rect.IsEmpty ? owner : 0;
    }

    private static bool IsIndependentOwnedWindow(nint hwnd, nint owner, long style, long exStyle)
    {
        if (!NativeMethods.IsWindowEnabled(owner) || (exStyle & NativeMethods.WsExDlgModalFrame) != 0)
            return false;
        const long frame = NativeMethods.WsCaption | NativeMethods.WsThickFrame;
        bool documentFrame = (style & frame) == frame &&
            (style & (NativeMethods.WsMinimizeBox | NativeMethods.WsMaximizeBox)) != 0;
        if (!documentFrame && (exStyle & NativeMethods.WsExAppWindow) == 0) return false;
        var className = new StringBuilder(256);
        return NativeMethods.GetClassName(hwnd, className, className.Capacity) > 0 && className.ToString() != "#32770";
    }

    private static bool IsMaximized(nint hwnd)
    {
        var placement = new NativeMethods.WindowPlacement
        {
            Length = Marshal.SizeOf<NativeMethods.WindowPlacement>(),
        };

        return NativeMethods.GetWindowPlacement(hwnd, ref placement) &&
               placement.ShowCommand == SwShowMaximized;
    }

    private static RectI ToRectI(NativeMethods.Rect rect) =>
        RectI.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
