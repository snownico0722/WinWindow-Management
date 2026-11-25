using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace WinWindow;

public partial class ManagedWindowControls : Window
{
    private const int StripWidth = 84, StripHeight = 29;
    internal const int BadgeSize = 46;
    private readonly MainWindow _managerWindow;
    private readonly WindowItem _item;
    private Native.Point? _badgeAnchor;
    private bool _collapsing;
    private nint _hwnd;

    internal ManagedWindowControls(MainWindow managerWindow, WindowItem item)
    {
        _managerWindow = managerWindow;
        _item = item;
        InitializeComponent();
        DataContext = item;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.ConfigureWindowControl(_hwnd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WindowMessage);
        };
    }

    private static nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int mouseActivate = 0x21, noActivate = 3;
        if (message != mouseActivate) return 0;
        // 收到鼠标点击，仍让目标保持前台，避免点击图钉就切走游戏或输入框。
        handled = true;
        return noActivate;
    }

    internal Native.Rect? VisibleBadgeBounds => _badgeAnchor.HasValue && Badge.IsVisible &&
        Native.GetWindowRect(_hwnd, out var bounds) ? bounds : null;

    internal void Follow(int opacity)
    {
        if (_badgeAnchor is { } anchor && (_collapsing || _item.IsCollapsedToIcon))
        {
            ShowControl(anchor, badge: true);
            return;
        }
        _badgeAnchor = null;
        if (_item.ClickThrough || !Native.TryGetTitleControlsAnchor(_item.Hwnd, out var frame))
        {
            if (IsVisible) Hide();
            return;
        }
        if (OpacityToggle.IsChecked != (_item.Opacity == opacity))
            OpacityToggle.IsChecked = _item.Opacity == opacity;
        string hint = UiText.Format("ToggleTitlebarOpacityHint", opacity);
        if (!Equals(OpacityToggle.ToolTip, hint)) OpacityToggle.ToolTip = hint;
        ShowControl(Native.TitleControlAnchor(_item.Hwnd, frame, StripHeight), badge: false);
    }

    private bool ShowControl(Native.Point anchor, bool badge)
    {
        nint target = badge ? 0 : _item.Hwnd;
        var visibility = badge ? Visibility.Visible : Visibility.Collapsed;
        if (Badge.Visibility != visibility)
        {
            Badge.Visibility = visibility;
            TitleStrip.Visibility = badge ? Visibility.Collapsed : Visibility.Visible;
        }
        int width = badge ? BadgeSize : StripWidth, height = badge ? BadgeSize : StripHeight;
        if (Width != width) Width = width;
        if (Height != height) Height = height;
        if (_hwnd == 0) new WindowInteropHelper(this).EnsureHandle();
        var bounds = Native.OverlayBounds(badge ? _hwnd : target, anchor, width, height);
        if (bounds.Right <= bounds.Left || !Native.PositionWindowControl(_hwnd, target, bounds))
        {
            Hide();
            return false;
        }
        if (IsVisible) return true;
        Show();
        if (Native.PositionWindowControl(_hwnd, target, bounds)) return true;
        Hide();
        return false;
    }

    private async void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if (!_item.CanOperate || _collapsing || !Native.TryGetTitleControlsAnchor(_item.Hwnd, out var frame)) return;
        _badgeAnchor = _managerWindow.FindBadgeAnchor(_item, frame);
        _collapsing = true;
        try
        {
            await _managerWindow.RunAsync(async () =>
            {
                // 有可点击的恢复入口以后才隐藏原窗口。
                if (!ShowControl(_badgeAnchor.Value, badge: true))
                    throw new InvalidOperationException(UiText.Get("CollapseRejected"));
                await _managerWindow.Manager.CollapseToIconAsync(_item);
            });
        }
        finally
        {
            _collapsing = false;
            _managerWindow.RefreshManagedControls();
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_item.CanOperate && !_collapsing) await _managerWindow.ExecuteRowAsync(_item, "activate");
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        await _managerWindow.ExecuteRowAsync(_item, "pin");
        Pin.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    private async void Opacity_Click(object sender, RoutedEventArgs e)
    {
        await _managerWindow.ExecuteRowAsync(_item, "toggleOpacity");
    }
}
