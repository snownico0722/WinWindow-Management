using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeatWin.App;
using NeatWin.Core;
using NeatWin.Recording;

namespace WinWindow;

public partial class MainWindow
{
    private LayoutPreferences? _layoutPreferences;
    private Task? _layoutLoad;
    private bool _displayingMode;
    private long _layoutEdit;

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedValue is not string language) return;
        _store.Preferences.Language = language;
        ApplyLanguage(language);
        foreach (var item in Manager.Items) item.NotifyLanguage();
        if (_lastRecorderStatus is { } recorder) DisplayRecorder(recorder);
        DismissStatus();
        RefreshView();
        _store.SaveSoon();
    }

    private void SettingsNav_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) SelectSettingsSection();
    }

    private void SelectSettingsSection()
    {
        var pages = new[] { GeneralSection, LayoutSection, KeysSection, DataSection };
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == SettingsNav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsNav.SelectedIndex is 1 or 3 && _layoutPreferences is null && _layoutLoad is null)
            _ = LoadLayoutSettingsAsync();
    }

    private async Task LoadLayoutSettingsAsync()
    {
        if (_layoutLoad is { } pending) { await pending; return; }
        _layoutLoad = ReadLayoutSettingsAsync();
        try { await _layoutLoad; }
        finally { _layoutLoad = null; }
    }

    private async Task ReadLayoutSettingsAsync()
    {
        LayoutSection.IsEnabled = DataSection.IsEnabled = FooterModes.IsEnabled = false;
        try
        {
            var snapshot = await EnsureLayout().ReadSettingsAsync();
            if (_quitting) return;
            DisplayPreferences(snapshot.Preferences);
            DisplayRecorder(snapshot.Recorder);
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
        finally
        {
            FooterModes.IsEnabled = true;
            LayoutSection.IsEnabled = DataSection.IsEnabled = _layoutPreferences is not null;
        }
    }

    private static void Choose(Panel panel, Enum value)
    {
        foreach (var button in panel.Children.OfType<RadioButton>())
            button.IsChecked = (string)button.Tag == value.ToString();
    }

    private void DisplayPreferences(LayoutPreferences value)
    {
        _layoutPreferences = value;
        var o = value.Options;
        var b = value.Behavior;
        var p = value.Task.Profile;
        Choose(AlgorithmChoices, o.AlgorithmMode);
        Choose(StrengthChoices, o.SmartStrength);
        Choose(VideoChoices, b.VideoBlackBarTendency);
        VerticalFill.IsChecked = b.PreferReversibleVerticalFill;
        VideoBars.IsChecked = b.RemoveVideoBlackBars;
        RescueWindows.IsChecked = o.RescueOffscreenWindows;
        UsefulResize.IsChecked = p.AllowUsefulResize;
        AllowBleed.IsChecked = p.AllowPeripheralBleed;
        ProtectEdges.IsChecked = p.ProtectWindowEdges;
        ReferenceEnabled.IsChecked = value.UseReference;
        NeighborSnap.Text = o.NeighborSnapDistance.ToString();
        AlignSnap.Text = o.AlignmentSnapDistance.ToString();
        ScreenSnap.Text = o.ScreenSnapDistance.ToString();
        EdgeAdjust.Text = o.MaximumEdgeAdjustment.ToString();
        ResizeRatio.Text = Math.Round(o.MaximumSizeChangeRatio * 100).ToString();
        PassCount.Text = o.Passes.ToString();
        BleedAmount.Text = p.MaximumBleedDip.ToString("0");
        ComfortWidth.Text = p.ComfortableWidthDip.ToString("0");
        ComfortHeight.Text = p.ComfortableHeightDip.ToString("0");
        var calibration = value.Task.Calibration;
        Calibrate.IsChecked = calibration is not null;
        PhysicalWidth.Text = (calibration?.WidthMillimeters ?? 600).ToString("0");
        ViewDistance.Text = (calibration?.DistanceMillimeters ?? 600).ToString("0");
        UpdateLayoutFields(value);
    }

    private void UpdateLayoutFields(LayoutPreferences value)
    {
        bool smart = value.Options.AlgorithmMode == TidyAlgorithmMode.Smart;
        SmartOptions.Visibility = smart ? Visibility.Visible : Visibility.Collapsed;
        ClassicOptions.Visibility = smart ? Visibility.Collapsed : Visibility.Visible;
        VideoChoices.Visibility = value.Behavior.RemoveVideoBlackBars ? Visibility.Visible : Visibility.Collapsed;
        BleedAmount.IsEnabled = value.Task.Profile.AllowPeripheralBleed;
        CalibrationFields.Visibility = value.Task.Calibration is null ? Visibility.Collapsed : Visibility.Visible;
        DisplayMode(value.Mode);
    }

    private void DisplayMode(AutomaticLayoutMode mode)
    {
        _displayingMode = true;
        try { Choose(FooterModes, mode); }
        finally { _displayingMode = false; }
    }

    private async void FooterMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _quitting || _displayingMode) return;
        var mode = Enum.Parse<AutomaticLayoutMode>((string)((RadioButton)sender).Tag);
        if (_layoutPreferences is null) await LoadLayoutSettingsAsync();
        if (_layoutPreferences is { } current)
            await SaveLayoutPreferencesAsync(current with { Mode = mode });
        else
            DisplayMode(AutomaticLayoutMode.Off);
    }

    private ViewingCalibration CalibrateDisplay()
    {
        var work = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea;
        var dpi = (uint)Math.Round(VisualTreeHelper.GetDpi(this).PixelsPerInchX);
        return new(new RectI(work.X, work.Y, work.Width, work.Height), dpi, 600, 600);
    }

    private async void LayoutPreference_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPreferences is not { } v) return;
        var task = v.Task;
        var profile = task.Profile;
        LayoutPreferences next;
        if (sender is RadioButton radio)
        {
            var tag = (string)radio.Tag;
            next = radio.GroupName switch
            {
                nameof(AlgorithmChoices) => v with { Options = v.Options with { AlgorithmMode = Enum.Parse<TidyAlgorithmMode>(tag) } },
                nameof(StrengthChoices) => v with { Options = v.Options with { SmartStrength = Enum.Parse<SmartTidyStrength>(tag) } },
                nameof(VideoChoices) => v with { Behavior = v.Behavior with { VideoBlackBarTendency = Enum.Parse<VideoBlackBarTendency>(tag) } },
                _ => v
            };
        }
        else if (sender is CheckBox box)
        {
            bool on = box.IsChecked == true;
            next = box.Name switch
            {
                nameof(VerticalFill) => v with { Behavior = v.Behavior with { PreferReversibleVerticalFill = on } },
                nameof(VideoBars) => v with { Behavior = v.Behavior with { RemoveVideoBlackBars = on } },
                nameof(RescueWindows) => v with { Options = v.Options with { RescueOffscreenWindows = on } },
                nameof(UsefulResize) => v with { Task = task with { Profile = profile with { AllowUsefulResize = on } } },
                nameof(ProtectEdges) => v with { Task = task with { Profile = profile with { ProtectWindowEdges = on } } },
                nameof(AllowBleed) => v with { Task = task with { Profile = profile with { AllowPeripheralBleed = on } } },
                nameof(Calibrate) => v with { Task = task with { Calibration = on ? CalibrateDisplay() : null } },
                nameof(ReferenceEnabled) => v with { UseReference = on },
                _ => v
            };
            if (box == Calibrate && on) PhysicalWidth.Text = ViewDistance.Text = "600";
        }
        else return;
        await SaveLayoutPreferencesAsync(next);
    }

    private void LayoutNumber_Key(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ((TextBox)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    private async void LayoutNumber_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_quitting || _layoutPreferences is not { } v || sender is not TextBox box) return;
        var limits = ((string)box.Tag).Split(',');
        if (!int.TryParse(box.Text, out var n) || n < int.Parse(limits[0]) || n > int.Parse(limits[1]))
        {
            DisplayPreferences(v);
            ShowStatus(UiText.Format("IntegerRange", limits[0], limits[1]));
            return;
        }
        var o = v.Options;
        var t = v.Task;
        var p = t.Profile;
        var next = box.Name switch
        {
            nameof(NeighborSnap) => v with { Options = o with { NeighborSnapDistance = n } },
            nameof(AlignSnap) => v with { Options = o with { AlignmentSnapDistance = n } },
            nameof(ScreenSnap) => v with { Options = o with { ScreenSnapDistance = n } },
            nameof(EdgeAdjust) => v with { Options = o with { MaximumEdgeAdjustment = n } },
            nameof(ResizeRatio) => v with { Options = o with { MaximumSizeChangeRatio = n / 100.0 } },
            nameof(PassCount) => v with { Options = o with { Passes = n } },
            nameof(BleedAmount) => v with { Task = t with { Profile = p with { MaximumBleedDip = n } } },
            nameof(ComfortWidth) => v with { Task = t with { Profile = p with { ComfortableWidthDip = n } } },
            nameof(ComfortHeight) => v with { Task = t with { Profile = p with { ComfortableHeightDip = n } } },
            nameof(PhysicalWidth) when t.Calibration is { } c => v with { Task = t with { Calibration = c with { WidthMillimeters = n } } },
            nameof(ViewDistance) when t.Calibration is { } c => v with { Task = t with { Calibration = c with { DistanceMillimeters = n } } },
            _ => v
        };
        await SaveLayoutPreferencesAsync(next);
    }

    private async Task SaveLayoutPreferencesAsync(LayoutPreferences next)
    {
        if (next == _layoutPreferences || _quitting) return;
        _layoutPreferences = next;
        long edit = ++_layoutEdit;
        UpdateLayoutFields(next);
        try { await EnsureLayout().ApplyAsync(next); }
        catch (Exception ex)
        {
            if (edit == _layoutEdit) await LoadLayoutSettingsAsync();
            ShowStatus(ex.Message);
        }
    }

    private async void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPreferences is not { } v) return;
        var defaults = v with { Options = new(), Behavior = new(), Task = new(new TaskLayoutProfile()) };
        await SaveLayoutPreferencesAsync(defaults);
        if (_layoutPreferences is { } current) DisplayPreferences(current);
    }

    private RecorderStatus? _lastRecorderStatus;

    private void DisplayRecorder(RecorderStatus status)
    {
        _lastRecorderStatus = status;
        RecordSwitch.IsEnabled = status.IsAvailable;
        RecordSwitch.IsChecked = status.IsAvailable && !status.IsPaused;
        RecordStatus.Text = status.Error ?? UiText.Format("RecorderCounts", status.StateText, status.RecordedGestures, status.ContextRecords);
    }

    private async void RecordSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = RecordSwitch.IsChecked == true;
        try { await EnsureLayout().SetRecordingAsync(on); }
        catch (Exception ex) { await LoadLayoutSettingsAsync(); ShowStatus(ex.Message); }
    }

    private async void OpenData_Click(object sender, RoutedEventArgs e) => await RunAsync(() => EnsureLayout().OpenDataAsync());
    private async void ExportData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = UiText.Get("ZipFilter"), FileName = UiText.Format("ExportFile", DateTime.Now) };
        if (dialog.ShowDialog(this) == true) await RunAsync(async () => { await EnsureLayout().ExportAsync(dialog.FileName); ShowStatus(UiText.Get("Exported")); });
    }
    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, UiText.Get("ClearQuestion"), UiText.Get("ClearRecords"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await RunAsync(async () => { await EnsureLayout().ClearAsync(); ShowStatus(UiText.Get("RecordsCleared")); });
    }
}
