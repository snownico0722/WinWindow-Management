using static NeatWin.App.LayoutStrings;
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
    private readonly System.Windows.Forms.Timer _manualArrangeTimer = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _placementShadowTimer = new() { Interval = PlacementShadowReplay.IntervalMs };
    private readonly SolverFeedbackSession _solverFeedback = new();
    private readonly PlacementShadowReplay _placementShadow = new();
    private PlacementShadowReplay.Example[]? _placementShadowBatch;
    private int _placementShadowEpoch;
    private readonly Action<string> _report;
    private readonly Action<bool>? _undoChanged;
    private readonly Action<RecorderStatus>? _recorderChanged;
    private readonly ReversibleVerticalFillManager _verticalFillManager;
    private readonly AutoTidyManager _autoTidyManager;
    private readonly RecorderSession _recorder;
    private readonly WindowBehaviorMonitor _behaviorMonitor;
    private bool _useImportedPlacement;
    private TidyOptions _tidyOptions;
    private SmartBehaviorOptions _smartBehaviorOptions;
    private readonly IntentReferenceStore _referenceStore;
    private bool _undoWasSmart, _undoReclaim;
    private string? _undoRequest;
    private IReadOnlyList<TidyMove> _undoPlan = [];
    private IReadOnlyList<WindowLayerOrder> _undoLayers = [];
    private readonly LayoutRunJournal _journal;
    private bool _placementRetryPending;
    private CompiledLayoutRules? _lastRules;
    private LayoutFocus? _lastFocus;
    private WorkingPairRule[] _lastPairs = [];
    private WindowSizeFloor[] _lastFloors = [];
    private PlacementPreference[] _lastPlacements = [];
    private TaskWindowHint[] _lastLiveHints = [];
    private readonly TaskLayoutSession _taskSession = new();
    private readonly GesturePreviewCache _gesturePreview = new();
    private TaskPreferences _taskPreferences;
    private AutomaticLayoutMode _automaticMode;
    private readonly WorkspaceAutoTidyMonitor _workspaceMonitor;
    private readonly TaskSurfaces _surfaces;
    private SurfaceRule[]? _surfaceRules;
    private bool _tidyRunning;
    private readonly ForegroundHistory _focus;

    internal NeatWinApplicationContext(string directory, Action<string> report, Action<RecorderStatus>? recorderChanged,
        Action<bool>? undoChanged, CancellationToken stopping, ForegroundHistory? focus = null,
        Action<LayoutRunObservation>? snapshotObserver = null, Action<string, GestureEvent>? gestureObserver = null,
        Func<bool>? snapshotWantsGestures = null, Func<nint, uint, long, string, bool>? keepRunning = null,
        Action<SurfaceEvent>? surfaceObserver = null, Func<bool>? snapshotWantsSurfaces = null,
        Action<DesktopInteraction>? interactionObserver = null, Action<SolverFeedback>? feedbackObserver = null)
    {
        _stopping = stopping;
        _focus = focus ?? new();
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
        _autoTidyManager = new AutoTidyManager();
        _verticalFillManager = new(_autoTidyManager, handle => _lastLiveHints.Any(h => h.Handle == handle && h.TextFocus));
        _recorder = new RecorderSession(_referenceStore, _settingsStore.RecordingEnabled) {
            VisualSampling = _settingsStore.VisualSampling, GestureObserver = gestureObserver, SnapshotWantsGestures = snapshotWantsGestures,
            InteractionObserver = interactionObserver, SurfaceObserver = surfaceObserver, SnapshotWantsSurfaces = snapshotWantsSurfaces,
            ReadPlacementFocus = (windows, tick) => _focus.Resolve(windows, tick)
        };
        _recorder.WindowRetired = handle =>
        {
            if (_placementShadowBatch?.Any(example => example.Scope.Includes(handle)) == true) _placementShadowEpoch++;
            _solverFeedback.Retire(handle);
            _placementShadow.Retire(handle);
            _taskSession.Retire(handle);
        };
        _journal = new LayoutRunJournal(_windowManager, _referenceStore, _recorder, snapshotObserver);
        _behaviorMonitor = new(_windowManager, _referenceStore, _settingsStore, _recorder,
            () => _stopping.IsCancellationRequested || _tidyRunning || NativeWindowActivity.PointerBusy, _focus, keepRunning,
            () => AutomaticLayoutPolicy.AllowsAutomaticDismissal(_automaticMode));
        _useImportedPlacement = _settingsStore.UseImportedPlacement;
        _surfaceRules = _referenceStore.Rules.Read().SurfaceRules;
        _surfaces = new TaskSurfaces { CaptureOverlay = _recorder.CaptureOverlay, IgnoreVisit = _focus.IgnoreLocalVisit, Role = w => _recorder.SemanticRole(w, default), ChatPurpose = NativeWindowActivity.ChatSurface, AppKey = _recorder.TryReadAppKey,
            Rules = () => _useImportedPlacement ? _surfaceRules : null,
            ReadContextPress = () => _recorder.InputHistory.ContextAt(Environment.TickCount64),
            JustClicked = w => _focus.RecentlyClicked(w, Environment.TickCount64), Changed = _recorder.SurfaceChanged,
            ResetObserved = _recorder.ResetSurfaceObservations };
        _recorder.IsLocalSurface = w => _surfaces.IsLocal(w) || _recorder.CaptureOverlay(w);
        _workspaceMonitor = new WorkspaceAutoTidyMonitor(fresh => fresh ? _windowManager.Capture() : _windowManager.CaptureCoalesced(),
            () => _tidyRunning || _autoTidyManager.IsGestureInProgress, _focus, _surfaces, _recorder.WorkspaceChanged);
        _autoTidyManager.PointerObserved += _recorder.PointerObserved;
        _autoTidyManager.TaskInput += (target, pid, now, click) =>
        {
            // 点击和滚动可以更新任务关系。桌面几何只由独立的空间变化或松手调度，持续操作同一窗口不能再次授权搬动它。
            _focus.Interact(target, pid, now, click);
            _recorder.RecordPointer(target, pid);
        };
        _manualArrangeTimer.Tick += (_, _) => RunTidy();
        _placementShadowTimer.Tick += OnPlacementShadowTick;
        _placementShadowTimer.Start();
        _autoTidyManager.GestureStarted += handle =>
        {
            _journal.MarkGesture(handle);
            _workspaceMonitor.GestureStarted(handle);
            if (_automaticMode != AutomaticLayoutMode.Off)
                _gesturePreview.Begin(_tidyOptions.SmartStrength,
                    () => new(new WindowManager().Capture(), _referenceStore.Read()));
        };
        _autoTidyManager.GesturePointerReleased += _workspaceMonitor.PointerReleased;
        _autoTidyManager.GestureProgress += gesture => _gesturePreview.Offer(gesture, Environment.TickCount64);
        _autoTidyManager.GestureObserved += gesture =>
        {
            _journal.MarkGesture(gesture.WindowHandle);
            if (gesture.StartRect.Width != gesture.EndRect.Width || gesture.StartRect.Height != gesture.EndRect.Height)
                _videoBlackBarDetector.Forget(gesture.WindowHandle);
            try
            {
                var releasedDesktop = _windowManager.Capture();
                if (releasedDesktop.Any(w => w.Handle == gesture.WindowHandle && _surfaces.IsLocal(w)))
                { _solverFeedback.Clear(); _workspaceMonitor.CancelLocalGesture(); return; }
                if (releasedDesktop.FirstOrDefault(w => w.Handle == gesture.WindowHandle && w.ProcessId == gesture.ProcessId &&
                    w.IsForeground && w.IsManageable && !w.IsTopmost) is { } moved)
                {
                    _taskSession.Gesture(gesture, DateTimeOffset.UtcNow);
                    _focus.Placed(moved, gesture, Environment.TickCount64, releasedDesktop);
                    if (_referenceStore.Enabled)
                    {
                        // 手势比已在后台计算的历史规则更新。旧批次不能覆盖已经发生的纠正。
                        if (_placementShadowBatch?.Any(example => example.Axes.Any(axis => PlacementShadowReplay.Contradicts(axis, gesture))) == true)
                            _placementShadowEpoch++;
                        var working = releasedDesktop.Where(w => w.Handle == moved.Handle || !_surfaces.IsLocal(w)).ToArray();
                        if (_solverFeedback.Observe(gesture, working, Environment.TickCount64, DateTimeOffset.UtcNow) is { } feedback)
                        {
                            feedbackObserver?.Invoke(feedback);
                            _referenceStore.Placement.LearnFeedback(feedback, _recorder.CanWriteDiagnostics);
                            // 松手的几何辅助先完成，样本在消息队列下一轮写盘。暂时写失败可在维护或退出时重试。
                            if (_recorder.CanWriteDiagnostics) _dispatch.BeginInvoke((Action)_referenceStore.Placement.FlushFeedback);
                        }
                        _placementShadow.Observe(gesture, moved, working, Environment.TickCount64);
                    }
                }
                else _solverFeedback.Clear();
                _workspaceMonitor.GestureCompleted(gesture, releasedDesktop);
            }
            catch (Exception ex) { Trace.TraceWarning($"Gesture snapshot failed: {ex.Message}"); }
        };
        _automaticMode = _settingsStore.LoadAutomaticLayoutMode();
        if (!ConfigureAutomation(_automaticMode, arrangeNow: true)) _automaticMode = AutomaticLayoutMode.Off;
        _recorder.SetLiveSensing(_automaticMode);
        _workspaceMonitor.Requested += OnAutomaticLayoutRequested;
        _autoTidyManager.TidyRequested += OnFollowHandRequested;
        _journal.Completed += OnLayoutVerified;
        _journal.ReplanAroundBlockedWindow += () =>
        {
            if (!AutomaticLayoutPolicy.WatchesWorkspace(_automaticMode)) return;
            _placementRetryPending = true;
            _workspaceMonitor.RequestCurrent();
        };
        _workspaceMonitor.Failed += ex => _report(LayoutText.Format("ObservationFailed", ex.Message));
        _recorder.StatusChanged += OnRecorderStatusChanged;


    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gesturePreview.Clear();
            _manualArrangeTimer.Dispose();
            _placementShadowTimer.Dispose();
            ClearPlacementShadow();
            _autoTidyManager.TidyRequested -= OnFollowHandRequested;
            _workspaceMonitor.Dispose(); _verticalFillManager.Dispose(); _autoTidyManager.Dispose(); _journal.Dispose(); _behaviorMonitor.Dispose();
            _referenceStore.Placement.FlushFeedback();
            _recorder.StatusChanged -= OnRecorderStatusChanged; _recorder.Dispose();
            _dispatch.Dispose();
        }
        base.Dispose(disposing);
    }

    internal Task DispatchAsync(Action action) => _dispatch.InvokeAsync(action);
    internal Task<T> ReadAsync<T>(Func<T> read) => _dispatch.InvokeAsync(read);

    internal LayoutPreferences Preferences => new(_automaticMode, _tidyOptions, _smartBehaviorOptions,
        _taskPreferences, _referenceStore.Enabled, _recorder.VisualSampling, _useImportedPlacement);
    internal RecorderStatus Recorder => _recorder.Status;
    internal BehaviorSettings Behavior => _behaviorMonitor.Status;
    internal void SetBehaviorLearning(bool enabled) => _behaviorMonitor.SetLearning(enabled);
    internal void SetBehaviorEnabled(string id, bool enabled) => _behaviorMonitor.SetEnabled(id, enabled);
    internal void ImportBehaviorRules(string path) => _behaviorMonitor.ImportRules(path);
    internal void ImportPlacementRules(string path)
    {
        _referenceStore.ImportPlacementRules(path);
        ClearPlacementShadow();
        _settingsStore.UseImportedPlacement = _useImportedPlacement = true;
        _surfaceRules = _referenceStore.Rules.Read().SurfaceRules;
        _surfaces.Reset();
        _taskSession.Invalidate();
    }

    internal void ApplyPreferences(LayoutPreferences value)
    {
        if (value.Options != _tidyOptions || value.Behavior != _smartBehaviorOptions)
        {
            _settingsStore.SaveLayoutOptions(value.Options, value.Behavior);
            _tidyOptions = value.Options;
            _smartBehaviorOptions = value.Behavior;
            if (!_smartBehaviorOptions.RemoveVideoBlackBars) _videoBlackBarDetector.Reset();
            if (!_smartBehaviorOptions.PreferReversibleVerticalFill) _verticalFillManager.Track([], false);
        }
        if (value.Task != _taskPreferences)
        {
            value.Task.Save(_preferencesPath);
            _taskPreferences = value.Task;
        }
        if (value.VisualSampling != _recorder.VisualSampling)
        {
            _settingsStore.VisualSampling = value.VisualSampling;
            _recorder.VisualSampling = value.VisualSampling;
        }
        if (value.UseReference != _referenceStore.Enabled) _referenceStore.SetEnabled(value.UseReference);
        if (value.UseImportedPlacement != _useImportedPlacement)
        {
            _settingsStore.UseImportedPlacement = value.UseImportedPlacement;
            _useImportedPlacement = value.UseImportedPlacement;
            _surfaces.Reset();
        }
        _taskSession.Invalidate();
        ClearPlacementShadow();
        _gesturePreview.Clear();
        if (value.Mode == _automaticMode) return;
        var previous = _automaticMode;
        if (!ConfigureAutomation(value.Mode, arrangeNow: true))
        {
            if (!ConfigureAutomation(previous)) _automaticMode = AutomaticLayoutMode.Off;
            throw new InvalidOperationException(LayoutText.Get("MonitorUnavailable"));
        }
        try { _settingsStore.SaveAutomaticLayoutMode(value.Mode); }
        catch
        {
            if (!ConfigureAutomation(previous)) _automaticMode = AutomaticLayoutMode.Off;
            throw;
        }
        _behaviorMonitor.ResetIdleHistory();
        _automaticMode = value.Mode;
        _recorder.SetLiveSensing(_automaticMode);
    }

    internal void SetRecording(bool enabled)
    {
        if (!_recorder.Status.IsAvailable) throw new InvalidOperationException(_recorder.Status.Error ?? LayoutText.Get("RecorderUnavailable"));
        if (enabled == !_recorder.Status.IsPaused) return;
        _settingsStore.RecordingEnabled = enabled;
        _recorder.TogglePause();
        ConfigureTaskInput(_automaticMode);
        _behaviorMonitor.RecordingChanged();
    }

    internal void OpenRecorderData() => _recorder.OpenDataDirectory();
    internal void ExportRecorderData(string path) => _recorder.ExportRecords(path);
    internal void ClearRecorderData()
    {
        _recorder.Clear();
        ClearPlacementShadow();
        _behaviorMonitor.ClearLearning();
        _taskSession.Invalidate();
    }
    private void ClearPlacementShadow()
    {
        _solverFeedback.Clear();
        _placementShadowEpoch++;
        _placementShadow.Clear();
    }

    private async void OnPlacementShadowTick(object? sender, EventArgs args)
    {
        _referenceStore.Placement.FlushFeedback();
        if (_placementShadowBatch != null || _stopping.IsCancellationRequested || !_referenceStore.Enabled) return;
        var batch = _placementShadow.Take();
        if (batch.Length == 0) return;
        int epoch = _placementShadowEpoch;
        _placementShadowBatch = batch;
        try
        {
            // 线程池只处理已捕获的快照。结束时仅发布规则，不调用窗口或自动整理入口。
            var findings = await Task.Run(() => PlacementShadowReplay.Analyze(batch, _stopping), _stopping);
            if (epoch == _placementShadowEpoch && !_stopping.IsCancellationRequested && _referenceStore.Enabled)
                _placementShadow.Publish(findings, Environment.TickCount64);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception ex) { Trace.TraceWarning($"Placement shadow replay unavailable: {ex.Message}"); }
        finally { _placementShadowBatch = null; }
    }

    private void OnRecorderStatusChanged(RecorderStatus status) => _recorderChanged?.Invoke(status);

    private bool ConfigureAutomation(AutomaticLayoutMode mode, bool arrangeNow = false)
    {
        if ((AutomaticLayoutPolicy.UsesFollowHand(mode) || mode == AutomaticLayoutMode.FullAssist) && !_autoTidyManager.IsAvailable) return false;
        _placementRetryPending = false;
        _gesturePreview.Clear();
        if (!_workspaceMonitor.SetMode(mode, arrangeNow))
        {
            _autoTidyManager.Enabled = false;
            _autoTidyManager.SetGesturePreview(false);
            _autoTidyManager.SetAttentionInput(false);
            return false;
        }
        _autoTidyManager.SetGesturePreview(mode != AutomaticLayoutMode.Off);
        _autoTidyManager.Enabled = AutomaticLayoutPolicy.UsesFollowHand(mode);
        ConfigureTaskInput(mode);
        return true;
    }

    internal void RefreshPointerObservation()
    {
        _autoTidyManager.SetPointerObservation(_recorder.SnapshotWantsSurfaces?.Invoke() == true);
        _recorder.ReportInteractionCoverage(_autoTidyManager.PointerHookAvailable);
    }
    private void ConfigureTaskInput(AutomaticLayoutMode mode)
    {
        _autoTidyManager.SetAttentionInput(AutomaticLayoutPolicy.UsesFullPlanner(mode) ||
            AutomaticLayoutPolicy.WatchesOpenings(mode) || _recorder.CanWriteDiagnostics);
        RefreshPointerObservation();
        _recorder.PointerCaptureAvailable = _autoTidyManager.TaskInputAvailable;
    }

    private void OnAutomaticLayoutRequested(AutomaticLayoutRequest request)
    {
        if (request.Mode != _automaticMode) return;
        var route = AutomaticLayoutPolicy.Route(request.Mode, request.Trigger);
        if (route == LayoutRoute.LocalOpening && request.LocalOpening is { } local)
        {
            var opened = _recorder.WorkspaceChanged(local.Incoming, "confirmed-opening");
            RunTidy(showActivity: false, LayoutRoute.LocalOpening, "automatic-opening", cause: request.Cause, localOpening: local, changeEvents: opened is long id ? [id] : null);
            return;
        }
        if (route != LayoutRoute.Smart) return;
        var trigger = request.Trigger == LayoutTrigger.AfterGesture ? "automatic-gesture" : "automatic-workspace";
        bool mayReplan = !_placementRetryPending;
        _placementRetryPending = false;
        RunTidy(showActivity: false, route.Value, trigger, mayReplan, request.Opening, request.Cause, changeEvents: request.ChangeEvents, omittedChanges: request.OmittedChanges);
    }

    private void OnFollowHandRequested(ManualWindowGesture gesture)
    {
        if (_stopping.IsCancellationRequested || !AutomaticLayoutPolicy.UsesFollowHand(_automaticMode) || _tidyRunning) return;
        _tidyRunning = true;
        _solverFeedback.Clear();
        try
        {
            var snapshot = _windowManager.Capture();
            var visible = _visibilityAnalyzer.SelectVisibleWorkingSet(snapshot);
            var moved = visible.FirstOrDefault(item => item.Window.Handle == gesture.WindowHandle);
            if (moved == null || !moved.Window.IsForeground || moved.Window.ProcessId != gesture.ProcessId ||
                moved.Window.WorkArea != gesture.WorkArea || !SameRect(moved.Window.VisualRect, gesture.EndRect)) return;
            var interaction = _autoTidyManager.CaptureInteractionContext(visible);
            if (interaction.ForegroundWindow != gesture.WindowHandle || !NativeWindowActivity.AllowsAutomaticPlacement(interaction.ForegroundWindow)) return;
            var preview = _gesturePreview.Take(gesture, snapshot, interaction.ForegroundWindow, Environment.TickCount64);
            var hint = IntentEvidence.Resolve(_referenceStore.Read(), moved.Window.WorkArea, moved.Window.Dpi, DateTimeOffset.UtcNow);
            var personal = SmartPersonalizationState.Default with { PreferredGapPixels = hint.GapPixels };
            var focus = _focus.Resolve(snapshot, Environment.TickCount64, out var attention);
            var snapContext = new TaskLayoutContext(Focus: focus, Attention: attention, GestureAnchor: moved.Window.Handle);
            if (_useImportedPlacement && _referenceStore.Rules.Read() is { Scenes.Length: > 0 } rules)
            {
                // 松手已有明确对象。实例用途只用来选择适用的个人场景，轻度辅助仍只调整手下这扇窗。
                var use = rules.Scenes.Any(r => r.ForegroundUse != null || r.ReferenceUse != null)
                    ? _recorder.ObservePlacementUse(snapshot, Environment.TickCount64, focus) : null;
                snapContext = snapContext with { Rules = rules, InstanceUses = _recorder.LiveInstanceUses(snapshot, use), AppKeys = snapshot.Where(w => w.IsManageable || w.SurfaceState != null)
                    .ToDictionary(w => w.Handle, _recorder.TryReadAppKey) };
                snapContext = snapContext with { Scene = ActiveLayoutScene.Resolve(snapshot, snapContext, includeDefaults: false) };
            }
            var decision = FollowHandAssistant.Decide(gesture,
                visible.Where(v => v.Window.MonitorHandle == moved.Window.MonitorHandle).ToArray(), _tidyOptions, interaction, personal, preview, snapContext);
            if (!decision.ShouldApply || decision.TargetRect == moved.Window.VisualRect ||
                (!moved.Window.IsResizable && (decision.TargetRect.Width != moved.Window.VisualRect.Width ||
                    decision.TargetRect.Height != moved.Window.VisualRect.Height))) return;
            var target = _focus.LimitLiveReturn(moved.Window, decision.TargetRect, Environment.TickCount64);
            if (target == moved.Window.VisualRect || target != decision.TargetRect && target.Intersect(gesture.WorkArea) != target) return;
            var assisted = new IntentLayoutPlan([new(moved.Window, target)], [], []) { Scene = snapContext.Scene };
            if (assisted.Scene?.Allows(snapshot, assisted) == false) return;
            _autoTidyManager.SuppressFor(500);
            _workspaceMonitor.ApplicationRequested(snapshot, assisted);
            if (_stopping.IsCancellationRequested) return;
            var application = _windowManager.ApplyLayout(assisted, interaction.ForegroundWindow, gestureWindow: gesture.WindowHandle, allowsMove: AllowsCurrentReturn);
            _focus.Submitted(application.Plan, snapshot, Environment.TickCount64);
            _placementShadow.Submitted(null, application.Plan, Environment.TickCount64);
            _journal.Record(snapshot, application, "follow-hand", releaseInput: gesture, reusedPreview: preview?.Used == true);
        }
        catch { /* Assistance must not interrupt the completed manual action. */ }
        finally { _tidyRunning = false; }
    }

    private bool AllowsCurrentReturn(TidyMove move) =>
        _focus.LimitLiveReturn(move.Window, move.TargetVisualRect, Environment.TickCount64) == move.TargetVisualRect;

    internal void RunTidy() => RunTidy(showActivity: true, LayoutRoute.ManualAlgorithm);

    private void RunTidy(bool showActivity, LayoutRoute route = LayoutRoute.ManualAlgorithm, string? source = null,
        bool allowPlacementReplan = false, ForegroundHistory.Opening? opening = null, WorkspaceChangeCause cause = WorkspaceChangeCause.None,
        ForegroundHistory.NewTaskOpening? localOpening = null, long[]? changeEvents = null, int omittedChanges = 0)
    {
        if (_stopping.IsCancellationRequested) return;
        if (_tidyRunning || _autoTidyManager.IsGestureInProgress || NativeWindowActivity.IsMoving)
        {
            if (showActivity) _manualArrangeTimer.Start();
            return;
        }
        if (showActivity) _manualArrangeTimer.Stop();
        _solverFeedback.Clear();
        _tidyRunning = true;
        try
        {
            var tiling = route == LayoutRoute.FullTiling;
            bool localOnly = route == LayoutRoute.LocalOpening;
            if (localOnly && (localOpening == null || !AutomaticLayoutPolicy.WatchesOpenings(_automaticMode))) return;
            var effectiveOptions = route is LayoutRoute.Smart or LayoutRoute.LocalOpening
                ? _tidyOptions with { AlgorithmMode = TidyAlgorithmMode.Smart } : _tidyOptions;
            var smart = !tiling && effectiveOptions.AlgorithmMode == TidyAlgorithmMode.Smart;
            var explicitSmart = smart && route == LayoutRoute.ManualAlgorithm;
            var fullDesktop = explicitSmart || (smart && AutomaticLayoutPolicy.WatchesWorkspace(_automaticMode));
            var trigger = source ?? (showActivity ? tiling ? "explicit-tiling" : smart ? "explicit" : "explicit-classic" : "classic-follow");
            bool workspaceObservation = trigger == "automatic-workspace" || localOnly;
            var foreground = NativeMethods.GetForegroundWindow();
            if (!showActivity && !NativeWindowActivity.AllowsAutomaticPlacement(foreground))
            {
                _workspaceMonitor.RequestCurrent(cause);
                return;
            }
            var snapshot = _windowManager.Capture();
            if (showActivity) _surfaces.Update(snapshot, null);
            var physicalFront = snapshot.FirstOrDefault(w => w.IsForeground);
            bool throughCapture = trigger == "automatic-workspace" && physicalFront != null &&
                physicalFront.Handle == foreground && _surfaces.IsCaptureOverlay(physicalFront);
            if (!showActivity && !throughCapture && physicalFront != null && _surfaces.IsLocal(physicalFront)) return;
            // 截图事件本身从未发起这次请求。与此同时发生的独立变化仍可依据原工作窗求解，
            // 下发时继续核验真正的前台 HWND，原生移动使用非激活路径。
            var taskForeground = throughCapture ? _workspaceMonitor.WorkingForeground : foreground;
            snapshot = snapshot.Select(w => _surfaces.IsLocal(w)
                ? w with { IsManageable = false, SurfaceState = null, IsForeground = !throughCapture && w.IsForeground }
                : throughCapture ? w with { IsForeground = w.Handle == taskForeground } : w).ToArray();
            if (workspaceObservation && !snapshot.Any(w => w.IsForeground && w.Handle == taskForeground)) return;
            if (trigger == "automatic-gesture" &&
                (_autoTidyManager.IsGestureInProgress || _autoTidyManager.LastGesture is not { } ended ||
                 foreground != ended.WindowHandle || !snapshot.Any(w => w.IsForeground && w.Handle == ended.WindowHandle &&
                     w.ProcessId == ended.ProcessId && w.IsManageable)))
            {
                _workspaceMonitor.RequestCurrent(cause);
                return;
            }
            long capturedAt = Environment.TickCount64;
            if (workspaceObservation && _focus.NavigationFor(snapshot, capturedAt)?.SettleUntil > capturedAt)
            {
                _workspaceMonitor.RequestCurrent(cause);
                return;
            }
            if (smart && _smartBehaviorOptions.RemoveVideoBlackBars && trigger == "automatic-workspace" &&
                _videoBlackBarDetector.Fits.AwaitingGeometry(snapshot, BrowserVideoBlackBarDetector.ReadPage, capturedAt))
            {
                _workspaceMonitor.RequestCurrent(cause);
                return;
            }
            var visibleWorkingSet = fullDesktop
                ? _visibilityAnalyzer.SelectForManualArrange(snapshot, effectiveOptions.RescueOffscreenWindows)
                : _visibilityAnalyzer.SelectVisibleWorkingSet(snapshot, includeStackAccess: smart);
            VideoBlackBarHint? videoHint = null;
            (nint Window, BrowserBarObservation Detail)? browserBars = null;
            // 点击后开窗和拖动松手都对延迟敏感。这两条路径只复用已有视频测量，让窗口落位先于下一轮采样。
            if (smart && !localOnly && _smartBehaviorOptions.RemoveVideoBlackBars)
            {
                videoHint = _videoBlackBarDetector.TryDetect(visibleWorkingSet, cachedOnly: opening != null || trigger == "automatic-gesture");
                if (_videoBlackBarDetector.LastObservation is { } observation)
                    browserBars = (_videoBlackBarDetector.ObservationWindow, observation);
            }
            IReadOnlyList<TidyMove> plan;
            IntentLayoutPlan? detailed = null;
            TaskLayoutContext? recordedContext = null, reclaimContext = null;
            IntentLayoutPlan? baseline = null;
            Func<IntentLayoutPlan>? diagnosticBaseline = null;
            ForegroundReading? initialReading = null, workspaceReading = null;
            bool reusedPreview = false;
            IntentLayoutPlan? scored = null;
            PlacementShadowReplay.Frame? shadowFrame = null;
            if (tiling)
            {
                detailed = LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, null, snapshot, null);
                plan = detailed.Moves;
            }
            else if (smart)
            {
                _recorder.SetLiveSensing(_automaticMode, manualRequest: explicitSmart);
                var rules = _referenceStore.Rules.Read();
                _surfaceRules = rules.SurfaceRules;
                var activities = _recorder.LiveActivities(snapshot);
                var focusObservedAt = DateTimeOffset.UtcNow;
                var focus = _focus.Resolve(snapshot, Environment.TickCount64, out var attention);
                var liveHints = _recorder.LiveHints(snapshot);
                liveHints = _journal.Feedback.Bind(snapshot, liveHints, Environment.TickCount64);
                bool pairSmart = explicitSmart || workspaceObservation || trigger == "automatic-gesture";
                var floors = _recorder.BindSizeFloors(snapshot);
                var observedFocus = focus;
                var front = snapshot.FirstOrDefault(w => w.IsForeground);
                var reading = ForegroundReading.Read(front);
                if (workspaceObservation && reading.LastInput.HasValue) workspaceReading = reading;
                var frontRole = front == null ? WindowUseRole.Unknown : _recorder.SemanticRole(front, reading);
                if (workspaceObservation && !reading.Caret && _recorder.AwaitingOfficeSurface(reading.Root))
                {
                    _workspaceMonitor.RequestCurrent(cause);
                    return;
                }
                if (localOpening != null &&
                    (front == null || reading.Root != front.Handle || focus.Main != front.Handle ||
                     !_focus.ConsumeLocalOpening(localOpening, snapshot, Environment.TickCount64, reading.LastInput) ||
                     frontRole == WindowUseRole.Note || WindowUseRoles.IsFloating(frontRole) ||
                     liveHints.Any(h => h.Anchor && h.Handle == front.Handle))) return;
                if (opening != null)
                {
                    var incoming = opening.Incoming;
                    var resident = opening.Resident;
                    bool matches = front?.Handle == incoming.Handle && front.ProcessId == incoming.ProcessId &&
                        front.VisualRect == incoming.VisualRect && front.WorkArea == incoming.WorkArea &&
                        snapshot.Any(w => w.Handle == resident.Handle && w.ProcessId == resident.ProcessId && w.IsManageable &&
                            w.VisualRect == resident.VisualRect && w.MonitorHandle == front.MonitorHandle && w.WorkArea == front.WorkArea);
                    // 前面短暂的输入、导航或测量门槛可以重试。到此已核对现场，本次开窗证据只能消费一次。
                    _focus.Dispatched(opening);
                    if (!matches || focus.Main != incoming.Handle || focus.Navigation != null ||
                        focus.Companion != 0 && focus.Companion != resident.Handle)
                    {
                        _workspaceMonitor.RequestCurrent(cause);
                        return;
                    }
                    if (frontRole == WindowUseRole.Note || WindowUseRoles.IsFloating(frontRole) ||
                        attention.GetValueOrDefault(incoming.Handle)?.ManualFloatPreference > 0 ||
                        liveHints.Any(h => h.Anchor && (h.Handle == incoming.Handle || h.Handle == resident.Handle)))
                    {
                        _workspaceMonitor.RequestCurrent(cause);
                        return;
                    }
                    focus = new(incoming.Handle, resident.Handle, focus.Switches,
                        opening.ResidentLeft ? "opening-resident-left" : "opening-resident-right");
                }
                var observedPair = trigger == "automatic-gesture" && focus.Main != foreground ? null : focus;
                var usage = pairSmart ? _recorder.ObservePlacementUse(snapshot, capturedAt, observedFocus) : null;
                var learned = pairSmart ? _recorder.Preferences(snapshot, focus, usage) : [];
                var instanceUses = pairSmart ? _recorder.LiveInstanceUses(snapshot, usage) : null;
                var slots = pairSmart && _useImportedPlacement ? _recorder.BindSlots(rules, snapshot, observedPair, instanceUses) : [];
                var placements = CompiledLayoutRules.ApplySlots(learned, slots);
                var pairs = pairSmart && _useImportedPlacement ? _recorder.BindRules(rules, snapshot, liveHints, activities, observedPair, instanceUses) : [];
                // An interrupted opening may still expose the resident by adjusting it on
                // its own side. Only an idle input queue authorizes moving the incoming frame.
                bool firstPlacement = localOnly || trigger == "automatic-workspace" && front != null && reading.Root == front.Handle &&
                    reading.LastInput.HasValue && (opening != null ? _focus.OpeningStillIdle(opening, Environment.TickCount64, reading.LastInput) :
                        _focus.TakeInitialPlacement(front, focus, Environment.TickCount64, reading.LastInput));
                liveHints = WindowUseRoles.Bind(snapshot, liveHints, focus, attention, placements,
                    window => window.Handle == front?.Handle ? frontRole : _recorder.SemanticRole(window, reading), reading.Caret ? reading.Root : 0, activities,
                    reading.Root, firstPlacement ? front!.Handle : 0, _taskPreferences.Profile);
                if (liveHints.Any(h => h.InitialTextPlacement)) initialReading = reading;
                if (_useImportedPlacement && _lastRules != null && !ReferenceEquals(rules, _lastRules))
                    _taskSession.Invalidate();
                else if (!_lastPairs.SequenceEqual(pairs) || !_lastFloors.SequenceEqual(floors) ||
                    !_lastPlacements.SequenceEqual(placements) ||
                    focus.Main != _lastFocus?.Main || focus.Companion != _lastFocus?.Companion || focus.Evidence != _lastFocus?.Evidence ||
                    !(focus.NavigationAncestors ?? []).SequenceEqual(_lastFocus?.NavigationAncestors ?? []) ||
                    !ActivityHints.Equivalent(_lastLiveHints, liveHints))
                    _taskSession.InvalidateExactRepeat();
                _lastPairs = pairs; _lastFloors = floors; _lastPlacements = placements;
                _lastRules = rules;
                _lastFocus = focus;
                _lastLiveHints = liveHints;
                var taskContext = _taskSession.Capture(snapshot, _taskPreferences.Profile, Displays(snapshot),
                    _taskPreferences.Calibration, DateTimeOffset.UtcNow, explicitSmart) with
                {
                    PreferReversibleVerticalFill = _smartBehaviorOptions.PreferReversibleVerticalFill,
                    Focus = focus,
                    AllowGestureAssist = trigger == "automatic-gesture",
                    IsWorkspaceObservation = workspaceObservation,
                    WorkspaceCause = cause,
                    TraceCandidateAxes = _recorder.CanWriteDiagnostics || _recorder.SnapshotWantsGestures?.Invoke() == true,
                    ObservedChangeEvents = changeEvents, OmittedChanges = omittedChanges,
                    ReadingInput = _recorder.InputHistory.Read(snapshot, capturedAt),
                    OriginSide = workspaceObservation ? _focus.ObserveWorkingSide(snapshot, capturedAt) : 0,
                    FirstPlacementWindow = firstPlacement && opening == null ? front!.Handle : 0,
                    LocalPlacementWindow = localOpening?.Incoming.Handle ?? 0,
                    NavigationObservedAt = focus.Navigation is { Length: > 0 } ? focusObservedAt : null,
                    Attention = attention,
                    WindowHints = liveHints,
                    VideoHint = videoHint is { Confidence: >= .82 } ? videoHint with { Tendency = _smartBehaviorOptions.VideoBlackBarTendency } : null,
                    BrowserBarSample = browserBars,
                    VideoFits = _smartBehaviorOptions.RemoveVideoBlackBars
                        ? _videoBlackBarDetector.Fits.Read(snapshot, BrowserVideoBlackBarDetector.ReadPage) : null,
                    Rules = _useImportedPlacement ? rules : rules with { Pairs = [], RolePairs = [], AppSlots = [], Scenes = null, SurfaceRules = null }, Activities = activities, InstanceUses = instanceUses,
                    AppKeys = _useImportedPlacement && rules.Scenes is { Length: > 0 } ? snapshot.Where(w => w.IsManageable || w.SurfaceState != null)
                        .ToDictionary(w => w.Handle, w => _recorder.TryReadAppKey(w)) : null,
                    UseWorkingPairs = pairSmart,
                    RulePairs = pairs,
                    SizeFloors = floors,
                };
                if (trigger == "automatic-gesture" && taskContext.GestureAnchor != 0 && taskContext.GestureAnchor != focus.Main)
                    taskContext = taskContext with { Focus = new(taskContext.GestureAnchor, 0, 0, "manual-gesture") };
                var reference = _referenceStore.Read();
                var preview = trigger == "automatic-gesture" && taskContext.GestureAnchor != 0 && taskContext.RecentGesture is { } release
                    ? _gesturePreview.Take(release, snapshot, foreground, capturedAt) : null;
                taskContext = GesturePlacement.Resolve(taskContext with { Placement = placements }, visibleWorkingSet, effectiveOptions, reference, preview);
                taskContext = _taskSession.PreserveVerifiedTask(snapshot, taskContext);
                reusedPreview = preview?.Used == true;
                // Save the learned inputs separately so an offline rule edit can replace an old slot.
                if (_referenceStore.Enabled)
                    taskContext = taskContext with { SolverRatings = _referenceStore.Placement.Ratings(), ShadowTime = focusObservedAt };
                recordedContext = taskContext with { Placement = learned };
                if (placements.Length > 0 || taskContext.SolverRatings is { Length: > 0 })
                {
                    if (_recorder.CanWriteDiagnostics)
                    {
                        var defaultContext = taskContext with { Placement = slots, SolverRatings = null };
                        diagnosticBaseline = () => LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, reference, snapshot, defaultContext);
                    }
                    taskContext = taskContext with { Placement = placements };
                }
                // 普通点击、滚动和因应用拒绝位置而重试的调用不能消费后台结果。
                bool shadowEligible = _referenceStore.Enabled && (explicitSmart || trigger == "automatic-gesture" ||
                    trigger == "automatic-workspace" && allowPlacementReplan && (cause & ~WorkspaceChangeCause.Requested) != 0);
                if (shadowEligible)
                {
                    var axes = _placementShadow.Bind(snapshot, taskContext.Focus, capturedAt);
                    taskContext = taskContext with { ShadowAxes = axes, ShadowTime = focusObservedAt };
                    recordedContext = recordedContext with { ShadowAxes = axes };
                    shadowFrame = new(route, visibleWorkingSet.ToArray(), effectiveOptions, reference, snapshot.ToArray(), taskContext, focusObservedAt);
                }
                reclaimContext = taskContext;
                detailed = LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, reference, snapshot, taskContext);
                plan = detailed.Moves;
                scored = detailed;
            }
            else plan = _tidyEngine.CreatePlan(visibleWorkingSet, effectiveOptions);

            detailed = detailed is null ? new(plan, [], []) : detailed with { Moves = plan };
            // 计划的后处理和原生提交都只能使用这扇新窗的几何。不可借强度调节向邻窗或 Z 序扩权。
            nint onlyWindow = reclaimContext?.GeometryOnlyWindow ?? 0;
            if (onlyWindow != 0)
            {
                detailed = detailed with
                {
                    Moves = detailed.Moves.Where(m => m.Window.Handle == onlyWindow).ToArray(),
                    Layers = [],
                };
                plan = detailed.Moves;
            }
            _autoTidyManager.SuppressFor(650);
            _workspaceMonitor.ApplicationRequested(snapshot, detailed);
            if (_stopping.IsCancellationRequested) return;
            var proposed = detailed;
            var releaseInput = trigger == "automatic-gesture" ? recordedContext?.RecentGesture : null;
            var application = _windowManager.ApplyLayout(detailed, showActivity ? null : foreground, reclaimContext, releaseInput?.WindowHandle ?? 0, initialReading, workspaceReading, showActivity ? null : AllowsCurrentReturn);
            // 刚输入或开始手动操作时放弃旧方案；新手势、焦点或窗口结构变化有自己的调度入口。
            detailed = application.Plan; plan = detailed.Moves;
            _videoBlackBarDetector.Fits.Follow(plan, Environment.TickCount64);
            // 分栏也可能恰好消掉黑边。按实际提交的尺寸认领比例，候选名称不能证明视口是否达标。
            if (videoHint != null && plan.FirstOrDefault(m => m.Window.Handle == videoHint.WindowHandle &&
                (m.TargetVisualRect.Width != m.Window.VisualRect.Width || m.TargetVisualRect.Height != m.Window.VisualRect.Height)) is { } videoMove)
                _videoBlackBarDetector.RecordApplied(videoMove);
            _verticalFillManager.Track(plan, smart && _smartBehaviorOptions.PreferReversibleVerticalFill);
            var layerOrders = detailed.Layers;
            IReadOnlyList<WindowSnapshot> requestedDesktop = snapshot;
            nint reclaimed = 0;
            if ((explicitSmart || AutomaticLayoutPolicy.AllowsAutomaticDismissal(_automaticMode)) &&
                onlyWindow == 0 && !proposed.IsDividerGesture && !throughCapture && reclaimContext != null && !application.Interrupted && application.Failures.Count == 0 &&
                proposed.Moves.Count == plan.Count && proposed.Layers.Count == layerOrders.Count)
                reclaimed = _behaviorMonitor.TryReclaim(snapshot, detailed, reclaimContext, effectiveOptions.SmartStrength, foreground, _focus);
            if (reclaimed != 0)
            {
                application = application with { MinimizeRequested = snapshot.First(w => w.Handle == reclaimed) };
                requestedDesktop = snapshot.Where(w => w.Handle != reclaimed).OrderBy(w => w.ZOrder)
                    .Select((w, rank) => w with { ZOrder = rank }).ToArray();
                _workspaceMonitor.ApplicationRequested(requestedDesktop, detailed);
            }
            bool hasUndo = plan.Count > 0 || layerOrders.Count > 0 || reclaimed != 0;
            if (hasUndo)
            {
                _undoReclaim = reclaimed != 0;
                if (!_undoReclaim) _behaviorMonitor.ForgetReclaimUndo();
                _undoPlan = plan; _undoLayers = layerOrders; _undoWasSmart = smart;
                _undoChanged?.Invoke(true);
            }
            bool verifyTaskRequest = false;
            if (smart)
            {
                _focus.Submitted(detailed, requestedDesktop, Environment.TickCount64);
                if (reclaimed == 0 || plan.Count != 0 || layerOrders.Count != 0)
                    verifyTaskRequest = _taskSession.Requested(requestedDesktop, detailed, DateTimeOffset.UtcNow, explicitSmart, reclaimContext);
            }
            if (diagnosticBaseline != null)
            {
                try { baseline = diagnosticBaseline(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Diagnostic baseline unavailable: {ex.Message}"); }
            }
            _placementShadow.Submitted(!application.Interrupted && application.Failures.Count == 0 && reclaimed == 0 &&
                proposed.Moves.Count == plan.Count && proposed.Layers.Count == layerOrders.Count ? shadowFrame : null,
                application.Plan, application.FirstGeometrySubmittedTick ?? Environment.TickCount64);
            var requestId = _journal.Record(snapshot, application, trigger, baseline ?? scored ?? proposed, proposed, scored, recordedContext,
                smart ? effectiveOptions.SmartStrength : null, smart ? fullDesktop : null, allowPlacementReplan, releaseInput: releaseInput, reusedPreview: reusedPreview, openingTick: opening?.Visit ?? localOpening?.Visit, shadow: _placementShadow);
            if (smart && _referenceStore.Enabled && !application.Interrupted && application.Failures.Count == 0 &&
                reclaimed == 0 && onlyWindow == 0 && !throughCapture && !detailed.IsDividerGesture)
                _solverFeedback.Submitted(requestId, snapshot.Where(w => !_surfaces.IsLocal(w)).ToArray(), detailed, Environment.TickCount64);
            if (verifyTaskRequest) _taskSession.BindRequest(requestId);
            if (hasUndo) _undoRequest = requestId;
            if (showActivity)
            {
                var kinds = string.Join(TextCatalog.Culture.TwoLetterISOLanguageName == "zh" ? "、" : ", ", detailed.Groups.Select(g => KindName(g.Selected)).Distinct());
                var notes = string.Join(" ", application.Notes.Where(n => n.Contains("未")).Select(DisplayNote)
                    .Concat(application.Failures.Select(f => f.Win32Error).Distinct()
                        .Select(code => LayoutText.Format("PositionRejected", code))));
                if (reclaimed != 0) notes = (notes + " " + LayoutText.Get("IdleMinimizeRequested")).Trim();
                if (plan.Count != 0 || layerOrders.Count != 0)
                    _report(LayoutText.Format("LayoutRequested", kinds.Length > 0 ? kinds : LayoutText.Get("DesktopLayout"),
                        plan.Count, layerOrders.Count, notes).TrimEnd());
                else if (notes.Length > 0) _report(notes);
                else if (visibleWorkingSet.Count == 0)
                    _report(LayoutText.Get("NoWindows"));
                else if (smart)
                    _report(LayoutText.Get("NoLayoutImprovement"));
                else
                    _report(LayoutText.Format("LayoutKept", visibleWorkingSet.Count));
            }
        }
        catch (Exception exception)
        {
            _report(LayoutText.Format("ArrangeFailed", exception.Message));
        }
        finally { _tidyRunning = false; }
    }

    private static bool SameRect(RectI a, RectI b) =>
        Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 &&
        Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;

    internal void UndoTidy()
    {
        if (_stopping.IsCancellationRequested || _tidyRunning || NativeWindowActivity.IsMoving || (_undoPlan.Count == 0 && _undoLayers.Count == 0 && !_undoReclaim))
        { _report(LayoutText.Get("NothingToUndo")); return; }
        try
        {
            _solverFeedback.Clear();
            var current = _windowManager.Capture().ToDictionary(w => w.Handle);
            var reverse = LayoutUndoPlanner.Create(new(_undoPlan, _undoLayers, []), current.Values.ToArray());
            _autoTidyManager.SuppressFor(650);
            _workspaceMonitor.ApplicationRequested(current.Values.ToArray(), reverse);
            if (_stopping.IsCancellationRequested) return;
            var application = _windowManager.ApplyLayout(reverse);
            _placementShadow.Submitted(null, application.Plan, Environment.TickCount64);
            foreach (var move in application.Plan.Moves) _videoBlackBarDetector.Forget(move.Window.Handle);
            if (_undoWasSmart)
                _taskSession.RejectExplicitly(application.Plan.Moves.Select(m => m.Window.Handle)
                    .Concat(application.Plan.Layers.SelectMany(l => l.FrontToBack.Select(w => w.Handle))));
            _journal.Record(current.Values.ToArray(), application, "undo", undoOfRequest: _undoRequest);
            bool restored = _undoReclaim && _behaviorMonitor.UndoReclaim();
            var skipped = _undoPlan.Count - application.Plan.Moves.Count + (_undoReclaim && !restored ? 1 : 0);
            _undoReclaim = false;
            _undoPlan = []; _undoLayers = []; _undoWasSmart = false; _undoRequest = null;
            _undoChanged?.Invoke(false);
            _report(LayoutText.Format("UndoRequested", application.Plan.Moves.Count + (restored ? 1 : 0), skipped));
        }
        catch (Exception ex) { _report(LayoutText.Format("UndoFailed", ex.Message)); }
    }

    private static string DisplayNote(string note) => note switch
    {
        "桌面在规划后发生变化，本次未执行。" => LayoutText.Get("DesktopChanged"),
        "指定层级未实现，未套用依赖该层级的窗口位置。" => LayoutText.Get("LayerPlacementFailed"),
        "检测到新的手动调整，未覆盖窗口位置。" => LayoutText.Get("ManualMoveDetected"),
        "层级未执行：窗口状态改变或被其他窗口隔开。" => LayoutText.Get("LayerSkipped"),
        "层级调用未全部成功，已在安全条件允许时恢复原顺序。" => LayoutText.Get("LayerIncomplete"),
        "组内层级已核对，未设置永久置顶。" => LayoutText.Get("LayerVerified"),
        _ => note
    };

    private static string KindName(string kind) => kind switch
    {
        "full-tiling" => LayoutText.Get("Tiled"), "tiling-insufficient-space" => LayoutText.Get("InsufficientSpace"),
        "window-access" or "working-pair-access" or "alternate-access" => LayoutText.Get("WindowAccess"), "display-reflow" => LayoutText.Get("DisplayReflow"),
        "video-aspect-shrink" or "video-aspect-expand" => LayoutText.Get("VideoAspectLayout"),
        "video-current" => LayoutText.Get("PreservePlacement"),
        "learned-position" or "shadow-placement" => LayoutText.Get("LearnedPosition"),
        "attention-layout" or "attention-side-stack" => LayoutText.Get("AttentionLayout"),
        "layered-workspace" => LayoutText.Get("LayeredWorkspace"),
        "working-overlap" => LayoutText.Get("WorkingOverlap"),
        "working-pair" => LayoutText.Get("WorkingPair"), "working-region" => LayoutText.Get("WatchedRegion"),
        "task-vertical-fill" => LayoutText.Get("ReversibleFill"),
        "task-edge" => LayoutText.Get("SharedEdge"), "task-fit" or "working-pair-grow" => LayoutText.Get("ContentFit"), "task-bleed" => LayoutText.Get("EdgeBleed"),
        "task-columns" or "ordered-chain" or "weighted-pair-projection" => LayoutText.Get("KeepColumns"), "rescue" => LayoutText.Get("RescueArea"),
        "edge-row" => LayoutText.Get("EdgeRow"), "stack" => LayoutText.Get("Stack"), "restack" => LayoutText.Get("Restack"),
        "columns" => LayoutText.Get("Columns"), "rows" => LayoutText.Get("Rows"), "grid" => LayoutText.Get("Grid"), "local" => LayoutText.Get("LocalAdjust"), _ => LayoutText.Get("PreservePlacement"),
    };

    private void OnLayoutVerified(LayoutRunObservation observation, IReadOnlyList<WindowSnapshot> snapshot)
    {
        if (observation.Trigger is not ("explicit" or "automatic-gesture" or "automatic-workspace" or "automatic-opening" or "explicit-tiling") ||
            observation.Actual is null) return;
        if (observation.Trigger is "explicit" or "automatic-gesture" or "automatic-workspace" or "automatic-opening")
        {
            if (observation.Outcome == "observed-match")
                _taskSession.VerifyRequest(observation.RequestId, snapshot.Select(w => _surfaces.IsLocal(w)
                    ? w with { IsManageable = false, SurfaceState = null } : w).ToArray());
            else _taskSession.CancelVerification(observation.RequestId);
        }
        if (observation.Outcome == "intervened") return;
        if (observation.SubmissionFailures is { Length: > 0 } refused)
        {
            var errors = string.Join(", ", refused.Select(f => f.Win32Error).Distinct());
            _report(LayoutText.Format("PositionRejected", errors));
            return;
        }
        // An empty request has no positions to confirm. Keep its original explanation on screen.
        if (observation.MinimizeTarget.HasValue && observation.MinimizeVerified == false)
        {
            _report(LayoutText.Get("IdleMinimizeUnconfirmed")); return;
        }
        if (observation.Targets.Length == 0 && observation.Layers.Length == 0)
        {
            if (observation.MinimizeVerified == true) _report(LayoutText.Get("IdleMinimizeVerified"));
            return;
        }
        var actual = observation.Actual.Windows;
        var matched = observation.Targets.Count(t => actual.Any(w => w.Id == t.Id && SameRect(w.Rect, t.Target)));
        var description = string.Join(TextCatalog.Culture.TwoLetterISOLanguageName == "zh" ? "、" : ", ", observation.Groups.Select(g => KindName(g.Selected)).Distinct());
        _report(observation.Outcome == "observed-match"
            ? LayoutText.Format("PositionsVerified", description, matched)
            : LayoutText.Format("PositionsPartial", description, matched, observation.Targets.Length));
        if (observation.ApplyNotes.Any(n => n.Contains("未执行") || n.Contains("未套用") || n.Contains("未覆盖")))
            _report(string.Join(" ", observation.ApplyNotes.Select(DisplayNote)));
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
