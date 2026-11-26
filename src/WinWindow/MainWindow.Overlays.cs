using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinWindow;

public partial class MainWindow
{
    private const string QuickOpacityGeometry = "M8,2 A6,6 0 1 0 8,14 A6,6 0 1 0 8,2 M8,2.4 L8,13.6 M8,5.2 L12.6,5.2 M8,8 L13.4,8 M8,10.8 L12.6,10.8";

    private readonly Dictionary<nint, TitleBarOverlay> _titleOverlays = new();
    private readonly Dictionary<WindowItem, CollapsedIconOverlay> _collapsedOverlays = new();
    private readonly Dictionary<WindowItem, (int X, int Y)> _collapsedIconPositions = new();
    private readonly HashSet<WindowItem> _collapsingIcons = new();
    private readonly DispatcherTimer _overlayTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(220) };
    private Native.EventProc? _titleLocationCallback;
    private nint _titleLocationHook;
    private bool _pickerPreviewActive;
    private readonly DispatcherTimer _launchAvailabilityTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromSeconds(1) };

    private void InitializeOverlays()
    {
        _overlayTimer.Tick += (_, _) => SyncManagedOverlays();
        _launchAvailabilityTimer.Tick += async (_, _) => await RunAsync(Manager.RefreshLaunchAvailabilityAsync);
    }

    private void QuickOpacity_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitQuickOpacity();
            QuickOpacityBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            QuickOpacityBox.Text = _store.Preferences.QuickOpacity.ToString();
        }
    }

    private void QuickOpacity_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitQuickOpacity();

    private void CommitQuickOpacity()
    {
        if (!_ready || _quitting) return;
        if (!int.TryParse(QuickOpacityBox.Text.Trim(), out int value) || value is < 1 or > 99)
        {
            QuickOpacityBox.Text = _store.Preferences.QuickOpacity.ToString();
            ShowStatus(UiText.Format("IntegerRange", 1, 99));
            return;
        }
        if (_store.Preferences.QuickOpacity == value)
        {
            QuickOpacityBox.Text = value.ToString();
            return;
        }
        _store.Preferences.QuickOpacity = value;
        QuickOpacityBox.Text = value.ToString();
        _store.SaveSoon();
        SyncManagedOverlays();
    }

    internal void ToggleQuickOpacity(WindowItem item)
    {
        if (!item.IsAvailable) return;
        int target = Math.Clamp(_store.Preferences.QuickOpacity, 1, 99);
        int current = DesiredOpacity(item);
        int next;
        // 若当前不等于预设值，记下切换前的百分比以便再次点击还原；若本就处于预设值，则回退到原百分比或 100%。
        if (current != target)
        {
            item.OpacityBeforeQuickToggle = current;
            next = target;
        }
        else
        {
            next = item.OpacityBeforeQuickToggle is int prev && prev > 0 && prev != target ? prev : 100;
            item.OpacityBeforeQuickToggle = null;
        }
        QueueOpacity(item, next);
        SyncManagedOverlays();
    }

    private async Task CollapseManagedWindowAsync(WindowItem item)
    {
        if (!item.CanOperate || !Native.TryGetFrameBounds(item.Hwnd, out var frame) || !_collapsingIcons.Add(item)) return;
        try
        {
            _collapsedIconPositions[item] = FindCollapsedIconPosition(item, frame);
            // 建立可点击的恢复入口，再改动外部窗口。任何一步失败都保留原窗口或列表的恢复入口。
            SyncManagedOverlays();
            if (!_collapsedOverlays.TryGetValue(item, out var badge) || !badge.IsVisible || !Native.IsTopmost(badge.Handle))
                throw new InvalidOperationException(UiText.Get("CollapseRejected"));
            await Manager.CollapseToIconAsync(item);
        }
        finally
        {
            _collapsingIcons.Remove(item);
            SyncManagedOverlays();
        }
    }

    private (int X, int Y) FindCollapsedIconPosition(WindowItem item, Native.Rect frame)
    {
        var size = CollapsedIconOverlay.WindowSizeDip;
        int inset = (int)Math.Round(6 * Native.WindowDpi(item.Hwnd) / 96.0);
        var anchor = new Native.Point { X = frame.Left + inset, Y = frame.Top + inset };
        var first = Native.OverlayBounds(item.Hwnd, anchor, size, size);
        var others = _collapsedOverlays.Where(pair => pair.Key != item).Select(pair => pair.Value.VisibleBounds)
            .OfType<Native.Rect>().ToArray();
        if (!others.Any(other => Native.Overlaps(first, other))) return (first.Left, first.Top);
        int step = first.Right - first.Left + inset;
        for (int i = 1; i <= others.Length; i++)
        {
            foreach (var candidate in new[]
            {
                new Native.Point { X = first.Left + i * step, Y = first.Top },
                new Native.Point { X = first.Left - i * step, Y = first.Top },
                new Native.Point { X = first.Left, Y = first.Top + i * step },
                new Native.Point { X = first.Left, Y = first.Top - i * step }
            })
            {
                var bounds = Native.OverlayBounds(item.Hwnd, candidate, size, size);
                if (!others.Any(other => Native.Overlaps(bounds, other))) return (bounds.Left, bounds.Top);
            }
        }
        return (first.Left, first.Top);
    }

    internal bool HasTitleOverlay(WindowItem item) =>
        _titleOverlays.TryGetValue(item.Hwnd, out var overlay) && overlay.IsVisible;

    internal nint GetTitleOverlayHandle(WindowItem item) =>
        _titleOverlays.TryGetValue(item.Hwnd, out var overlay) ? overlay.Handle : 0;

    internal bool MoveTitleOverlayForTest(WindowItem item, int deltaX) =>
        _titleOverlays.TryGetValue(item.Hwnd, out var overlay) && overlay.MoveHorizontally(deltaX);

    internal bool MovePressedTitleOverlayForTest(WindowItem item, int deltaX) =>
        _titleOverlays.TryGetValue(item.Hwnd, out var overlay) && overlay.MovePressedForTest(deltaX);

    internal void PauseOverlayPollingForTest() => _overlayTimer.Stop();

    internal bool HasCollapsedIconOverlay(WindowItem item) =>
        _collapsedOverlays.TryGetValue(item, out var overlay) && overlay.IsVisible;

    internal nint GetCollapsedIconOverlayHandle(WindowItem item) =>
        _collapsedOverlays.TryGetValue(item, out var overlay) ? overlay.Handle : 0;

    internal bool MoveCollapsedIconForTest(WindowItem item, int x, int y) =>
        _collapsedOverlays.TryGetValue(item, out var overlay) && overlay.MoveAndDropForTest(x, y);

    private Task ExpandManagedWindowAsync(WindowItem item)
    {
        Native.Rect? movedBadge = _collapsedOverlays.TryGetValue(item, out var overlay) && overlay.UserMoved
            ? overlay.VisibleBounds : null;
        return Manager.RestoreParkingAsync(item, movedBadge: movedBadge);
    }

    private void UpdateTitleLocationHook(bool enabled)
    {
        if (enabled == (_titleLocationCallback is not null)) return;
        if (enabled)
        {
            _titleLocationCallback = FollowTargetLocation;
            _titleLocationHook = Native.HookExternalWindowLocations(_titleLocationCallback);
            // 系统钩子不可用时退回原有频率，窗口移动仍可得到跟随。
            _overlayTimer.Interval = TimeSpan.FromMilliseconds(_titleLocationHook == 0 ? 45 : 220);
        }
        else
        {
            if (_titleLocationHook != 0) Native.UnhookWinEvent(_titleLocationHook);
            _titleLocationHook = 0;
            _titleLocationCallback = null;
            _overlayTimer.Interval = TimeSpan.FromMilliseconds(220);
        }
    }

    private void FollowTargetLocation(nint hook, uint evt, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_quitting || !_ready || _picking || _pickerPreviewActive || objectId != 0 || childId != 0 ||
            !_titleOverlays.TryGetValue(hwnd, out var overlay)) return;
        var item = overlay.Target;
        if (!item.IsManaged || !item.IsAvailable || item.IsIconCollapsed || _collapsingIcons.Contains(item) ||
            item.ClickThrough || DesiredOpacity(item) == 0 || item.Identity is not { } identity ||
            Native.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid != (uint)identity.ProcessId) return;
        // 位置事件由注册钩子的 UI 线程接收，只更新命中的浮条。置顶和按键内容继续由常规同步处理。
        overlay.FollowPosition();
    }

    private void SyncManagedOverlays()
    {
        if (_quitting || !_ready)
        {
            DisposeManagedOverlays();
            return;
        }

        // 等工作台建立句柄后再挂载，避免无头初始化时先创建浮条。
        if (_hwnd == 0) return;
        _launchAvailabilityTimer.IsEnabled = IsVisible && WindowState != WindowState.Minimized &&
            Manager.Items.Any(item => !item.IsAvailable);

        var activeItems = Manager.Items
            .Where(x => x.IsManaged && x.IsAvailable && x.Identity is not null)
            .ToHashSet();

        foreach (var stale in _titleOverlays.Values.Where(bar => !activeItems.Contains(bar.Target)).ToArray())
        {
            _titleOverlays.Remove(stale.Target.Hwnd);
            stale.Dispose();
        }
        foreach (var stale in _collapsedOverlays.Keys.Where(k => !activeItems.Contains(k) || (!k.IsIconCollapsed && !_collapsingIcons.Contains(k))).ToArray())
        {
            _collapsedOverlays[stale].Dispose();
            _collapsedOverlays.Remove(stale);
        }

        foreach (var stale in _collapsedIconPositions.Keys.Where(item => !activeItems.Contains(item) ||
            (!item.IsIconCollapsed && !_collapsingIcons.Contains(item))).ToArray())
            _collapsedIconPositions.Remove(stale);

        if (activeItems.Count == 0)
        {
            _overlayTimer.Stop();
            UpdateTitleLocationHook(false);
            return;
        }

        bool suppressTitleBars = _picking || _pickerPreviewActive;
        int quickOpacity = Math.Clamp(_store.Preferences.QuickOpacity, 1, 99);

        foreach (var item in activeItems)
        {
            if (item.IsIconCollapsed || _collapsingIcons.Contains(item))
            {
                if (_titleOverlays.TryGetValue(item.Hwnd, out var bar)) bar.Hide();
                if (!_collapsedOverlays.TryGetValue(item, out var iconOverlay))
                {
                    iconOverlay = new CollapsedIconOverlay(this, item);
                    _collapsedOverlays[item] = iconOverlay;
                }
                var initialPos = _collapsedIconPositions.TryGetValue(item, out var pos) ? pos : ((int X, int Y)?)null;
                iconOverlay.ShowAndSync(initialPos, _store.Preferences.AggressiveTopmost,
                    moved => _collapsedIconPositions[item] = moved);
                continue;
            }

            int currentOpacity = DesiredOpacity(item);
            if (suppressTitleBars || item.IsMinimized || item.IsHidden || item.ClickThrough || currentOpacity == 0 ||
                item.Identity is not { } identity || !Native.Matches(identity))
            {
                if (_titleOverlays.TryGetValue(item.Hwnd, out var bar)) bar.Hide();
                continue;
            }

            if (!_titleOverlays.TryGetValue(item.Hwnd, out var titleOverlay))
            {
                titleOverlay = new TitleBarOverlay(this, item);
                _titleOverlays[item.Hwnd] = titleOverlay;
            }

            titleOverlay.Update(quickOpacity, currentOpacity);
        }

        UpdateTitleLocationHook(_titleOverlays.Count != 0);
        if (!_overlayTimer.IsEnabled) _overlayTimer.Start();
    }

    private void DisposeManagedOverlays()
    {
        _overlayTimer.Stop();
        UpdateTitleLocationHook(false);
        _launchAvailabilityTimer.Stop();
        foreach (var bar in _titleOverlays.Values) bar.Dispose();
        _titleOverlays.Clear();
        foreach (var icon in _collapsedOverlays.Values) icon.Dispose();
        _collapsedOverlays.Clear();
        _collapsedIconPositions.Clear();
    }

    private sealed class TitleBarOverlay : IDisposable
    {
        private const int WidthDip = 78;
        private const int HeightDip = 26;
        private const int DragThresholdDip = 4;

        private readonly MainWindow _owner;
        private readonly WindowItem _item;
        private readonly Window _window;
        private readonly Image _iconImage;
        private readonly TextBlock _initialText;
        private readonly Button _collapseButton;
        private readonly ToggleButton _pinButton;
        private readonly Path _pinPath;
        private readonly ToggleButton _opacityButton;
        private readonly Border _toolbar;
        private HwndSource? _source;
        private nint _hwnd;
        private bool _syncing, _disposed;
        private Native.Rect _track;
        private Native.Rect _lastBarBounds;
        private int _barWidthPixels;
        private double _scale = 1;
        private double _horizontalOffsetDip;
        private int? _dragCursorX;
        private bool _dragging;
        private DispatcherTimer? _dragTimer;

        public nint Handle => _hwnd;
        public bool IsVisible => !_disposed && _window.IsVisible;
        public WindowItem Target => _item;

        public TitleBarOverlay(MainWindow owner, WindowItem item)
        {
            _owner = owner;
            _item = item;

            _iconImage = new Image { Width = 15, Height = 15 };
            RenderOptions.SetBitmapScalingMode(_iconImage, BitmapScalingMode.HighQuality);
            _initialText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _initialText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

            var iconHost = new Grid { Width = 16, Height = 16 };
            iconHost.Children.Add(_iconImage);
            iconHost.Children.Add(_initialText);

            _collapseButton = new Button
            {
                Width = 22,
                Height = 20,
                Margin = new Thickness(0, 0, 2, 0),
                Content = iconHost
            };
            _collapseButton.SetResourceReference(FrameworkElement.StyleProperty, "RowIconButton");
            _collapseButton.Click += async (_, _) =>
            {
                if (!_syncing && !_disposed) await _owner.ExecuteRowAsync(_item, "collapseIcon");
            };

            _pinPath = new Path
            {
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 16
            };
            _pinPath.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1)
            });

            _pinButton = new ToggleButton
            {
                Width = 22,
                Height = 20,
                Margin = new Thickness(0, 0, 2, 0),
                Content = new Viewbox { Width = 13, Height = 13, Child = _pinPath }
            };
            _pinButton.SetResourceReference(FrameworkElement.StyleProperty, "IconToggle");
            _pinButton.Click += async (_, _) =>
            {
                if (!_syncing && !_disposed) await _owner.ExecuteRowAsync(_item, "pin");
            };

            var opacityPath = new Path
            {
                Data = Geometry.Parse(QuickOpacityGeometry),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 16
            };
            opacityPath.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1)
            });

            _opacityButton = new ToggleButton
            {
                Width = 22,
                Height = 20,
                Content = new Viewbox { Width = 13, Height = 13, Child = opacityPath }
            };
            _opacityButton.SetResourceReference(FrameworkElement.StyleProperty, "IconToggle");
            _opacityButton.Click += (_, _) =>
            {
                if (!_syncing && !_disposed) _owner.ToggleQuickOpacity(_item);
            };

            var barPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            barPanel.Children.Add(_collapseButton);
            barPanel.Children.Add(_pinButton);
            barPanel.Children.Add(_opacityButton);

            _toolbar = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3, 2, 3, 2),
                Cursor = Cursors.Arrow,
                ForceCursor = true,
                Child = barPanel
            };
            _toolbar.SetResourceReference(Border.BackgroundProperty, "Panel");
            _toolbar.SetResourceReference(Border.BorderBrushProperty, "Line");
            _toolbar.PreviewMouseLeftButtonDown += OnToolbarMouseDown;
            _toolbar.PreviewMouseMove += OnToolbarMouseMove;
            _toolbar.PreviewMouseLeftButtonUp += OnToolbarMouseUp;

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
                Width = WidthDip,
                Height = HeightDip,
                Content = _toolbar
            };
            _window.SourceInitialized += OnSourceInitialized;
            _item.PropertyChanged += OnItemPropertyChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            Native.ConfigureInteractiveOverlay(_hwnd);
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_disposed && e.PropertyName is nameof(WindowItem.Icon) or nameof(WindowItem.Pinned) or nameof(WindowItem.Opacity))
                SyncContent(_owner._store.Preferences.QuickOpacity, _owner.DesiredOpacity(_item));
        }

        private void OnToolbarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!Native.GetCursorPos(out var cursor)) return;
            _dragCursorX = cursor.X;
            _dragging = false;
            if (_dragTimer is null)
            {
                _dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
                _dragTimer.Tick += OnDragTick;
            }
            _dragTimer.Start();
        }

        private void OnDragTick(object? sender, EventArgs e)
        {
            if (!Native.IsPrimaryMouseButtonDown())
            {
                // 按钮仍持有捕获时等它收到松手事件，避免提前把一次快速横拖判成点击。
                _dragTimer?.Stop();
                if (_dragging || !_toolbar.IsMouseCaptureWithin) StopDrag();
                return;
            }
            if (Native.GetCursorPos(out var cursor)) UpdateDragPosition(cursor.X);
        }

        private void OnToolbarMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragCursorX is null || e.LeftButton != MouseButtonState.Pressed) return;
            if (Native.GetCursorPos(out var cursor)) UpdateDragPosition(cursor.X);
            if (_dragging) e.Handled = true;
        }

        private void OnToolbarMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragCursorX is null) return;
            if (Native.GetCursorPos(out var cursor)) UpdateDragPosition(cursor.X);
            if (_dragging)
            {
                e.Handled = true;
                if (Mouse.Captured is UIElement captured && _toolbar.IsAncestorOf(captured))
                    captured.ReleaseMouseCapture();
            }
            StopDrag();
        }

        private void UpdateDragPosition(int cursorX)
        {
            if (_dragCursorX is not int previousX || cursorX == previousX) return;
            int deltaX = cursorX - previousX;
            if (!_dragging)
            {
                if (Math.Abs(deltaX) < Math.Max(1, (int)Math.Ceiling(DragThresholdDip * _scale))) return;
                // 只在越过水平阈值后接管捕获。轻微抖动交给按钮的点击逻辑。
                _dragging = true;
                _toolbar.CaptureMouse();
            }
            _dragCursorX = cursorX;
            MoveHorizontally(deltaX);
        }

        internal bool MovePressedForTest(int deltaX)
        {
            if (_dragCursorX is not int x) return false;
            UpdateDragPosition(x + deltaX);
            return _dragging;
        }

        private void StopDrag()
        {
            _dragTimer?.Stop();
            _dragCursorX = null;
            _dragging = false;
            if (_toolbar.IsMouseCaptured) _toolbar.ReleaseMouseCapture();
        }

        private int MaxOffset => _track.Right - _track.Left - _barWidthPixels;
        private int OffsetPixels => Math.Clamp((int)Math.Round(_horizontalOffsetDip * _scale), 0, MaxOffset);

        public bool MoveHorizontally(int deltaX)
        {
            if (!IsVisible || _barWidthPixels == 0) return false;
            int previous = OffsetPixels;
            int next = (int)Math.Clamp((long)previous + deltaX, 0, MaxOffset);
            if (next == previous) return true;
            // 跟鼠标事件直接移动独立浮窗。只改 X，Y 保持贴在所属窗口的标题栏。
            if (!MoveTo(BarBounds(next))) return false;
            _horizontalOffsetDip = next / _scale;
            return true;
        }

        private Native.Rect BarBounds(int offset)
        {
            var bounds = _track;
            bounds.Left += offset;
            bounds.Right = bounds.Left + _barWidthPixels;
            return bounds;
        }

        private bool MoveTo(Native.Rect bounds)
        {
            if (!Native.MoveWindowControl(_hwnd, bounds.Left, bounds.Top)) return false;
            _lastBarBounds = bounds;
            return true;
        }

        private static nint WndProc(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
        {
            // WM_MOUSEACTIVATE -> MA_NOACTIVATE (3)，点击顶栏按钮时不抢走被管理窗口或其他前台应用的键盘焦点。
            if (msg == 0x0021)
            {
                handled = true;
                return 3;
            }
            return 0;
        }

        private void SyncContent(int quickOpacity, int currentOpacity)
        {
            _syncing = true;
            try
            {
                var icon = _item.Icon;
                _iconImage.Source = icon;
                _iconImage.Visibility = icon is not null ? Visibility.Visible : Visibility.Collapsed;
                _initialText.Text = _item.Initial;
                _initialText.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
                _collapseButton.ToolTip = UiText.Get("CollapseToIcon");

                _pinButton.IsChecked = _item.Pinned;
                _pinButton.IsEnabled = !_item.IsBusy;
                _pinButton.ToolTip = _item.Pinned ? UiText.Get("UnpinKeepListed") : UiText.Get("PinWindow");
                _pinPath.Data = Geometry.Parse(_item.Pinned ? PinActiveGeometry : PinInactiveGeometry);

                bool isQuickActive = currentOpacity == quickOpacity;
                _opacityButton.IsChecked = isQuickActive;
                _opacityButton.IsEnabled = !_item.IsBusy;
                string opacityAction = UiText.Get(isQuickActive ? "RestoreQuickOpacity" : "ToggleQuickOpacity");
                _opacityButton.ToolTip = opacityAction;
                System.Windows.Automation.AutomationProperties.SetName(_opacityButton, opacityAction);
            }
            finally { _syncing = false; }
        }

        private bool TryGetPlacement(out Native.Rect bounds)
        {
            bounds = default;
            double scale = Native.WindowDpi(_item.Hwnd) / 96.0;
            int widthPx = (int)Math.Ceiling(WidthDip * scale);
            int heightPx = (int)Math.Ceiling(HeightDip * scale);
            if (!Native.TryGetTitleBarOverlayTrack(_item.Hwnd, widthPx, heightPx, scale, out var track))
                return false;
            _track = track;
            _barWidthPixels = widthPx;
            _scale = scale;
            bounds = BarBounds(OffsetPixels);
            return true;
        }

        public void FollowPosition()
        {
            if (_disposed || _hwnd == 0) return;
            if (!TryGetPlacement(out var bounds))
            {
                Hide();
                return;
            }
            if (!_window.IsVisible)
            {
                if (_item.Identity is { } identity && Native.Matches(identity))
                    Update(_owner._store.Preferences.QuickOpacity, _owner.DesiredOpacity(_item));
                return;
            }

            // 跨 DPI 时先用完整定位调整外框。同尺寸移动只提交坐标，不重新布局或改变层级。
            var previous = _lastBarBounds;
            if (bounds.Right - bounds.Left != previous.Right - previous.Left ||
                bounds.Bottom - bounds.Top != previous.Bottom - previous.Top)
            {
                if (!Position(bounds)) Hide();
            }
            else if (bounds.Left != previous.Left || bounds.Top != previous.Top)
            {
                if (!MoveTo(bounds)) Hide();
            }
        }

        public void Update(int quickOpacity, int currentOpacity)
        {
            if (_disposed || _item.Hwnd == 0)
            {
                Hide();
                return;
            }

            if (_hwnd == 0) new WindowInteropHelper(_window).EnsureHandle();

            if (!TryGetPlacement(out var bounds) || !Position(bounds))
            {
                Hide();
                return;
            }

            SyncContent(quickOpacity, currentOpacity);
            if (_window.IsVisible) return;
            _window.Show();
            if (!Position(bounds)) Hide();
        }

        private bool Position(Native.Rect bounds)
        {
            if (!Native.PositionWindowControl(_hwnd, _item.Hwnd, bounds)) return false;
            _lastBarBounds = bounds;
            return true;
        }

        public void SyncLayer()
        {
            if (IsVisible && _item.Identity is { } identity &&
                Native.GetWindowThreadProcessId(identity.Hwnd, out uint pid) != 0 && pid == (uint)identity.ProcessId)
                Native.PositionWindowControl(_hwnd, identity.Hwnd);
        }

        public void Hide()
        {
            StopDrag();
            if (!_disposed && _window.IsVisible) _window.Hide();
        }

        public void Dispose()
        {
            if (_disposed) return;
            StopDrag();
            _disposed = true;
            _item.PropertyChanged -= OnItemPropertyChanged;
            _source?.RemoveHook(WndProc);
            _source = null;
            try { _window.Close(); } catch (InvalidOperationException) { }
        }
    }

    private sealed class CollapsedIconOverlay : IDisposable
    {
        internal const int WindowSizeDip = 50;
        private const int IconSizeDip = 32;
        private const int CornerRadiusDip = 8;

        private readonly MainWindow _owner;
        private readonly WindowItem _item;
        private readonly Window _window;
        private readonly Border _card;
        private readonly Image _iconImage;
        private readonly TextBlock _initialText;
        private readonly MenuItem _expandMenu;
        private readonly MenuItem _resetMenu;
        private readonly MenuItem _removeMenu;
        private HwndSource? _source;
        private nint _hwnd;
        private bool _disposed, _placed, _capturing, _dragged;
        private bool _systemCorners;
        private (int Width, int Height, uint Dpi) _clipSize;
        private (int X, int Y) _lastDragPosition;
        private Native.Point _downCursor;
        private Native.Rect _downRect;
        private Action<(int X, int Y)>? _savePosition;

        public nint Handle => _hwnd;
        public bool IsVisible => !_disposed && _window.IsVisible;
        public bool UserMoved { get; private set; }
        public Native.Rect? VisibleBounds => IsVisible && Native.GetWindowRect(_hwnd, out var rect) ? rect : null;

        public CollapsedIconOverlay(MainWindow owner, WindowItem item)
        {
            _owner = owner;
            _item = item;

            _iconImage = new Image { Width = IconSizeDip, Height = IconSizeDip };
            RenderOptions.SetBitmapScalingMode(_iconImage, BitmapScalingMode.HighQuality);

            _initialText = new TextBlock
            {
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _initialText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

            var innerHost = new Grid();
            innerHost.Children.Add(_iconImage);
            innerHost.Children.Add(_initialText);

            var innerPlate = new Border
            {
                Margin = new Thickness(1.5),
                CornerRadius = new CornerRadius(5.5),
                BorderThickness = new Thickness(1),
                Child = innerHost
            };
            innerPlate.SetResourceReference(Border.BackgroundProperty, "Panel");
            innerPlate.SetResourceReference(Border.BorderBrushProperty, "Line");

            // 双层描边在复杂桌面背景上仍能辨认，保持 50 DIP 的卡片和 32 DIP 的图标。
            _card = new Border
            {
                CornerRadius = new CornerRadius(CornerRadiusDip),
                BorderThickness = new Thickness(2.5),
                Cursor = Cursors.Hand,
                Child = innerPlate
            };
            _card.SetResourceReference(Border.BackgroundProperty, "Panel");
            _card.SetResourceReference(Border.BorderBrushProperty, "Accent");

            var menu = new ContextMenu();
            _expandMenu = new MenuItem();
            _expandMenu.Click += async (_, _) => { if (!_disposed) await _owner.ExecuteRowAsync(_item, "expandIcon"); };
            _resetMenu = new MenuItem();
            _resetMenu.Click += async (_, _) => { if (!_disposed) await _owner.ExecuteRowAsync(_item, "reset"); };
            _removeMenu = new MenuItem();
            _removeMenu.Click += async (_, _) => { if (!_disposed) await _owner.ExecuteRowAsync(_item, "remove"); };
            menu.Items.Add(_expandMenu);
            menu.Items.Add(_resetMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(_removeMenu);
            _card.ContextMenu = menu;

            _card.MouseEnter += (_, _) => innerPlate.SetResourceReference(Border.BackgroundProperty, "Selection");
            _card.MouseLeave += (_, _) => innerPlate.SetResourceReference(Border.BackgroundProperty, "Panel");
            _card.MouseLeftButtonDown += OnMouseDown;
            _card.MouseMove += OnMouseMove;
            _card.MouseLeftButtonUp += OnMouseUp;
            _card.LostMouseCapture += (_, _) => _capturing = false;

            _window = new Window
            {
                Title = "",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = false,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                Focusable = false,
                Width = WindowSizeDip,
                Height = WindowSizeDip,
                Content = _card
            };
            _window.SetResourceReference(Control.BackgroundProperty, "Panel");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                // 留一像素玻璃框供 DWM 合成圆角，按钮区仍由应用处理鼠标输入。
                WindowChrome.SetWindowChrome(_window, new WindowChrome
                {
                    CaptionHeight = 0,
                    ResizeBorderThickness = new Thickness(0),
                    GlassFrameThickness = new Thickness(1),
                    UseAeroCaptionButtons = false
                });
                WindowChrome.SetIsHitTestVisibleInChrome(_card, true);
            }
            _window.SourceInitialized += OnSourceInitialized;
            _window.DpiChanged += (_, _) =>
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyCorners));
            _item.PropertyChanged += OnItemPropertyChanged;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            Native.ConfigureInteractiveOverlay(_hwnd);
            _systemCorners = Native.TryUseSystemOverlayCorners(_hwnd);
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        }

        private void ApplyCorners()
        {
            if (_disposed || _systemCorners || !Native.GetWindowRect(_hwnd, out var bounds)) return;
            var size = (Width: bounds.Right - bounds.Left, Height: bounds.Bottom - bounds.Top, Dpi: Native.WindowDpi(_hwnd));
            if (size == _clipSize || size.Width <= 0 || size.Height <= 0) return;
            int diameter = (int)Math.Round(2 * CornerRadiusDip * size.Dpi / 96.0);
            if (Native.RoundOverlayRegion(_hwnd, size.Width, size.Height, diameter))
                _clipSize = size;
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_disposed && e.PropertyName is nameof(WindowItem.Icon) or nameof(WindowItem.Title))
                SyncVisuals();
        }

        private static nint WndProc(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
        {
            if (msg == 0x0021)
            {
                handled = true;
                return 3;
            }
            return 0;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_disposed || _hwnd == 0 || !Native.GetCursorPos(out _downCursor) ||
                !Native.GetWindowRect(_hwnd, out _downRect)) return;
            _lastDragPosition = (_downRect.Left, _downRect.Top);
            _capturing = true;
            _dragged = false;
            _card.CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_capturing || e.LeftButton != MouseButtonState.Pressed || _hwnd == 0 ||
                !Native.GetCursorPos(out var current)) return;
            int dx = current.X - _downCursor.X;
            int dy = current.Y - _downCursor.Y;
            if (!_dragged && Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;
            _dragged = true;
            e.Handled = true;
            // 拖动只提交新坐标。建立置顶和拖动中的平移各走自己的原生操作。
            var next = (X: _downRect.Left + dx, Y: _downRect.Top + dy);
            if (next != _lastDragPosition && Native.MoveWindowControl(_hwnd, next.X, next.Y))
                _lastDragPosition = next;
        }

        private async void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_capturing) return;
            bool wasDragged = _dragged;
            _capturing = false;
            _dragged = false;
            if (_card.IsMouseCaptured) _card.ReleaseMouseCapture();
            e.Handled = true;

            if (wasDragged)
            {
                FinishDrop();
                return;
            }

            if (!_disposed && _item.IsIconCollapsed && _item.CanOperate)
                await _owner.ExecuteRowAsync(_item, "expandIcon");
        }

        private void FinishDrop()
        {
            Native.FitIconOverlayToWorkArea(_hwnd);
            ApplyCorners();
            if (!Native.GetWindowRect(_hwnd, out var rect)) return;
            UserMoved |= rect.Left != _downRect.Left || rect.Top != _downRect.Top;
            _savePosition?.Invoke((rect.Left, rect.Top));
        }

        internal bool MoveAndDropForTest(int x, int y)
        {
            if (!IsVisible || !Native.GetWindowRect(_hwnd, out _downRect) || !Native.MoveWindowControl(_hwnd, x, y))
                return false;
            FinishDrop();
            return UserMoved;
        }

        private void SyncVisuals()
        {
            var icon = _item.Icon;
            _iconImage.Source = icon;
            _iconImage.Visibility = icon is not null ? Visibility.Visible : Visibility.Collapsed;
            _initialText.Text = _item.Initial;
            _initialText.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
            _card.ToolTip = UiText.Format("CollapsedIconHint", _item.Title);
            _expandMenu.Header = UiText.Get("ExpandWindow");
            _resetMenu.Header = UiText.Get("ResetWindow");
            _removeMenu.Header = UiText.Get("RemoveWindow");
        }

        public void ShowAndSync((int X, int Y)? initialPosition, bool aggressiveTopmost, Action<(int X, int Y)> savePosition)
        {
            if (_disposed) return;
            _savePosition = savePosition;
            if (_hwnd == 0) new WindowInteropHelper(_window).EnsureHandle();

            SyncVisuals();

            int x = initialPosition?.X ?? 120;
            int y = initialPosition?.Y ?? 120;
            bool justPlaced = !_placed;
            if (justPlaced)
            {
                _placed = true;
                Native.PositionOverlay(_hwnd, x, y);
            }

            if (!_window.IsVisible)
            {
                _window.Show();
                if (justPlaced) Native.PositionOverlay(_hwnd, x, y);
                Native.FitIconOverlayToWorkArea(_hwnd);
                ApplyCorners();
                if (Native.GetWindowRect(_hwnd, out var rect))
                    _savePosition?.Invoke((rect.Left, rect.Top));
            }
            else if (!_capturing && (!Native.IsTopmost(_hwnd) || (aggressiveTopmost && Native.OverlappingTopmostAbove(_hwnd))))
            {
                Native.Raise(_hwnd);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _item.PropertyChanged -= OnItemPropertyChanged;
            _source?.RemoveHook(WndProc);
            _source = null;
            try { _window.Close(); } catch (InvalidOperationException) { }
        }
    }
}
