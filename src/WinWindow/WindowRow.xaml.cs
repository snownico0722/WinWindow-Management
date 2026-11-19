using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinWindow;

// Each row owns its editor state. Selecting, recycling or updating a different
// row must not redirect an edit or replace text that the user is still typing.
public partial class WindowRow : UserControl
{
    private const string PinActiveGeometry = "M5.2,2.2 L10.8,2.2 M6,2.2 L6,6.8 L3.6,9.5 L12.4,9.5 L10,6.8 L10,2.2 M8,9.5 L8,14.5";
    private const string PinInactiveGeometry = "M5.2,2.2 L10.8,2.2 M10,2.2 L10,6.8 L12.4,9.5 L9.6,9.5 M6,2.2 L6,4.4 M3.6,9.5 L6.8,9.5 M8,11.2 L8,14.5 M2.5,2.5 L13.5,13.5";
    private const string ParkMinimizeGeometry = "M3,13 L13,13 M8,2.5 L8,9.8 M5,7 L8,10 L11,7";
    private const string ParkRestoreGeometry = "M3,13 L13,13 M8,10.2 L8,3 M5,6 L8,3 L11,6";

    private WindowItem? _item;
    private bool _syncing, _dirty;
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;

    public WindowRow()
    {
        InitializeComponent();
        OpacitySlider.LostMouseCapture += (_, _) => Sync();
        Loaded += (_, _) => Attach(DataContext as WindowItem);
        Unloaded += (_, _) => Attach(null);
        SizeChanged += (_, e) => BackgroundLabel.Visibility = e.NewSize.Width >= 500 ? Visibility.Visible : Visibility.Collapsed;
        DataContextChanged += (_, _) => { if (IsLoaded) Attach(DataContext as WindowItem); };
    }

    private void Attach(WindowItem? item)
    {
        if (ReferenceEquals(_item, item)) { Sync(); return; }
        CommitNumber();
        CloseInlineNumber();
        if (_item is not null) _item.PropertyChanged -= ItemChanged;
        _item = item; _dirty = false;
        if (_item is not null) _item.PropertyChanged += ItemChanged;
        Sync();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (_item is not { } item) return;
        _syncing = true;
        try
        {
            CardFrame.Opacity = item.IsAvailable ? 1.0 : 0.72;
            CardFrame.SetResourceReference(Border.BackgroundProperty, item.IsAvailable ? "Panel" : "Paper");

            PinButton.IsChecked = item.Pinned;
            PinButton.IsEnabled = !item.IsBusy;
            PinButton.ToolTip = item.Pinned ? "取消置顶（保留在列表中）" : "置顶窗口";
            PinIcon.Data = System.Windows.Media.Geometry.Parse(item.Pinned ? PinActiveGeometry : PinInactiveGeometry);

            ThroughButton.IsChecked = item.ClickThrough;
            ThroughButton.ToolTip = item.ClickThrough ? "关闭鼠标穿透" : "开启鼠标穿透";
            RemoveButton.IsEnabled = !item.IsBusy;

            bool restore = item.IsHidden || item.IsMinimized;
            ParkIcon.Data = System.Windows.Media.Geometry.Parse(restore ? ParkRestoreGeometry : ParkMinimizeGeometry);
            ParkButton.SetResourceReference(Control.ForegroundProperty, restore ? "Accent" : "Muted");
            ParkButton.ToolTip = restore ? "恢复窗口" : "强制最小化";
            System.Windows.Automation.AutomationProperties.SetName(ParkButton, restore ? "恢复窗口" : "强制最小化");

            RowState.Text = !item.IsAvailable ? "等待启动"
                : item.IsBusy ? "处理中"
                : item.IsHidden ? "已隐藏"
                : item.IsMinimized ? "已最小化"
                : item.Opacity == 0 ? "完全透明"
                : item.Remembered ? "已记住"
                : "";

            BackgroundToggle.IsChecked = item.BackgroundEnabled;

            int value = Owner?.DesiredOpacity(item) ?? item.Opacity;
            if (!OpacitySlider.IsMouseCaptureWithin) OpacitySlider.Value = value;
            OpacityDisplayButton.Content = value + "%";
            if (!_dirty) OpacityNumber.Text = value.ToString();

            bool showGhostWarning = item.IsAvailable && value < 15 && !item.ClickThrough && !item.IsHidden && !item.IsMinimized;
            GhostGuardButton.Visibility = showGhostWarning ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncing = false; }
    }

    private async void Execute(string action)
    {
        if (_item is { } item && Owner is { } owner) await owner.ExecuteRowAsync(item, action);
        Sync();
    }

    private void Title_Click(object sender, RoutedEventArgs e) => Execute("activate");
    private void Pin_Click(object sender, RoutedEventArgs e) => Execute("pin");
    private void Through_Click(object sender, RoutedEventArgs e) => Execute("through");
    private void EnableThrough_Click(object sender, RoutedEventArgs e)
    {
        if (_item is { ClickThrough: false }) Execute("through");
    }
    private void Background_Click(object sender, RoutedEventArgs e) => Execute("background");
    private void Park_Click(object sender, RoutedEventArgs e) => Execute("park");
    private void Remove_Click(object sender, RoutedEventArgs e) => Execute("remove");
    private void Remember_Click(object sender, RoutedEventArgs e) => Execute("remember");
    private void Reset_Click(object sender, RoutedEventArgs e) => Execute("reset");
    private void Copy_Click(object sender, RoutedEventArgs e) => Execute("copy");

    private void RowMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (_item is null) { e.Handled = true; return; }
        RememberItem.IsChecked = _item.Remembered;
        RememberItem.IsEnabled = !_item.IsBusy && (_item.IsAvailable || _item.Remembered);
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _item is not { IsAvailable: true } item || !(OpacitySlider.IsMouseCaptureWithin || OpacitySlider.IsKeyboardFocusWithin)) return;
        int value = (int)Math.Round(e.NewValue);
        Owner?.QueueOpacity(item, value);
        OpacityDisplayButton.Content = value + "%";
        if (!_dirty) { _syncing = true; OpacityNumber.Text = value.ToString(); _syncing = false; }
        bool showGhostWarning = item.IsAvailable && value < 15 && !item.ClickThrough && !item.IsHidden && !item.IsMinimized;
        GhostGuardButton.Visibility = showGhostWarning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Slider_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!OpacitySlider.IsKeyboardFocusWithin || _item is not { IsAvailable: true } item || e.Delta == 0) return;
        int current = Owner?.DesiredOpacity(item) ?? item.Opacity;
        int next = Math.Clamp(current + (e.Delta > 0 ? 5 : -5), 0, 100);
        e.Handled = true;
        Owner?.QueueOpacity(item, next);
        Sync();
    }

    private void EditOpacity_Click(object sender, RoutedEventArgs e)
    {
        if (_item is not { IsAvailable: true }) return;
        OpacityDisplayButton.Visibility = Visibility.Collapsed;
        OpacityEditHost.Visibility = Visibility.Visible;
        OpacityNumber.Focus();
        OpacityNumber.SelectAll();
    }

    private void CloseInlineNumber()
    {
        OpacityEditHost.Visibility = Visibility.Collapsed;
        OpacityDisplayButton.Visibility = Visibility.Visible;
    }

    private void Number_Focus(object sender, KeyboardFocusChangedEventArgs e) => _dirty = false;

    private void Number_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && OpacityNumber.IsKeyboardFocusWithin) _dirty = true;
    }

    private void CommitNumber()
    {
        if (!_dirty || _item is not { IsAvailable: true } item) return;
        _dirty = false;
        if (int.TryParse(OpacityNumber.Text, out int value) && value is >= 0 and <= 100) Owner?.QueueOpacity(item, value);
        else Owner?.ShowStatus("不透明度请输入 0–100。");
        Sync();
    }

    private void Number_Blur(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitNumber();
        CloseInlineNumber();
    }

    private void Number_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitNumber(); CloseInlineNumber(); e.Handled = true; }
        if (e.Key == Key.Escape) { _dirty = false; CloseInlineNumber(); Sync(); e.Handled = true; }
    }
}
