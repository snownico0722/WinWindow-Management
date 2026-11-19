using System.Runtime.InteropServices;

namespace NeatWin.Windows;

// Shared by both executables. No files, IPC server, process names or persistent window identity.
internal static class AutomationGuard
{
    private const string PropertyName = "NeatWin.AutomationUntil.v1";

    internal static void Mark(nint hwnd)
    {
        // TickCount uses modular uint arithmetic, including the 49-day wrap. Never store zero.
        var expiry = unchecked((uint)Environment.TickCount + 2000u);
        _ = SetProp(hwnd, PropertyName, (nint)(long)(expiry == 0 ? 1u : expiry));
    }

    internal static nint Stamp(nint hwnd) => GetProp(hwnd, PropertyName);
    internal static bool IsUtility(nint hwnd) => GetProp(hwnd, "NeatWin.Utility.v1") != nint.Zero;
    internal static void MarkUtility(nint hwnd) => SetProp(hwnd, "NeatWin.Utility.v1", (nint)1);

    internal static bool IsMarked(nint hwnd)
    {
        var value = GetProp(hwnd, PropertyName);
        if (value == nint.Zero) return false;
        var remaining = unchecked((int)((uint)value.ToInt64() - (uint)Environment.TickCount));
        return remaining is > 0 and <= 2000;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(nint hwnd, string name, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint hwnd, string name);
}
