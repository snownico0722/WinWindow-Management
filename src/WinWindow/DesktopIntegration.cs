using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WinWindow;

internal sealed class DesktopIntegration : IDisposable
{
    public static readonly uint WakeMessage = Native.RegisterWindowMessage("WinWindow.Management.Show.v1");
    private readonly MainWindow _window;
    private readonly nint _hwnd;
    private readonly HwndSource _source;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Native.EventProc _eventCallback;
    private readonly List<nint> _hooks = new();
    private Dictionary<int, string> _actions = new();
    private Dictionary<string, string> _hotkeys = new();
    public event Action<string>? ActionRequested;
    public event Action<bool>? WindowsChanged;
    public event Action<WindowItem>? RecoveryRequested;
    public DesktopIntegration(MainWindow window, WindowManager manager)
    {
        _window = window;
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(Message);
        _tray = new Forms.NotifyIcon { Text = "WinWindow · 窗口管理", Icon = System.Drawing.SystemIcons.Application, Visible = true, ContextMenuStrip = _menu };
        _tray.DoubleClick += (_, _) => Dispatch("show");
        _menu.Items.Add("打开窗口管理", null, (_, _) => Dispatch("show"));
        _menu.Items.Add("切换小窗", null, (_, _) => Dispatch("compact"));
        var recover = new Forms.ToolStripMenuItem("找回窗口");
        _menu.Items.Add(recover);
        _menu.Opening += (_, _) =>
        {
            recover.DropDownItems.Clear();
            foreach (var item in manager.Items.Where(x => x.IsManaged && (x.IsHidden || x.IsParked || x.Opacity == 0)).ToArray())
            {
                var captured = item;
                string title = item.Title.Length > 40 ? item.Title[..40] + "…" : item.Title;
                recover.DropDownItems.Add(title, null, (_, _) => _window.Dispatcher.BeginInvoke(new Action(() => RecoveryRequested?.Invoke(captured))));
            }
            recover.Enabled = recover.DropDownItems.Count != 0;
        };
        _menu.Items.Add("恢复全部窗口原始状态", null, (_, _) => Dispatch("recover"));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Dispatch("quit"));
        _eventCallback = (_, ev, hwnd, objectId, childId, _, _) =>
        {
            if (hwnd == 0 || (ev >= 0x8000 && (objectId != 0 || childId != 0)) || _window.Dispatcher.HasShutdownStarted) return;
            _window.Dispatcher.BeginInvoke(new Action(() => WindowsChanged?.Invoke(ev == 3)));
        };
        AddHook(3, 3);
        AddHook(0x16, 0x17);
        AddHook(0x8000, 0x8003);
        AddHook(0x800C, 0x800C);
    }
    private void AddHook(uint first, uint last)
    {
        nint hook = Native.SetWinEventHook(first, last, 0, _eventCallback, 0, 0, 2);
        if (hook != 0) _hooks.Add(hook);
    }
    private void Dispatch(string action) => _window.Dispatcher.BeginInvoke(new Action(() => ActionRequested?.Invoke(action)));
    private nint Message(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if ((uint)msg == WakeMessage) { Dispatch("show"); handled = true; }
        else if (msg == 0x0312 && _actions.TryGetValue(wp.ToInt32(), out string? action))
        {
            if (!_window.IsEditingHotkey) Dispatch(action);
            handled = true;
        }
        return 0;
    }
    public static (uint Modifiers, uint Key) ParseHotkey(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0);
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        uint mods = 0;
        for (int i = 0; i < parts.Length - 1; i++)
            mods |= parts[i].ToLowerInvariant() switch { "win" => 8u, "ctrl" => 2u, "alt" => 1u, "shift" => 4u, _ => throw new ArgumentException("快捷键修饰键应为 Win、Ctrl、Alt 或 Shift。") };
        if (mods == 0 || !Enum.TryParse(parts.LastOrDefault(), true, out Key key) || key is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException("快捷键需要至少一个修饰键和一个普通按键。");
        return (mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    private Dictionary<int, string> Register(Dictionary<string, string> settings)
    {
        var result = new Dictionary<int, string>();
        try
        {
            int id = 1;
            foreach (var entry in settings)
            {
                var key = ParseHotkey(entry.Value);
                if (key.Key == 0) continue;
                if (!Native.RegisterHotKey(_hwnd, id, key.Modifiers | 0x4000, key.Key))
                    throw new Win32Exception($"快捷键 {entry.Value} 已被占用或无法注册，原快捷键已保留。");
                result.Add(id++, entry.Key);
            }
            return result;
        }
        catch { foreach (int id in result.Keys) Native.UnregisterHotKey(_hwnd, id); throw; }
    }
    public void ApplyHotkeys(Dictionary<string, string> settings)
    {
        foreach (int id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        try { _actions = Register(settings); _hotkeys = new(settings); }
        catch { _actions = Register(_hotkeys); throw; }
    }
    public static void SetAutostart(bool enabled)
    {
        bool dev = Environment.GetCommandLineArgs().Contains("--dev");
        string name = dev ? "WinWindowManagement-dev" : "WinWindowManagement";
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径。");
            string command = $"\"{exe}\"";
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                command += $" \"{Assembly.GetExecutingAssembly().Location}\"";
            key.SetValue(name, command + (dev ? " --dev" : "") + " --minimized");
        }
        else key.DeleteValue(name, false);
    }
    public void Dispose()
    {
        foreach (int id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        foreach (nint hook in _hooks) Native.UnhookWinEvent(hook);
        _source.RemoveHook(Message);
        _tray.Visible = false; _tray.Dispose(); _menu.Dispose();
    }
}
