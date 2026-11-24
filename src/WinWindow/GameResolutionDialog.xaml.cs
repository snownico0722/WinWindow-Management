using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WinWindow;

public partial class GameResolutionDialog : Window
{
    private readonly WindowIdentity _identity;
    private readonly GameResolutionExperiment _experiment;
    private readonly bool _isUnity;

    internal GameResolutionDialog(WindowIdentity identity, GameResolutionExperiment experiment)
    {
        _identity = identity;
        _experiment = experiment;
        InitializeComponent();

        _isUnity = string.Equals(identity.ClassName, "UnityWndClass", StringComparison.Ordinal);
        CopyUnityArgsButton.IsEnabled = _isUnity;

        try
        {
            var current = experiment.GetCurrentMode(identity.Hwnd);
            var modes = experiment.GetDisplayModes(identity.Hwnd);
            CurrentModeText.Text = UiText.Format("GameResolutionCurrentMode", current.ToString());
            ModeBox.ItemsSource = modes;

            var selected = modes.FirstOrDefault(x => x.Width == 2560 && x.Height == 1440 && x.BitsPerPixel == current.BitsPerPixel)
                ?? modes.FirstOrDefault(x => x.Width == 1920 && x.Height == 1080 && x.BitsPerPixel == current.BitsPerPixel)
                ?? modes.FirstOrDefault(x => x.Width == current.Width && x.Height == current.Height && x.BitsPerPixel == current.BitsPerPixel)
                ?? modes.FirstOrDefault();
            ModeBox.SelectedItem = selected;
            ApplyOutputButton.IsEnabled = selected is not null;
            CopyUnityArgsButton.IsEnabled = _isUnity && selected is not null;
            UpdateUnityArguments();
        }
        catch (Exception error)
        {
            SetStatus(error.Message);
            ApplyOutputButton.IsEnabled = false;
            CopyUnityArgsButton.IsEnabled = false;
        }

        RestoreOutputButton.IsEnabled = experiment.HasLease;
        UnityLaunchHint.Text = _isUnity
            ? UiText.Get("GameResolutionLaunchHint")
            : UiText.Get("GameResolutionNotUnity");
    }

    private DisplayModeOption? SelectedMode => ModeBox.SelectedItem as DisplayModeOption;

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUnityArguments();
        ApplyOutputButton.IsEnabled = SelectedMode is not null;
        CopyUnityArgsButton.IsEnabled = _isUnity && SelectedMode is not null;
    }

    private void UpdateUnityArguments()
    {
        ArgsText.Text = SelectedMode is { } mode
            ? GameResolutionExperiment.FormatUnityBorderlessArguments(mode)
            : string.Empty;
    }

    private async void ApplyOutput_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMode is not { } mode) return;
        ApplyOutputButton.IsEnabled = false;
        try
        {
            string message = await System.Threading.Tasks.Task.Run(() => _experiment.ApplyOutputMode(_identity, mode));
            SetStatus(message);
            CurrentModeText.Text = UiText.Format("GameResolutionCurrentMode", _experiment.GetCurrentMode(_identity.Hwnd).ToString());
            RestoreOutputButton.IsEnabled = _experiment.HasLease;
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally { ApplyOutputButton.IsEnabled = SelectedMode is not null; }
    }

    private async void RestoreOutput_Click(object sender, RoutedEventArgs e)
    {
        RestoreOutputButton.IsEnabled = false;
        try
        {
            string? message = await System.Threading.Tasks.Task.Run(_experiment.RestoreNow);
            if (message is not null) SetStatus(message);
            CurrentModeText.Text = UiText.Format("GameResolutionCurrentMode", _experiment.GetCurrentMode(_identity.Hwnd).ToString());
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally { RestoreOutputButton.IsEnabled = _experiment.HasLease; }
    }

    private void CopyUnityArgs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMode is not { } mode) return;
        try
        {
            Clipboard.SetText(GameResolutionExperiment.FormatUnityBorderlessArguments(mode));
            SetStatus(UiText.Get("GameResolutionArgsCopied"));
        }
        catch (Exception error) { SetStatus(error.Message); }
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
