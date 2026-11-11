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
    private WindowItem? _item;
    private bool _syncing, _dirty;
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;
    public WindowRow()
    {
        InitializeComponent();
        OpacitySlider.LostMouseCapture += (_, _) => Sync();
        Loaded += (_, _) => Attach(DataContext as WindowItem);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, _) => { if (IsLoaded) Attach(DataContext as WindowItem); };
    }
    private void Attach(WindowItem? item)
    {
        if (ReferenceEquals(_item, item)) { Sync(); return; }
        CommitNumber();
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
            PinButton.IsChecked = item.Pinned;
            PinButton.IsEnabled = !item.IsBusy;
            PinButton.ToolTip = item.Pinned ? "取消置顶，保留管理" : "置顶窗口";
            ThroughButton.IsChecked = item.ClickThrough;
            ThroughButton.ToolTip = item.ClickThrough ? "关闭鼠标穿透" : "开启鼠标穿透";
            RemoveButton.IsEnabled = !item.IsBusy;
            bool restore = item.IsHidden || item.IsMinimized;
            ParkButton.Content = restore ? "\uE923" : "\uE921";
            ParkButton.ToolTip = restore ? "恢复窗口" : "强制最小化";
            System.Windows.Automation.AutomationProperties.SetName(ParkButton, restore ? "恢复窗口" : "强制最小化");
            RowState.Text = item.IsBusy ? "处理中" : item.IsHidden ? "已隐藏" : item.IsMinimized ? "已最小化" : item.Opacity == 0 ? "完全透明" : "";
            int value = Owner?.DesiredOpacity(item) ?? item.Opacity;
            if (!OpacitySlider.IsMouseCaptureWithin) OpacitySlider.Value = value;
            if (!_dirty) OpacityNumber.Text = value.ToString();
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
        if (!_dirty) { _syncing = true; OpacityNumber.Text = value.ToString(); _syncing = false; }
    }
    private void Number_Focus(object sender, KeyboardFocusChangedEventArgs e) => _dirty = false;
    private void Number_Changed(object sender, TextChangedEventArgs e)
    { if (!_syncing && OpacityNumber.IsKeyboardFocusWithin) _dirty = true; }
    private void CommitNumber()
    {
        if (!_dirty || _item is not { IsAvailable: true } item) return;
        _dirty = false;
        if (int.TryParse(OpacityNumber.Text, out int value) && value is >= 0 and <= 100) Owner?.QueueOpacity(item, value);
        else Owner?.ShowStatus("不透明度请输入 0–100。");
        Sync();
    }
    private void Number_Blur(object sender, KeyboardFocusChangedEventArgs e) => CommitNumber();
    private void Number_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitNumber(); e.Handled = true; }
        if (e.Key == Key.Escape) { _dirty = false; Sync(); e.Handled = true; }
    }
}
