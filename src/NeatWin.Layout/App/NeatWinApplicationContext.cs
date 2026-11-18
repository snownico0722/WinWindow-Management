using System.Diagnostics;
using NeatWin.Reference;
using NeatWin.Recording;
using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.App;

internal sealed class NeatWinApplicationContext : ApplicationContext
{
    private readonly WindowManager _windowManager = new();
    private readonly VisibilityAnalyzer _visibilityAnalyzer = new();
    private readonly TidyEngine _tidyEngine = new();
    private readonly SettingsStore _settingsStore;
    private readonly string _preferencesPath;
    private readonly CancellationToken _stopping;
    private readonly BrowserVideoBlackBarDetector _videoBlackBarDetector = new();
    private readonly Control _dispatch = new();
    private readonly Action<string> _report;
    private readonly Action<bool>? _undoChanged;
    private readonly Action<RecorderStatus>? _recorderChanged;
    private readonly ReversibleVerticalFillManager _verticalFillManager;
    private readonly AutoTidyManager _autoTidyManager;
    private readonly RecorderSession _recorder;
    private TidyOptions _tidyOptions;
    private SmartBehaviorOptions _smartBehaviorOptions;
    private readonly IntentReferenceStore _referenceStore;
    private bool _undoWasSmart;
    private IReadOnlyList<TidyMove> _undoPlan = [];
    private IReadOnlyList<WindowLayerOrder> _undoLayers = [];
    private readonly LayoutRunJournal _journal;
    private readonly TaskLayoutSession _taskSession = new();
    private TaskPreferences _taskPreferences;
    private AutomaticLayoutMode _automaticMode;
    private readonly WorkspaceAutoTidyMonitor _workspaceMonitor;
    private bool _tidyRunning;

    internal NeatWinApplicationContext(string directory, Action<string> report, Action<RecorderStatus>? recorderChanged, Action<bool>? undoChanged, CancellationToken stopping)
    {
        _stopping = stopping;
        _report = report;
        _undoChanged = undoChanged;
        _recorderChanged = recorderChanged;
        _ = _dispatch.Handle; // Own the WinForms message queue without creating a settings form.
        _settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        _preferencesPath = Path.Combine(directory, "human-factors.json");
        _taskPreferences = TaskPreferences.Load(_preferencesPath);
        _referenceStore = new IntentReferenceStore(Path.Combine(directory, "Recorder"));
        _tidyOptions = _settingsStore.LoadTidyOptions();
        _smartBehaviorOptions = _settingsStore.LoadSmartBehaviorOptions();
        _verticalFillManager = new ReversibleVerticalFillManager();
        _autoTidyManager = new AutoTidyManager();
        _recorder = new RecorderSession(_referenceStore, _settingsStore.RecordingEnabled);
        _journal = new LayoutRunJournal(_windowManager, _referenceStore, _recorder);
        _workspaceMonitor = new WorkspaceAutoTidyMonitor(_windowManager.Capture,
            () => _tidyRunning || _autoTidyManager.IsGestureActive || NativeWindowActivity.IsMoving);
        _autoTidyManager.GestureStarted += handle =>
        {
            _journal.MarkGesture(handle);
            _workspaceMonitor.GestureStarted(handle);
        };
        _autoTidyManager.GestureObserved += gesture =>
        {
            _journal.MarkGesture(gesture.WindowHandle);
            _taskSession.Gesture(gesture, DateTimeOffset.UtcNow);
            try
            {
                if (_windowManager.Capture().Any(w => w.Handle == gesture.WindowHandle && w.IsManageable && !w.IsTopmost))
                    _workspaceMonitor.GestureCompleted(gesture);
            }
            catch (Exception ex) { Trace.TraceWarning($"Gesture snapshot failed: {ex.Message}"); }
        };
        _automaticMode = _settingsStore.LoadAutomaticLayoutMode();
        if (!ConfigureAutomation(_automaticMode)) _automaticMode = AutomaticLayoutMode.Off;
        _workspaceMonitor.Requested += OnAutomaticLayoutRequested;
        _autoTidyManager.TidyRequested += OnFollowHandRequested;
        _journal.Completed += OnLayoutVerified;
        _workspaceMonitor.Failed += ex => _report($"自动整理观察失败：{ex.Message}");
        _recorder.StatusChanged += OnRecorderStatusChanged;


    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoTidyManager.TidyRequested -= OnFollowHandRequested;
            _workspaceMonitor.Dispose(); _autoTidyManager.Dispose(); _journal.Dispose();
            _recorder.StatusChanged -= OnRecorderStatusChanged; _recorder.Dispose();
            _verticalFillManager.Dispose(); _dispatch.Dispose();
        }
        base.Dispose(disposing);
    }

    internal Task DispatchAsync(Action action) => _dispatch.InvokeAsync(action);
    internal Task<T> ReadAsync<T>(Func<T> read) => _dispatch.InvokeAsync(read);

    internal LayoutPreferences Preferences => new(_automaticMode, _tidyOptions, _smartBehaviorOptions,
        _taskPreferences, _referenceStore.Enabled);
    internal RecorderStatus Recorder => _recorder.Status;

    internal void ApplyPreferences(LayoutPreferences value)
    {
        if (value.Options != _tidyOptions || value.Behavior != _smartBehaviorOptions)
        {
            _settingsStore.SaveLayoutOptions(value.Options, value.Behavior);
            _tidyOptions = value.Options;
            _smartBehaviorOptions = value.Behavior;
            if (!_smartBehaviorOptions.PreferReversibleVerticalFill) _verticalFillManager.Track([], false);
        }
        if (value.Task != _taskPreferences)
        {
            value.Task.Save(_preferencesPath);
            _taskPreferences = value.Task;
        }
        if (value.UseReference != _referenceStore.Enabled) _referenceStore.SetEnabled(value.UseReference);
        _taskSession.Invalidate();
        if (value.Mode == _automaticMode) return;
        var previous = _automaticMode;
        if (!ConfigureAutomation(value.Mode, arrangeNow: true))
        {
            if (!ConfigureAutomation(previous)) _automaticMode = AutomaticLayoutMode.Off;
            throw new InvalidOperationException("无法启用自动整理监听。");
        }
        try { _settingsStore.SaveAutomaticLayoutMode(value.Mode); }
        catch
        {
            if (!ConfigureAutomation(previous)) _automaticMode = AutomaticLayoutMode.Off;
            throw;
        }
        _automaticMode = value.Mode;
    }

    internal void SetRecording(bool enabled)
    {
        if (!_recorder.Status.IsAvailable) throw new InvalidOperationException(_recorder.Status.Error ?? "记录器不可用。");
        if (enabled == !_recorder.Status.IsPaused) return;
        _settingsStore.RecordingEnabled = enabled;
        _recorder.TogglePause();
    }

    internal void OpenRecorderData() => _recorder.OpenDataDirectory();
    internal void ExportRecorderData(string path) => _recorder.ExportRecords(path);
    internal void ClearRecorderData() { _recorder.Clear(); _taskSession.Invalidate(); }
    private void OnRecorderStatusChanged(RecorderStatus status) => _recorderChanged?.Invoke(status);

    private bool ConfigureAutomation(AutomaticLayoutMode mode, bool arrangeNow = false)
    {
        if (mode != AutomaticLayoutMode.Off && !_autoTidyManager.IsAvailable) return false;
        if (!_workspaceMonitor.SetMode(mode, arrangeNow))
        {
            _autoTidyManager.Enabled = false;
            return false;
        }
        // Only the legacy light mode needs pointer sampling; full Smart does not.
        _autoTidyManager.Enabled = mode == AutomaticLayoutMode.LightAssist;
        return true;
    }

    private void OnAutomaticLayoutRequested(AutomaticLayoutRequest request)
    {
        if (request.Mode != _automaticMode) return;
        var route = AutomaticLayoutPolicy.Route(request.Mode, request.Trigger);
        if (route != LayoutRoute.Smart) return;
        var trigger = request.Trigger == LayoutTrigger.AfterGesture ? "automatic-gesture" : "automatic-workspace";
        RunTidy(showActivity: false, route.Value, trigger);
    }

    private void OnFollowHandRequested(ManualWindowGesture gesture)
    {
        if (_stopping.IsCancellationRequested || _automaticMode != AutomaticLayoutMode.LightAssist || _tidyRunning) return;
        _tidyRunning = true;
        try
        {
            var snapshot = _windowManager.Capture();
            var visible = _visibilityAnalyzer.SelectVisibleWorkingSet(snapshot);
            var moved = visible.FirstOrDefault(item => item.Window.Handle == gesture.WindowHandle);
            if (moved is null || moved.Window.WorkArea != gesture.WorkArea || !SameRect(moved.Window.VisualRect, gesture.EndRect)) return;
            var interaction = _autoTidyManager.CaptureInteractionContext(visible);
            var hint = IntentEvidence.Resolve(_referenceStore.Read(), moved.Window.WorkArea, moved.Window.Dpi, DateTimeOffset.UtcNow);
            var personal = SmartPersonalizationState.Default with { PreferredGapPixels = hint.GapPixels };
            var decision = FollowHandAssistant.Decide(gesture,
                visible.Where(v => v.Window.MonitorHandle == moved.Window.MonitorHandle).ToArray(), _tidyOptions, interaction, personal);
            if (!decision.ShouldApply || decision.TargetRect == moved.Window.VisualRect ||
                (!moved.Window.IsResizable && (decision.TargetRect.Width != moved.Window.VisualRect.Width ||
                    decision.TargetRect.Height != moved.Window.VisualRect.Height))) return;
            _autoTidyManager.SuppressFor(500);
            var assisted = new[] { new TidyMove(moved.Window, decision.TargetRect) };
            _workspaceMonitor.ApplicationRequested(snapshot, new(assisted, [], []));
            if (_stopping.IsCancellationRequested) return;
            var application = _windowManager.ApplyLayout(new(assisted, [], []));
            _journal.Record(snapshot, application.Plan, "follow-hand", application.Notes);
        }
        catch { /* Assistance must not interrupt the completed manual action. */ }
        finally { _tidyRunning = false; }
    }

    internal void RunTidy() => RunTidy(showActivity: true, LayoutRoute.ManualAlgorithm);

    private void RunTidy(bool showActivity, LayoutRoute route = LayoutRoute.ManualAlgorithm, string? source = null)
    {
        if (_stopping.IsCancellationRequested || _tidyRunning || (_autoTidyManager.IsGestureActive || NativeWindowActivity.IsMoving)) return;
        _tidyRunning = true;
        try
        {
            var tiling = route == LayoutRoute.FullTiling;
            var effectiveOptions = route == LayoutRoute.Smart ? _tidyOptions with { AlgorithmMode = TidyAlgorithmMode.Smart } : _tidyOptions;
            var smart = !tiling && effectiveOptions.AlgorithmMode == TidyAlgorithmMode.Smart;
            var trigger = source ?? (showActivity ? tiling ? "explicit-tiling" : smart ? "explicit" : "explicit-classic" : "classic-follow");
            var snapshot = _windowManager.Capture();
            var visibleWorkingSet = _visibilityAnalyzer.SelectVisibleWorkingSet(snapshot,
                includeStackAccess: smart);
            VideoBlackBarHint? videoHint = null;
            if (smart && _smartBehaviorOptions.RemoveVideoBlackBars)
                videoHint = _videoBlackBarDetector.TryDetect(visibleWorkingSet);
            IReadOnlyList<TidyMove> plan;
            IntentLayoutPlan? detailed = null;
            if (tiling)
            {
                detailed = LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, null, snapshot, null);
                plan = detailed.Moves;
            }
            else if (smart)
            {
                var taskContext = _taskSession.Capture(snapshot, _taskPreferences.Profile, Displays(snapshot),
                    _taskPreferences.Calibration, DateTimeOffset.UtcNow) with
                { PreferReversibleVerticalFill = _smartBehaviorOptions.PreferReversibleVerticalFill };
                if (videoHint is { Confidence: >= .82 })
                    taskContext = taskContext with { WindowHints = [new(videoHint.WindowHandle, PassiveVisual: true)], VideoHint = videoHint };
                detailed = LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, _referenceStore.Read(), snapshot, taskContext);
                plan = detailed.Moves;
                // The task objective replaces the old geometry-only fill pass. A video refinement
                // is retained only when task utility and screen-aware access survive.
                var beforeVideoPlan = plan;
                plan = VideoAspectPostProcessor.Refine(visibleWorkingSet, plan, effectiveOptions, _smartBehaviorOptions, videoHint);
                if (!IntentLayoutPlanner.TaskRefinementAcceptable(visibleWorkingSet, detailed, plan, effectiveOptions, taskContext, snapshot))
                    plan = detailed.Moves;
                if (videoHint is not null)
                {
                    var browser = visibleWorkingSet.FirstOrDefault(item => item.Window.Handle == videoHint.WindowHandle);
                    if (browser is not null)
                    {
                        var before = beforeVideoPlan.FirstOrDefault(move => move.Window.Handle == videoHint.WindowHandle)?.TargetVisualRect ?? browser.Window.VisualRect;
                        var after = plan.FirstOrDefault(move => move.Window.Handle == videoHint.WindowHandle)?.TargetVisualRect ?? browser.Window.VisualRect;
                        if (before != after) _videoBlackBarDetector.RecordApplied(videoHint.WindowHandle, after);
                    }
                }
            }
            else plan = _tidyEngine.CreatePlan(visibleWorkingSet, effectiveOptions);

            detailed = detailed is null ? new(plan, [], []) : detailed with { Moves = plan };
            _autoTidyManager.SuppressFor(650);
            _workspaceMonitor.ApplicationRequested(snapshot, detailed);
            if (_stopping.IsCancellationRequested) return;
            var application = _windowManager.ApplyLayout(detailed);
            detailed = application.Plan; plan = detailed.Moves;
            _verticalFillManager.Track(plan, smart && _smartBehaviorOptions.PreferReversibleVerticalFill);
            var layerOrders = detailed.Layers;
            if (plan.Count > 0 || layerOrders.Count > 0)
            {
                _undoPlan = plan; _undoLayers = layerOrders; _undoWasSmart = smart;
                _undoChanged?.Invoke(true);
            }
            if (smart)
                _taskSession.Requested(snapshot, detailed, DateTimeOffset.UtcNow);
            _journal.Record(snapshot, detailed, trigger, application.Notes);
            if (showActivity)
            {
                var kinds = string.Join("、", detailed.Groups.Select(g => KindName(g.Selected)).Distinct());
                var message = plan.Count == 0 && layerOrders.Count == 0
                    ? $"已检查 {visibleWorkingSet.Count} 个当前可见窗口，本次保留原状，未找到更合适的可执行方案。"
                    : $"{kinds}：已请求调整 {plan.Count} 个窗口、{layerOrders.Count} 组层级。";
                _report(application.Notes.Any(n => n.Contains("未")) ? message + " " + string.Join(" ", application.Notes) : message);
            }
        }
        catch (Exception exception)
        {
            _report($"整理失败：{exception.Message}");
        }
        finally { _tidyRunning = false; }
    }

    private static bool SameRect(RectI a, RectI b) =>
        Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 &&
        Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;

    internal void UndoTidy()
    {
        if (_stopping.IsCancellationRequested || _tidyRunning || (_autoTidyManager.IsGestureActive || NativeWindowActivity.IsMoving) || (_undoPlan.Count == 0 && _undoLayers.Count == 0))
        { _report("没有可撤销的整理。"); return; }
        try
        {
            var current = _windowManager.Capture().ToDictionary(w => w.Handle);
            var reverse = LayoutUndoPlanner.Create(new(_undoPlan, _undoLayers, []), current.Values.ToArray());
            _autoTidyManager.SuppressFor(650);
            _workspaceMonitor.ApplicationRequested(current.Values.ToArray(), reverse);
            if (_stopping.IsCancellationRequested) return;
            var application = _windowManager.ApplyLayout(reverse);
            if (_undoWasSmart)
                _taskSession.RejectExplicitly(application.Plan.Moves.Select(m => m.Window.Handle)
                    .Concat(application.Plan.Layers.SelectMany(l => l.FrontToBack.Select(w => w.Handle))));
            _journal.Record(current.Values.ToArray(), application.Plan, "undo", application.Notes);
            var skipped = _undoPlan.Count - application.Plan.Moves.Count;
            _undoPlan = []; _undoLayers = []; _undoWasSmart = false;
            _undoChanged?.Invoke(false);
            _report($"已请求还原 {application.Plan.Moves.Count} 个窗口；跳过 {skipped} 个已再次调整、关闭或状态改变的窗口。");
        }
        catch (Exception ex) { _report($"撤销失败：{ex.Message}"); }
    }

    private static string KindName(string kind) => kind switch
    {
        "full-tiling" => "完整平铺", "tiling-insufficient-space" => "可平铺空间不足，保留原状",
        "task-vertical-fill" => "可逆纵向填满",
        "task-edge" => "共同观看贴边", "task-fit" => "内容尺度调整", "task-bleed" => "边缘空间交换",
        "task-columns" => "保留列关系重排", "rescue" => "恢复可操作区域",
        "edge-row" => "叠边并排", "stack" => "成组叠放", "restack" => "调整前后层级",
        "columns" => "左右排布", "rows" => "上下排布", "grid" => "多行排布", "local" => "局部微调", _ => "保留当前关系",
    };

    private void OnLayoutVerified(LayoutRunObservation observation)
    {
        if (observation.Trigger is not ("explicit" or "automatic-gesture" or "automatic-workspace" or "explicit-tiling") ||
            observation.Actual is null || observation.Outcome == "intervened") return;
        if (observation.Trigger is "explicit" or "automatic-gesture" or "automatic-workspace")
        {
            if (observation.Outcome == "observed-match") _taskSession.Verify(_windowManager.Capture());
            else _taskSession.Invalidate();
        }
        var actual = observation.Actual.Windows;
        var matched = observation.Targets.Count(t => actual.Any(w => w.Id == t.Id && SameRect(w.Rect, t.Target)));
        var description = string.Join("、", observation.Groups.Select(g => KindName(g.Selected)).Distinct());
        _report(observation.Outcome == "observed-match"
            ? $"{description}：已确认 {matched} 个窗口的目标位置；层级已核对。"
            : $"{description}：{matched}/{observation.Targets.Length} 个位置匹配；其余可能被应用限制，诊断已记录。");
        if (observation.ApplyNotes.Any(n => n.Contains("未执行") || n.Contains("未套用") || n.Contains("未覆盖")))
            _report(string.Join(" ", observation.ApplyNotes));
    }

    private static TaskDisplay[] Displays(IReadOnlyList<WindowSnapshot> snapshot) =>
        Screen.AllScreens.Select((screen, index) =>
        {
            var work = screen.WorkingArea; var bounds = screen.Bounds;
            var area = new RectI(work.X, work.Y, work.Width, work.Height);
            var monitor = snapshot.FirstOrDefault(w => w.WorkArea == area)?.MonitorHandle ?? (nint)(-index - 1);
            return new TaskDisplay(monitor, area, new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        }).ToArray();
}
