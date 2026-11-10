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
    private Dictionary<string, TextBox> _keyEditors = new();
    private DesktopIntegration? _desktop;
    private HwndSource? _source;
    private nint _hwnd;
    private string _filter = "all";
    private bool _ready, _syncing, _picking, _compact, _quitting, _allowClose;
    private Rect _expandedBounds;
    private WindowState _expandedState = WindowState.Normal;
    private WindowState _lastState = WindowState.Normal;
    private Task? _initialization;
    private WindowItem? _opacityEditItem;
    private string _opacityEditText = "";
    internal WindowItem? Selected => WindowList.SelectedItem as WindowItem;
    internal bool IsEditingHotkey => _keyEditors.Values.Any(x => x.IsKeyboardFocusWithin);
    internal MainWindow(StateStore store, bool testMode = false)
    {
        _store = store; _testMode = testMode; Manager = new(store);
        InitializeComponent();
        SelectHint.Text = "选择一个窗口\n在这里调整它的状态";
        _view = CollectionViewSource.GetDefaultView(Manager.Items);
        _view.Filter = FilterItem;
        WindowList.ItemsSource = _view;
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
        Manager.Notice += ShowStatus;
        Manager.Changed += RefreshView;
        store.Error += ShowStatus;
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); Run(Manager.Refresh); };
        _aggressiveTimer.Tick += (_, _) => { if (CanEnforceOwn() && Native.OverlappingTopmostAbove(_hwnd)) Native.Raise(_hwnd); };
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
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WindowMessage);
        int corners = 2; Native.DwmSetWindowAttribute(_hwnd, 33, ref corners, 4);
        Native.FitToWorkArea(_hwnd);
        if (_testMode) return;
        _desktop = new(this, Manager);
        _desktop.ActionRequested += async action => await OnDesktopActionAsync(action);
        _desktop.RecoveryRequested += async item => await RunAsync(async () => { await Manager.ReleaseAsync(item); ShowStatus("已恢复窗口的原始状态。"); });
        _desktop.WindowsChanged += foreground =>
        {
            if (_quitting) return;
            if (foreground && CanEnforceOwn()) Native.Raise(_hwnd);
            if (((IsVisible && WindowState != WindowState.Minimized) || Manager.HasRules) && !_refreshTimer.IsEnabled) _refreshTimer.Start();
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
        if (_testMode) { Run(Manager.Refresh); return; }
        await RunAsync(async () =>
        {
            await Manager.RecoverPreviousSessionAsync();
            Manager.Refresh();
            if (_store.Preferences.Maximized) WindowState = WindowState.Maximized;
            if (_store.LoadWarning.Length != 0) ShowStatus(_store.LoadWarning);
        });
    }
    internal void ShowStatus(string message) => StatusText.Text = message;
    private bool FilterItem(object value)
    {
        if (value is not WindowItem item) return false;
        bool inSection = _filter switch { "managed" => item.IsManaged || item.Remembered, "parked" => item.IsParked || item.IsHidden || item.IsMinimized, _ => true };
        string query = SearchBox.Text.Trim();
        return inSection && (query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void RefreshView()
    {
        if (!_ready) return;
        _view.Refresh();
        int count = _view.Cast<object>().Count();
        CountText.Text = $"{count} 个窗口";
        EmptyListText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton button) return;
        string mode = (string)button.Tag;
        bool settings = mode == "settings";
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        WindowPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        if (!settings)
        {
            _filter = mode;
            PageTitle.Text = mode switch { "managed" => "已管理窗口", "parked" => "已收起窗口", _ => "窗口管理" };
            Run(Manager.Refresh);
        }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RefreshView(); }
    private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_opacityEditItem is not null) ApplyOpacityText();
        _opacityEditItem = null;
        _syncing = true;
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
            if (!OpacityBox.IsKeyboardFocusWithin) item.Changed(nameof(item.Opacity));
        }
        finally { _syncing = false; }
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { ShowStatus(e.Message); }
        finally { if (_ready) { SyncSelected(); RefreshView(); } }
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { ShowStatus(e.Message); }
        finally { if (_ready) { SyncSelected(); RefreshView(); } }
    }
    private void Pin_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) Run(() => Manager.SetPin(item, ((CheckBox)sender).IsChecked == true)); }
    private void ClickThrough_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) Run(() => Manager.SetClickThrough(item, ((CheckBox)sender).IsChecked == true)); }
    private void Remember_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) Run(() => Manager.SetRemembered(item, ((CheckBox)sender).IsChecked == true)); }
    private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _syncing || Selected is not { } item || !(OpacitySlider.IsMouseCaptureWithin || OpacitySlider.IsKeyboardFocusWithin)) return;
        Run(() => Manager.SetOpacity(item, (int)Math.Round(e.NewValue)));
    }
    private void ApplyOpacityText()
    {
        var item = _opacityEditItem;
        string text = _opacityEditText;
        _opacityEditItem = null;
        if (_syncing || item is null) return;
        if (!int.TryParse(text, out int value) || value is < 0 or > 100) { ShowStatus("不透明度请输入 0–100。允许完全透明。"); item.Changed(nameof(item.Opacity)); return; }
        if (value != item.Opacity) Run(() => Manager.SetOpacity(item, value));
        if (OpacityBox.IsKeyboardFocusWithin && Selected == item) { _opacityEditItem = item; _opacityEditText = item.Opacity.ToString(); }
    }
    private void OpacityBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyOpacityText(); e.Handled = true; } }
    private void OpacityBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { ApplyOpacityText(); _opacityEditItem = null; SyncSelected(); }
    private async void Park_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) await RunAsync(async () => ShowStatus(await Manager.ParkAsync(item))); }
    private async void RestoreWindow_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) await RunAsync(async () => { await Manager.RestoreParkingAsync(item); ShowStatus("已恢复窗口；置顶、透明度设置保持不变。"); }); }
    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) await RunAsync(async () => { await Manager.ReleaseAsync(item); ShowStatus("已恢复原始状态。"); }); }
    private async void Forget_Click(object sender, RoutedEventArgs e) { if (Selected is { } item) await RunAsync(async () => { await Manager.ReleaseAsync(item, true); ShowStatus("已移出管理并删除保存的规则。"); }); }
    private async void RecoverAll_Click(object sender, RoutedEventArgs e) => await RecoverAllAsync();
    private async Task RecoverAllAsync() => await RunAsync(async () => { int failures = await Manager.RestoreAllAsync(); ShowStatus(failures == 0 ? "所有被管理窗口已恢复原始状态。" : $"有 {failures} 个窗口未能恢复，恢复记录仍保留。"); });
    private void Refresh_Click(object sender, RoutedEventArgs e) => Run(Manager.Refresh);
    private async void WindowList_DoubleClick(object sender, MouseButtonEventArgs e) { if (Selected is { IsAvailable: true } item) await RunAsync(() => Manager.RestoreParkingAsync(item)); }

    private void Picker_Down(object sender, MouseButtonEventArgs e)
    {
        _picking = true; PickerButton.CaptureMouse(); Mouse.OverrideCursor = Cursors.Cross; e.Handled = true;
        ShowStatus("拖到目标窗口后松开，将其置顶；Esc 取消。");
    }
    private void Picker_Move(object sender, MouseEventArgs e) { if (_picking) e.Handled = true; }
    private void CancelPicker() { _picking = false; Mouse.OverrideCursor = null; if (PickerButton.IsMouseCaptured) PickerButton.ReleaseMouseCapture(); }
    private void Picker_LostCapture(object sender, MouseEventArgs e) { if (_picking) CancelPicker(); }
    private void Picker_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        nint target = Native.TargetUnderPointer();
        CancelPicker(); e.Handled = true;
        if (target == 0) { ShowStatus("没有选中其他应用的窗口。"); return; }
        Run(() => { AllNav.IsChecked = true; SearchBox.Clear(); var item = Manager.SelectTarget(target); Manager.SetPin(item, true); SelectForTest(item); ShowStatus("已置顶选中的窗口。"); });
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && _picking) { CancelPicker(); ShowStatus("已取消选择。"); e.Handled = true; } }

    private bool CanEnforceOwn() => _hwnd != 0 && OwnWindowPolicy.Enforce(IsVisible, WindowState == WindowState.Minimized, _store.Preferences.ManualTopmost, _compact, _store.Preferences.AbsoluteTopmost);
    private void ApplyOwnPolicy()
    {
        if (!_ready || _hwnd == 0) return;
        bool focused = IsVisible && WindowState != WindowState.Minimized && Native.GetForegroundWindow() == _hwnd;
        bool topmost = OwnWindowPolicy.Topmost(_store.Preferences.ManualTopmost, _compact, _store.Preferences.PinWhenFocused, focused);
        if (Topmost != topmost) Topmost = topmost;
        else if (Native.IsTopmost(_hwnd) != topmost) Run(() => Native.SetTopmost(_hwnd, topmost));
        OwnStateText.Text = _compact ? "小窗" : _store.Preferences.ManualTopmost ? "已置顶" : focused && _store.Preferences.PinWhenFocused ? "焦点临时置顶" : "窗口管理";
        bool aggressive = CanEnforceOwn() && _store.Preferences.AggressiveTopmost;
        if (aggressive && !_aggressiveTimer.IsEnabled) _aggressiveTimer.Start();
        else if (!aggressive) _aggressiveTimer.Stop();
    }
    internal void ShowShell()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _compact ? WindowState.Normal : _lastState;
        Topmost = true; Native.Raise(_hwnd);
        Activate(); Native.SetForegroundWindow(_hwnd);
        Dispatcher.BeginInvoke(new Action(ApplyOwnPolicy), DispatcherPriority.ContextIdle);
        Run(Manager.Refresh);
    }
    private void OwnPin_Click(object sender, RoutedEventArgs e) { _store.Preferences.ManualTopmost = OwnPinBox.IsChecked == true; _store.SaveSoon(); ApplyOwnPolicy(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (!_compact) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Compact_Click(object sender, RoutedEventArgs e) => ToggleCompact();
    internal void ToggleCompact()
    {
        if (!_compact)
        {
            _expandedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _expandedState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowState = WindowState.Normal; _compact = true;
            MinWidth = 360; MinHeight = 110; Width = 360; Height = 110;
            Workspace.Visibility = StatusBar.Visibility = OwnPinBox.Visibility = OwnStateText.Visibility = MaximizeButton.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = Visibility.Visible; ResizeMode = ResizeMode.NoResize;
        }
        else
        {
            _compact = false; ResizeMode = ResizeMode.CanResize;
            MinWidth = 880; MinHeight = 560;
            Left = _expandedBounds.Left; Top = _expandedBounds.Top; Width = _expandedBounds.Width; Height = _expandedBounds.Height;
            Workspace.Visibility = StatusBar.Visibility = OwnPinBox.Visibility = OwnStateText.Visibility = MaximizeButton.Visibility = Visibility.Visible;
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
        e.Handled = true;
        var box = (TextBox)sender;
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
        _desktop?.ApplyHotkeys(keys);
        _store.Preferences.Hotkeys = keys; _store.SavePreferences(); ShowStatus("快捷键已应用。");
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
            if (item is null || !item.IsAvailable) { ShowStatus("请先切到目标窗口，或在列表中选择它。"); return; }
            switch (action)
            {
                case "pin": Manager.SetPin(item, !Native.IsTopmost(item.Hwnd)); break;
                case "park": ShowStatus(await Manager.ParkAsync(item)); break;
                case "opacityUp": Manager.SetOpacity(item, item.Opacity + 10); break;
                case "opacityDown": Manager.SetOpacity(item, item.Opacity - 10); break;
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
        if (_store.Preferences.CloseToTray && _desktop is not null) { Hide(); ApplyOwnPolicy(); return; }
        await QuitAsync();
    }
    internal async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true; _refreshTimer.Stop(); _aggressiveTimer.Stop(); CancelPicker(); SaveGeometry();
        int failures = await Manager.RestoreAllAsync(false);
        if (failures != 0)
        {
            _quitting = false; ShowShell(); ShowStatus($"还有 {failures} 个窗口未恢复，暂未退出。请先恢复或关闭这些目标窗口。"); return;
        }
        _allowClose = true; Cleanup(); Application.Current.Shutdown();
    }
    internal void Cleanup()
    {
        _refreshTimer.Stop(); _aggressiveTimer.Stop(); _desktop?.Dispose(); _desktop = null;
        _source?.RemoveHook(WindowMessage); _source = null;
        _store.Error -= ShowStatus; Manager.Notice -= ShowStatus; Manager.Changed -= RefreshView;
    }
}
