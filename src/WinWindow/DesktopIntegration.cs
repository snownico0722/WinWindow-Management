using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Media;
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
    private readonly Icon _trayIcon;
    private readonly Forms.ToolStripItem _undo;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Native.EventProc _eventCallback;
    private readonly List<nint> _hooks = new();
    private Dictionary<int, string> _actions = new();
    private Dictionary<string, string> _hotkeys = new();
    private bool _disposed, _eventQueued, _foregroundPending;
    public event Action<string>? ActionRequested;
    public event Action<bool>? WindowsChanged;
    internal nint LastExternalForeground { get; private set; }
    public event Action<WindowItem>? RecoveryRequested;

    public DesktopIntegration(MainWindow window, WindowManager manager)
    {
        _window = window;
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(Message);
        if (WakeMessage == 0) throw new Win32Exception(UiText.Get("WakeUnavailable"));
        // Only this parameterless Show request is permitted across UIPI.
        if (!Native.ChangeWindowMessageFilterEx(_hwnd, WakeMessage, 1, 0))
            window.ShowStatus(UiText.Format("WakePermission", Marshal.GetLastPInvokeError()));
        _trayIcon = LoadTrayIcon();
        _tray = new Forms.NotifyIcon { Text = "WinWindow", Icon = _trayIcon, Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatch("togglePanel"); };
        AddTrayAction("OpenPanel", "show");
        AddTrayAction("ToggleCapsule", "compact");
        AddTrayAction("Arrange", "arrange");
        _undo = AddTrayAction("UndoLast", "undoLayout");
        _undo.Enabled = false;
        var managed = new Forms.ToolStripMenuItem { Tag = "TrayManaged" };
        _menu.Items.Add(managed);
        var recover = new Forms.ToolStripMenuItem { Tag = "FindHidden" };
        _menu.Items.Add(recover);
        _menu.Opening += (_, _) =>
        {
            foreach (Forms.ToolStripItem option in _menu.Items)
                if (option.Tag is string key) option.Text = UiText.Get(key);
            managed.DropDownItems.Clear();
            foreach (var item in manager.Items.Where(x => x.IsManaged || x.Remembered).OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray())
            {
                var captured = item;
                string title = item.Title.Length > 44 ? item.Title[..44] + "…" : item.Title;
                var entry = new Forms.ToolStripMenuItem(title) { Checked = item.Pinned, CheckOnClick = false, ToolTipText = item.Pinned ? UiText.Get("UnpinHint") : UiText.Get("PinHint") };
                entry.Click += (_, _) => _window.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (!_disposed) await _window.ExecuteRowAsync(captured, "pin");
                }));
                managed.DropDownItems.Add(entry);
            }
            managed.Enabled = managed.DropDownItems.Count != 0;
            recover.DropDownItems.Clear();
            foreach (var item in manager.Items.Where(x => x.IsManaged && (x.IsHidden || x.IsParked || x.Opacity == 0)).ToArray())
            {
                var captured = item;
                string title = item.Title.Length > 40 ? item.Title[..40] + "…" : item.Title;
                recover.DropDownItems.Add(title, null, (_, _) => _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) RecoveryRequested?.Invoke(captured); })));
            }
            recover.Enabled = recover.DropDownItems.Count != 0;
        };
        AddTrayAction("AddWindow", "pickWindow");
        var unpinAll = AddTrayAction("UnpinAll", "unpinAll");
        _menu.Opening += (_, _) => unpinAll.Enabled = manager.Items.Any(x => (x.IsManaged || x.Remembered) && (x.Pinned || x.WantsPin));
        AddTrayAction("RestoreAll", "recover");
        _menu.Items.Add(new Forms.ToolStripSeparator());
        AddTrayAction("Quit", "quit");
        var initialForeground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(initialForeground, out uint lastForegroundProcess);
        _window.LayoutFocus.Visit(initialForeground, lastForegroundProcess, Environment.TickCount64);
        if (initialForeground != 0 && Native.IsTarget(initialForeground)) LastExternalForeground = initialForeground;
        _eventCallback = (_, ev, hwnd, objectId, childId, _, eventTime) =>
        {
            if (_disposed || hwnd == 0 || (ev >= 0x8000 && (objectId != 0 || childId != 0)) || _window.Dispatcher.HasShutdownStarted) return;
            var now = Environment.TickCount64;
            long tick = now - unchecked((uint)now - eventTime);
            if (ev is 0x8000 or 0x8002 or 0x17 && Native.GetAncestor(hwnd, 2) == hwnd)
            {
                Native.GetWindowThreadProcessId(hwnd, out var surfacePid);
                if (ev == 0x8000) _window.LayoutFocus.Created(hwnd, surfacePid, tick);
                else _window.LayoutFocus.Shown(hwnd, surfacePid, tick);
            }
            if (ev == 0x8001) _window.LayoutFocus.Retire(hwnd);
            if (ev == 3)
            {
                Native.GetWindowThreadProcessId(hwnd, out var current);
                _window.LayoutFocus.Visit(hwnd, current, tick);
                if (Native.IsTarget(hwnd)) LastExternalForeground = hwnd;
                var previous = lastForegroundProcess;
                lastForegroundProcess = current;
                if (previous != 0 && current != previous)
                    _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) manager.OnApplicationDeactivated(previous); }));
                _foregroundPending = true;
            }
            if (_eventQueued) return;
            _eventQueued = true;
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                bool foreground = _foregroundPending;
                _eventQueued = _foregroundPending = false;
                if (!_disposed) WindowsChanged?.Invoke(foreground);
            }));
        };
        AddHook(3, 3); AddHook(0x16, 0x17); AddHook(0x8000, 0x8003); AddHook(0x800C, 0x800C);
    }

    private Forms.ToolStripItem AddTrayAction(string textKey, string action)
    {
        var item = _menu.Items.Add(UiText.Get(textKey), null, (_, _) => Dispatch(action));
        item.Tag = textKey;
        return item;
    }

    private static Icon LoadTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/WinWindow;component/Assets/WinWindow.ico");
        using var stream = System.Windows.Application.GetResourceStream(uri).Stream;
        using var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }

    internal void SetUndoAvailable(bool available) => _undo.Enabled = available;

    private void AddHook(uint first, uint last)
    {
        nint hook = Native.SetWinEventHook(first, last, 0, _eventCallback, 0, 0, 0);
        if (hook != 0) _hooks.Add(hook);
    }

    private void Dispatch(string action) => _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) ActionRequested?.Invoke(action); }));

    private nint Message(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if ((uint)msg == WakeMessage && wp == 0 && lp == 0) { Dispatch("show"); handled = true; }
        else if (msg == 0x0312 && _actions.TryGetValue(wp.ToInt32(), out string? action))
        {
            if (!_window.CaptureRegisteredHotkey(_hotkeys[action])) Dispatch(action == "show" ? "togglePanel" : action);
            handled = true;
        }
        return 0;
    }

    public void Notify(string message) => _tray.ShowBalloonTip(4000, "WinWindow", message, Forms.ToolTipIcon.Warning);

    internal void NotifyInfo(string message) => _tray.ShowBalloonTip(2500, "WinWindow", message, Forms.ToolTipIcon.Info);

    private readonly SoundPlayer _pinSound = new(typeof(DesktopIntegration).Assembly
        .GetManifestResourceStream("WinWindow.Resources.PinFeedback.wav")!);

    internal void PlayPinSound() => _pinSound.Play();

    public static (uint Modifiers, uint Key) ParseHotkey(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0);
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        uint mods = 0;
        for (int i = 0; i < parts.Length - 1; i++)
            mods |= parts[i].ToLowerInvariant() switch { "win" => 8u, "ctrl" => 2u, "alt" => 1u, "shift" => 4u, _ => throw new ArgumentException(UiText.Get("InvalidModifier")) };
        if (mods == 0 || !Enum.TryParse(parts.LastOrDefault(), true, out Key key) || key is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException(UiText.Get("MissingShortcutKey"));
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
                if (!Native.RegisterHotKey(_hwnd, id, key.Modifiers | 0x4000, key.Key)) throw new Win32Exception(UiText.Format("ShortcutRegistrationFailed", entry.Value));
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
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException(UiText.Get("MissingExePath"));
            string command = $"\"{exe}\"";
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)) command += $" \"{Environment.GetCommandLineArgs()[0]}\"";
            key.SetValue(name, command + (dev ? " --dev" : "") + " --minimized");
        }
        else key.DeleteValue(name, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        foreach (nint hook in _hooks) Native.UnhookWinEvent(hook);
        _source.RemoveHook(Message);
        _tray.Visible = false; _pinSound.Dispose(); _tray.Dispose(); _menu.Dispose();
        _trayIcon.Dispose();
    }
}
