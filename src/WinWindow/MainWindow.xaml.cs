using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly ListCollectionView _view, _pickView;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _aggressiveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _opacityTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<WindowItem, int> _pendingOpacity = new();
    private Dictionary<string, TextBox> _keyEditors = new();
    private DesktopIntegration? _desktop;
    private HwndSource? _source;
    private nint _hwnd;
    private bool _ready, _picking, _compact, _quitting, _allowClose;
    private Point? _brandDown;
    private Rect _expandedBounds;
    private WindowState _expandedState = WindowState.Normal, _lastState = WindowState.Normal;
    private Task? _initialization;
    internal WindowItem? Selected => WindowList.SelectedItem as WindowItem;
    internal bool IsEditingHotkey => _keyEditors.Values.Any(x => x.IsKeyboardFocusWithin);
    internal MainWindow(StateStore store, bool testMode = false)
    {
        _store = store; _testMode = testMode; Manager = new(store);
        InitializeComponent();
        _view = new ListCollectionView(Manager.Items) { Filter = FilterManaged };
        _pickView = new ListCollectionView(Manager.Items) { Filter = FilterAvailable };
        WindowList.ItemsSource = _view; PickList.ItemsSource = _pickView;
        _keyEditors = new() { ["show"] = ShowKeyBox, ["pin"] = PinKeyBox, ["park"] = ParkKeyBox, ["recover"] = RecoverKeyBox, ["opacityUp"] = UpKeyBox, ["opacityDown"] = DownKeyBox };
        var p = store.Preferences;
        if (double.IsFinite(p.Width)) Width = Math.Max(MinWidth, p.Width);
        if (double.IsFinite(p.Height)) Height = Math.Max(MinHeight, p.Height);
        if (p.Left is double left && p.Top is double top && double.IsFinite(left) && double.IsFinite(top)) { WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top; }
        OwnPinBox.IsChecked = p.ManualTopmost;
        DarkBox.IsChecked = p.Dark; TrayBox.IsChecked = !p.CloseToTray; StartupBox.IsChecked = p.StartWithWindows;
        FocusPinBox.IsChecked = p.PinWhenFocused; AbsoluteBox.IsChecked = p.AbsoluteTopmost; AggressiveBox.IsChecked = p.AggressiveTopmost;
        AggressiveBox.IsEnabled = p.AbsoluteTopmost;
        foreach (var pair in _keyEditors) pair.Value.Text = p.Hotkeys.GetValueOrDefault(pair.Key, "");
        Palette.Apply(p.Dark);
        Manager.Changed += RefreshView; Manager.Notice += ShowStatus; store.Error += ShowStatus;
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await RunAsync(Manager.RefreshAsync); };
        _aggressiveTimer.Tick += (_, _) => { if (CanEnforceOwn() && Native.OverlappingTopmostAbove(_hwnd)) Native.Raise(_hwnd); };
        _opacityTimer.Tick += (_, _) => DrainOpacity();
        _statusTimer.Tick += (_, _) => DismissStatus();
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitializeAsync();
        Activated += (_, _) => ApplyOwnPolicy();
        Deactivated += (_, _) => ApplyOwnPolicy();
        IsVisibleChanged += (_, _) => { if (!IsVisible) AddPopup.IsOpen = false; ApplyOwnPolicy(); };
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastState = WindowState; else AddPopup.IsOpen = false; ApplyOwnPolicy(); };
        Closing += OnClosing;
        _ready = true; RefreshView();
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
        _desktop.RecoveryRequested += async item => await ExecuteRowAsync(item, "reset");
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
    private Task InitializeCoreAsync() => RunAsync(async () =>
    {
        if (!_testMode) await Manager.RecoverPreviousSessionAsync();
        await Manager.RefreshAsync();
        if (_quitting) return;
        if (_store.Preferences.Maximized && !_compact) WindowState = WindowState.Maximized;
        if (_store.LoadWarning.Length != 0) ShowStatus(_store.LoadWarning);
    });
    private void QueueRefresh() { if (!_quitting && !_refreshTimer.IsEnabled) _refreshTimer.Start(); }
    internal void ShowStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || _quitting) return;
        StatusText.Text = message;
        if (!_compact) StatusBar.Visibility = Visibility.Visible;
        _statusTimer.Stop(); _statusTimer.Start();
    }
    private void DismissStatus() { _statusTimer.Stop(); StatusText.Text = ""; StatusBar.Visibility = Visibility.Collapsed; }
    private void DismissStatus_Click(object sender, MouseButtonEventArgs e) => DismissStatus();
    private bool FilterManaged(object value) => value is WindowItem item && (item.IsManaged || item.Remembered) && MatchesSearch(item, SearchBox.Text);
    private bool FilterAvailable(object value) => value is WindowItem { IsAvailable: true, Identity: not null } item && !item.IsHidden && MatchesSearch(item, PickSearch.Text);
    private static bool MatchesSearch(WindowItem item, string query) => query.Trim().Length == 0 || item.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) || item.ProcessName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    private void RefreshView()
    {
        if (!_ready || _quitting) return;
        // Keep row controls and their keyboard/mouse capture alive while values
        // change. Refresh filtering only when the visible membership changes.
        if (!_view.Cast<WindowItem>().SequenceEqual(Manager.Items.Where(x => FilterManaged(x)))) _view.Refresh();
        if (AddPopup.IsOpen && !_pickView.Cast<WindowItem>().SequenceEqual(Manager.Items.Where(x => FilterAvailable(x)))) _pickView.Refresh();
        EmptyListText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyListText.Text = SearchBox.Text.Length == 0 ? "暂无管理的窗口" : "没有匹配的窗口";
        UnpinAllButton.IsEnabled = Manager.Items.Any(x => (x.IsManaged || x.Remembered) && x.Pinned && !x.IsBusy);
        if (_pendingOpacity.Count != 0) _opacityTimer.Start();
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RefreshView(); }
    internal void SelectForTest(WindowItem item) { WindowList.SelectedItem = item; WindowList.ScrollIntoView(item); }
    private void Run(Action action)
    {
        if (_quitting) return;
        try { action(); } catch (Exception e) { ShowStatus(e.Message); }
        finally { RefreshView(); }
    }
    internal async Task RunAsync(Func<Task> action)
    {
        if (_quitting) return;
        try { await action(); } catch (Exception e) { ShowStatus(e.Message); }
        finally { RefreshView(); }
    }
    internal Task ExecuteRowAsync(WindowItem item, string action) => RunAsync(async () =>
    {
        WindowList.SelectedItem = item;
        switch (action)
        {
            case "activate": await Manager.ActivateAsync(item); break;
            case "pin": await Manager.SetListedPinAsync(item, !item.Pinned); break;
            case "through": await Manager.SetClickThroughAsync(item, !item.ClickThrough); break;
            case "park":
                if (item.IsMinimized || item.IsHidden) await Manager.RestoreParkingAsync(item);
                else ShowStatus(await Manager.ParkAsync(item));
                break;
            case "reset": _pendingOpacity.Remove(item); await Manager.ReleaseAsync(item); break;
            case "remove": _pendingOpacity.Remove(item); await Manager.RemoveAsync(item); break;
            case "remember": Manager.SetRemembered(item, !item.Remembered); break;
            case "copy": Clipboard.SetText($"{item.Title}\n{item.Details}\n{item.ProcessPath}"); break;
        }
    });
    internal int DesiredOpacity(WindowItem item) => _pendingOpacity.GetValueOrDefault(item, item.Opacity);
    internal void QueueOpacity(WindowItem item, int value)
    {
        if (_quitting || !Manager.Items.Contains(item)) return;
        _pendingOpacity[item] = Math.Clamp(value, 0, 100); _opacityTimer.Start();
    }
    private void DrainOpacity()
    {
        _opacityTimer.Stop();
        if (_quitting) return;
        foreach (var pair in _pendingOpacity.ToArray())
            if (!pair.Key.IsBusy) _ = ApplyOpacityAsync(pair.Key, pair.Value);
    }
    private async Task ApplyOpacityAsync(WindowItem item, int value)
    {
        await RunAsync(() => Manager.SetOpacityAsync(item, value));
        if (_pendingOpacity.TryGetValue(item, out int latest) && latest == value) _pendingOpacity.Remove(item);
        item.Changed(nameof(item.Opacity));
        if (!_quitting && _pendingOpacity.Count != 0) _opacityTimer.Start();
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        AddPopup.IsOpen = !AddPopup.IsOpen;
        if (AddPopup.IsOpen) await RunAsync(Manager.RefreshAsync);
    }
    private void AddPopup_Opened(object? sender, EventArgs e)
    {
        PickSearch.Clear(); _pickView.Refresh(); PickSearch.Focus();
    }
    private void PickSearch_Changed(object sender, TextChangedEventArgs e) { if (_ready) _pickView.Refresh(); }
    private async void Pick_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is WindowItem item) await AddTargetAsync(item.Hwnd); }
    private async void AddPopup_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { AddPopup.IsOpen = false; AddButton.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && (PickList.SelectedItem as WindowItem ?? _pickView.Cast<WindowItem>().FirstOrDefault()) is { } item)
        { e.Handled = true; await AddTargetAsync(item.Hwnd); }
    }
    internal Task AddTargetAsync(nint hwnd) => RunAsync(async () =>
    {
        AddPopup.IsOpen = false;
        var item = Manager.SelectTarget(hwnd);
        await Manager.SetPinAsync(item, true);
        SearchBox.Clear(); RefreshView(); WindowList.ScrollIntoView(item);
    });
    private void Picker_Down(object sender, MouseButtonEventArgs e)
    {
        AddPopup.IsOpen = false; _picking = true; PickerButton.CaptureMouse(); Mouse.OverrideCursor = Cursors.Cross; e.Handled = true;
    }
    private void Picker_Move(object sender, MouseEventArgs e) { if (_picking) e.Handled = true; }
    private void CancelPicker() { _picking = false; Mouse.OverrideCursor = null; if (PickerButton.IsMouseCaptured) PickerButton.ReleaseMouseCapture(); }
    private void Picker_LostCapture(object sender, MouseEventArgs e) { if (_picking) CancelPicker(); }
    private async void Picker_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        nint target = Native.TargetUnderPointer(); CancelPicker(); e.Handled = true;
        if (target != 0) await AddTargetAsync(target);
    }
    private void DragPicker_Click(object sender, RoutedEventArgs e) => Add_Click(sender, e);
    private async void UnpinAll_Click(object sender, RoutedEventArgs e) => await RunAsync(Manager.UnpinAllAsync);
    private async void RecoverAll_Click(object sender, RoutedEventArgs e) => await RecoverAllAsync();
    private Task RecoverAllAsync()
    {
        _pendingOpacity.Clear();
        return RunAsync(async () => { int failures = await Manager.RestoreAllAsync(); ShowStatus(failures == 0 ? "已恢复全部窗口。" : $"{failures} 个窗口未恢复，记录已保留。"); });
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(Manager.RefreshAsync);
    private void More_Click(object sender, RoutedEventArgs e)
    { var button = (Button)sender; button.ContextMenu.PlacementTarget = button; button.ContextMenu.Placement = PlacementMode.Bottom; button.ContextMenu.IsOpen = true; }
    private void ShowSettings(bool show)
    { AddPopup.IsOpen = false; WindowPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible; SettingsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed; }
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsPage.Visibility != Visibility.Visible);
    private void Back_Click(object sender, RoutedEventArgs e) => ShowSettings(false);
    private void CompactSettings_Click(object sender, RoutedEventArgs e) { if (_compact) ToggleCompact(); ShowSettings(true); }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_picking) { CancelPicker(); e.Handled = true; }
        else if (SettingsPage.Visibility == Visibility.Visible && !IsEditingHotkey) { ShowSettings(false); e.Handled = true; }
    }
    private void Brand_Click(object sender, RoutedEventArgs e) => ToggleCompact();
    private void Brand_Down(object sender, MouseButtonEventArgs e)
    { _brandDown = e.GetPosition(this); BrandArea.CaptureMouse(); e.Handled = true; }
    private void Brand_Move(object sender, MouseEventArgs e)
    {
        if (_brandDown is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        Point p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _brandDown = null; BrandArea.ReleaseMouseCapture(); e.Handled = true;
        DragMove();
    }
    private void Brand_Up(object sender, MouseButtonEventArgs e)
    {
        bool click = _brandDown is not null && BrandArea.IsMouseOver;
        _brandDown = null; BrandArea.ReleaseMouseCapture(); e.Handled = true;
        if (click) ToggleCompact();
    }
    private void Brand_LostCapture(object sender, MouseEventArgs e) => _brandDown = null;
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
        try { Native.Raise(_hwnd); Activate(); Native.SetForegroundWindow(_hwnd); }
        finally { ApplyOwnPolicy(); }
        QueueRefresh();
    }
    private void OwnPin_Click(object sender, RoutedEventArgs e) { _store.Preferences.ManualTopmost = OwnPinBox.IsChecked == true; _store.SaveSoon(); ApplyOwnPolicy(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (!_compact) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    internal void ToggleCompact()
    {
        if (_quitting) return;
        AddPopup.IsOpen = false; CancelPicker();
        if (!_compact)
        {
            _expandedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _expandedState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            _compact = true; WindowState = WindowState.Normal;
            MinWidth = 180; MinHeight = 50; Width = 180; Height = 50;
            Workspace.Visibility = HeaderActions.Visibility = Visibility.Collapsed;
            BrandArea.Width = double.NaN; BrandArea.HorizontalAlignment = HorizontalAlignment.Stretch; BrandArea.ToolTip = "展开窗口";
            ResizeMode = ResizeMode.NoResize; DismissStatus();
        }
        else
        {
            _compact = false; ResizeMode = ResizeMode.CanResize;
            MinWidth = 880; MinHeight = 560;
            Left = _expandedBounds.Left; Top = _expandedBounds.Top; Width = _expandedBounds.Width; Height = _expandedBounds.Height;
            Workspace.Visibility = HeaderActions.Visibility = Visibility.Visible;
            BrandArea.Width = 128; BrandArea.HorizontalAlignment = HorizontalAlignment.Left; BrandArea.ToolTip = "收起为小窗";
            WindowState = _expandedState;
        }
        Native.FitToWorkArea(_hwnd); ApplyOwnPolicy();
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var p = _store.Preferences;
        p.Dark = DarkBox.IsChecked == true; p.CloseToTray = TrayBox.IsChecked != true;
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
                case "opacityUp": QueueOpacity(item, DesiredOpacity(item) + 10); break;
                case "opacityDown": QueueOpacity(item, DesiredOpacity(item) - 10); break;
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
        e.Cancel = true; SaveGeometry(); CancelPicker(); AddPopup.IsOpen = false;
        if (!_quitting && _store.Preferences.CloseToTray && _desktop is not null) { Hide(); ApplyOwnPolicy(); return; }
        await QuitAsync();
    }
    internal async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true; AddPopup.IsOpen = false;
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
