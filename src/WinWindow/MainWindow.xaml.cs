using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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
using NeatWin.App;

namespace WinWindow;

public partial class MainWindow : Window
{
    private const string PinActiveGeometry = "M5.2,2.2 L10.8,2.2 M6,2.2 L6,6.8 L3.6,9.5 L12.4,9.5 L10,6.8 L10,2.2 M8,9.5 L8,14.5";
    private const string PinInactiveGeometry = "M5.2,2.2 L10.8,2.2 M10,2.2 L10,6.8 L12.4,9.5 L9.6,9.5 M6,2.2 L6,4.4 M3.6,9.5 L6.8,9.5 M8,11.2 L8,14.5 M2.5,2.5 L13.5,13.5";
    private const string CollapseHintGeometry = "M2,7.5 L7.5,2 M7.5,2 L3.5,2 M7.5,2 L7.5,6";
    private const string ExpandHintGeometry = "M2,2 L7.5,7.5 M7.5,7.5 L3.5,7.5 M7.5,7.5 L7.5,3.5";

    private readonly StateStore _store;
    internal WindowManager Manager { get; }
    private readonly bool _testMode;
    private readonly ListCollectionView _view, _pickView;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _aggressiveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _opacityTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    private readonly Dictionary<WindowItem, int> _pendingOpacity = new();
    private readonly Dictionary<string, TextBox> _keyEditors;
    private DesktopIntegration? _desktop;
    private DesktopLayout? _layout;
    private FlashOverlay? _flash;
    private string LayoutDirectory => Path.Combine(_store.DirectoryPath, "Layout");
    private HwndSource? _source;
    private nint _hwnd;
    private bool _ready, _picking, _pickerDragged, _compact, _quitting, _allowClose, _searchPinnedOpen;
    private Point? _brandDown, _pickerStart;
    private UIElement? _pickerOwner;
    private readonly DispatcherTimer _pickerTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };
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
        _keyEditors = new()
        {
            ["arrange"] = ArrangeKeyBox, ["show"] = ShowKeyBox, ["pin"] = PinKeyBox,
            ["park"] = ParkKeyBox, ["recover"] = RecoverKeyBox,
            ["opacityUp"] = UpKeyBox, ["opacityDown"] = DownKeyBox
        };
        var p = store.Preferences;
        if (double.IsFinite(p.Width)) Width = Math.Max(MinWidth, p.Width);
        if (double.IsFinite(p.Height)) Height = Math.Max(MinHeight, p.Height);
        if (p.Left is double left && p.Top is double top && double.IsFinite(left) && double.IsFinite(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top;
        }
        OwnPinBox.IsChecked = p.ManualTopmost;
        SyncOwnPinGlyph();
        DarkBox.IsChecked = p.Dark; TrayBox.IsChecked = !p.CloseToTray; StartupBox.IsChecked = p.StartWithWindows;
        FocusPinBox.IsChecked = p.PinWhenFocused; AbsoluteBox.IsChecked = p.AbsoluteTopmost; AggressiveBox.IsChecked = p.AggressiveTopmost;
        AggressiveBox.IsEnabled = p.AbsoluteTopmost;
        foreach (var pair in _keyEditors) pair.Value.Text = p.Hotkeys.GetValueOrDefault(pair.Key, "");
        Palette.Apply(p.Dark);
        Manager.Changed += RefreshView; Manager.Notice += ShowStatus; store.Error += ShowStatus;
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await RunAsync(Manager.RefreshAsync); };
        _aggressiveTimer.Tick += (_, _) => { if (CanEnforceOwn() && Native.OverlappingTopmostAbove(_hwnd)) Native.Raise(_hwnd); };
        _opacityTimer.Tick += (_, _) => DrainOpacity();
        _pickerTimer.Tick += (_, _) => UpdatePickerOutline();
        _statusTimer.Tick += (_, _) => DismissStatus();
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await InitializeAsync();
        Activated += (_, _) => ApplyOwnPolicy();
        Deactivated += (_, _) => { if (!_picking) _flash?.Hide(); ApplyOwnPolicy(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { AddPopup.IsOpen = false; _flash?.Hide(); } ApplyOwnPolicy(); };
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastState = WindowState; else { AddPopup.IsOpen = false; _flash?.Hide(); } ApplyOwnPolicy(); };
        Closing += OnClosing;
        _ready = true; RefreshView();
    }

    private void SyncOwnPinGlyph()
    {
        OwnPinIcon.Data = Geometry.Parse(OwnPinBox.IsChecked == true ? PinActiveGeometry : PinInactiveGeometry);
    }

    private FlashOverlay Outline => _flash ??= new FlashOverlay();

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
        if (DesktopLayout.NeedsBackgroundSession(LayoutDirectory)) await LoadLayoutSettingsAsync();
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
        if (!_view.Cast<WindowItem>().SequenceEqual(Manager.Items.Where(x => FilterManaged(x)))) _view.Refresh();
        if (AddPopup.IsOpen && !_pickView.Cast<WindowItem>().SequenceEqual(Manager.Items.Where(x => FilterAvailable(x)))) _pickView.Refresh();

        var managedAll = Manager.Items.Where(x => x.IsManaged || x.Remembered).ToArray();
        int totalManaged = managedAll.Length;
        int pinnedCount = managedAll.Count(x => x.Pinned);
        int parkedCount = managedAll.Count(x => x.IsHidden || x.IsMinimized || x.Opacity == 0);

        EmptyStatePanel.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyListText.Text = SearchBox.Text.Length == 0 ? "暂无管理的窗口" : "没有匹配的窗口";
        EmptyHintText.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool anyCanUnpin = managedAll.Any(x => x.Pinned && !x.IsBusy);
        UnpinAllButton.IsEnabled = anyCanUnpin;

        bool shouldShowSearch = _searchPinnedOpen || SearchBox.Text.Length != 0;
        SearchBox.Visibility = shouldShowSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchToggleButton.IsChecked = shouldShowSearch;
        ManagedSummaryText.Visibility = shouldShowSearch ? Visibility.Collapsed : Visibility.Visible;
        ManagedSummaryText.Text = totalManaged == 0 ? "" : pinnedCount > 0 ? $"已管理 {totalManaged} · {pinnedCount} 置顶" : $"已管理 {totalManaged}";

        if (_compact)
        {
            BrandTitleText.Text = totalManaged == 0 ? "WinWindow"
                : parkedCount > 0 ? $"{pinnedCount} 置顶 · {parkedCount} 隐藏"
                : pinnedCount > 0 ? $"{pinnedCount} 置顶"
                : $"已管理 {totalManaged}";
        }
        else
        {
            BrandTitleText.Text = "WinWindow";
        }

        if (_pendingOpacity.Count != 0) _opacityTimer.Start();
    }

    private void SearchToggle_Click(object sender, RoutedEventArgs e)
    {
        _searchPinnedOpen = SearchToggleButton.IsChecked == true;
        if (!_searchPinnedOpen) SearchBox.Clear();
        RefreshView();
        if (SearchBox.Visibility == Visibility.Visible) SearchBox.Focus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _searchPinnedOpen = false;
        SearchBox.Clear();
        RefreshView();
        e.Handled = true;
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
            case "activate":
                await Manager.ActivateAsync(item);
                break;
            case "pin":
                bool nextPin = !item.Pinned;
                await Manager.SetListedPinAsync(item, nextPin);
                if (item.Hwnd != 0) Outline.Show(item.Hwnd, transient: true, muted: !nextPin);
                break;
            case "through": await Manager.SetClickThroughAsync(item, !item.ClickThrough); break;
            case "background": Manager.SetBackgroundEnabled(item, !item.BackgroundEnabled); break;
            case "park":
                if (item.IsMinimized || item.IsHidden)
                {
                    await Manager.RestoreParkingAsync(item);
                }
                else
                {
                    _flash?.Hide();
                    ShowStatus(await Manager.ParkAsync(item));
                }
                break;
            case "reset": _pendingOpacity.Remove(item); await Manager.ReleaseAsync(item); break;
            case "remove": _flash?.Hide(); _pendingOpacity.Remove(item); await Manager.RemoveAsync(item); break;
            case "remember": Manager.SetRemembered(item, !item.Remembered); break;
            case "copy": Clipboard.SetText($"{item.Title}\n{item.Details}\n{item.ProcessPath}"); ShowStatus("窗口信息已复制。"); break;
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
    { if ((sender as FrameworkElement)?.DataContext is WindowItem item) await AddTargetAsync(item.Hwnd, pin: false); }

    private async void AddPopup_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { AddPopup.IsOpen = false; AddButton.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && (PickList.SelectedItem as WindowItem ?? _pickView.Cast<WindowItem>().FirstOrDefault()) is { } item)
        { e.Handled = true; await AddTargetAsync(item.Hwnd, pin: false); }
    }

    private async void PickPinned_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is WindowItem item) await AddTargetAsync(item.Hwnd); }

    internal Task AddTargetAsync(nint hwnd, bool pin = true) => RunAsync(async () =>
    {
        AddPopup.IsOpen = false;
        var item = Manager.SelectTarget(hwnd);
        if (pin)
        {
            await Manager.SetPinAsync(item, true);
            Outline.Show(item.Hwnd, transient: true);
        }
        else Manager.AddToList(item);
        SearchBox.Clear(); RefreshView(); WindowList.ScrollIntoView(item);
    });

    private void Picker_Down(object sender, MouseButtonEventArgs e)
    {
        AddPopup.IsOpen = false;
        _picking = true;
        _pickerDragged = false;
        _pickerStart = e.GetPosition(this);
        _pickerOwner = sender as UIElement;
        _pickerOwner?.CaptureMouse();
        e.Handled = true;
    }

    private void Picker_Move(object sender, MouseEventArgs e)
    {
        if (!_picking || _pickerDragged || _pickerStart is not Point start) return;
        Point p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        e.Handled = true;
        _pickerDragged = true;
        Mouse.OverrideCursor = Cursors.Cross;
        UpdatePickerOutline();
        _pickerTimer.Start();
    }

    private void UpdatePickerOutline() => PreviewPickerTarget(Native.TargetUnderPointer());

    internal void PreviewPickerTarget(nint target)
    {
        if (target == 0) _flash?.Hide();
        else Outline.Show(target, transient: false);
    }

    private void CancelPicker()
    {
        _pickerTimer.Stop();
        _picking = false;
        _pickerDragged = false;
        _pickerStart = null;
        Mouse.OverrideCursor = null;
        if (_pickerOwner is { IsMouseCaptured: true } owner) owner.ReleaseMouseCapture();
        _pickerOwner = null;
        _flash?.Hide();
    }

    private void Picker_LostCapture(object sender, MouseEventArgs e) { if (_picking) CancelPicker(); }

    private async void Picker_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_picking) return;
        bool dragged = _pickerDragged;
        nint target = dragged ? Native.TargetUnderPointer() : 0;
        CancelPicker();
        e.Handled = true;
        if (!dragged)
        {
            ShowStatus("按住「拖拽置顶」并拖向目标窗口后松开即可置顶。");
            return;
        }
        if (target != 0) await AddTargetAsync(target, pin: true);
    }

    private void DragPicker_Click(object sender, RoutedEventArgs e) => ShowStatus("按住「拖拽置顶」并拖向目标窗口后松开即可置顶。");
    private async void UnpinAll_Click(object sender, RoutedEventArgs e) => await RunAsync(Manager.UnpinAllAsync);
    private async void RecoverAll_Click(object sender, RoutedEventArgs e) => await RecoverAllAsync();

    private Task RecoverAllAsync()
    {
        _pendingOpacity.Clear();
        return RunAsync(async () => { int failures = await Manager.RestoreAllAsync(); ShowStatus(failures == 0 ? "已恢复全部窗口原样。" : $"{failures} 个窗口未恢复，记录已保留。"); });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(Manager.RefreshAsync);

    private DesktopLayout EnsureLayout() => _layout ??= new DesktopLayout(LayoutDirectory, message =>
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => ShowStatus(message)));
    }, status =>
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!_quitting) DisplayRecorder(status); }));
    }, available =>
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
        {
            UndoArrangeButton.IsEnabled = available;
            _desktop?.SetUndoAvailable(available);
        }));
    });

    private async void Arrange_Click(object sender, RoutedEventArgs e) => await RunAsync(() => EnsureLayout().ArrangeAsync());
    private async void UndoLayout_Click(object sender, RoutedEventArgs e) => await RunAsync(() => EnsureLayout().UndoAsync());

    private void More_Click(object sender, RoutedEventArgs e)
    { var button = (Button)sender; button.ContextMenu.PlacementTarget = button; button.ContextMenu.Placement = PlacementMode.Bottom; button.ContextMenu.IsOpen = true; }

    private void ShowSettings(bool show)
    {
        AddPopup.IsOpen = false;
        _flash?.Hide();
        WindowPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) SelectSettingsSection();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings(SettingsPage.Visibility != Visibility.Visible);
    private void Back_Click(object sender, RoutedEventArgs e) => ShowSettings(false);
    private void CompactSettings_Click(object sender, RoutedEventArgs e) { if (_compact) ToggleCompact(); ShowSettings(true); }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !_compact && SettingsPage.Visibility != Visibility.Visible)
        {
            _searchPinnedOpen = true;
            RefreshView();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
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

    private void OwnPin_Click(object sender, RoutedEventArgs e)
    {
        _store.Preferences.ManualTopmost = OwnPinBox.IsChecked == true;
        SyncOwnPinGlyph();
        _store.SaveSoon();
        ApplyOwnPolicy();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (!_compact) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal void ToggleCompact()
    {
        if (_quitting) return;
        AddPopup.IsOpen = false; CancelPicker(); _flash?.Hide();
        if (!_compact)
        {
            _expandedBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _expandedState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            _compact = true; WindowState = WindowState.Normal;
            MinWidth = 240; MinHeight = 46; Width = 240; Height = 46;
            Workspace.Visibility = HeaderActions.Visibility = Visibility.Collapsed;
            CompactActions.Visibility = Visibility.Visible;
            BrandHintIcon.Data = Geometry.Parse(ExpandHintGeometry);
            BrandArea.ToolTip = "点击展开主面板，按住拖动胶囊";
            ShellFrame.CornerRadius = new CornerRadius(14);
            ResizeMode = ResizeMode.NoResize; DismissStatus();
        }
        else
        {
            _compact = false; ResizeMode = ResizeMode.CanResize;
            MinWidth = 500; MinHeight = 380;
            Left = _expandedBounds.Left; Top = _expandedBounds.Top;
            Width = Math.Max(MinWidth, _expandedBounds.Width);
            Height = Math.Max(MinHeight, _expandedBounds.Height);
            Workspace.Visibility = HeaderActions.Visibility = Visibility.Visible;
            CompactActions.Visibility = Visibility.Collapsed;
            BrandHintIcon.Data = Geometry.Parse(CollapseHintGeometry);
            BrandArea.ToolTip = "点击收起为悬浮胶囊，按住拖动窗口";
            ShellFrame.CornerRadius = new CornerRadius(12);
            WindowState = _expandedState;
        }
        RefreshView();
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
        e.Handled = true;
        var box = (TextBox)sender;
        string action = _keyEditors.First(x => x.Value == box).Key;
        string previous = _store.Preferences.Hotkeys.GetValueOrDefault(action, "");
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { box.Text = previous; return; }
        if ((key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            box.Clear();
            return;
        }
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

    internal bool CaptureRegisteredHotkey(string chord)
    {
        var editor = _keyEditors.Values.FirstOrDefault(box => box.IsKeyboardFocusWithin);
        if (editor is null) return false;
        editor.Text = chord;
        return true;
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        var defaults = Preferences.DefaultHotkeys();
        foreach (var pair in _keyEditors) pair.Value.Text = defaults.GetValueOrDefault(pair.Key, "");
    }

    private void ApplyHotkeys_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var keys = _keyEditors.ToDictionary(x => x.Key, x => x.Value.Text.Trim());
        var used = new HashSet<(uint Modifiers, uint Key)>();
        foreach (string value in keys.Values)
        {
            var chord = DesktopIntegration.ParseHotkey(value);
            if (chord.Key != 0 && !used.Add(chord))
                throw new ArgumentException($"两个操作使用了 {value}，请调整后再应用。");
        }
        _desktop?.ApplyHotkeys(keys);
        _store.Preferences.Hotkeys = keys;
        _store.SavePreferences();
        ShowStatus("快捷键已应用。");
    });

    private async Task OnDesktopActionAsync(string action)
    {
        if (_quitting) return;
        if (action == "show") { ShowShell(); return; }
        if (action == "compact") { ShowShell(); ToggleCompact(); return; }
        if (action == "quit") { await QuitAsync(); return; }
        if (action == "arrange") { await RunAsync(() => EnsureLayout().ArrangeAsync()); return; }
        if (action == "undoLayout") { await RunAsync(() => EnsureLayout().UndoAsync()); return; }
        if (action == "recover") { await RecoverAllAsync(); return; }
        await RunAsync(async () =>
        {
            nint foreground = Native.GetForegroundWindow();
            var item = Native.IsTarget(foreground) ? Manager.SelectTarget(foreground) : Selected;
            if (item is null || !item.IsAvailable) return;
            switch (action)
            {
                case "pin":
                    bool nextPin = !Native.IsTopmost(item.Hwnd);
                    await Manager.SetPinAsync(item, nextPin);
                    Outline.Show(item.Hwnd, transient: true, muted: !nextPin);
                    break;
                case "park":
                    _flash?.Hide();
                    ShowStatus(await Manager.ParkAsync(item));
                    break;
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
        e.Cancel = true; SaveGeometry(); CancelPicker(); AddPopup.IsOpen = false; _flash?.Hide();
        if (!_quitting && _store.Preferences.CloseToTray && _desktop is not null) { Hide(); ApplyOwnPolicy(); return; }
        await QuitAsync();
    }

    internal async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true; _layout?.Dispose(); AddPopup.IsOpen = false; _flash?.Dispose(); _flash = null;
        _refreshTimer.Stop(); _aggressiveTimer.Stop(); _opacityTimer.Stop(); _pendingOpacity.Clear();
        CancelPicker(); SaveGeometry(); ApplyOwnPolicy();
        int failures = await Manager.ShutdownAsync();
        if (failures != 0) _desktop?.Notify($"{failures} 个窗口未恢复，记录已保留，下次启动继续恢复。");
        _allowClose = true; Cleanup(); Application.Current.Shutdown();
    }

    internal void Cleanup()
    {
        _pickerTimer.Stop(); _refreshTimer.Stop(); _aggressiveTimer.Stop(); _opacityTimer.Stop(); _statusTimer.Stop();
        _flash?.Dispose(); _flash = null;
        _layout?.Dispose(); _layout = null;
        _desktop?.Dispose(); _desktop = null;
        _source?.RemoveHook(WindowMessage); _source = null;
        _store.Error -= ShowStatus; Manager.Notice -= ShowStatus; Manager.Changed -= RefreshView;
    }

    // The drag picker and completed pin actions share one non-activating outline.
    private sealed class FlashOverlay : IDisposable
    {
        private readonly Window _window;
        private readonly Border _frame;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(460) };
        private nint _hwnd;
        private bool _disposed;
        private bool _muted;

        public FlashOverlay()
        {
            _frame = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(3), Margin = new Thickness(2), IsHitTestVisible = false };
            _frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            _window = new Window
            {
                Title = "",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                Focusable = false,
                IsHitTestVisible = false,
                Width = 32,
                Height = 32,
                Content = _frame
            };
            _window.SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(_window).Handle;
                Native.ConfigureOverlay(_hwnd);
            };
            _timer.Tick += (_, _) => Hide();
        }

        public void Show(nint targetHwnd, bool transient, bool muted = false)
        {
            if (_disposed || targetHwnd == 0 || !Native.TryGetFrameBounds(targetHwnd, out var rect))
            {
                Hide();
                return;
            }
            _timer.Stop();
            if (_muted != muted)
            {
                _muted = muted;
                _frame.SetResourceReference(Border.BorderBrushProperty, muted ? "Muted" : "Accent");
            }
            if (_hwnd == 0) new WindowInteropHelper(_window).EnsureHandle();
            const int inset = 5;
            var bounds = new Native.Rect { Left = rect.Left - inset, Top = rect.Top - inset, Right = rect.Right + inset, Bottom = rect.Bottom + inset };
            // Both DWM and SetWindowPos use desktop pixels in our per-monitor-aware process.
            if (!_window.IsVisible)
            {
                Native.PositionOutline(_hwnd, bounds);
                _window.Show();
            }
            if (!Native.GetWindowRect(_hwnd, out var current) || !bounds.Equals(current) || !Native.IsTopmost(_hwnd))
                Native.PositionOutline(_hwnd, bounds);
            if (transient) _timer.Start();
        }

        public void Hide()
        {
            _timer.Stop();
            if (!_disposed && _window.IsVisible) _window.Hide();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            try { _window.Close(); } catch (InvalidOperationException) { }
        }
    }
}
