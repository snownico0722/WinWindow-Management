using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinWindow;

internal static class Native
{
    internal const long Layered = 0x80000, Transparent = 0x20, TopmostStyle = 8;
    internal const uint NoSize = 1, NoMove = 2, NoActivate = 0x10, AsyncPosition = 0x4000;
    internal const int Hide = 0, Normal = 1, Minimized = 2, Maximized = 3, ShowNoActivate = 4, Restore = 9, ForceMinimize = 11;
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Placement
    {
        public uint Length; public uint Flags; public uint ShowCmd;
        public Point MinPosition; public Point MaxPosition; public Rect NormalPosition;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo { public Point Reserved; public Point MaxSize; public Point MaxPosition; public Point MinTrack; public Point MaxTrack; }
    internal delegate bool EnumProc(nint hwnd, nint data);
    internal delegate void EventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
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
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(nint hwnd, uint message, nint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint filter);
    [DllImport("user32.dll")] internal static extern uint InSendMessageEx(nint reserved);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint first, uint last, nint module, EventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    public static string Title(nint hwnd) { var b = new StringBuilder(2048); GetWindowText(hwnd, b, b.Capacity); return b.ToString(); }
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
                try { var b = new StringBuilder(32768); uint size = (uint)b.Capacity; if (QueryFullProcessImageName(handle, 0, b, ref size)) path = b.ToString(); }
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
    public static List<WindowItem> Enumerate()
    {
        var result = new List<WindowItem>();
        EnumWindows((hwnd, data) =>
        {
            if (!IsTarget(hwnd) || !IsWindowVisible(hwnd)) return true;
            long style = Style(hwnd);
            if ((style & 0x80) != 0 && (style & 0x40000) == 0) return true;
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
    public static WindowSnapshot Capture(WindowIdentity id)
    {
        EnsureIdentity(id);
        long style = Style(id.Hwnd);
        bool readable = GetLayeredWindowAttributes(id.Hwnd, out uint color, out byte alpha, out uint flags);
        return new() { Identity = id, Title = Title(id.Hwnd), ExtendedStyle = style, Topmost = (style & TopmostStyle) != 0, LayeredReadable = readable, ColorKey = color, Alpha = readable ? alpha : (byte)255, LayeredFlags = flags };
    }
    public static void EnsureIdentity(WindowIdentity id) { if (!Matches(id)) throw new InvalidOperationException("目标窗口已关闭或已被替换。"); }
    private static void Check(bool ok, string action) { if (!ok) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{action}失败，请检查目标权限或兼容性。"); }
    public static void SetStyleBits(nint hwnd, long mask, long bits)
    {
        long current = Style(hwnd), value = (current & ~mask) | (bits & mask);
        if (current == value) return;
        Marshal.SetLastPInvokeError(0);
        nint previous = IntPtr.Size == 8 ? SetWindowLong64(hwnd, -20, (nint)value) : (nint)SetWindowLong32(hwnd, -20, (int)value);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) Check(false, "修改窗口样式");
        if ((Style(hwnd) & mask) != (value & mask)) throw new InvalidOperationException("目标未接受窗口样式修改。");
    }
    public static void SetTopmost(nint hwnd, bool value)
    {
        Check(SetWindowPos(hwnd, value ? (nint)(-1) : (nint)(-2), 0, 0, 0, 0, NoMove | NoSize | NoActivate), "切换置顶");
        if (IsTopmost(hwnd) != value) throw new InvalidOperationException("窗口未接受置顶设置。");
    }
    public static bool SetOwnTopmost(nint hwnd, bool value)
    {
        if (IsTopmost(hwnd) == value) return true;
        nint foreground = GetForegroundWindow();
        nint after = value ? (nint)(-1) : (nint)(-2);
        if (!value && foreground != hwnd && IsWindowVisible(foreground) && !IsIconic(foreground) && !IsTopmost(foreground) && !IsShell(foreground)) after = foreground;
        return SetWindowPos(hwnd, after, 0, 0, 0, 0, NoMove | NoSize | NoActivate) && IsTopmost(hwnd) == value;
    }
    public static bool Raise(nint hwnd) => SetWindowPos(hwnd, (nint)(-1), 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    public static void SetOpacity(WindowSnapshot original, int percent)
    {
        nint hwnd = original.Identity.Hwnd;
        if ((original.ExtendedStyle & Layered) != 0 && !original.LayeredReadable) throw new InvalidOperationException("该窗口使用逐像素透明，不支持修改不透明度。");
        bool added = (Style(hwnd) & Layered) == 0;
        try
        {
            SetStyleBits(hwnd, Layered, Layered);
            uint color = original.LayeredReadable ? original.ColorKey : 0;
            uint flags = 2 | (original.LayeredReadable ? original.LayeredFlags & 1 : 0);
            byte wanted = ToAlpha(percent);
            Check(SetLayeredWindowAttributes(hwnd, color, wanted, flags), "调整不透明度");
            if (!GetLayeredWindowAttributes(hwnd, out _, out byte actual, out uint applied) || (applied & 2) == 0 || actual != wanted) throw new InvalidOperationException("窗口未接受不透明度设置。");
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
        Check(GetWindowPlacement(hwnd, ref p), "读取窗口位置"); return p;
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
                    if (!unchanged) Check(SetLayeredWindowAttributes(hwnd, snapshot.ColorKey, snapshot.Alpha, snapshot.LayeredFlags), "恢复透明状态");
                });
        }
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(" ", errors));
    }
    public static nint TargetUnderPointer()
    {
        GetCursorPos(out Point p); nint hwnd = GetAncestor(WindowFromPoint(p), 2); return IsTarget(hwnd) ? hwnd : 0;
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
