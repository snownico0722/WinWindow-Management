using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

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
    public bool RemovedPendingBackground { get; set; }
    public bool RestoreActivation { get; set; }
    public bool? InjectedBackground { get; set; }
    public bool EditedTopmost { get; set; }
    public bool EditedOpacity { get; set; }
    public bool EditedClickThrough { get; set; }
    public Dictionary<string, bool>? OriginalAudioMutes { get; set; }
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
    public bool Muted { get; set; }
    public bool KeepRunning { get; set; }
    public bool Matches(WindowItem item) => item.Identity is { } id &&
        string.Equals(ProcessPath, id.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ClassName, id.ClassName, StringComparison.Ordinal) &&
        string.Equals(Title, item.Title, StringComparison.Ordinal);
}
internal sealed class WindowItem : Observable
{
    public WindowIdentity? Identity { get; init; }
    public string SavedProcessPath { get; init; } = "";
    public WindowSnapshot? Original { get; set; }
    public HashSet<string>? MutedSessionIds { get; set; }
    public string? RuleId { get; set; }
    public nint Hwnd => Identity?.Hwnd ?? 0;
    public string ProcessPath => Identity?.ProcessPath ?? SavedProcessPath;
    public string ProcessName => Identity is not null ? Path.GetFileName(Identity.ProcessPath)
        : !string.IsNullOrWhiteSpace(SavedProcessPath) ? Path.GetFileName(SavedProcessPath)
        : UiText.Get("WaitingForWindow");
    private volatile ImageSource? _icon;
    private int _requestedIcon;
    public ImageSource? Icon
    {
        get
        {
            if ((Identity is not null || !string.IsNullOrWhiteSpace(SavedProcessPath)) &&
                Interlocked.CompareExchange(ref _requestedIcon, 1, 0) == 0)
                _ = LoadIconAsync();
            return _icon;
        }
    }

    private async Task LoadIconAsync()
    {
        var icon = await Task.Run(() => Identity is { } identity
            ? Native.WindowIcon(identity)
            : Native.ExecutableIcon(SavedProcessPath)).ConfigureAwait(false);
        if (icon is null) return;

        void Publish()
        {
            _icon = icon;
            Changed(nameof(Icon));
        }

        // 异步绑定可以在后台线程首次读取 Icon，通知同时会触发浮窗和行控件的 UI 更新。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            Publish();
        else if (!dispatcher.HasShutdownStarted)
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Publish);
    }
    public string Initial
    {
        get
        {
            string source = !string.IsNullOrWhiteSpace(ProcessPath) ? Path.GetFileNameWithoutExtension(ProcessPath) : Title;
            foreach (char c in source)
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            return "W";
        }
    }
    public string Details => Identity is null ? UiText.Get("SavedRule") : $"PID {Identity.ProcessId}  ·  HWND 0x{Identity.Handle:X}  ·  {Identity.ClassName}";
    private string _title = "";
    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value) && Identity is null) Changed(nameof(Initial)); }
    }
    private bool _backgroundEnabled;
    public bool BackgroundEnabled { get => _backgroundEnabled; set => Set(ref _backgroundEnabled, value); }
    private bool _available = true;
    public bool IsAvailable { get => _available; set { if (Set(ref _available, value)) NotifyState(); } }
    // Membership is independent from TOPMOST and the recovery snapshot.
    public bool Listed { get; set; }
    public bool IsManaged => Listed || Original is { RemovedPendingBackground: false };
    public bool AutoRemember { get; set; } = true;
    public bool WantsPin { get; set; }
    public bool Remembered => RuleId is not null;
    private bool _pinned;
    public bool Pinned { get => _pinned; set { if (Set(ref _pinned, value)) NotifyState(); } }
    private int _opacity = 100;
    public int Opacity { get => _opacity; set { if (Set(ref _opacity, value)) NotifyState(); } }
    public int? OpacityBeforeQuickToggle { get; set; }
    private bool _clickThrough;
    public bool ClickThrough { get => _clickThrough; set { if (Set(ref _clickThrough, value)) NotifyState(); } }
    private bool _muted;
    public bool Muted { get => _muted; set { if (Set(ref _muted, value)) NotifyState(); } }
    private bool _minimized;
    public bool IsMinimized { get => _minimized; set { if (Set(ref _minimized, value)) NotifyState(); } }
    private bool _hidden;
    public bool IsHidden { get => _hidden; set { if (Set(ref _hidden, value)) NotifyState(); } }
    private bool _iconCollapsed;
    public bool IsIconCollapsed
    {
        get => _iconCollapsed;
        set
        {
            if (!value) IconWindowGeometry = null;
            if (Set(ref _iconCollapsed, value)) NotifyState();
        }
    }
    internal Native.IconWindowGeometry? IconWindowGeometry { get; set; }
    private bool _busy;
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) { Changed(nameof(CanOperate)); Changed(nameof(CanLaunch)); NotifyState(); } } }
    public bool CanOperate => IsAvailable && !IsBusy;
    private bool _canLaunch;
    public bool CanLaunch
    {
        get => _canLaunch && !IsBusy;
        set => Set(ref _canLaunch, value);
    }
    public bool IsParked => Original?.Parked == true;
    public string StateText => !IsAvailable ? UiText.Get("PendingWindow") : IsBusy ? UiText.Get("Working") : IsIconCollapsed ? UiText.Get("CollapsedIconState") : IsHidden ? UiText.Get("HiddenRecoverable") : IsMinimized ? UiText.Get("Minimized") : Opacity == 0 ? UiText.Get("TransparentRecoverable") : Pinned ? UiText.Get("Pinned") : IsManaged ? UiText.Get("Managed") : UiText.Get("Normal");
    public void NotifyLanguage()
    {
        Changed(nameof(ProcessName)); Changed(nameof(Details)); Changed(nameof(StateText));
    }

    public void NotifyState()
    {
        Changed(nameof(StateText)); Changed(nameof(IsManaged)); Changed(nameof(IsParked));
        Changed(nameof(Remembered)); Changed(nameof(CanOperate)); Changed(nameof(CanLaunch));
    }
}
internal sealed class Preferences
{
    public string Language { get; set; } = "system";
    public bool Dark { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool RunAsAdministrator { get; set; } = true;
    public bool PinWhenFocused { get; set; } = true;
    public bool AbsoluteTopmost { get; set; } = true;
    public bool AggressiveTopmost { get; set; }
    public bool EnablePinSound { get; set; } = true;
    public bool ShowPinNotifications { get; set; }
    public int QuickOpacity { get; set; } = 50;
    public bool EnhancedBackgroundKeep { get; set; }
    public bool LockBackgroundCursorPos { get; set; }
    public bool SnapshotLookbackEnabled { get; set; } = true;
    public bool ManualTopmost { get; set; }
    public double Width { get; set; } = 580;
    public double Height { get; set; } = 500;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public List<WindowRule> Rules { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();
    public static Dictionary<string, string> DefaultHotkeys() => new()
    {
        ["show"] = "Win+Ctrl+Space", ["pin"] = "Win+Ctrl+T", ["park"] = "Win+Ctrl+M",
        ["arrange"] = "", ["recover"] = "Win+Ctrl+Shift+R", ["opacityUp"] = "Win+Ctrl+OemPlus", ["opacityDown"] = "Win+Ctrl+OemMinus"
    };
}
internal static class OwnWindowPolicy
{
    public static bool Topmost(bool manual, bool compact, bool focusPolicy, bool focused) => manual || compact || (focusPolicy && focused);
    public static bool Enforce(bool visible, bool minimized, bool manual, bool compact, bool absolute) => visible && !minimized && (manual || compact) && absolute;
}
