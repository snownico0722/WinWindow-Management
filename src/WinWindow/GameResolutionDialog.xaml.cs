using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WinWindow;

public partial class GameResolutionDialog : Window
{
    private readonly WindowIdentity _identity;
    private readonly GameResolutionExperiment _experiment;
    private readonly DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _isUnity, _launchStarted;

    internal GameResolutionDialog(WindowIdentity identity, GameResolutionExperiment experiment)
    {
        _identity = identity;
        _experiment = experiment;
        InitializeComponent();

        _isUnity = string.Equals(identity.ClassName, "UnityWndClass", StringComparison.Ordinal);
        if (!_isUnity)
        {
            CopyUnityArgsButton.IsEnabled = false;
            LaunchUnityButton.IsEnabled = false;
        }

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
            LaunchUnityButton.IsEnabled = false;
        }

        RestoreOutputButton.IsEnabled = experiment.HasLease;
        UnityLaunchHint.Text = _isUnity
            ? UiText.Get("GameResolutionLaunchHint")
            : UiText.Get("GameResolutionNotUnity");
        _processTimer.Tick += (_, _) => UpdateLaunchAvailability();
        _processTimer.Start();
        Closed += (_, _) => _processTimer.Stop();
        UpdateLaunchAvailability();
    }

    private DisplayModeOption? SelectedMode => ModeBox.SelectedItem as DisplayModeOption;

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUnityArguments();
        ApplyOutputButton.IsEnabled = SelectedMode is not null;
        CopyUnityArgsButton.IsEnabled = _isUnity && SelectedMode is not null;
        UpdateLaunchAvailability();
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

    private void LaunchUnity_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUnity || SelectedMode is not { } mode) return;
        if (IsOriginalProcessRunning())
        {
            SetStatus(UiText.Get("GameResolutionLaunchWait"));
            return;
        }

        try
        {
            string executable = _identity.ProcessPath;
            if (!File.Exists(executable)) throw new FileNotFoundException(UiText.Get("GameResolutionExecutableMissing"), executable);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            };
            foreach (string argument in GameResolutionExperiment.UnityBorderlessArguments(mode))
                start.ArgumentList.Add(argument);
            Process.Start(start) ?? throw new InvalidOperationException(UiText.Get("GameResolutionLaunchFailed"));
            _launchStarted = true;
            SetStatus(UiText.Get("GameResolutionLaunchStarted"));
        }
        catch (Exception error) { SetStatus(error.Message); }
        UpdateLaunchAvailability();
    }

    private bool IsOriginalProcessRunning()
    {
        try { return Native.MatchesProcess(_identity); }
        catch { return true; }
    }

    private void UpdateLaunchAvailability()
    {
        RestoreOutputButton.IsEnabled = _experiment.HasLease;
        LaunchUnityButton.IsEnabled = _isUnity && !_launchStarted && SelectedMode is not null &&
            !IsOriginalProcessRunning() && File.Exists(_identity.ProcessPath);
        if (_isUnity && IsOriginalProcessRunning())
            UnityLaunchHint.Text = UiText.Get("GameResolutionLaunchHint");
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
