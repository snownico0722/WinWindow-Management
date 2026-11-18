namespace NeatWin.App;

// WinForms timers and out-of-context window events need a message pump. Keep
// layout planning, foreign-window calls and the settings dialog off the WPF UI.
internal sealed class DesktopLayout : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<NeatWinApplicationContext> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DesktopLayout(string directory, Action<string> report)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new NeatWinApplicationContext(directory, report, _stop.Token);
                _ready.SetResult(context);
                Application.Run(context);
            }
            catch (Exception ex)
            {
                if (!_ready.TrySetException(ex)) report($"桌面整理已停止：{ex.Message}");
            }
        }) { Name = "WinWindow desktop layout", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static bool NeedsBackgroundSession(string directory)
    {
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        return settings.RecordingEnabled || settings.LoadAutomaticLayoutMode() != Core.AutomaticLayoutMode.Off;
    }

    private async Task RunAsync(Action<NeatWinApplicationContext> action)
    {
        var context = await _ready.Task.ConfigureAwait(false);
        if (!_stop.IsCancellationRequested)
            await context.DispatchAsync(() => { if (!_stop.IsCancellationRequested) action(context); }).ConfigureAwait(false);
    }

    internal Task ArrangeAsync() => RunAsync(context => context.RunTidy());
    internal Task UndoAsync() => RunAsync(context => context.UndoTidy());
    internal Task OpenSettingsAsync() => RunAsync(context => context.ShowSettings());

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
