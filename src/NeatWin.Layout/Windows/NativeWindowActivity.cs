using NeatWin.Core;
using System.Runtime.InteropServices;
using System.Text;

namespace NeatWin.Windows;

internal static class NativeWindowActivity
{
    // 微信多个独立功能共用同一 Qt 窗口类。仅为已匹配的聊天宿主或截图工具框读取系统窗标题，返回用途。
    internal static SurfacePurpose ChatSurface(WindowSnapshot w)
    {
        var text = new StringBuilder(128);
        return NativeMethods.GetWindowText(w.Handle, text, text.Capacity) > 0 ? TaskSurfaces.ChatCaption(text.ToString()) : SurfacePurpose.Unknown;
    }

    internal static RectI? CaretClientRect(nint root)
    {
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(0, ref info) || info.Caret == 0 ||
            NativeMethods.GetAncestor(info.Caret, NativeMethods.GaRoot) != root) return null;
        var r = info.CaretRect;
        return r.Bottom > r.Top ? RectI.FromEdges(r.Left, r.Top, r.Right, r.Bottom) : null;
    }

    internal static bool IsShellForeground(nint foreground)
    {
        if (foreground == 0 || foreground == NativeMethods.GetShellWindow()) return true;
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot);
        var target = root != 0 ? root : foreground;
        // 同时检查当前前台句柄与根句柄类名，避免 XAML Island 子宿主（如托盘溢出岛、贴靠栏、CLaunch、开始菜单）漏判。
        if (WindowManager.IsSystemSurface(foreground) || (target != foreground && WindowManager.IsSystemSurface(target)))
            return true;
        NativeMethods.GetWindowThreadProcessId(target, out uint pid);
        if (pid == 0) return true;
        string? proc = WindowManager.ResolveProcessName(pid);
        var cls = new StringBuilder(256);
        _ = NativeMethods.GetClassName(target, cls, cls.Capacity);
        if (!WindowManager.TryGetRect(target, out var rect)) rect = default;
        return SurfaceAutonomy.IsTransientWorkspaceFreezeOverlay(proc, cls.ToString(), rect, default, false, false);
    }

    internal static bool SuspendsWorkspace(nint foreground)
    {
        // 工作台、系统贴靠栏、托盘溢出面板与 CLaunch 快捷启动器激活时必须挂起自动整理，防止残留焦点把后台浏览器压扁。
        if (IsShellForeground(foreground) || AutomationGuard.IsUtility(foreground)) return true;
        NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        return pid == 0 || pid == Environment.ProcessId;
    }

    // The system move/size loop only covers dragging the window frame. Explorer's OLE
    // file drag owns the pointer from button-down until drop/cancel, even outside Explorer.
    internal static bool PointerBusy => NativeMethods.GetAsyncKeyState(0x01) < 0 ||
        NativeMethods.GetAsyncKeyState(0x02) < 0 || HasGuiFlags(0x02 | 0x04 | 0x08 | 0x10); // move/size or menu

    internal static bool AllowsAutomaticPlacement(nint expectedForeground)
    {
        var current = NativeMethods.GetForegroundWindow();
        return current == expectedForeground && !SuspendsWorkspace(current) && !PointerBusy;
    }

    // Query the real foreground thread's move/size loop, including the short interval in which
    // NeatWin suppresses its own WinEvent tail. Do not mistake an early user correction for failure.
    internal static bool IsMoving => HasGuiFlags(0x02);

    private static bool HasGuiFlags(int flags)
    {
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        return NativeMethods.GetGUIThreadInfo(0, ref info) && (info.Flags & flags) != 0;
    }
}
