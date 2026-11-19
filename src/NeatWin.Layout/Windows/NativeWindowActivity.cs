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
            var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            return NativeMethods.GetGUIThreadInfo(0, ref info) && (info.Flags & 2) != 0;
        }
    }
}
