using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NeatWin.Core;

namespace WinWindow;

internal static class Native
{
    internal const long Layered = 0x80000, Transparent = 0x20, TopmostStyle = 8, ToolWindow = 0x80, NoActivateStyle = 0x08000000L;
    internal const uint NoSize = 1, NoMove = 2, NoActivate = 0x10, AsyncPosition = 0x4000;
    private const uint NoZOrder = 4, NoOwnerZOrder = 0x0200, NoSendChanging = 0x0400;
    internal const uint EventLocationChange = 0x800B;
    internal const int Hide = 0, Normal = 1, Minimized = 2, Maximized = 3, ShowNoActivate = 4, Restore = 9, ForceMinimize = 11;
    private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputEvent
    {
        public uint Type;
        public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Placement
    {
        public uint Length; public uint Flags; public uint ShowCmd;
        public Point MinPosition; public Point MaxPosition; public Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    internal readonly record struct IconWindowGeometry(Rect Frame, Rect Outer, MonitorInfo Display);
    internal readonly record struct IconRestorePlacement(Placement Placement, nint Monitor, Rect Work);
    [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo { public Point Reserved; public Point MaxSize; public Point MaxPosition; public Point MinTrack; public Point MaxTrack; }
    internal delegate bool EnumProc(nint hwnd, nint data);
    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);
    internal delegate void EventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetActiveWindow();
    [DllImport("user32.dll")] internal static extern nint GetFocus();
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint hwnd, string name);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLong64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLong64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(nint hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetLayeredWindowAttributes(nint hwnd, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MouseInputEvent[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(in Rect rect, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(nint hwnd, uint message, nint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint filter);
    [DllImport("user32.dll")] internal static extern uint InSendMessageEx(nint reserved);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint first, uint last, nint module, EventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);

    internal static nint HookExternalWindowEvents(uint first, uint last, EventProc callback)
    {
        const uint outOfContext = 0, skipOwnProcess = 2;
        // 排除本进程，移动浮条时不会向同一钩子再发一次位置事件。
        return SetWinEventHook(first, last, 0, callback, 0, 0, outOfContext | skipOwnProcess);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeRect(nint hwnd, int attribute, out Rect rect, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIconW(string file, int index, uint flags, out nint largeIcon, nint smallIcon, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("kernel32.dll", EntryPoint = "K32EnumProcessModulesEx", SetLastError = true)] private static extern bool EnumProcessModulesEx(nint process, [Out] nint[] modules, uint cb, out uint needed, uint filter);
    [DllImport("kernel32.dll", EntryPoint = "K32GetModuleFileNameExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameEx(nint process, nint module, StringBuilder fileName, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint baseAddress, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateRemoteThread(nint process, nint attributes, nuint stackSize, nint startAddress, nint parameter, uint creationFlags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(nint thread, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint streamFlags, out nint sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint streamFlags, out nint audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, [MarshalAs(UnmanagedType.IUnknown)] out object? session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, in Guid context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, in Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(in Guid grouping, in Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint client);
        [PreserveSig] int UnregisterAudioSessionNotification(nint client);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint instanceId);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, nint eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wp, nint lp, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern nint GetClassLong64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(nint hwnd, int index);

    public static ImageSource? WindowIcon(WindowIdentity id)
    {
        if (Matches(id))
        {
            const uint getIcon = 0x007F, abortIfHung = 0x0002;
            // Request the large, alternate small, then small window icon. Borrowed HICONs stay with the owner.
            foreach (var size in new[] { 1, 2, 0 })
            {
                if (SendMessageTimeout(id.Hwnd, getIcon, size, 0, abortIfHung, 60, out var icon) == 0) break;
                if (CopyIcon(icon) is { } bitmap) return bitmap;
            }
            foreach (var index in new[] { -14, -34 })
            {
                var icon = IntPtr.Size == 8 ? GetClassLong64(id.Hwnd, index) : (nint)GetClassLong32(id.Hwnd, index);
                if (CopyIcon(icon) is { } bitmap) return bitmap;
            }
        }
        return ExecutableIcon(id.ProcessPath);
    }

    private static ImageSource? CopyIcon(nint icon)
    {
        if (icon == 0) return null;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or InvalidOperationException) { return null; }
    }

    internal static ImageSource? ExecutableIcon(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        if (IconCache.TryGetValue(processPath, out var cached)) return cached;
        if (!File.Exists(processPath)) return null;
        try
        {
            // 保留窗口自己的图标，只有按路径取图时才向 Shell 请求 64 像素图标。
            int result = SHDefExtractIconW(processPath, 0, 0, out nint large, 0, 64);
            try
            {
                if (result == 0 && CopyIcon(large) is { } icon)
                    return IconCache.GetOrAdd(processPath, icon);
            }
            finally
            {
                if (large != 0) DestroyIcon(large);
            }

            using var fallback = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            var bitmap = fallback is null ? null : CopyIcon(fallback.Handle);
            return bitmap is null ? null : IconCache.GetOrAdd(processPath, bitmap);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }

    internal static bool PositionOutline(nint hwnd, Rect rect) =>
        SetWindowPos(hwnd, (nint)(-1), rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, NoActivate);

    public static bool TryGetFrameBounds(nint hwnd, out Rect rect)
    {
        rect = default;
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (DwmGetWindowAttributeRect(hwnd, 9, out rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd, out rect))
            return false;
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    public static void ConfigureOverlay(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return;
        long mask = Transparent | ToolWindow | NoActivateStyle;
        long value = Style(hwnd) | mask;
        if (IntPtr.Size == 8) SetWindowLong64(hwnd, -20, (nint)value);
        else SetWindowLong32(hwnd, -20, (int)value);
    }

    public static void ConfigureInteractiveOverlay(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return;
        // 顶栏快捷按钮和收起后的置顶大图标需要接收鼠标点击与拖拽，但不能抢走当前工作窗口焦点，也不进入任务栏与 Alt+Tab。
        long value = (Style(hwnd) & ~Transparent) | ToolWindow | NoActivateStyle;
        if (IntPtr.Size == 8) SetWindowLong64(hwnd, -20, (nint)value);
        else SetWindowLong32(hwnd, -20, (int)value);
    }

    internal static uint WindowDpi(nint hwnd)
    {
        if (hwnd != 0 && IsWindow(hwnd) && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi;
        }
        return 96u;
    }

    internal static bool PositionOverlay(nint hwnd, int x, int y) =>
        SetWindowPos(hwnd, (nint)(-1), x, y, 0, 0, NoSize | NoActivate);

    internal static bool TryGetTitleBarOverlayTrack(nint targetHwnd, int barWidthPx, int barHeightPx, double scale, out Rect track)
    {
        track = default;
        if (!TryGetFrameBounds(targetHwnd, out var frame)) return false;
        if (DwmGetWindowAttribute(targetHwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        bool hasMonitor = GetMonitorInfo(MonitorFromWindow(targetHwnd, 2), ref info);
        if (hasMonitor && (!Overlaps(frame, info.Work) ||
            (!IsZoomed(targetHwnd) && frame.Left <= info.Monitor.Left && frame.Top <= info.Monitor.Top &&
             frame.Right >= info.Monitor.Right && frame.Bottom >= info.Monitor.Bottom))) return false;
        int baseLeft = hasMonitor ? Math.Max(frame.Left, info.Work.Left) : frame.Left;
        int baseTop = hasMonitor ? Math.Max(frame.Top, info.Work.Top) : frame.Top;
        int left = baseLeft + (int)Math.Round(8 * scale);
        int top = baseTop + (int)Math.Round(4 * scale);
        // 横拖后仍给目标窗口右侧的系统按钮留出原有余量。
        int right = frame.Right - (int)Math.Round(72 * scale);
        int bottom = frame.Bottom;
        if (hasMonitor)
        {
            right = Math.Min(right, info.Work.Right);
            bottom = Math.Min(bottom, info.Work.Bottom);
        }
        if (right - left < barWidthPx || bottom - top < barHeightPx) return false;
        track = new Rect { Left = left, Top = top, Right = right, Bottom = top + barHeightPx };
        return true;
    }

    internal static bool MoveWindowControl(nint hwnd, int x, int y) =>
        SetWindowPos(hwnd, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate | NoOwnerZOrder | NoSendChanging);

    internal static bool TryGetIconWindowGeometry(nint hwnd, out IconWindowGeometry geometry)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        geometry = default;
        if (!TryGetFrameBounds(hwnd, out var frame) || !GetWindowRect(hwnd, out var outer) ||
            !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return false;
        geometry = new(frame, outer, info);
        return true;
    }

    internal static IconRestorePlacement? PlanIconRestore(nint hwnd, Placement placement, IconWindowGeometry geometry, Rect? movedBadge)
    {
        var frame = geometry.Frame;
        var outer = geometry.Outer;
        bool maximized = placement.ShowCmd == Maximized;
        if (movedBadge is null && IsOnDesktop(frame)) return null;

        var anchor = movedBadge ?? frame;
        nint monitor = MonitorFromRect(in anchor, 2);
        var display = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref display)) return null;
        if (maximized && MonitorFromWindow(hwnd, 0) == monitor) return null;

        bool toolWindow = (Style(hwnd) & ToolWindow) != 0;
        var work = display.Work;
        if (work.Right <= work.Left || work.Bottom <= work.Top) return null;
        // WINDOWPLACEMENT 用工作区坐标，DWM 边框用屏幕坐标。在完成定位后才换回工作区坐标。
        if (maximized)
        {
            var normal = placement.NormalPosition;
            if (!toolWindow)
                normal = OffsetRect(normal, geometry.Display.Work.Left - geometry.Display.Monitor.Left,
                    geometry.Display.Work.Top - geometry.Display.Monitor.Top);
            bool valid = normal.Right > normal.Left && normal.Bottom > normal.Top;
            frame = valid ? normal : geometry.Frame;
        }
        if (movedBadge is { } badge)
            frame = FrameByBadge(frame, badge, work, WindowDpi(hwnd));
        else
            frame = FitFrame(frame, work, frame.Left, frame.Top);

        if (!maximized)
        {
            frame.Left += outer.Left - geometry.Frame.Left;
            frame.Top += outer.Top - geometry.Frame.Top;
            frame.Right += outer.Right - geometry.Frame.Right;
            frame.Bottom += outer.Bottom - geometry.Frame.Bottom;
        }
        int workX = toolWindow ? 0 : display.Work.Left - display.Monitor.Left;
        int workY = toolWindow ? 0 : display.Work.Top - display.Monitor.Top;
        placement.NormalPosition = OffsetRect(frame, -workX, -workY);
        if (maximized)
        {
            // 用目标屏的最大化位置和正常位置一起提交，避免先在旧屏显示后再还原换屏。
            placement.MaxPosition = new Point
            {
                X = display.Work.Left - workX + geometry.Outer.Left - geometry.Display.Work.Left,
                Y = display.Work.Top - workY + geometry.Outer.Top - geometry.Display.Work.Top
            };
        }
        return new(placement, monitor, work);
    }

    private static Rect OffsetRect(Rect rect, int x, int y) =>
        new() { Left = rect.Left + x, Top = rect.Top + y, Right = rect.Right + x, Bottom = rect.Bottom + y };

    internal static bool FrameInside(Rect frame, Rect area) =>
        frame.Left >= area.Left && frame.Top >= area.Top && frame.Right <= area.Right && frame.Bottom <= area.Bottom;

    internal static int VirtualDesktopLeft() => GetSystemMetrics(76);

    internal static bool IsOnDesktop(Rect frame)
    {
        List<RectI> uncovered = [RectI.FromEdges(frame.Left, frame.Top, frame.Right, frame.Bottom)];
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var display = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref display))
            {
                var bounds = display.Monitor;
                uncovered = RectRegion.Subtract(uncovered, RectI.FromEdges(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                    int.MaxValue);
            }
            return uncovered.Count != 0;
        }, 0);
        return uncovered.Count == 0;
    }

    internal static bool IsRestoredWindow(nint hwnd, bool maximized, nint monitor = 0) =>
        IsWindowVisible(hwnd) && !IsIconic(hwnd) && IsZoomed(hwnd) == maximized &&
        (monitor == 0 || MonitorFromWindow(hwnd, 0) == monitor);

    private static Rect FrameByBadge(Rect frame, Rect badge, Rect work, uint dpi)
    {
        int inset = (int)Math.Round(6 * dpi / 96.0);
        return FitFrame(frame, work, badge.Left - inset, badge.Top - inset);
    }

    private static Rect FitFrame(Rect frame, Rect work, int left, int top)
    {
        int width = Math.Min(frame.Right - frame.Left, work.Right - work.Left);
        int height = Math.Min(frame.Bottom - frame.Top, work.Bottom - work.Top);
        left = Math.Clamp(left, work.Left, work.Right - width);
        top = Math.Clamp(top, work.Top, work.Bottom - height);
        return new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    internal static bool NearFrame(Rect first, Rect second) =>
        Math.Abs(first.Left - second.Left) <= 2 && Math.Abs(first.Top - second.Top) <= 2 &&
        Math.Abs(first.Right - second.Right) <= 2 && Math.Abs(first.Bottom - second.Bottom) <= 2;

    internal static bool IsAtBadge(nint hwnd, Rect badge, Rect work) =>
        TryGetFrameBounds(hwnd, out var frame) &&
        FrameInside(frame, work) &&
        NearFrame(frame, FrameByBadge(frame, badge, work, WindowDpi(hwnd)));

    internal static bool PostFitRestoredWindow(WindowIdentity identity, Rect? badge, Rect work)
    {
        EnsureIdentity(identity);
        nint hwnd = identity.Hwnd;
        if (!TryGetFrameBounds(hwnd, out var frame) || !GetWindowRect(hwnd, out var outer)) return false;
        var wanted = badge is { } anchor ? FrameByBadge(frame, anchor, work, WindowDpi(hwnd))
            : FitFrame(frame, work, frame.Left, frame.Top);
        int x = wanted.Left + outer.Left - frame.Left;
        int y = wanted.Top + outer.Top - frame.Top;
        int width = wanted.Right - wanted.Left + (outer.Right - frame.Right) - (outer.Left - frame.Left);
        int height = wanted.Bottom - wanted.Top + (outer.Bottom - frame.Bottom) - (outer.Top - frame.Top);
        return SetWindowPos(hwnd, 0, x, y, width, height, NoZOrder | NoActivate | NoOwnerZOrder | AsyncPosition);
    }

    internal static bool RoundOverlayRegion(nint hwnd, int width, int height, int diameter)
    {
        nint region = CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
        if (region == 0) return false;
        // 用最外侧直边的像素判断实际区域是否偏小。只补 GDI 少给的边，避免无条件放宽圆角。
        var (right, bottom) = OverlayRegionEdges(region, width, height);
        if (!right || !bottom)
        {
            DeleteObject(region);
            region = CreateRoundRectRgn(0, 0, width + (right ? 0 : 1), height + (bottom ? 0 : 1), diameter, diameter);
            if (region == 0) return false;
        }
        // 系统在成功设置后接管区域句柄，此处只释放设置失败的区域。
        if (SetWindowRgn(hwnd, region, true) != 0) return true;
        DeleteObject(region);
        return false;
    }

    internal static bool TryUseSystemOverlayCorners(nint hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return false;
        const int cornerPreference = 33, borderColor = 34;
        int round = 2;
        if (DwmSetWindowAttribute(hwnd, cornerPreference, ref round, sizeof(int)) != 0) return false;
        // 外层描边由应用绘制，避免再叠一圈系统边框。
        int noBorder = unchecked((int)0xfffffffe);
        DwmSetWindowAttribute(hwnd, borderColor, ref noBorder, sizeof(int));
        return true;
    }

    internal static bool UsesSystemOverlayCorners(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, 33, out int corner, sizeof(int)) == 0 && corner == 2;

    private static (bool Right, bool Bottom) OverlayRegionEdges(nint region, int width, int height) =>
        (PtInRegion(region, width - 1, height / 2), PtInRegion(region, width / 2, height - 1));

    internal static (bool Right, bool Bottom) GetOverlayRegionEdgesForTest(nint hwnd, int width, int height)
    {
        nint region = CreateRoundRectRgn(0, 0, width, height, 0, 0);
        if (region == 0) return default;
        try
        {
            return GetWindowRgn(hwnd, region) != 0 ? OverlayRegionEdges(region, width, height) : default;
        }
        finally { DeleteObject(region); }
    }

    internal static bool IsPrimaryMouseButtonDown()
    {
        const int swapButton = 23, leftButton = 1, rightButton = 2;
        return GetAsyncKeyState(GetSystemMetrics(swapButton) == 0 ? leftButton : rightButton) < 0;
    }

    public static Rect OverlayBounds(nint hwnd, Point anchor, int width, int height)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(anchor, 2), ref info)) return default;
        var dpi = GetDpiForWindow(hwnd);
        int w = (int)Math.Ceiling(width * dpi / 96.0), h = (int)Math.Ceiling(height * dpi / 96.0);
        int x = Math.Clamp(anchor.X, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - w));
        int y = Math.Clamp(anchor.Y, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - h));
        return new Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    public static bool PositionWindowControl(nint hwnd, nint target, Rect? bounds = null)
    {
        bool badge = target == 0;
        bool topmost = badge || IsTopmost(target);
        if (!SetOwnTopmost(hwnd, topmost)) return false;
        bool inLayer = badge || HasTargetBelow(hwnd, target);
        nint after = inLayer ? 0 : GetWindow(target, 3);
        // 插在目标正上方，仍让挡住目标的窗口盖住按钮。跨过置顶层边界时用 HWND_TOP 保留现有层。
        if (after != 0 && IsTopmost(after) != topmost) after = 0;
        if (after == hwnd) inLayer = true;
        uint flags = NoActivate | NoOwnerZOrder | (inLayer ? NoZOrder : 0);
        if (bounds is not { } rect)
            return inLayer || SetWindowPos(hwnd, after, 0, 0, 0, 0, flags | NoMove | NoSize | NoSendChanging);

        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (GetWindowRect(hwnd, out var current))
        {
            bool samePosition = rect.Left == current.Left && rect.Top == current.Top;
            bool sameSize = width == current.Right - current.Left && height == current.Bottom - current.Top;
            if (inLayer && samePosition && sameSize) return true;
            if (samePosition) flags |= NoMove;
            if (sameSize) flags |= NoSize | NoSendChanging;
        }
        return SetWindowPos(hwnd, after, rect.Left, rect.Top, width, height, flags);
    }

    private static bool HasTargetBelow(nint overlay, nint target)
    {
        // 不可见的辅助 HWND 不遮挡原窗，夹在中间时保留浮条现有层级。
        for (nint next = GetWindow(overlay, 2); next != 0; next = GetWindow(next, 2))
        {
            if (next == target) return true;
            if (IsWindowVisible(next) && !IsIconic(next) &&
                (DwmGetWindowAttribute(next, 14, out int cloaked, 4) != 0 || cloaked == 0)) return false;
        }
        return false;
    }

    internal static void FitIconOverlayToWorkArea(nint hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) || !GetWindowRect(hwnd, out Rect r)) return;
        int width = Math.Max(32, r.Right - r.Left);
        int height = Math.Max(32, r.Bottom - r.Top);
        int x = Math.Clamp(r.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        int y = Math.Clamp(r.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        SetWindowPos(hwnd, (nint)(-1), x, y, 0, 0, NoSize | NoActivate);
    }

    public static string Title(nint hwnd) { var b = new StringBuilder(Math.Max(256, GetWindowTextLength(hwnd) + 1)); GetWindowText(hwnd, b, b.Capacity); return b.ToString(); }
    public static string ClassName(nint hwnd) { var b = new StringBuilder(256); GetClassName(hwnd, b, b.Capacity); return b.ToString(); }
    public static long Style(nint hwnd) => IntPtr.Size == 8 ? GetWindowLong64(hwnd, -20).ToInt64() : GetWindowLong32(hwnd, -20);
    public static bool IsTopmost(nint hwnd) => (Style(hwnd) & TopmostStyle) != 0;
    public static byte ToAlpha(int percent) => (byte)((Math.Clamp(percent, 0, 100) * 255 + 50) / 100);
    public static int ToPercent(byte alpha) => (alpha * 100 + 127) / 255;
    public static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    private static bool IsShell(nint hwnd) => ClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "DV2ControlHost";
    public static bool IsTarget(nint hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != Environment.ProcessId && !IsShell(hwnd);
    }
    public static WindowIdentity? Identity(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            long started = process.StartTime.ToUniversalTime().Ticks;
            string path = process.ProcessName;
            nint handle = OpenProcess(0x1000, false, pid);
            if (handle != 0)
            {
                try { path = ReadProcessPath(handle) ?? path; }
                finally { CloseHandle(handle); }
            }
            return new(hwnd.ToInt64(), (int)pid, started, path, ClassName(hwnd));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }
    public static bool Matches(WindowIdentity identity)
    {
        if (!IsWindow(identity.Hwnd)) return false;
        var current = Identity(identity.Hwnd);
        return current is not null && current.ProcessId == identity.ProcessId && current.ProcessStarted == identity.ProcessStarted && current.ClassName == identity.ClassName;
    }
    private static string? ReadProcessPath(nint process)
    {
        var path = new StringBuilder(32768);
        uint size = (uint)path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
    }

    public static bool CanLaunchApplication(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return false;
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != current.SessionId) continue;
                    nint handle = OpenProcess(0x1000, false, (uint)process.Id);
                    // 同名进程的路径无法读取时保守等待，避免重复启动。
                    if (handle == 0)
                    {
                        if (!process.HasExited) return false;
                        continue;
                    }
                    try
                    {
                        var runningPath = ReadProcessPath(handle);
                        if (runningPath is null || string.Equals(runningPath, path, StringComparison.OrdinalIgnoreCase))
                            return false;
                    }
                    finally { CloseHandle(handle); }
                }
                catch (InvalidOperationException) { } // The process exited during enumeration.
                catch (Win32Exception e) when (e.NativeErrorCode is 87 or 1168) { }
            }
            return true;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static Task<bool> LaunchApplicationAsync(string path)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(LaunchApplication(path)); }
            catch (Exception e) { result.SetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static bool LaunchApplication(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException(UiText.Get("ApplicationPathUnavailable"));
        if (!CanLaunchApplication(path)) return false;

        // 让桌面资源管理器按当前用户的正常权限启动，避免继承 WinWindow 的管理员令牌。
        var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic windows = shell.Windows();
        object location = 0, root = null!;
        const int desktopClass = 8, needDispatch = 1;
        dynamic? desktop = windows.FindWindowSW(ref location, ref root, desktopClass, out int _, needDispatch);
        if (desktop is null) throw new InvalidOperationException(UiText.Get("ApplicationShellUnavailable"));
        desktop.Document.Application.ShellExecute(path, "", Path.GetDirectoryName(path), "open", Normal);
        return true;
    }

    internal static bool MatchesProcess(WindowIdentity identity)
    {
        nint process = OpenProcess(0x1000, false, (uint)identity.ProcessId);
        if (process == 0) return false;
        // Windows retains the process object while an observer still holds a handle.
        const uint stillActive = 259;
        try { return MatchesProcess(identity, process) && GetExitCodeProcess(process, out uint code) && code == stillActive; }
        finally { CloseHandle(process); }
    }

    private static bool MatchesProcess(WindowIdentity identity, nint process) =>
        GetProcessTimes(process, out long created, out _, out _, out _) &&
        DateTime.FromFileTimeUtc(created).Ticks == identity.ProcessStarted;

    internal const uint AppActivationMessage = 0x001C;
    internal const string NoFocusLossProperty = "NoFocusLoss_Injected_8A11C8C7";
    private const uint ProcessInjectAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageReadWrite = 0x04;
    private const uint ListModules32Bit = 0x01, ListModules64Bit = 0x02, RemoteTimeoutMs = 5000;
    private const ushort MachineI386 = 0x014C, MachineAmd64 = 0x8664;
    private static readonly object NativeExtractSync = new();
    private static readonly ConcurrentDictionary<bool, (byte[] Bytes, string Hash)?> CachedNoFocusLoss = new();

    internal static bool IsNoFocusLossInjected(nint hwnd) =>
        hwnd != 0 && IsWindow(hwnd) && GetProp(hwnd, NoFocusLossProperty) != 0;

    internal static bool HasNoFocusLossSupport() =>
        LoadNoFocusLossPayload(true) is not null && LoadNoFocusLossPayload(false) is not null;

    internal static bool HasLoadedNoFocusLossModule(int processId)
    {
        nint process = OpenProcess(0x0400 | 0x0010, false, (uint)processId);
        if (process == 0) return false;
        try
        {
            if (!IsWow64Process2(process, out ushort procMachine, out ushort nativeMachine)) return false;
            ushort machine = procMachine == 0 ? nativeMachine : procMachine;
            if (machine != MachineAmd64 && machine != MachineI386) return false;
            return EnumerateRemoteModules(process, machine == MachineAmd64).Exists(m => IsNoFocusLossName(m.Path));
        }
        catch (Win32Exception) { return false; }
        finally { CloseHandle(process); }
    }

    internal static bool SetBackgroundActivity(
        WindowIdentity identity,
        bool enabled,
        bool enhancedInjection,
        bool lockCursorPosition,
        bool wasInjected,
        string storageDirectory,
        out bool targetMayHaveChanged)
    {
        targetMayHaveChanged = false;
        if (enabled)
        {
            EnsureIdentity(identity);
            if (!enhancedInjection)
            {
                targetMayHaveChanged = true;
                if (Directory.Exists(storageDirectory) && (wasInjected || IsNoFocusLossInjected(identity.Hwnd)))
                    DisableAndUnloadNoFocusLoss(identity, storageDirectory);
                PostAppActivation(identity, true, throwOnError: true);
                return false;
            }
            if (!HasNoFocusLossSupport() && !string.IsNullOrEmpty(typeof(App).Assembly.Location))
            {
                PostAppActivation(identity, true, throwOnError: true);
                return false;
            }
            nint process = OpenProcess(ProcessInjectAccess, false, (uint)identity.ProcessId);
            if (process == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("BackgroundRejected"));
            try
            {
                if (!MatchesProcess(identity, process)) throw new InvalidOperationException(UiText.Get("IdentityUnavailable"));
                bool is64Bit = ResolveTargetBitness(process);
                string dllPath = MaterializeNoFocusLossLibrary(is64Bit, storageDirectory)
                    ?? throw new FileNotFoundException(UiText.Get("BackgroundRejected"));
                targetMayHaveChanged = true;
                InjectAndInitializeNoFocusLoss(process, is64Bit, dllPath, identity.Hwnd, lockCursorPosition);
            }
            finally { CloseHandle(process); }
            PostAppActivation(identity, true, throwOnError: false);
            return true;
        }

        targetMayHaveChanged = true;
        if (Directory.Exists(storageDirectory) && (wasInjected || IsNoFocusLossInjected(identity.Hwnd)))
            DisableAndUnloadNoFocusLoss(identity, storageDirectory);
        PostAppActivation(identity, false, throwOnError: false);
        return false;
    }

    internal static Dictionary<string, bool> GetProcessMutes(WindowIdentity identity)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (MatchesProcess(identity))
        {
            VisitProcessAudioSessions(CollectProcessTreeIds((uint)identity.ProcessId), (id, volume) =>
            {
                if (volume.GetMute(out bool muted) == 0) result[id] = muted;
            });
        }
        return result;
    }

    internal static void SetProcessMutes(WindowIdentity identity, IReadOnlyDictionary<string, bool> sessions, bool? mute = null)
    {
        if (sessions.Count == 0 || (mute.HasValue && !MatchesProcess(identity))) return;
        // Undo is keyed by the exact WASAPI session instance, including a child
        // that is still playing after its launcher has exited.
        var targets = mute.HasValue ? CollectProcessTreeIds((uint)identity.ProcessId) : null;
        VisitProcessAudioSessions(targets, (id, volume) =>
        {
            if (sessions.TryGetValue(id, out bool original))
                Marshal.ThrowExceptionForHR(volume.SetMute(mute ?? original, 0));
        });
    }

    private static HashSet<uint> CollectProcessTreeIds(uint rootPid)
    {
        var result = new HashSet<uint> { rootPid };
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == 0 || snapshot == -1) return result;
        try
        {
            var children = new Dictionary<uint, List<uint>>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.ProcessId == 0 || entry.ProcessId == entry.ParentProcessId) continue;
                    if (!children.TryGetValue(entry.ParentProcessId, out var list))
                        children[entry.ParentProcessId] = list = new List<uint>();
                    list.Add(entry.ProcessId);
                } while (Process32Next(snapshot, ref entry));
            }
            var queue = new Queue<uint>();
            queue.Enqueue(rootPid);
            while (queue.Count != 0 && result.Count < 64)
            {
                uint current = queue.Dequeue();
                if (!children.TryGetValue(current, out var kids)) continue;
                foreach (uint child in kids)
                    if (result.Add(child)) queue.Enqueue(child);
            }
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private static void VisitProcessAudioSessions(HashSet<uint>? targetPids, Action<string, ISimpleAudioVolume> visitor)
    {
        object? enumeratorObj = null;
        IMMDeviceCollection? collection = null;
        try
        {
            var clsid = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            enumeratorObj = clsid is null ? null : Activator.CreateInstance(clsid);
            if (enumeratorObj is not IMMDeviceEnumerator devices) return;

            if (devices.EnumAudioEndpoints(0, 1, out collection) == 0 && collection is not null &&
                collection.GetCount(out uint deviceCount) == 0 && deviceCount > 0)
            {
                for (uint d = 0; d < Math.Min(deviceCount, 16u); d++)
                {
                    if (collection.Item(d, out var endpoint) != 0 || endpoint is null) continue;
                    try { VisitEndpointSessions(endpoint, targetPids, visitor); }
                    finally { ReleaseCom(endpoint); }
                }
                return;
            }

            if (devices.GetDefaultAudioEndpoint(0, 1, out var fallback) == 0 && fallback is not null)
            {
                try { VisitEndpointSessions(fallback, targetPids, visitor); }
                finally { ReleaseCom(fallback); }
            }
        }
        finally
        {
            ReleaseCom(collection);
            ReleaseCom(enumeratorObj);
        }
    }

    private static void VisitEndpointSessions(IMMDevice endpoint, HashSet<uint>? targetPids, Action<string, ISimpleAudioVolume> visitor)
    {
        object? managerObj = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (endpoint.Activate(in iid, 23, 0, out managerObj) != 0 ||
                managerObj is not IAudioSessionManager2 audio ||
                audio.GetSessionEnumerator(out sessions) != 0 ||
                sessions is null ||
                sessions.GetCount(out int count) != 0)
                return;

            for (int i = 0; i < Math.Min(count, 128); i++)
            {
                if (sessions.GetSession(i, out var session) != 0 || session is null) continue;
                try
                {
                    if (session is IAudioSessionControl2 control &&
                        session is ISimpleAudioVolume volume &&
                        control.IsSystemSoundsSession() != 0 &&
                        control.GetProcessId(out uint pid) == 0 &&
                        pid != 0 &&
                        (targetPids is null || targetPids.Contains(pid)) &&
                        control.GetSessionInstanceIdentifier(out nint sessionId) == 0 &&
                        sessionId != 0)
                    {
                        try
                        {
                            if (Marshal.PtrToStringUni(sessionId) is { Length: > 0 } id)
                                visitor(id, volume);
                        }
                        finally { Marshal.FreeCoTaskMem(sessionId); }
                    }
                }
                finally { ReleaseCom(session); }
            }
        }
        finally
        {
            ReleaseCom(sessions);
            ReleaseCom(managerObj);
        }
    }

    private static void ReleaseCom(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
            Marshal.ReleaseComObject(instance);
    }

    internal static void PostAppActivation(WindowIdentity identity, bool enabled, bool throwOnError)
    {
        if (!Matches(identity))
        {
            if (throwOnError) EnsureIdentity(identity);
            return;
        }
        var foreground = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(foreground, out uint pid);
        // Windows already supplies the real activation for the foreground process.
        // WM_ACTIVATEAPP has no default focus-transfer behavior.
        if (pid == identity.ProcessId) return;
        if (!PostMessage(identity.Hwnd, AppActivationMessage, enabled ? 1 : 0, (nint)thread) && throwOnError)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("BackgroundRejected"));
    }

    private static bool ResolveTargetBitness(nint process)
    {
        if (!IsWow64Process2(process, out ushort procMachine, out ushort nativeMachine))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("BackgroundRejected"));
        ushort machine = procMachine == 0 ? nativeMachine : procMachine;
        if (machine == MachineAmd64)
        {
            if (IntPtr.Size != 8) throw new InvalidOperationException(UiText.Get("BackgroundUnsupported"));
            return true;
        }
        if (machine == MachineI386) return false;
        throw new InvalidOperationException(UiText.Get("BackgroundUnsupported"));
    }

    private static (byte[] Bytes, string Hash)? LoadNoFocusLossPayload(bool is64Bit) =>
        CachedNoFocusLoss.GetOrAdd(is64Bit, static bit64 =>
        {
            string resource = bit64 ? "WinWindow.Native.NoFocusLoss.x64.dll" : "WinWindow.Native.NoFocusLoss.x86.dll";
            using var stream = typeof(App).Assembly.GetManifestResourceStream(resource);
            if (stream is not null)
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                byte[] data = memory.ToArray();
                return (data, Convert.ToHexString(SHA256.HashData(data), 0, 4).ToLowerInvariant());
            }
            return null;
        });

    private static string? MaterializeNoFocusLossLibrary(bool is64Bit, string storageDirectory)
    {
        var payload = LoadNoFocusLossPayload(is64Bit);
        if (payload is null) return null;
        string prefix = is64Bit ? "NoFocusLoss64-" : "NoFocusLoss-";
        string fileName = $"{prefix}{payload.Value.Hash}.dll";
        lock (NativeExtractSync)
        {
            Directory.CreateDirectory(storageDirectory);
            string path = Path.Combine(storageDirectory, fileName);
            if (!MatchesPayload(path, payload.Value.Bytes))
            {
                string temp = Path.Combine(storageDirectory, $"{fileName}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temp, payload.Value.Bytes);
                try { File.Move(temp, path, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    try { File.Delete(temp); } catch (IOException) { }
                    if (!MatchesPayload(path, payload.Value.Bytes)) throw;
                }
            }
            return path;
        }
    }

    private static bool MatchesPayload(string path, byte[] payload)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != payload.Length) return false;
            byte[] current = new byte[payload.Length];
            stream.ReadExactly(current);
            return current.AsSpan().SequenceEqual(payload);
        }
        catch (IOException) { return false; }
    }

    private static void InjectAndInitializeNoFocusLoss(nint process, bool is64Bit, string dllPath, nint hwnd, bool lockCursorPosition)
    {
        var modules = EnumerateRemoteModules(process, is64Bit);
        var loaded = modules.Find(m => string.Equals(m.Path, dllPath, StringComparison.OrdinalIgnoreCase));
        if (loaded.Base == 0)
        {
            if (modules.Exists(m => IsNoFocusLossName(m.Path)))
                throw new InvalidOperationException(UiText.Get("BackgroundRejected"));
            nint loadLibrary = ResolveRemoteLoadLibraryW(modules);
            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');
            nint remotePath = VirtualAllocEx(process, 0, (nuint)pathBytes.Length, MemCommitReserve, PageReadWrite);
            if (remotePath == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("BackgroundRejected"));
            bool canFree = true;
            try
            {
                if (!WriteProcessMemory(process, remotePath, pathBytes, (nuint)pathBytes.Length, out nuint written) ||
                    written != (nuint)pathBytes.Length)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("BackgroundRejected"));
                if (!RunRemoteThread(process, loadLibrary, remotePath, RemoteTimeoutMs, out _, out bool threadMayStillRun))
                {
                    canFree = !threadMayStillRun;
                    throw new InvalidOperationException(UiText.Get("BackgroundRejected"));
                }
            }
            finally
            {
                if (canFree) VirtualFreeEx(process, remotePath, 0, MemRelease);
            }
            modules = EnumerateRemoteModules(process, is64Bit);
            loaded = modules.Find(m => string.Equals(m.Path, dllPath, StringComparison.OrdinalIgnoreCase));
            if (loaded.Base == 0)
                throw new InvalidOperationException(UiText.Get("BackgroundRejected"));
        }

        nint initialize = ResolveRemoteExport(modules, loaded.Base, loaded.Path,
            ["NoFocusLoss_Initialize", "_NoFocusLoss_Initialize@4"]);
        nint targetParam = is64Bit ? hwnd : unchecked((nint)(uint)hwnd.ToInt64());
        if (!RunRemoteThread(process, initialize, targetParam, RemoteTimeoutMs, out uint initResult, out _))
            throw new InvalidOperationException(UiText.Get("BackgroundRejected"));

        if (initResult != 1)
        {
            if (initResult == 0) TryUnloadLoadedModule(process, modules, loaded);
            throw new InvalidOperationException(UiText.Get("BackgroundRejected"));
        }

        nint setCursorLock = ResolveRemoteExport(modules, loaded.Base, loaded.Path,
            ["NoFocusLoss_SetCursorPositionLock", "_NoFocusLoss_SetCursorPositionLock@4"]);
        if (!RunRemoteThread(process, setCursorLock, lockCursorPosition ? 1 : 0, RemoteTimeoutMs, out uint lockResult, out _) ||
            lockResult != 1)
            throw new InvalidOperationException(UiText.Get("BackgroundRejected"));
    }

    private static void DisableAndUnloadNoFocusLoss(WindowIdentity identity, string storageDirectory)
    {
        nint process = OpenProcess(ProcessInjectAccess, false, (uint)identity.ProcessId);
        if (process == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (MatchesProcess(identity) || Matches(identity))
                throw new Win32Exception(error, UiText.Get("BackgroundUnloadFailed"));
            return;
        }
        try
        {
            if (!MatchesProcess(identity, process)) return;
            if (!IsWow64Process2(process, out ushort procMachine, out ushort nativeMachine))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Get("BackgroundUnloadFailed"));
            ushort machine = procMachine == 0 ? nativeMachine : procMachine;
            if (machine != MachineAmd64 && machine != MachineI386) return;
            bool is64Bit = machine == MachineAmd64;
            if (is64Bit && IntPtr.Size != 8) return;

            var modules = EnumerateRemoteModules(process, is64Bit);
            var loaded = modules.Find(m => IsNoFocusLossName(m.Path) &&
                string.Equals(Path.GetDirectoryName(m.Path), storageDirectory, StringComparison.OrdinalIgnoreCase));
            if (loaded.Base == 0) return;

            if (!TryUnloadLoadedModule(process, modules, loaded))
                throw new InvalidOperationException(UiText.Get("BackgroundUnloadFailed"));
        }
        finally { CloseHandle(process); }
    }

    private static bool TryUnloadLoadedModule(nint process, List<(nint Base, string Path)> modules, (nint Base, string Path) loaded)
    {
        nint shutdown = ResolveRemoteExport(modules, loaded.Base, loaded.Path,
            ["NoFocusLoss_ShutdownAndUnload", "_NoFocusLoss_ShutdownAndUnload@4"]);
        return RunRemoteThread(process, shutdown, 0, RemoteTimeoutMs, out uint exitCode, out _) && exitCode == 1;
    }

    private static bool RunRemoteThread(nint process, nint startAddress, nint parameter, uint timeoutMs, out uint exitCode, out bool threadMayStillRun)
    {
        exitCode = 0;
        threadMayStillRun = false;
        nint thread = CreateRemoteThread(process, 0, 0, startAddress, parameter, 0, out _);
        if (thread == 0) return false;
        try
        {
            uint wait = WaitForSingleObject(thread, timeoutMs);
            if (wait != 0)
            {
                threadMayStillRun = true;
                return false;
            }
            return GetExitCodeThread(thread, out exitCode);
        }
        finally { CloseHandle(thread); }
    }

    private static List<(nint Base, string Path)> EnumerateRemoteModules(nint process, bool is64Bit)
    {
        uint filter = is64Bit ? ListModules64Bit : ListModules32Bit;
        nint[] buffer = new nint[256];
        uint needed = 0;
        bool ok = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint cb = (uint)(buffer.Length * IntPtr.Size);
            if (EnumProcessModulesEx(process, buffer, cb, out needed, filter))
            {
                if (needed <= cb) { ok = true; break; }
                buffer = new nint[(int)(needed / (uint)IntPtr.Size) + 16];
                continue;
            }
            if (Marshal.GetLastPInvokeError() != 299) break;
            Thread.Sleep(15);
        }
        if (!ok) Check(false, UiText.Get("BackgroundActivity"));
        int count = Math.Min(buffer.Length, (int)(needed / (uint)IntPtr.Size));
        var modules = new List<(nint Base, string Path)>(count);
        var sb = new StringBuilder(32768);
        for (int i = 0; i < count; i++)
        {
            if (buffer[i] == 0) continue;
            sb.Clear();
            if (GetModuleFileNameEx(process, buffer[i], sb, sb.Capacity) != 0)
                modules.Add((buffer[i], sb.ToString()));
        }
        return modules;
    }

    private static bool IsNoFocusLossName(string path) =>
        Path.GetFileName(path).StartsWith("NoFocusLoss", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static nint ResolveRemoteLoadLibraryW(List<(nint Base, string Path)> modules)
    {
        var remoteKernel32 = modules.Find(m => string.Equals(Path.GetFileName(m.Path), "kernel32.dll", StringComparison.OrdinalIgnoreCase));
        if (remoteKernel32.Base == 0) throw new InvalidOperationException(UiText.Get("BackgroundRejected"));
        return ResolveRemoteExport(modules, remoteKernel32.Base, remoteKernel32.Path, ["LoadLibraryW"]);
    }

    private static string ReadAsciiString(BlobReader reader)
    {
        var sb = new StringBuilder(64);
        while (reader.RemainingBytes > 0)
        {
            byte b = reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static nint ResolveRemoteExport(List<(nint Base, string Path)> modules, nint moduleBase, string modulePath, string[] names, int depth = 0)
    {
        if (depth > 4) throw new InvalidDataException("Export forwarder chain too deep.");
        using var stream = new FileStream(modulePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream);
        var dir = pe.PEHeaders.PEHeader?.ExportTableDirectory ?? default;
        if (dir.RelativeVirtualAddress == 0 || dir.Size < 40)
            throw new InvalidDataException($"Missing export directory in {Path.GetFileName(modulePath)}.");

        var dirBlock = pe.GetSectionData(dir.RelativeVirtualAddress);
        if (dirBlock.Length < 40)
            throw new InvalidDataException($"Truncated export directory in {Path.GetFileName(modulePath)}.");
        var header = dirBlock.GetReader();
        header.Offset = 20;
        int functionCount = header.ReadInt32();
        int nameCount = header.ReadInt32();
        int functionsRva = header.ReadInt32();
        int namesRva = header.ReadInt32();
        int ordinalsRva = header.ReadInt32();

        var functionsBlock = pe.GetSectionData(functionsRva);
        var namesBlock = pe.GetSectionData(namesRva);
        var ordinalsBlock = pe.GetSectionData(ordinalsRva);

        for (int i = 0; i < nameCount; i++)
        {
            var namePtrReader = namesBlock.GetReader();
            namePtrReader.Offset = i * 4;
            int nameRva = namePtrReader.ReadInt32();
            string current = ReadAsciiString(pe.GetSectionData(nameRva).GetReader());
            if (!Array.Exists(names, wanted => string.Equals(current, wanted, StringComparison.Ordinal))) continue;

            var ordReader = ordinalsBlock.GetReader();
            ordReader.Offset = i * 2;
            ushort ordinalIndex = ordReader.ReadUInt16();
            if (ordinalIndex >= functionCount) break;

            var funcReader = functionsBlock.GetReader();
            funcReader.Offset = ordinalIndex * 4;
            uint rva = funcReader.ReadUInt32();

            if (rva >= (uint)dir.RelativeVirtualAddress && rva < (uint)(dir.RelativeVirtualAddress + dir.Size))
            {
                string forward = ReadAsciiString(pe.GetSectionData((int)rva).GetReader());
                int dot = forward.LastIndexOf('.');
                if (dot <= 0 || dot >= forward.Length - 1)
                    throw new InvalidDataException($"Invalid forwarded export {forward}.");
                string fwdDll = forward[..dot] + ".dll";
                string fwdFunc = forward[(dot + 1)..];
                var target = modules.Find(m => string.Equals(Path.GetFileName(m.Path), fwdDll, StringComparison.OrdinalIgnoreCase));
                if (target.Base == 0)
                    throw new DllNotFoundException($"Forwarded module {fwdDll} is not loaded in target process.");
                return ResolveRemoteExport(modules, target.Base, target.Path, [fwdFunc], depth + 1);
            }
            return unchecked((nint)((nuint)moduleBase + rva));
        }
        throw new EntryPointNotFoundException($"Export {names[0]} not found in {Path.GetFileName(modulePath)}.");
    }

    public static List<WindowItem> Enumerate()
    {
        var result = new List<WindowItem>();
        EnumWindows((hwnd, data) =>
        {
            if (!IsTarget(hwnd) || !IsWindowVisible(hwnd)) return true;
            long style = Style(hwnd);
            if ((style & ToolWindow) != 0 && (style & 0x40000) == 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            string title = Title(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            var identity = Identity(hwnd);
            if (identity is not null)
            {
                int opacity = (style & Layered) != 0 && GetLayeredWindowAttributes(hwnd, out _, out byte a, out uint f) && (f & 2) != 0 ? ToPercent(a) : 100;
                result.Add(new() { Identity = identity, Title = title, Pinned = (style & TopmostStyle) != 0, ClickThrough = (style & Transparent) != 0, Opacity = opacity, IsMinimized = IsIconic(hwnd) });
            }
            return true;
        }, 0);
        return result;
    }
    internal static DesktopSnapshot.Window[] CaptureDesktopWindows(IReadOnlySet<nint> managed,
        nint workbench, nint foreground, Dictionary<uint, string> apps)
    {
        var result = new List<DesktopSnapshot.Window>();
        if (!EnumWindows((hwnd, data) =>
        {
            bool isManaged = managed.Contains(hwnd);
            if (hwnd != workbench && !IsTarget(hwnd)) return true;
            var title = Title(hwnd).Replace('\r', ' ').Replace('\n', ' ');
            var visible = IsWindowVisible(hwnd);
            var style = Style(hwnd);
            if (!isManaged && hwnd != workbench &&
                (!visible && string.IsNullOrWhiteSpace(title) ||
                 (style & (ToolWindow | NoActivateStyle)) != 0 && string.IsNullOrWhiteSpace(title)))
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;
            if (!apps.TryGetValue(pid, out var app))
                apps[pid] = app = Path.GetFileName(Identity(hwnd)?.ProcessPath) ?? "";
            var cloaked = DwmGetWindowAttribute(hwnd, 14, out int cloak, 4) == 0 && cloak != 0;
            bool minimized = IsIconic(hwnd);
            var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
            DesktopSnapshot.Bounds? normal = GetWindowPlacement(hwnd, ref placement)
                ? DesktopSnapshot.Bounds.From(placement.NormalPosition) : null;
            DesktopSnapshot.Bounds? frame = (TryGetFrameBounds(hwnd, out var rect) || GetWindowRect(hwnd, out rect))
                ? DesktopSnapshot.Bounds.From(rect) : null;
            uint? dpi = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) ? GetDpiForWindow(hwnd) : null;
            int opacity = (style & Layered) != 0 && GetLayeredWindowAttributes(hwnd, out _, out byte a, out uint f) &&
                (f & 2) != 0 ? ToPercent(a) : 100;
            result.Add(new(DesktopSnapshot.Handle(hwnd), pid, app, ClassName(hwnd), title, result.Count + 1,
                frame, normal, dpi, visible, minimized, IsZoomed(hwnd), cloaked, (style & TopmostStyle) != 0,
                (style & Transparent) != 0, opacity, hwnd == foreground, isManaged));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result.ToArray();
    }

    public static WindowSnapshot Capture(WindowIdentity id)
    {
        EnsureIdentity(id);
        long style = Style(id.Hwnd);
        bool readable = GetLayeredWindowAttributes(id.Hwnd, out uint color, out byte alpha, out uint flags);
        return new() { Identity = id, Title = Title(id.Hwnd), ExtendedStyle = style, Topmost = (style & TopmostStyle) != 0, LayeredReadable = readable, ColorKey = color, Alpha = readable ? alpha : (byte)255, LayeredFlags = flags };
    }
    public static void EnsureIdentity(WindowIdentity id) { if (!Matches(id)) throw new InvalidOperationException(UiText.Get("TargetReplaced")); }
    private static void Check(bool ok, string action) { if (!ok) throw new Win32Exception(Marshal.GetLastPInvokeError(), UiText.Format("NativeFailed", action)); }
    public static void SetStyleBits(nint hwnd, long mask, long bits)
    {
        long current = Style(hwnd), value = (current & ~mask) | (bits & mask);
        if (current == value) return;
        Marshal.SetLastPInvokeError(0);
        nint previous = IntPtr.Size == 8 ? SetWindowLong64(hwnd, -20, (nint)value) : (nint)SetWindowLong32(hwnd, -20, (int)value);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) Check(false, UiText.Get("ChangeStyle"));
        if ((Style(hwnd) & mask) != (value & mask)) throw new InvalidOperationException(UiText.Get("StyleRejected"));
    }
    public static void SetTopmost(nint hwnd, bool value)
    {
        Check(SetWindowPos(hwnd, value ? (nint)(-1) : (nint)(-2), 0, 0, 0, 0, NoMove | NoSize | NoActivate), UiText.Get("TogglePin"));
        if (IsTopmost(hwnd) != value) throw new InvalidOperationException(UiText.Get("PinRejected"));
    }
    internal static bool CoversForegroundAfterUnpin(WindowIdentity oldWindow, nint foreground)
    {
        nint old = oldWindow.Hwnd;
        return foreground != 0 && foreground != old && !IsShell(foreground) &&
            Matches(oldWindow) && !IsTopmost(old) && !IsTopmost(foreground) &&
            !IsFullyTransparent(old) &&
            GetAncestor(foreground, 2) == foreground && GetAncestor(foreground, 3) != GetAncestor(old, 3) &&
            TryGetFrameBounds(old, out var oldBounds) && TryGetFrameBounds(foreground, out var foregroundBounds) &&
            Overlaps(oldBounds, foregroundBounds) && IsAbove(old, foreground);
    }

    private static bool IsFullyTransparent(nint hwnd) => (Style(hwnd) & Layered) != 0 &&
        GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) && (flags & 2) != 0 && alpha == 0;

    internal static bool RevealClickedWindowAfterUnpin(WindowIdentity oldWindow, WindowIdentity clickedWindow)
    {
        nint clicked = clickedWindow.Hwnd;
        if (GetForegroundWindow() != clicked || !Matches(clickedWindow) || !CoversForegroundAfterUnpin(oldWindow, clicked)) return false;
        nint after = GetWindow(oldWindow.Hwnd, 3);
        if (after == clicked) return false;
        if (after != 0 && IsTopmost(after)) after = 0;
        // 只让已获得焦点的点击目标越过遮挡它的旧窗。保留上方其他普通窗口的顺序。
        return GetForegroundWindow() == clicked &&
            SetWindowPos(clicked, after, 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerZOrder | NoSendChanging);
    }
    public static bool SetOwnTopmost(nint hwnd, bool value)
    {
        if (IsTopmost(hwnd) == value) return true;
        nint foreground = GetForegroundWindow();
        nint after = value ? (nint)(-1) : (nint)(-2);
        if (!value && foreground != hwnd && IsWindowVisible(foreground) && !IsIconic(foreground) && !IsTopmost(foreground) && !IsShell(foreground)) after = foreground;
        // 本体与浮层的原生置顶只改变各自的层级。这里没有几何变化需要 WPF 参与。
        return SetWindowPos(hwnd, after, 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerZOrder | NoSendChanging)
            && IsTopmost(hwnd) == value;
    }
    public static bool Raise(nint hwnd) => SetWindowPos(hwnd, (nint)(-1), 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    public static void SetOpacity(WindowSnapshot original, int percent)
    {
        nint hwnd = original.Identity.Hwnd;
        if ((original.ExtendedStyle & Layered) != 0 && !original.LayeredReadable) throw new InvalidOperationException(UiText.Get("PerPixelOpacity"));
        bool added = (Style(hwnd) & Layered) == 0;
        try
        {
            SetStyleBits(hwnd, Layered, Layered);
            uint color = original.LayeredReadable ? original.ColorKey : 0;
            uint flags = 2 | (original.LayeredReadable ? original.LayeredFlags & 1 : 0);
            byte wanted = ToAlpha(percent);
            Check(SetLayeredWindowAttributes(hwnd, color, wanted, flags), UiText.Get("ChangeOpacity"));
            if (!GetLayeredWindowAttributes(hwnd, out _, out byte actual, out uint applied) || (applied & 2) == 0 || actual != wanted) throw new InvalidOperationException(UiText.Get("OpacityRejected"));
        }
        catch
        {
            if (added) { try { SetStyleBits(hwnd, Layered, 0); } catch (Win32Exception) { } catch (InvalidOperationException) { } }
            throw;
        }
    }
    public static void SetClickThrough(WindowSnapshot original, bool value, int opacity)
    {
        nint hwnd = original.Identity.Hwnd;
        if (value && (Style(hwnd) & Layered) == 0) SetOpacity(original, opacity);
        SetStyleBits(hwnd, Transparent, value ? Transparent : 0);
    }
    public static Placement GetPlacement(nint hwnd)
    {
        var p = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        Check(GetWindowPlacement(hwnd, ref p), UiText.Get("ReadPlacement")); return p;
    }
    public static bool ApplyPlacement(nint hwnd, Placement placement)
    {
        placement.Length = (uint)Marshal.SizeOf<Placement>(); placement.Flags |= 4;
        return SetWindowPlacement(hwnd, ref placement);
    }
    public static void RestoreStyles(WindowSnapshot snapshot, bool unpin = false)
    {
        EnsureIdentity(snapshot.Identity);
        nint hwnd = snapshot.Identity.Hwnd;
        var errors = new List<string>();
        void Attempt(Action action) { try { action(); } catch (Exception e) when (e is Win32Exception or InvalidOperationException) { errors.Add(e.Message); } }
        bool topmost = !unpin && snapshot.Topmost;
        if ((snapshot.EditedTopmost || unpin) && IsTopmost(hwnd) != topmost) Attempt(() => SetTopmost(hwnd, topmost));
        if (snapshot.EditedOpacity || snapshot.EditedClickThrough)
        {
            long mask = Layered | (snapshot.EditedClickThrough ? Transparent : 0);
            Attempt(() => SetStyleBits(hwnd, mask, snapshot.ExtendedStyle));
            if (snapshot.EditedOpacity && (snapshot.ExtendedStyle & Layered) != 0 && snapshot.LayeredReadable)
                Attempt(() =>
                {
                    bool unchanged = GetLayeredWindowAttributes(hwnd, out uint color, out byte alpha, out uint flags) && color == snapshot.ColorKey && alpha == snapshot.Alpha && flags == snapshot.LayeredFlags;
                    if (!unchanged) Check(SetLayeredWindowAttributes(hwnd, snapshot.ColorKey, snapshot.Alpha, snapshot.LayeredFlags), UiText.Get("RestoreOpacity"));
                });
        }
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors));
    }
    public static nint TargetUnderPointer()
    {
        GetCursorPos(out Point p); nint hwnd = GetAncestor(WindowFromPoint(p), 2); return IsTarget(hwnd) ? hwnd : 0;
    }
    internal static bool SendClickForTest(Point point, nint expected)
    {
        if (!SetCursorPos(point.X, point.Y) || GetAncestor(WindowFromPoint(point), 2) != expected) return false;
        var down = new MouseInputEvent { Mouse = new MouseInput { Flags = 0x0002 } };
        var up = new MouseInputEvent { Mouse = new MouseInput { Flags = 0x0004 } };
        int size = Marshal.SizeOf<MouseInputEvent>();
        uint sent = SendInput(2, [down, up], size);
        if (sent == 1) SendInput(1, [up], size);
        return sent == 2;
    }
    public static bool OverlappingTopmostAbove(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out Rect own)) return false;
        for (nint next = GetWindow(hwnd, 3); next != 0; next = GetWindow(next, 3))
        {
            GetWindowThreadProcessId(next, out uint pid);
            if (pid == Environment.ProcessId || !IsWindowVisible(next) || IsIconic(next) || !IsTopmost(next) || IsShell(next)) continue;
            if (DwmGetWindowAttribute(next, 14, out int cloaked, 4) == 0 && cloaked != 0) continue;
            if (GetWindowRect(next, out Rect r) && Overlaps(own, r)) return true;
        }
        return false;
    }
    internal static bool IsAbove(nint front, nint back)
    {
        bool found = false;
        EnumWindows((hwnd, data) => { if (hwnd == front) { found = true; return false; } return hwnd != back; }, 0);
        return found;
    }
    public static void FitToWorkArea(nint hwnd)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) || !GetWindowRect(hwnd, out Rect r)) return;
        int x = Math.Clamp(r.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - 120));
        int y = Math.Clamp(r.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - 80));
        SetWindowPos(hwnd, 0, x, y, 0, 0, NoSize | NoActivate | 4);
    }
    public static void ConstrainMaximize(nint hwnd, nint data, int minWidth = 0, int minHeight = 0)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
        var m = Marshal.PtrToStructure<MinMaxInfo>(data);
        m.MaxPosition = new() { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        m.MaxSize = new() { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        if (minWidth > 0) m.MinTrack.X = minWidth;
        if (minHeight > 0) m.MinTrack.Y = minHeight;
        Marshal.StructureToPtr(m, data, false);
    }
}
