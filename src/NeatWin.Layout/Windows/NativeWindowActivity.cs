using System.Runtime.InteropServices;

namespace NeatWin.Windows;

internal static class NativeWindowActivity
{
    internal static bool IsShellForeground(nint foreground)
    {
        if (foreground == 0 || foreground == NativeMethods.GetShellWindow()) return true;
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot);
        return WindowManager.IsSystemSurface(root != 0 ? root : foreground);
    }

    // The system move/size loop only covers dragging the window frame. Explorer's OLE
    // file drag owns the pointer from button-down until drop/cancel, even outside Explorer.
    internal static bool PointerBusy => NativeMethods.GetAsyncKeyState(0x01) < 0 ||
        NativeMethods.GetAsyncKeyState(0x02) < 0 || HasGuiFlags(0x02 | 0x04 | 0x08 | 0x10); // move/size or menu

    internal static bool AllowsAutomaticPlacement(nint expectedForeground)
    {
        var current = NativeMethods.GetForegroundWindow();
        return current == expectedForeground && !IsShellForeground(current) && !PointerBusy;
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
