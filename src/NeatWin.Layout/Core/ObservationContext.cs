namespace NeatWin.Core;

// 数字 ID 保持旧回放与个人规则兼容。原始身份与它并存，便于直接追踪同应用的不同窗口。
public sealed record RawWindowIdentity(string Handle, uint ProcessId, string? App, string? ExecutablePath,
    DateTime? ProcessStartedUtc, string? ClassName, string? Title, uint? Style = null, uint? ExtendedStyle = null);
public sealed record ObservedWindow(int Id, RectI Rect, RectI WorkArea, uint Dpi,
    int ZOrder, bool Foreground, bool Topmost, bool Manageable, double VisibleRatio, WindowExposure? Exposure = null,
    string? AppKey = null, bool? Resizable = null, string? SurfaceState = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int OwnerId = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool LocalSurface = false, RawWindowIdentity? Identity = null);
public sealed record WorkspaceFrame(DateTimeOffset Time, ObservedWindow[] Windows, bool Truncated = false,
    RecordedDisplay[]? Displays = null);
public sealed record WorkspaceTransition(int Version, string Session, string Cause,
    WorkspaceFrame? Before, WorkspaceFrame After);
public sealed record RelationReferenceSample(DateTimeOffset Time, string Context, int WindowCount,
    string Kind, double OverlapRatio);
public sealed record PlannedWindow(int Id, RectI Target);
public sealed record PlannedGroup(int[] Ids, string Selected, double StackEvidence,
    LayoutCandidateTrace[] Candidates, TaskGroupExplanation? Task = null);
public sealed record ProjectedPlan(PlannedWindow[] Targets, int[][] Layers, string[] Selected);
public sealed record RejectedWindowMove(int Id, RectI Target, int Win32Error);
public sealed record RecordedGestureTiming(int WindowId, bool ReusedPreview, long? ReleaseToFirstGeometrySubmitMs,
    long? ReleaseToGestureWindowSubmitMs);
public sealed record RecordedShadowFinding(int WindowId, string Axis, string Diagnosis, bool AlternativeSelected);
public sealed record RecordedShadowRule(int WindowId, string Axis, int Value, int Tolerance, string Change);
public sealed record LayoutRunObservation(int Version, string Session, string RequestId, string Trigger,
    WorkspaceFrame Before, PlannedWindow[] Targets, int[][] Layers, PlannedGroup[] Groups,
    WorkspaceFrame? Actual, string Outcome, string[] ApplyNotes, ProjectedPlan? BasePlan = null, ProjectedPlan? Proposal = null, ProjectedPlan? ScoredPlan = null,
    CompiledLayoutRules? Rules = null, RecordedTaskSignals? Signals = null, SmartTidyStrength? Strength = null, bool? FullDesktop = null, RejectedWindowMove[]? SubmissionFailures = null,
    int? MinimizeTarget = null, bool? MinimizeVerified = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? UndoOfRequest = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RecordedGestureTiming? GestureTiming = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    long? OpeningToFirstGeometrySubmitMs = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SceneEffect[]? SceneEffects = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SceneEffect[]? ActualSceneEffects = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RecordedShadowFinding[]? ShadowReplay = null)
{
    internal const int CurrentVersion = 11;
}

/// <summary>Maps native identities only in memory; destroying a window retires its ID.</summary>
public sealed class ObservationIdentityTracker
{
    private readonly Dictionary<(nint Handle, uint Process), int> _ids = new();
    private int _sequence;

    public int Id(WindowSnapshot window) => Id(window.Handle, window.ProcessId);

    internal int Id(nint handle, uint process)
    {
        var key = (handle, process);
        if (!_ids.TryGetValue(key, out var id))
        {
            if (_ids.Count >= 1024) _ids.Clear();
            _ids[key] = id = ++_sequence;
        }
        return id;
    }

    public void Reset() => _ids.Clear();

    public void Retire(nint handle)
    {
        foreach (var key in _ids.Keys.Where(k => k.Handle == handle).ToArray()) _ids.Remove(key);
    }

    public WorkspaceFrame Capture(IReadOnlyList<WindowSnapshot> snapshot,
        RecordedDisplay[]? displays = null, Func<WindowSnapshot, string?>? appKey = null, Func<WindowSnapshot, bool>? isLocalSurface = null,
        Func<WindowSnapshot, RawWindowIdentity?>? rawIdentity = null)
    {
        var result = new List<ObservedWindow>();
        var ordered = snapshot.OrderBy(w => w.ZOrder).ToArray();
        // Occluders may include unmanageable windows. Never misreport covered area as visible.
        var screens = displays?.Select(d => d.Bounds).ToArray() ?? ordered.Select(w => w.WorkArea).Distinct().ToArray();
        var sampled = ordered.Take(64).ToArray();
        var sampledIds = sampled.ToDictionary(window => window.Handle, Id);
        foreach (var window in sampled)
        {
            var front = ordered.TakeWhile(w => w.ZOrder < window.ZOrder).Select(w => w.VisualRect);
            var exposure = WindowExposure.Measure(window.VisualRect, screens, front);
            result.Add(new(sampledIds[window.Handle], window.VisualRect, window.WorkArea, window.Dpi, window.ZOrder,
                window.IsForeground, window.IsTopmost, window.IsManageable, exposure.Visible, exposure,
                window.IsManageable || window.SurfaceState != null ? appKey?.Invoke(window) : null, window.IsResizable, window.SurfaceState, sampledIds.GetValueOrDefault(window.Owner), isLocalSurface?.Invoke(window) == true, rawIdentity?.Invoke(window)));
        }
        return new(DateTimeOffset.UtcNow, result.ToArray(), snapshot.Count > 64, displays);
    }

    public static bool SameState(WorkspaceFrame? first, WorkspaceFrame second) =>
        first is not null && first.Truncated == second.Truncated && first.Windows.SequenceEqual(second.Windows) &&
        (first.Displays ?? []).SequenceEqual(second.Displays ?? []);
}

public sealed record RecordedWindowHint(int Id, bool Passive, bool Media, bool Anchor, TaskContentRegion[]? Regions,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    WindowUseRole Role = WindowUseRole.Unknown,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    double FloatingAffinity = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool TextFocus = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool InitialTextPlacement = false);
public sealed record RecordedPlacement(int Id, int Partner, double Center, double Width, double Order,
    int Cell, double Confidence, double? Overlap,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? Height = null, bool PreferWidth = true, bool PreferCenter = true,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PlacementConfidence? AxisConfidence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PlacementUsage? Usage = null);
// 只记录证据和匿名窗口 ID，像素、浏览器标题和句柄不随录像导出。
public sealed record BrowserBarObservation(string Decision, double Aspect, double BarConfidence, int WindowId = 0);
public sealed record RecordedVideo(int Id, VideoBlackBarOrientation Orientation, double ContentAspectRatio,
    RectI Viewport, double Confidence, WindowExposure? Exposure, VideoBlackBarTendency Tendency = VideoBlackBarTendency.Shrink);
public sealed record RecordedSizeFloor(int Id, int MinWidth, int MinHeight);
public sealed record RecordedNavigationHop(int Source, int Target, string Evidence, long AgeMilliseconds, string SourceRole);
public sealed record RecordedNavigation(DateTimeOffset? ObservedAt, RecordedNavigationHop[] Hops);
public sealed record RecordedTaskSignals(int Main, int Companion, int Switches, string? FocusEvidence,
    RecordedWindowHint[] Hints, RecordedPlacement[] Placements,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RecordedSizeFloor[]? SizeFloors = null, Dictionary<int, string>? Activities = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<int, LayoutAttention>? Attention = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int GestureAnchor = 0, RecordedVideo? Video = null, RectI? GestureTarget = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RecordedNavigation? Navigation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? WorkspaceCause = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<int, VideoViewportFit>? VideoFits = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int FirstPlacementWindow = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int OriginSide = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RecordedShadowRule[]? ShadowRules = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    BrowserBarObservation? BrowserBars = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool VerifiedTaskRepeat = false, Dictionary<int, ReadingInteraction>? ReadingInput = null, long[]? ObservedChangeEvents = null, int OmittedChanges = 0)
{
    internal static RecordedTaskSignals From(TaskLayoutContext context, IReadOnlyDictionary<nint, int> ids, IReadOnlyList<WindowSnapshot>? snapshot = null)
    {
        int Id(nint handle) => ids.GetValueOrDefault(handle);
        var focus = context.Focus;
        var floors = (context.SizeFloors ?? []).Where(f => Id(f.Handle) != 0)
            .Select(f => new RecordedSizeFloor(Id(f.Handle), f.MinWidth, f.MinHeight)).ToArray();
        RecordedVideo? video = null;
        if (context.VideoHint is { Confidence: >= .82 } hint && Id(hint.WindowHandle) != 0 && !hint.ViewportVisualRect.IsEmpty)
        {
            var window = snapshot?.FirstOrDefault(w => w.Handle == hint.WindowHandle);
            var exposure = window == null ? null : WindowExposure.Measure(hint.ViewportVisualRect,
                context.Displays?.Select(d => d.Bounds) ?? [window.WorkArea],
                snapshot!.Where(w => w.ZOrder < window.ZOrder).Select(w => w.VisualRect));
            video = new(Id(hint.WindowHandle), hint.Orientation, hint.ContentAspectRatio, hint.ViewportVisualRect,
                hint.Confidence, exposure, hint.Tendency);
        }
        // Keep the connected suffix ending at the live main window. A disappeared ancestor
        // has no current session mapping and cannot be joined to an unrelated earlier hop.
        RecordedNavigation? navigation = null;
        if (focus?.Navigation is { Length: > 0 } path && Id(focus.Main) != 0)
        {
            var hops = new List<RecordedNavigationHop>(path.Length);
            nint target = focus.Main;
            for (int i = path.Length - 1; i >= 0; i--)
            {
                var hop = path[i];
                if (hop.Target != target || Id(hop.Source) == 0) break;
                string role = hop.Source == focus.Companion ? "return" :
                    focus.IsNavigationAncestor(hop.Source) ? "waiting" : "source";
                hops.Add(new(Id(hop.Source), Id(target), hop.Evidence, hop.AgeMilliseconds, role));
                target = hop.Source;
            }
            hops.Reverse();
            if (hops.Count > 0) navigation = new(context.NavigationObservedAt, hops.ToArray());
        }
        var shadow = context.ShadowAxes?.Where(a => Id(a.Window) != 0).Select(a =>
            new RecordedShadowRule(Id(a.Window), a.Axis.ToString(), a.Value, a.Tolerance, a.Change.ToString())).ToArray();
        var fits = context.VideoFits?.Where(pair => Id(pair.Key) != 0).ToDictionary(pair => Id(pair.Key), pair => pair.Value);
        return new(Id(focus?.Main ?? 0), Id(focus?.Companion ?? 0), focus?.Switches ?? 0, focus?.Evidence,
            (context.WindowHints ?? []).Where(h => Id(h.Handle) != 0)
                .Select(h => new RecordedWindowHint(Id(h.Handle), h.PassiveVisual, h.MediaCompanion, h.Anchor, h.ProtectedRegions, h.Role, h.FloatingAffinity, h.TextFocus, h.InitialTextPlacement)).ToArray(),
            (context.Placement ?? []).Where(p => Id(p.Window) != 0)
                .Select(p => new RecordedPlacement(Id(p.Window), Id(p.Partner), p.Center, p.Width, p.Order,
                    p.RevealedCell, p.Confidence, p.InnerOverlap, p.Height, p.PreferWidth, p.PreferCenter, p.AxisConfidence, p.Usage)).ToArray(),
            floors.Length > 0 ? floors : null, context.Activities?.Where(a => Id(a.Key) != 0)
                .ToDictionary(a => Id(a.Key), a => a.Value), context.Attention?.Where(a => Id(a.Key) != 0)
                .ToDictionary(a => Id(a.Key), a => a.Value), Id(context.GestureAnchor), video, context.GestureTarget, navigation, context.WorkspaceCause == WorkspaceChangeCause.None ? null : context.WorkspaceCause.ToString(),
            fits?.Count > 0 ? fits : null, Id(context.FirstPlacementWindow), context.OriginSide, shadow is { Length: > 0 } ? shadow : null,
            context.BrowserBarSample is { } sample ? sample.Detail with { WindowId = Id(sample.Window) } : null,
            context.VerifiedTaskRepeat, context.ReadingInput is { Count: > 0 } input
                ? input.Where(e => Id(e.Key) != 0).ToDictionary(e => Id(e.Key), e => e.Value) : null, context.ObservedChangeEvents, context.OmittedChanges);
    }

    private static ShadowAxis? ReadShadowRule(RecordedShadowRule value) =>
        value.WindowId > 0 && value.Tolerance is > 0 and <= 200 &&
        Enum.TryParse<PlacementAxis>(value.Axis, out var axis) && Enum.IsDefined(axis) &&
        Enum.TryParse<ShadowChange>(value.Change, out var change) && Enum.IsDefined(change) ?
            new(value.WindowId, axis, value.Value, 0, value.Tolerance, change) : null;

    // During offline replay the session-local ids are the simulated handles.
    internal TaskLayoutContext ApplyToReplay(TaskLayoutContext context, int recordVersion = LayoutRunObservation.CurrentVersion) => context with
    {
        Focus = new(Main, Companion, Switches, FocusEvidence ?? "recent-switch-pair",
            Navigation?.Hops.Where(h => h.SourceRole is "waiting" or "return").Select(h => (nint)h.Source).ToArray(),
            Navigation?.Hops.Select(h => new NavigationHop(h.Source, h.Target, h.Evidence, h.AgeMilliseconds)).ToArray()),
        NavigationObservedAt = Navigation?.ObservedAt, FirstPlacementWindow = FirstPlacementWindow, OriginSide = OriginSide,
        WorkspaceCause = Enum.TryParse<WorkspaceChangeCause>(WorkspaceCause, out var cause) ? cause : WorkspaceChangeCause.None,
        WindowHints = Hints.Select(h => new TaskWindowHint(h.Id, h.Passive, ProtectedRegions: h.Regions,
            Anchor: h.Anchor, MediaCompanion: h.Media, Role: h.Role, FloatingAffinity: h.FloatingAffinity, TextFocus: h.TextFocus, InitialTextPlacement: h.InitialTextPlacement)).ToArray(),
        Placement = Placements.Select(p => new PlacementPreference(p.Id, p.Partner, p.Center, p.Width,
            p.Order, p.Cell, p.Confidence, p.Overlap, p.Height, p.PreferWidth, p.PreferCenter, p.AxisConfidence, p.Usage)).ToArray(),
        Activities = Activities?.ToDictionary(a => (nint)a.Key, a => a.Value),
        Attention = Attention?.ToDictionary(a => (nint)a.Key, a => a.Value),
        GestureAnchor = GestureAnchor, GestureTarget = GestureTarget,
        ShadowAxes = (ShadowRules ?? []).Select(ReadShadowRule).OfType<ShadowAxis>().ToArray(),
        VerifiedTaskRepeat = VerifiedTaskRepeat,
        ReadingInput = ReadingInput?.Where(e => e.Key > 0 && e.Value is { Scrolling: >= 0 and <= 1, CaretMotion: >= 0 and <= 1 })
            .ToDictionary(e => (nint)e.Key, e => e.Value),
        ObservedChangeEvents = ObservedChangeEvents, OmittedChanges = OmittedChanges,
        // 旧档只保存暗带和算出的比例。可以审查历史决定，但没有足够证据给新引擎重新授权缩窗。
        VideoFits = recordVersion < 10 ? null : VideoFits?.Where(pair => pair.Key > 0 && pair.Value is { IsValid: true })
            .ToDictionary(pair => (nint)pair.Key, pair => pair.Value),
        VideoHint = Video is { Id: > 0 } v && BrowserBars is { Decision: "confirmed-picture" } proof &&
            proof.WindowId == v.Id && proof.Aspect == v.ContentAspectRatio && recordVersion >= 10
            ? new(v.Id, v.Orientation, v.ContentAspectRatio, v.Viewport, v.Confidence, v.Tendency) : null,
        SizeFloors = SizeFloors?.Select(f => new WindowSizeFloor(f.Id, f.MinWidth, f.MinHeight)).ToArray()
    };
}
