using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NeatWin.Core;

namespace NeatWin.Windows;

public sealed partial class WindowManager
{
    internal static volatile Func<nint, bool>? CaptureFilterForTests;
    private IReadOnlyList<WindowSnapshot>? _cachedSnapshot;
    private long _cachedTick = long.MinValue;
    private static Dictionary<uint, (uint ParentPid, string Name)> _processTree = [];
    private static long _processTreeTick = long.MinValue;
    private static readonly object _processTreeLock = new();

    internal static uint ResolveParentProcessId(uint pid)
    {
        if (pid == 0) return 0;
        RefreshProcessTreeIfNeeded();
        lock (_processTreeLock)
            return _processTree.TryGetValue(pid, out var info) ? info.ParentPid : 0;
    }

    internal static string? ResolveProcessName(uint pid)
    {
        if (pid == 0) return null;
        RefreshProcessTreeIfNeeded();
        lock (_processTreeLock)
            if (_processTree.TryGetValue(pid, out var info) && !string.IsNullOrEmpty(info.Name))
                return info.Name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        { return null; }
    }

    private static void RefreshProcessTreeIfNeeded()
    {
        long now = Environment.TickCount64;
        lock (_processTreeLock)
        {
            if (now - _processTreeTick is >= 0 and <= 5_000 && _processTree.Count > 0) return;
            var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapProcess, 0);
            if (snapshot == 0 || snapshot == -1) return;
            try
            {
                var next = new Dictionary<uint, (uint, string)>();
                var entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
                if (NativeMethods.Process32First(snapshot, ref entry))
                {
                    do { next[entry.ProcessId] = (entry.ParentProcessId, entry.ExeFile ?? string.Empty); }
                    while (NativeMethods.Process32Next(snapshot, ref entry));
                }
                _processTree = next;
                _processTreeTick = now;
            }
            finally { _ = NativeMethods.CloseHandle(snapshot); }
        }
    }

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
    public IReadOnlyList<WindowSnapshot> Capture()
    {
        var result = new List<WindowSnapshot>();
        var foreground = NativeMethods.GetForegroundWindow();
        var shell = NativeMethods.GetShellWindow();
        var ownProcessId = (uint)Environment.ProcessId;
        var zOrder = 0;
        var includeWindow = CaptureFilterForTests;

        var success = NativeMethods.EnumWindows((hwnd, data) =>
        {
            if (includeWindow?.Invoke(hwnd) == false || hwnd == shell ||
                !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
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

            if (IsCloaked(hwnd))
            {
                return true;
            }

            var classBuilder = new StringBuilder(256);
            _ = NativeMethods.GetClassName(hwnd, classBuilder, classBuilder.Capacity);
            string className = classBuilder.ToString();
            if (WindowClassification.IsSystemSurface(className))
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
            bool isLayered = (exStyle & NativeMethods.WsExLayered) != 0;
            if ((style & NativeMethods.WsChild) != 0 ||
                (isLayered &&
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
            var hasCaption = (style & NativeMethods.WsCaption) == NativeMethods.WsCaption;
            var hasMaximizeBox = (style & NativeMethods.WsMaximizeBox) != 0;
            var workArea = ToRectI(monitorInfo.Work);
            var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(hwnd));
            string? processName = ResolveProcessName(processId);
            bool hasTitleText = NativeMethods.GetWindowTextLength(hwnd) > 0;

            // Tier 0A & Tier 0B：在采集入口剥除 18×18 事件靶、输入法浮条、浏览器无标题状态栏与瞬态贴靠栏/启动器浮层，
            // 彻底杜绝幽灵窗口污染遮挡几何或导致 SolverFeedback 丢失。
            bool ownedMenu = owner != 0 && !hasCaption && !isResizable && !clickThrough;
            bool excludedSurface = SurfaceAutonomy.IsGhostSurface(processName, className, visualRect, outerRect, workArea, dpi,
                    hwnd == foreground, isTopmost, isResizable, isToolWindow, clickThrough, isLayered, hasCaption, hasTitleText, owner, noActivate) ||
                SurfaceAutonomy.IsTransientWorkspaceFreezeOverlay(processName, className, visualRect, workArea, isTopmost, isToolWindow);
            bool possibleCapture = excludedSurface && WindowClassification.IsCaptureToolProcess(processName);
            if (excludedSurface && !possibleCapture) return true;

            uint parentPid = ResolveParentProcessId(processId);
            string? parentProcessName = parentPid != 0 && parentPid != processId ? ResolveProcessName(parentPid) : null;
            bool isChatFamily = WindowClassification.Identify(processName, className) == WindowUseRole.Chat ||
                WindowClassification.IsCrossProcessChatHelper(processName, parentProcessName);
            SurfacePurpose chatPurpose = SurfacePurpose.Unknown;
            if ((isChatFamily || possibleCapture) && hasTitleText)
            {
                var titleBuf = new StringBuilder(128);
                if (NativeMethods.GetWindowText(hwnd, titleBuf, titleBuf.Capacity) > 0)
                    chatPurpose = WindowClassification.ChatCaption(titleBuf.ToString());
            }

            bool hung = NativeMethods.IsHungAppWindow(hwnd);
            bool exemptPopup = hung || ownedMenu || SurfaceAutonomy.IsExemptFloatingPopup(processName, className, isResizable,
                hasMaximizeBox, exStyle, visualRect, dpi, chatPurpose, parentProcessName);
            bool nearFullGame = SurfaceAutonomy.IsProtectedNearFullscreenSurface(processName, visualRect, workArea);
            var fullScreen = visualRect == ToRectI(monitorInfo.Monitor);
            var isManageable = NativeMethods.IsWindowEnabled(hwnd) && !isTopmost && !clickThrough && !fullScreen &&
                !maximized &&
                !nearFullGame &&
                !exemptPopup &&
                independent &&
                !isToolWindow &&
                !noActivate &&
                visualRect.Area >= 10_000;

            var insets = new FrameInsets(
                Math.Max(0, visualRect.Left - outerRect.Left),
                Math.Max(0, visualRect.Top - outerRect.Top),
                Math.Max(0, outerRect.Right - visualRect.Right),
                Math.Max(0, outerRect.Bottom - visualRect.Bottom));

            var window = new WindowSnapshot(
                hwnd,
                visualRect,
                outerRect,
                workArea,
                monitor,
                insets,
                isResizable,
                hwnd == foreground,
                isManageable,
                zOrder,
                dpi,
                processId,
                isTopmost,
                independent && !isToolWindow && !noActivate && visualRect.Area >= 10_000
                    ? (fullScreen ? "fullscreen" : maximized || nearFullGame ? "maximized" : "windowed") : null)
            {
                Owner = owner,
                IsTransientMenu = SurfaceAutonomy.IsTransientMenu(processName, className, ownedMenu, noActivate, isToolWindow),
                NativeUtility = isToolWindow || noActivate || exemptPopup || owner != 0 && !independent,
                HasCaption = hasCaption,
                IndependentFrame = !exemptPopup && ((exStyle & NativeMethods.WsExAppWindow) != 0 ||
                    (style & (NativeMethods.WsMinimizeBox | NativeMethods.WsMaximizeBox)) != 0)
            };
            // 全屏取景仍交给 TaskSurfaces。截图本身不发起整理，独立变化可以沿用原工作窗。
            if (!excludedSurface || TaskSurfaces.IsCaptureTool(window, chatPurpose))
            {
                result.Add(window);
                zOrder++;
            }

            return true;
        }, nint.Zero);

        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows failed.");
        }

        _cachedSnapshot = TaskSurfaces.ResolveLocalRoots(result);
        _cachedTick = Environment.TickCount64;
        return _cachedSnapshot;
    }

    public void Apply(IReadOnlyList<TidyMove> plan) => SubmitMoves(plan, CanDeferMoves(plan), [], 0, out _, out _);

    private static bool CanDeferMoves(IReadOnlyList<TidyMove> plan)
    {
        if (plan.Count < 2) return false;
        var parent = NativeMethods.GetAncestor(plan[0].Window.Handle, NativeMethods.GaParent);
        if (parent == 0) return false;

        // EndDeferWindowPos 会同步发位置消息。用整批共享的 30ms 探测已卡住的队列，
        // 避免新近挂起尚未被 IsHungAppWindow 识别时拖住其余窗口。
        long deadline = Environment.TickCount64 + 30;
        foreach (var move in plan)
        {
            var hwnd = move.Window.Handle;
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid != move.Window.ProcessId ||
                (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64() & NativeMethods.WsChild) != 0 ||
                NativeMethods.GetAncestor(hwnd, NativeMethods.GaParent) != parent || NativeMethods.IsHungAppWindow(hwnd))
                return false;

            // GA_PARENT 取真实父级。独立的顶层有主窗口仍可与其他桌面窗口同批提交。
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0 || NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WmNull, 0, 0,
                    NativeMethods.SmtoBlock | NativeMethods.SmtoAbortIfHung, (uint)remaining, out _) == 0)
                return false;
        }
        return true;
    }

    private IReadOnlyList<WindowMoveFailure> SubmitMoves(IReadOnlyList<TidyMove> plan, bool deferMoves, List<string> notes,
        nint gestureWindow, out long? firstSubmitTick, out long? gestureSubmitTick)
    {
        firstSubmitTick = gestureSubmitTick = null;
        if (plan.Count == 0) return [];

        InvalidateCache();
        foreach (var move in plan) AutomationGuard.Mark(move.Window.Handle);
        // DeferWindowPos 不接受 SWP_ASYNCWINDOWPOS；逐窗提交仍用它隔离外部队列。
        const uint geometryFlags = NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder;

        if (deferMoves)
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
                        outer.X, outer.Y, outer.Width, outer.Height, geometryFlags);
                    if (deferred == 0) break;
                }
                if (deferred != 0)
                {
                    stage = "EndDeferWindowPos";
                    if (NativeMethods.EndDeferWindowPos(deferred))
                    {
                        firstSubmitTick = Environment.TickCount64;
                        if (gestureWindow != 0 && plan.Any(m => m.Window.Handle == gestureWindow)) gestureSubmitTick = firstSubmitTick;
                        return [];
                    }
                }
            }
            notes.Add($"{stage}: Win32 {Marshal.GetLastPInvokeError()}. Retrying individually.");
        }

        var failures = new List<WindowMoveFailure>();
        foreach (var move in plan)
        {
            var outer = ToOuterRect(move.Window, move.TargetVisualRect);
            if (!NativeMethods.SetWindowPos(move.Window.Handle, 0, outer.X, outer.Y, outer.Width, outer.Height,
                    geometryFlags | NativeMethods.SwpAsyncWindowPos))
                failures.Add(new(move, Marshal.GetLastPInvokeError()));
            else
            {
                long tick = Environment.TickCount64;
                firstSubmitTick ??= tick;
                if (move.Window.Handle == gestureWindow) gestureSubmitTick = tick;
            }
        }
        return failures;
    }

    // 规划用可见边框计算对齐，原生移动接口接收外框。提交时补回每扇窗自己的边距，才能让并排后的可见边缘对齐。
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

    internal static bool TryGetRect(nint hwnd, out RectI rect)
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
        return WindowClassification.IsSystemSurface(className.ToString());
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
        return NativeMethods.GetClassName(hwnd, className, className.Capacity) > 0 &&
            className.ToString() is not ("#32770" or "OperationStatusWindow" or "Ghost");
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
