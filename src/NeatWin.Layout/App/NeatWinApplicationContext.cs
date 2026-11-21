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
    private TaskPreferences _taskPreferences;
    private AutomaticLayoutMode _automaticMode;
    private readonly WorkspaceAutoTidyMonitor _workspaceMonitor;
    private bool _tidyRunning;
    private readonly ForegroundHistory _focus;

    internal NeatWinApplicationContext(string directory, Action<string> report, Action<RecorderStatus>? recorderChanged,
        Action<bool>? undoChanged, CancellationToken stopping, ForegroundHistory? focus = null,
        Action<LayoutRunObservation>? snapshotObserver = null)
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
        _verticalFillManager = new ReversibleVerticalFillManager();
        _autoTidyManager = new AutoTidyManager();
        _recorder = new RecorderSession(_referenceStore, _settingsStore.RecordingEnabled) { VisualSampling = _settingsStore.VisualSampling };
        _journal = new LayoutRunJournal(_windowManager, _referenceStore, _recorder, snapshotObserver);
        _behaviorMonitor = new(_windowManager, _referenceStore, _settingsStore, _recorder,
            () => _stopping.IsCancellationRequested || _tidyRunning || NativeWindowActivity.PointerBusy, _focus);
        _useImportedPlacement = _settingsStore.UseImportedPlacement;
        _workspaceMonitor = new WorkspaceAutoTidyMonitor(() => _windowManager.CaptureCoalesced(), () => _tidyRunning, _focus);
        _autoTidyManager.TaskInput += (target, pid, now, click) =>
        {
            _focus.Interact(target, pid, now, click);
            _recorder.RecordPointer(target, pid);
            if (_workspaceMonitor.TaskInput(target, pid, now)) _taskSession.Invalidate();
        };
        _manualArrangeTimer.Tick += (_, _) => RunTidy();
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
                if (_windowManager.Capture().FirstOrDefault(w => w.Handle == gesture.WindowHandle && w.IsManageable && !w.IsTopmost) is { } moved)
                {
                    _focus.Placed(moved, gesture, Environment.TickCount64);
                    _workspaceMonitor.GestureCompleted(gesture);
                }
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
            _manualArrangeTimer.Dispose();
            _autoTidyManager.TidyRequested -= OnFollowHandRequested;
            _workspaceMonitor.Dispose(); _autoTidyManager.Dispose(); _journal.Dispose(); _behaviorMonitor.Dispose();
            _recorder.StatusChanged -= OnRecorderStatusChanged; _recorder.Dispose();
            _verticalFillManager.Dispose(); _dispatch.Dispose();
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
        _settingsStore.UseImportedPlacement = _useImportedPlacement = true;
        _taskSession.Invalidate();
    }

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
        }
        _taskSession.Invalidate();
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
        _behaviorMonitor.ClearLearning();
        _taskSession.Invalidate();
    }
    private void OnRecorderStatusChanged(RecorderStatus status) => _recorderChanged?.Invoke(status);

    private bool ConfigureAutomation(AutomaticLayoutMode mode, bool arrangeNow = false)
    {
        if (mode is AutomaticLayoutMode.LightAssist or AutomaticLayoutMode.FullAssist && !_autoTidyManager.IsAvailable) return false;
        _placementRetryPending = false;
        if (!_workspaceMonitor.SetMode(mode, arrangeNow))
        {
            _autoTidyManager.Enabled = false;
            _autoTidyManager.SetAttentionInput(false);
            return false;
        }
        // Full Smart uses discrete interaction events. Continuous samples are for follow-hand.
        _autoTidyManager.Enabled = mode == AutomaticLayoutMode.LightAssist;
        ConfigureTaskInput(mode);
        return true;
    }

    private void ConfigureTaskInput(AutomaticLayoutMode mode)
    {
        _autoTidyManager.SetAttentionInput(AutomaticLayoutPolicy.UsesFullPlanner(mode) || _recorder.CanWriteDiagnostics);
        _recorder.PointerCaptureAvailable = _autoTidyManager.TaskInputAvailable;
    }

    private void OnAutomaticLayoutRequested(AutomaticLayoutRequest request)
    {
        if (request.Mode != _automaticMode) return;
        var route = AutomaticLayoutPolicy.Route(request.Mode, request.Trigger);
        if (route != LayoutRoute.Smart) return;
        var trigger = request.Trigger == LayoutTrigger.AfterGesture ? "automatic-gesture" : "automatic-workspace";
        bool mayReplan = !_placementRetryPending;
        _placementRetryPending = false;
        RunTidy(showActivity: false, route.Value, trigger, mayReplan);
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
            if (!NativeWindowActivity.AllowsAutomaticPlacement(interaction.ForegroundWindow)) return;
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
            var application = _windowManager.ApplyLayout(new(assisted, [], []), interaction.ForegroundWindow);
            _journal.Record(snapshot, application, "follow-hand");
        }
        catch { /* Assistance must not interrupt the completed manual action. */ }
        finally { _tidyRunning = false; }
    }

    internal void RunTidy() => RunTidy(showActivity: true, LayoutRoute.ManualAlgorithm);

    private void RunTidy(bool showActivity, LayoutRoute route = LayoutRoute.ManualAlgorithm, string? source = null, bool allowPlacementReplan = false)
    {
        if (_stopping.IsCancellationRequested) return;
        if (_tidyRunning || NativeWindowActivity.IsMoving)
        {
            if (showActivity) _manualArrangeTimer.Start();
            return;
        }
        if (showActivity) _manualArrangeTimer.Stop();
        _tidyRunning = true;
        try
        {
            var tiling = route == LayoutRoute.FullTiling;
            var effectiveOptions = route == LayoutRoute.Smart ? _tidyOptions with { AlgorithmMode = TidyAlgorithmMode.Smart } : _tidyOptions;
            var smart = !tiling && effectiveOptions.AlgorithmMode == TidyAlgorithmMode.Smart;
            var explicitSmart = smart && route == LayoutRoute.ManualAlgorithm;
            var fullDesktop = explicitSmart || (smart && _automaticMode == AutomaticLayoutMode.AutoFullAssist);
            var trigger = source ?? (showActivity ? tiling ? "explicit-tiling" : smart ? "explicit" : "explicit-classic" : "classic-follow");
            var foreground = NativeMethods.GetForegroundWindow();
            if (!showActivity && !NativeWindowActivity.AllowsAutomaticPlacement(foreground))
            {
                _workspaceMonitor.RequestCurrent();
                return;
            }
            var snapshot = _windowManager.Capture();
            long capturedAt = Environment.TickCount64;
            if (trigger == "automatic-workspace" && _focus.NavigationFor(snapshot, capturedAt)?.SettleUntil > capturedAt)
            {
                _workspaceMonitor.RequestCurrent();
                return;
            }
            var visibleWorkingSet = fullDesktop
                ? _visibilityAnalyzer.SelectForManualArrange(snapshot, effectiveOptions.RescueOffscreenWindows)
                : _visibilityAnalyzer.SelectVisibleWorkingSet(snapshot, includeStackAccess: smart);
            VideoBlackBarHint? videoHint = null;
            if (smart && _smartBehaviorOptions.RemoveVideoBlackBars)
                videoHint = _videoBlackBarDetector.TryDetect(visibleWorkingSet);
            IReadOnlyList<TidyMove> plan;
            IntentLayoutPlan? detailed = null;
            TaskLayoutContext? recordedContext = null, reclaimContext = null;
            IntentLayoutPlan? baseline = null;
            IntentLayoutPlan? scored = null;
            if (tiling)
            {
                detailed = LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, null, snapshot, null);
                plan = detailed.Moves;
            }
            else if (smart)
            {
                _recorder.SetLiveSensing(_automaticMode, manualRequest: explicitSmart);
                var rules = _referenceStore.Rules.Read();
                var activities = _recorder.LiveActivities(snapshot);
                var focusObservedAt = DateTimeOffset.UtcNow;
                var focus = _focus.Resolve(snapshot, Environment.TickCount64, out var attention);
                var liveHints = _recorder.LiveHints(snapshot);
                if (videoHint is { Confidence: >= .82 })
                {
                    var existing = liveHints.FirstOrDefault(w => w.Handle == videoHint.WindowHandle);
                    var mediaHint = (existing ?? new TaskWindowHint(videoHint.WindowHandle)) with { PassiveVisual = true, MediaCompanion = true };
                    liveHints = [.. liveHints.Where(w => w.Handle != videoHint.WindowHandle), mediaHint];
                }
                liveHints = _journal.Feedback.Bind(snapshot, liveHints, Environment.TickCount64);
                bool pairSmart = explicitSmart || trigger is "automatic-gesture" or "automatic-workspace";
                var pairs = pairSmart && _useImportedPlacement ? _recorder.BindRules(rules, snapshot, liveHints, activities) : [];
                var floors = _recorder.BindSizeFloors(snapshot);
                var learned = pairSmart ? _recorder.Preferences(snapshot, focus).Where(p => p.Confidence >= .40).ToArray() : [];
                var slots = pairSmart && _useImportedPlacement ? _recorder.BindSlots(rules, snapshot) : [];
                var placements = CompiledLayoutRules.ApplySlots(learned, slots);
                liveHints = WindowUseRoles.Bind(snapshot, liveHints, focus, attention, placements, _recorder.SemanticRole);
                if (!_lastPairs.SequenceEqual(pairs) || !_lastFloors.SequenceEqual(floors) ||
                    !_lastPlacements.SequenceEqual(placements) || !ReferenceEquals(rules, _lastRules) ||
                    focus.Main != _lastFocus?.Main || focus.Companion != _lastFocus?.Companion || focus.Evidence != _lastFocus?.Evidence ||
                    !(focus.NavigationAncestors ?? []).SequenceEqual(_lastFocus?.NavigationAncestors ?? []) ||
                    !ActivityHints.Equivalent(_lastLiveHints, liveHints))
                    _taskSession.Invalidate();
                _lastPairs = pairs; _lastFloors = floors; _lastPlacements = placements;
                _lastRules = rules;
                _lastFocus = focus;
                _lastLiveHints = liveHints;
                var taskContext = _taskSession.Capture(snapshot, _taskPreferences.Profile, Displays(snapshot),
                    _taskPreferences.Calibration, DateTimeOffset.UtcNow, explicitSmart) with
                {
                    PreferReversibleVerticalFill = _smartBehaviorOptions.PreferReversibleVerticalFill,
                    Focus = focus,
                    NavigationObservedAt = focus.Navigation is { Length: > 0 } ? focusObservedAt : null,
                    Attention = attention,
                    WindowHints = liveHints,
                    VideoHint = videoHint is { Confidence: >= .82 } ? videoHint : null,
                    Rules = _useImportedPlacement ? rules : rules with { Pairs = [], RolePairs = [], AppSlots = [] }, Activities = activities,
                    UseWorkingPairs = pairSmart,
                    RulePairs = pairs,
                    SizeFloors = floors,
                };
                if (trigger == "automatic-gesture" && taskContext.GestureAnchor != 0 && taskContext.GestureAnchor != focus.Main)
                    taskContext = taskContext with { Focus = new(taskContext.GestureAnchor, 0, 0, "manual-gesture") };
                var reference = _referenceStore.Read();
                taskContext = GesturePlacement.Resolve(taskContext, visibleWorkingSet, effectiveOptions, reference);
                // Save the learned inputs separately so an offline rule edit can replace an old slot.
                recordedContext = taskContext with { Placement = learned };
                if (placements.Length > 0)
                {
                    if (_recorder.CanWriteDiagnostics)
                        baseline = LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, reference, snapshot,
                            taskContext with { Placement = slots });
                    taskContext = taskContext with { Placement = placements };
                }
                reclaimContext = taskContext;
                detailed = LayoutDispatch.Create(route, visibleWorkingSet, effectiveOptions, reference, snapshot, taskContext);
                plan = detailed.Moves;
                scored = detailed;
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
            var proposed = detailed;
            var application = _windowManager.ApplyLayout(detailed, showActivity ? null : foreground);
            if (application.Interrupted && !showActivity) _workspaceMonitor.RequestCurrent();
            detailed = application.Plan; plan = detailed.Moves;
            _verticalFillManager.Track(plan, smart && _smartBehaviorOptions.PreferReversibleVerticalFill);
            var layerOrders = detailed.Layers;
            IReadOnlyList<WindowSnapshot> requestedDesktop = snapshot;
            nint reclaimed = 0;
            if (fullDesktop && reclaimContext != null && !application.Interrupted && application.Failures.Count == 0 &&
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
            if (smart)
                _taskSession.Requested(requestedDesktop, detailed, DateTimeOffset.UtcNow, explicitSmart);
            var requestId = _journal.Record(snapshot, application, trigger, baseline ?? scored ?? proposed, proposed, scored, recordedContext,
                smart ? effectiveOptions.SmartStrength : null, smart ? fullDesktop : null, allowPlacementReplan);
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
            var current = _windowManager.Capture().ToDictionary(w => w.Handle);
            var reverse = LayoutUndoPlanner.Create(new(_undoPlan, _undoLayers, []), current.Values.ToArray());
            _autoTidyManager.SuppressFor(650);
            _workspaceMonitor.ApplicationRequested(current.Values.ToArray(), reverse);
            if (_stopping.IsCancellationRequested) return;
            var application = _windowManager.ApplyLayout(reverse);
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
        "window-access" or "working-pair-access" => LayoutText.Get("WindowAccess"), "display-reflow" => LayoutText.Get("DisplayReflow"),
        "learned-position" => LayoutText.Get("LearnedPosition"),
        "attention-layout" => LayoutText.Get("AttentionLayout"),
        "working-overlap" => LayoutText.Get("WorkingOverlap"),
        "working-pair" => LayoutText.Get("WorkingPair"), "working-region" => LayoutText.Get("WatchedRegion"),
        "task-vertical-fill" => LayoutText.Get("ReversibleFill"),
        "task-edge" => LayoutText.Get("SharedEdge"), "task-fit" or "working-pair-grow" => LayoutText.Get("ContentFit"), "task-bleed" => LayoutText.Get("EdgeBleed"),
        "task-columns" => LayoutText.Get("KeepColumns"), "rescue" => LayoutText.Get("RescueArea"),
        "edge-row" => LayoutText.Get("EdgeRow"), "stack" => LayoutText.Get("Stack"), "restack" => LayoutText.Get("Restack"),
        "columns" => LayoutText.Get("Columns"), "rows" => LayoutText.Get("Rows"), "grid" => LayoutText.Get("Grid"), "local" => LayoutText.Get("LocalAdjust"), _ => LayoutText.Get("PreservePlacement"),
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
