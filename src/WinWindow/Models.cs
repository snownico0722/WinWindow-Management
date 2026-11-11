using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WinWindow;

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
internal sealed record WindowIdentity(long Handle, int ProcessId, long ProcessStarted, string ProcessPath, string ClassName)
{ [JsonIgnore] public nint Hwnd => (nint)Handle; }
internal sealed class WindowSnapshot
{
    public WindowIdentity Identity { get; set; } = new(0, 0, 0, "", "");
    public string Title { get; set; } = "";
    public long ExtendedStyle { get; set; }
    public bool Topmost { get; set; }
    public bool LayeredReadable { get; set; }
    public uint ColorKey { get; set; }
    public byte Alpha { get; set; } = 255;
    public uint LayeredFlags { get; set; }
    public bool EditedTopmost { get; set; }
    public bool EditedOpacity { get; set; }
    public bool EditedClickThrough { get; set; }
    public bool Parked { get; set; }
    public Native.Placement BeforeParking { get; set; }
}
internal sealed class WindowRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProcessPath { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Pinned { get; set; }
    public int Opacity { get; set; } = 100;
    public bool ClickThrough { get; set; }
    public bool Matches(WindowItem item) => item.Identity is { } id &&
        string.Equals(ProcessPath, id.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ClassName, id.ClassName, StringComparison.Ordinal) &&
        string.Equals(Title, item.Title, StringComparison.Ordinal);
}
internal sealed class WindowItem : Observable
{
    public WindowIdentity? Identity { get; init; }
    public WindowSnapshot? Original { get; set; }
    public string? RuleId { get; set; }
    public nint Hwnd => Identity?.Hwnd ?? 0;
    public string ProcessName => Identity is null ? "等待窗口出现" : System.IO.Path.GetFileName(Identity.ProcessPath);
    public string ProcessPath => Identity?.ProcessPath ?? "";
    public string Details => Identity is null ? "保存的窗口规则" : $"PID {Identity.ProcessId}  ·  HWND 0x{Identity.Handle:X}  ·  {Identity.ClassName}";
    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }
    private bool _available = true;
    public bool IsAvailable { get => _available; set { if (Set(ref _available, value)) NotifyState(); } }
    // Membership is independent from TOPMOST and the recovery snapshot.
    public bool Listed { get; set; }
    public bool IsManaged => Listed || Original is not null;
    public bool Remembered => RuleId is not null;
    private bool _pinned;
    public bool Pinned { get => _pinned; set { if (Set(ref _pinned, value)) NotifyState(); } }
    private int _opacity = 100;
    public int Opacity { get => _opacity; set { if (Set(ref _opacity, value)) NotifyState(); } }
    private bool _clickThrough;
    public bool ClickThrough { get => _clickThrough; set { if (Set(ref _clickThrough, value)) NotifyState(); } }
    private bool _minimized;
    public bool IsMinimized { get => _minimized; set { if (Set(ref _minimized, value)) NotifyState(); } }
    private bool _hidden;
    public bool IsHidden { get => _hidden; set { if (Set(ref _hidden, value)) NotifyState(); } }
    private bool _busy;
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) { Changed(nameof(CanOperate)); NotifyState(); } } }
    public bool CanOperate => IsAvailable && !IsBusy;
    public bool IsParked => Original?.Parked == true;
    public string StateText => !IsAvailable ? "等待窗口" : IsBusy ? "正在处理" : IsHidden ? "已隐藏 · 可恢复" : IsMinimized ? "已最小化" : Opacity == 0 ? "完全透明 · 可恢复" : Pinned ? "已置顶" : IsManaged ? "管理中" : "正常";
    public void NotifyState()
    {
        Changed(nameof(StateText)); Changed(nameof(IsManaged)); Changed(nameof(IsParked));
        Changed(nameof(Remembered)); Changed(nameof(CanOperate));
    }
}
internal sealed class Preferences
{
    public bool Dark { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool PinWhenFocused { get; set; } = true;
    public bool AbsoluteTopmost { get; set; } = true;
    public bool AggressiveTopmost { get; set; }
    public bool ManualTopmost { get; set; }
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 760;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public List<WindowRule> Rules { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();
    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        ["show"] = "Win+Ctrl+Space", ["pin"] = "Win+Ctrl+T", ["park"] = "Win+Ctrl+M",
        ["recover"] = "Win+Ctrl+Shift+R", ["opacityUp"] = "Win+Ctrl+OemPlus", ["opacityDown"] = "Win+Ctrl+OemMinus"
    };
}
internal static class OwnWindowPolicy
{
    public static bool Topmost(bool manual, bool compact, bool focusPolicy, bool focused) => manual || compact || (focusPolicy && focused);
    public static bool Enforce(bool visible, bool minimized, bool manual, bool compact, bool absolute) => visible && !minimized && (manual || compact) && absolute;
}
