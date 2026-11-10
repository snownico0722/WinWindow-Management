using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinWindow;

public partial class MainWindow : Window
{
    private readonly StateStore _store;
    internal WindowManager Manager { get; }
    private readonly bool _testMode;
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _aggressiveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _opacityTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<WindowItem, int> _pendingOpacity = new();
    private Dictionary<string, TextBox> _keyEditors = new();
    private DesktopIntegration? _desktop;
    private HwndSource? _source;
    private nint _hwnd;
    private string _filter = "all";
    private bool _ready, _syncing, _picking, _compact, _quitting, _allowClose, _drainingOpacity;
    private Rect _expandedBounds;
    private WindowState _expandedState = WindowState.Normal, _lastState = WindowState.Normal;
    private Task? _initialization;
    private WindowItem? _opacityEditItem;
    private string _opacityEditText = "";
    internal WindowItem? Selected => WindowList.SelectedItem as WindowItem;
    internal bool IsEditingHotkey => _keyEditors.Values.Any(x => x.IsKeyboardFocusWithin);
    internal MainWindow(StateStore store, bool testMode = false)
    {
        _store = store; _testMode = testMode; Manager = new(store);
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(Manager.Items);
        _view.Filter = FilterItem; WindowList.ItemsSource = _view;
        _keyEditors = new() { ["show"] = ShowKeyBox, ["pin"] = PinKeyBox, ["park"] = ParkKeyBox, ["recover"] = RecoverKeyBox, ["opacityUp"] = UpKeyBox, ["opacityDown"] = DownKeyBox };
        OpacityBox.GotKeyboardFocus += (_, _) => { _opacityEditItem = Selected; _opacityEditText = OpacityBox.Text; };
        OpacityBox.TextChanged += (_, _) => { if (!_syncing && OpacityBox.IsKeyboardFocusWithin && _opacityEditItem is not null) _opacityEditText = OpacityBox.Text; };
        var p = store.Preferences;
        if (double.IsFinite(p.Width)) Width = Math.Max(MinWidth, p.Width);
        if (double.IsFinite(p.Height)) Height = Math.Max(MinHeight, p.Height);
        if (p.Left is double left && p.Top is double top && double.IsFinite(left) && double.IsFinite(top)) { WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top; }
        OwnPinBox.IsChecked = p.ManualTopmost;
        DarkBox.IsChecked = p.Dark; TrayBox.IsChecked = p.CloseToTray; StartupBox.IsChecked = p.StartWithWindows;
        FocusPinBox.IsChecked = p.PinWhenFocused; AbsoluteBox.IsChecked = p.AbsoluteTopmost; AggressiveBox.IsChecked = p.AggressiveTopmost;
        AggressiveBox.IsEnabled = p.AbsoluteTopmost;
        foreach (var item in _keyEditors) item.Value.Text = p.Hotkeys.GetValueOrDefault(item.Key, "");
        Palette.Apply(p.Dark);
        Manager.Changed += RefreshView; Manager.Notice += ShowStatus; store.Error += ShowStatus;
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await RunAsync(Manager.RefreshAsync); };
        _aggressiveTimer.Tick += (_, _) => { if (CanEnforceOwn() && Native.OverlappingTopmostAbove(_hwnd)) Native.Raise(_hwnd); };
        _opacityTimer.Tick += async (_, _) => await DrainOpacityAsync();
        _statusTimer.Tick += (_, _) => DismissStatus();
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitializeAsync();
        Activated += (_, _) => ApplyOwnPolicy();
        Deactivated += (_, _) => ApplyOwnPolicy();
        IsVisibleChanged += (_, _) => ApplyOwnPolicy();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastState = WindowState; ApplyOwnPolicy(); };
        Closing += OnClosing;
        _ready = true;
    }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd); _source?.AddHook(WindowMessage);
        int corners = 2; Native.DwmSetWindowAttribute(_hwnd, 33, ref corners, 4);
        Native.FitToWorkArea(_hwnd);
        if (_testMode) return;
        _desktop = new(this, Manager);
        _desktop.ActionRequested += async action => await OnDesktopActionAsync(action);
        _desktop.RecoveryRequested += async item => await RunAsync(() => Manager.ReleaseAsync(item));
        _desktop.WindowsChanged += foreground =>
        {
            if (_quitting) return;
            if (foreground) { ApplyOwnPolicy(); if (CanEnforceOwn()) Native.Raise(_hwnd); }
            if ((IsVisible && WindowState != WindowState.Minimized) || Manager.HasRules) QueueRefresh();
        };
        Run(() => _desktop.ApplyHotkeys(_store.Preferences.Hotkeys));
    }
    private nint WindowMessage(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x0024)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Native.ConstrainMaximize(hwnd, lp, (int)Math.Ceiling(MinWidth * dpi.DpiScaleX), (int)Math.Ceiling(MinHeight * dpi.DpiScaleY));
            handled = true;
        }
        return 0;
    }
    internal Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        await RunAsync(async () =>
        {
            if (!_testMode) await Manager.RecoverPreviousSessionAsync();
            await Manager.RefreshAsync();
            if (_quitting) return;
            if (_store.Preferences.Maximized) WindowState = WindowState.Maximized;
            if (_store.LoadWarning.Length != 0) ShowStatus(_store.LoadWarning);
        });
    }
    private void QueueRefresh() { if (!_quitting && !_refreshTimer.IsEnabled) _refreshTimer.Start(); }
    internal void ShowStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || _quitting) return;
        StatusText.Text = message;
        if (!_compact) StatusBar.Visibility = Visibility.Visible;
        _statusTimer.Stop(); _statusTimer.Start();
    }
    private void DismissStatus() { _statusTimer.Stop(); StatusText.Text = ""; StatusBar.Visibility = Visibility.Collapsed; }
    private void DismissStatus_Click(object sender, RoutedEventArgs e) => DismissStatus();
    private bool FilterItem(object value)
    {
        if (value is not WindowItem item) return false;
        bool inSection = _filter switch { "managed" => item.IsManaged || item.Remembered, "parked" => item.IsParked || item.IsHidden || item.IsMinimized, _ => true };
        string query = SearchBox.Text.Trim();
        return inSection && (query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void RefreshView()
    {
        if (!_ready || _quitting) return;
        _view.Refresh(); EmptyListText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton button) return;
        _filter = (string)button.Tag; RefreshView();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RefreshView(); }
    private void ShowSettings(bool show)
    {
        WindowPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsPage.Visibility != Visibility.Visible);
    private void Back_Click(object sender, RoutedEventArgs e) => ShowSettings(false);
    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_opacityEditItem is not null) ApplyOpacityText();
        _opacityEditItem = null; _syncing = true;
        try
        {
            Inspector.DataContext = Selected;
            InspectorBody.Visibility = Selected is null ? Visibility.Collapsed : Visibility.Visible;
            SelectHint.Visibility = Selected is null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncing = false; }
    }
    internal void SelectForTest(WindowItem item) { WindowList.SelectedItem = item; WindowList.ScrollIntoView(item); }
    private void SyncSelected()
    {
        if (Selected is not { } item) return;
        _syncing = true;
        try
        {
            item.Changed(nameof(item.Pinned)); item.Changed(nameof(item.ClickThrough)); item.NotifyState();
            if (!OpacityBox.IsKeyboardFocusWithin && !_pendingOpacity.ContainsKey(item)) item.Changed(nameof(item.Opacity));
        }
        finally { _syncing = false; }
    }
    private void Run(Action action)
    {
        if (_quitting) return;
        try { action(); } catch (Exception e) { ShowStatus(e.Message); }
        finally { if (_ready) { SyncSelected(); RefreshView(); } }
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_quitting) return;
        try { await action(); } catch (Exception e) { ShowStatus(e.Message); }
        finally { if (_ready && !_quitting) { SyncSelected(); RefreshView(); } }
    }
    private async void Pin_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) { bool on = ((CheckBox)sender).IsChecked == true; await RunAsync(() => Manager.SetPinAsync(item, on)); } }
    private async void ClickThrough_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) { bool on = ((CheckBox)sender).IsChecked == true; await RunAsync(() => Manager.SetClickThroughAsync(item, on)); } }
    private void Remember_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) Run(() => Manager.SetRemembered(item, ((CheckBox)sender).IsChecked == true)); }
    private void QueueOpacity(WindowItem item, int value)
    {
        if (_quitting) return;
        _pendingOpacity[item] = value; _opacityTimer.Start();
    }
    private async Task DrainOpacityAsync()
    {
        if (_drainingOpacity || _quitting) return;
        _drainingOpacity = true;
        try
        {
            foreach (var pair in _pendingOpacity.ToArray())
            {
                if (_quitting) break;
                if (pair.Key.IsBusy) continue;
                _pendingOpacity.Remove(pair.Key);
                if (Manager.Items.Contains(pair.Key)) await RunAsync(() => Manager.SetOpacityAsync(pair.Key, pair.Value));
            }
            if (_pendingOpacity.Count == 0) _opacityTimer.Stop();
        }
        finally { _drainingOpacity = false; }
    }
    private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing || Selected is not { } item || !(OpacitySlider.IsMouseCaptureWithin || OpacitySlider.IsKeyboardFocusWithin)) return;
        QueueOpacity(item, (int)Math.Round(e.NewValue));
    }
    private void ApplyOpacityText()
    {
        var item = _opacityEditItem; string text = _opacityEditText; _opacityEditItem = null;
        if (_syncing || item is null) return;
        if (!int.TryParse(text, out int value) || value is < 0 or > 100) { ShowStatus("不透明度请输入 0–100。"); item.Changed(nameof(item.Opacity)); return; }
        if (value != item.Opacity) QueueOpacity(item, value);
    }
    private void OpacityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyOpacityText(); _opacityEditItem = Selected; _opacityEditText = OpacityBox.Text; e.Handled = true;
    }
    private void OpacityBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { ApplyOpacityText(); _opacityEditItem = null; SyncSelected(); }
    private async void Park_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) await RunAsync(async () => ShowStatus(await Manager.ParkAsync(item))); }
    private async void RestoreWindow_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) await RunAsync(() => Manager.RestoreParkingAsync(item)); }
    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) { _pendingOpacity.Remove(item); await RunAsync(() => Manager.ReleaseAsync(item)); } }
    private async void Forget_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) { _pendingOpacity.Remove(item); await RunAsync(() => Manager.ReleaseAsync(item, true)); } }
    private async void RecoverAll_Click(object sender, RoutedEventArgs e) => await RecoverAllAsync();
    private Task RecoverAllAsync()
    {
        _pendingOpacity.Clear();
        return RunAsync(async () => { int failures = await Manager.RestoreAllAsync(); ShowStatus(failures == 0 ? "已恢复全部窗口。" : $"{failures} 个窗口未恢复，记录已保留。"); });
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(Manager.RefreshAsync);
    private async void WindowList_DoubleClick(object sender, MouseButtonEventArgs e) { if (Selected is { IsAvailable: true } item) await RunAsync(() => Manager.RestoreParkingAsync(item)); }
    private void CopyDetails_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) Run(() => { Clipboard.SetText($"{item.Title}\n{item.Details}\n{item.ProcessPath}"); ShowStatus("已复制窗口信息。"); }); }

    private void Picker_Down(object sender, MouseButtonEventArgs e)
    {
        _picking = true; PickerButton.CaptureMouse(); Mouse.OverrideCursor = Cursors.Cross; e.Handled = true;
    }
    private void Picker_Move(object sender, MouseEventArgs e) { if (_picking) e.Handled = true; }
    private void CancelPicker() { _picking = false; Mouse.OverrideCursor = null; if (PickerButton.IsMouseCaptured) PickerButton.ReleaseMouseCapture(); }
    private void Picker_LostCapture(object sender, MouseEventArgs e) { if (_picking) CancelPicker(); }
    private async void Picker_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        nint target = Native.TargetUnderPointer(); CancelPicker(); e.Handled = true;
        if (target == 0) return;
        await RunAsync(async () =>
        {
            AllFilter.IsChecked = true; SearchBox.Clear();
            var item = Manager.SelectTarget(target); SelectForTest(item); await Manager.SetPinAsync(item, true);
        });
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_picking) { CancelPicker(); e.Handled = true; }
        else if (SettingsPage.Visibility == Visibility.Visible) { ShowSettings(false); e.Handled = true; }
    }

    private bool CanEnforceOwn() => !_quitting && _hwnd != 0 && OwnWindowPolicy.Enforce(IsVisible, WindowState == WindowState.Minimized, _store.Preferences.ManualTopmost, _compact, _store.Preferences.AbsoluteTopmost);
    internal void ApplyOwnPolicy()
    {
        if (!_ready || _hwnd == 0) return;
        bool showing = !_quitting && IsVisible && WindowState != WindowState.Minimized;
        bool focused = showing && IsActive && Native.GetForegroundWindow() == _hwnd;
        bool wanted = showing && OwnWindowPolicy.Topmost(_store.Preferences.ManualTopmost, _compact, _store.Preferences.PinWhenFocused, focused);
        Native.SetOwnTopmost(_hwnd, wanted);
        bool aggressive = CanEnforceOwn() && _store.Preferences.AggressiveTopmost;
        if (aggressive && !_aggressiveTimer.IsEnabled) _aggressiveTimer.Start();
        else if (!aggressive) _aggressiveTimer.Stop();
    }
    internal void ShowShell()
    {
        if (_quitting) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _compact ? WindowState.Normal : _lastState;
        // Explicit user wake is the only temporary raise. Clean up immediately,
        // including when Windows refuses foreground activation; no idle callback
        // or list refresh stands between the raise and its cleanup.
        try { Native.Raise(_hwnd); Activate(); Native.SetForegroundWindow(_hwnd); }
        finally { ApplyOwnPolicy(); }
        QueueRefresh();
    }
    private void OwnPin_Click(object sender, RoutedEventArgs e) { _store.Preferences.ManualTopmost = OwnPinBox.IsChecked == true; _store.SaveSoon(); ApplyOwnPolicy(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (!_compact) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Compact_Click(object sender, RoutedEventArgs e) => ToggleCompact();
    internal void ToggleCompact()
    {
        if (_quitting) return;
        if (!_compact)
        {
            _expandedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _expandedState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            _compact = true; WindowState = WindowState.Normal;
            MinWidth = 360; MinHeight = 110; Width = 360; Height = 110;
            Workspace.Visibility = OwnPinBox.Visibility = SettingsButton.Visibility = MaximizeButton.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = Visibility.Visible; ResizeMode = ResizeMode.NoResize; DismissStatus();
        }
        else
        {
            _compact = false; ResizeMode = ResizeMode.CanResize;
            MinWidth = 880; MinHeight = 560;
            Left = _expandedBounds.Left; Top = _expandedBounds.Top; Width = _expandedBounds.Width; Height = _expandedBounds.Height;
            Workspace.Visibility = OwnPinBox.Visibility = SettingsButton.Visibility = MaximizeButton.Visibility = Visibility.Visible;
            CompactPanel.Visibility = Visibility.Collapsed; WindowState = _expandedState;
        }
        Native.FitToWorkArea(_hwnd); ApplyOwnPolicy();
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var p = _store.Preferences;
        p.Dark = DarkBox.IsChecked == true; p.CloseToTray = TrayBox.IsChecked == true;
        p.PinWhenFocused = FocusPinBox.IsChecked == true; p.AbsoluteTopmost = AbsoluteBox.IsChecked == true;
        p.AggressiveTopmost = AggressiveBox.IsChecked == true; AggressiveBox.IsEnabled = p.AbsoluteTopmost;
        Palette.Apply(p.Dark); ApplyOwnPolicy(); _store.SaveSoon();
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        Run(() => { bool enabled = StartupBox.IsChecked == true; DesktopIntegration.SetAutostart(enabled); _store.Preferences.StartWithWindows = enabled; _store.SaveSoon(); });
        StartupBox.IsChecked = _store.Preferences.StartWithWindows;
    }
    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true; var box = (TextBox)sender;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { string action = _keyEditors.First(x => x.Value == box).Key; box.Text = _store.Preferences.Hotkeys.GetValueOrDefault(action, ""); return; }
        if ((key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None) { box.Clear(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        ModifierKeys mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None) return;
        string text = "";
        if (mods.HasFlag(ModifierKeys.Windows)) text += "Win+";
        if (mods.HasFlag(ModifierKeys.Control)) text += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) text += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) text += "Shift+";
        box.Text = text + key;
    }
    private void ApplyHotkeys_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var keys = _keyEditors.ToDictionary(x => x.Key, x => x.Value.Text.Trim());
        foreach (string value in keys.Values) DesktopIntegration.ParseHotkey(value);
        _desktop?.ApplyHotkeys(keys); _store.Preferences.Hotkeys = keys; _store.SavePreferences(); ShowStatus("快捷键已应用。");
    });
    private async Task OnDesktopActionAsync(string action)
    {
        if (_quitting) return;
        if (action == "show") { ShowShell(); return; }
        if (action == "compact") { ShowShell(); ToggleCompact(); return; }
        if (action == "quit") { await QuitAsync(); return; }
        if (action == "recover") { await RecoverAllAsync(); return; }
        await RunAsync(async () =>
        {
            nint foreground = Native.GetForegroundWindow();
            var item = Native.IsTarget(foreground) ? Manager.SelectTarget(foreground) : Selected;
            if (item is null || !item.IsAvailable) return;
            switch (action)
            {
                case "pin": await Manager.SetPinAsync(item, !Native.IsTopmost(item.Hwnd)); break;
                case "park": ShowStatus(await Manager.ParkAsync(item)); break;
                case "opacityUp": QueueOpacity(item, _pendingOpacity.GetValueOrDefault(item, item.Opacity) + 10); break;
                case "opacityDown": QueueOpacity(item, _pendingOpacity.GetValueOrDefault(item, item.Opacity) - 10); break;
            }
        });
    }
    internal void SaveGeometry()
    {
        Rect bounds = _compact ? _expandedBounds : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var p = _store.Preferences;
        if (!bounds.IsEmpty) { p.Left = bounds.Left; p.Top = bounds.Top; p.Width = bounds.Width; p.Height = bounds.Height; }
        p.Maximized = (_compact ? _expandedState : WindowState == WindowState.Minimized ? _lastState : WindowState) == WindowState.Maximized;
        _store.SavePreferences();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _testMode) return;
        e.Cancel = true;
        SaveGeometry(); CancelPicker();
        if (!_quitting && _store.Preferences.CloseToTray && _desktop is not null) { Hide(); ApplyOwnPolicy(); return; }
        await QuitAsync();
    }
    internal async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _refreshTimer.Stop(); _aggressiveTimer.Stop(); _opacityTimer.Stop(); _pendingOpacity.Clear();
        CancelPicker(); SaveGeometry(); ApplyOwnPolicy();
        int failures = await Manager.ShutdownAsync();
        if (failures != 0) _desktop?.Notify($"{failures} 个窗口未恢复，记录已保留，下次启动继续恢复。");
        _allowClose = true; Cleanup(); Application.Current.Shutdown();
    }
    internal void Cleanup()
    {
        _refreshTimer.Stop(); _aggressiveTimer.Stop(); _opacityTimer.Stop(); _statusTimer.Stop();
        _desktop?.Dispose(); _desktop = null;
        _source?.RemoveHook(WindowMessage); _source = null;
        _store.Error -= ShowStatus; Manager.Notice -= ShowStatus; Manager.Changed -= RefreshView;
    }
}
