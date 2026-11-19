using System.Runtime.InteropServices;

namespace NeatWin.Windows;

internal static class NativeWindowActivity
{
    // Query the real foreground thread's move/size loop, including the short interval in which
    // NeatWin suppresses its own WinEvent tail. Do not mistake an early user correction for failure.
    internal static bool IsMoving
    {
        get
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(0, ref info) && (info.Flags & 2) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal int Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal NativeMethods.Rect CaretRect;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
