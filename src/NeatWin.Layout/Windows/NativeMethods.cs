using System.Runtime.InteropServices;
using System.Text;

namespace NeatWin.Windows;

internal static class NativeMethods
{
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(nint parent, nint child);

    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromWindow(nint window, uint objectId, ref Guid id,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible? accessible);

    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const long WsChild = 0x40000000L;
    internal const long WsCaption = 0x00C00000L;
    internal const long WsMinimizeBox = 0x00020000L;
    internal const long WsMaximizeBox = 0x00010000L;
    internal const long WsExAppWindow = 0x00040000L;
    internal const long WsExDlgModalFrame = 0x00000001L;
    internal const long WsThickFrame = 0x00040000L;
    internal const long WsExTopmost = 0x00000008L;
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExLayered = 0x00080000L;
    [DllImport("user32.dll")]
    internal static extern bool GetLayeredWindowAttributes(nint hwnd, out uint color, out byte alpha, out uint flags);
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const uint GaParent = 1;
    internal const uint GaRoot = 2;
    internal const uint GwOwner = 4;
    internal const uint MonitorDefaultToNearest = 2;
    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaCloaked = 14;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoOwnerZOrder = 0x0200;
    internal const uint SwpAsyncWindowPos = 0x4000;
    internal const uint WmNull = 0x0000;
    internal const int WmHotkey = 0x0312;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmMButtonUp = 0x0208;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmMouseWheel = 0x020A;
    internal const int WmMouseHWheel = 0x020E;
    internal const uint MouseInputInjected = 0x00000001;
    private const uint SpiGetMouseWheelRouting = 0x201C;
    private const uint MouseWheelRoutingUnderPointer = 2;
    internal const uint WmNcHitTest = 0x0084;
    internal const int HtNowhere = 0;
    internal const int HtClient = 1;
    internal const int HtCaption = 2;
    internal const int WhMouseLl = 14;
    internal const int HcAction = 0;
    internal const uint SmtoBlock = 0x0001;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint EventSystemMoveSizeStart = 0x000A;
    internal const uint EventSystemMoveSizeEnd = 0x000B;
    internal const uint WinEventOutOfContext = 0x0000;
    internal const uint WinEventSkipOwnProcess = 0x0002;
    internal static readonly nint HwndMessage = new(-3);

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    internal delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);
    internal delegate nint LowLevelMouseProc(int code, nint message, nint dataPointer);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc enumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);

    internal static bool ScrollsInactiveWindows() =>
        SystemParametersInfo(SpiGetMouseWheelRouting, 0, out var routing, 0) && routing == MouseWheelRoutingUnderPointer;

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hwnd, ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint BeginDeferWindowPos(int windowCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint DeferWindowPos(
        nint positionInfo,
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndDeferWindowPos(nint positionInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeout(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nuint result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(
        int hookType,
        LowLevelMouseProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint message,
        nint dataPointer);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    internal static nint GetWindowLongPtr(nint hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new nint(GetWindowLong32(hwnd, index));

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MsllHookStruct
    {
        internal Point Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        internal int Length;
        internal int Flags;
        internal int ShowCommand;
        internal Point MinPosition;
        internal Point MaxPosition;
        internal Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] internal static extern bool GetLastInputInfo(ref LastInput value);
    [DllImport("user32.dll")] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder app);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] internal static extern bool StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(in Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(nint sessionGuid, uint streamFlags, out nint sessionControl);
        [PreserveSig]
        int GetSimpleAudioVolume(nint sessionGuid, uint streamFlags, out nint audioVolume);
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnum);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);
        [PreserveSig]
        int GetSession(int sessionIndex, [MarshalAs(UnmanagedType.IUnknown)] out object? session);
    }

    [ComImport]
    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out int state);
        [PreserveSig]
        int GetDisplayName(out nint name);
        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, in Guid context);
        [PreserveSig]
        int GetIconPath(out nint path);
        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, in Guid context);
        [PreserveSig]
        int GetGroupingParam(out Guid grouping);
        [PreserveSig]
        int SetGroupingParam(in Guid grouping, in Guid context);
        [PreserveSig]
        int RegisterAudioSessionNotification(nint client);
        [PreserveSig]
        int UnregisterAudioSessionNotification(nint client);
        [PreserveSig]
        int GetSessionIdentifier(out nint id);
        [PreserveSig]
        int GetSessionInstanceIdentifier(out nint instanceId);
        [PreserveSig]
        int GetProcessId(out uint processId);
        [PreserveSig]
        int IsSystemSoundsSession();
        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
    internal const int StretchColorOnColor = 3;
    internal const uint Th32csSnapProcess = 0x00000002;
    internal const int AudclntSNoSingleProcess = 0x0889000D;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExeFile;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsHungAppWindow(nint hwnd);

    [DllImport("gdi32.dll")]
    internal static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("powrprof.dll")]
    internal static extern uint CallNtPowerInformation(int level, nint inputBuffer, uint inputLength, out uint outputBuffer, uint outputLength);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProp(nint hwnd, string name, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetProp(nint hwnd, string name);
}
