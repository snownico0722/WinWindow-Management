using static NeatWin.App.LayoutStrings;
using NeatWin.Core;
using NeatWin.Recording;

namespace NeatWin.App;

internal sealed record LayoutPreferences(AutomaticLayoutMode Mode, TidyOptions Options,
    SmartBehaviorOptions Behavior, TaskPreferences Task, bool UseReference, bool VisualSampling = false, bool UseImportedPlacement = true);

// WinForms timers and out-of-context window events need a message pump. Keep
// layout planning and foreign-window calls off the WPF UI.
internal sealed class DesktopLayout : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<NeatWinApplicationContext> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DesktopLayout(string directory, Action<string> report, Action<RecorderStatus>? recorderChanged = null,
        Action<bool>? undoChanged = null, ForegroundHistory? focus = null, Action<LayoutRunObservation>? snapshotObserver = null, Action<string, GestureEvent>? gestureObserver = null,
        Func<bool>? snapshotWantsGestures = null, Func<nint, uint, long, string, bool>? keepRunning = null,
        Action<SurfaceEvent>? surfaceObserver = null, Func<bool>? snapshotWantsSurfaces = null,
        Action<DesktopInteraction>? interactionObserver = null, Action<SolverFeedback>? feedbackObserver = null)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new NeatWinApplicationContext(directory, report, recorderChanged, undoChanged, _stop.Token, focus, snapshotObserver, gestureObserver, snapshotWantsGestures, keepRunning, surfaceObserver, snapshotWantsSurfaces, interactionObserver, feedbackObserver);
                _ready.SetResult(context);
                Application.Run(context);
            }
            catch (Exception ex)
            {
                if (!_ready.TrySetException(ex)) report(LayoutText.Format("LayoutStopped", ex.Message));
            }
        }) { Name = "WinWindow desktop layout", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static bool NeedsBackgroundSession(string directory)
    {
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        var behavior = settings.LoadBehaviorConfiguration();
        return settings.RecordingEnabled || settings.LoadAutomaticLayoutMode() != AutomaticLayoutMode.Off ||
            behavior.Learn || behavior.Enabled is { Length: > 0 };
    }

    private async Task RunAsync(Action<NeatWinApplicationContext> action)
    {
        var context = await _ready.Task.ConfigureAwait(false);
        if (!_stop.IsCancellationRequested)
            await context.DispatchAsync(() => { if (!_stop.IsCancellationRequested) action(context); }).ConfigureAwait(false);
    }

    internal Task RefreshPointerObservationAsync() => RunAsync(context => context.RefreshPointerObservation());
    internal Task ArrangeAsync() => RunAsync(context => context.RunTidy());
    internal Task UndoAsync() => RunAsync(context => context.UndoTidy());
    internal async Task<(LayoutPreferences Preferences, RecorderStatus Recorder, BehaviorSettings Behavior)> ReadSettingsAsync()
    {
        var context = await _ready.Task.ConfigureAwait(false);
        return await context.ReadAsync(() => (context.Preferences, context.Recorder, context.Behavior)).ConfigureAwait(false);
    }
    internal Task ApplyAsync(LayoutPreferences value) => RunAsync(context => context.ApplyPreferences(value));
    internal Task SetRecordingAsync(bool value) => RunAsync(context => context.SetRecording(value));
    internal Task SetBehaviorLearningAsync(bool enabled) => RunAsync(context => context.SetBehaviorLearning(enabled));
    internal Task SetBehaviorEnabledAsync(string id, bool enabled) => RunAsync(context => context.SetBehaviorEnabled(id, enabled));
    internal Task ImportBehaviorRulesAsync(string path) => RunAsync(context => context.ImportBehaviorRules(path));
    internal Task ImportPlacementRulesAsync(string path) => RunAsync(context => context.ImportPlacementRules(path));
    internal Task OpenDataAsync() => RunAsync(context => context.OpenRecorderData());
    internal Task ExportAsync(string path) => RunAsync(context => context.ExportRecorderData(path));
    internal Task ClearAsync() => RunAsync(context => context.ClearRecorderData());

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _ = CloseAsync();
    }

    private async Task CloseAsync()
    {
        try
        {
            var context = await _ready.Task.ConfigureAwait(false);
            await context.DispatchAsync(context.ExitThread).ConfigureAwait(false);
        }
        // Initialization can fail, or shutdown can win the race with the message loop.
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }
}
