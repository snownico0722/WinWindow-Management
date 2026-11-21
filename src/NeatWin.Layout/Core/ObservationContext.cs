namespace NeatWin.Core;

// Session-local identifiers only. These DTOs deliberately have no title, process or HWND fields.
public sealed record ObservedWindow(int Id, RectI Rect, RectI WorkArea, uint Dpi,
    int ZOrder, bool Foreground, bool Topmost, bool Manageable, double VisibleRatio, WindowExposure? Exposure = null,
    string? AppKey = null, bool? Resizable = null, string? SurfaceState = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int OwnerId = 0);
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
    long? OpeningToFirstGeometrySubmitMs = null);

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
        RecordedDisplay[]? displays = null, Func<WindowSnapshot, string?>? appKey = null)
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
                window.IsManageable || window.SurfaceState != null ? appKey?.Invoke(window) : null, window.IsResizable, window.SurfaceState, sampledIds.GetValueOrDefault(window.Owner)));
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
    double? Height = null, bool PreferWidth = true, bool PreferCenter = true);
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
    RecordedNavigation? Navigation = null)
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
        return new(Id(focus?.Main ?? 0), Id(focus?.Companion ?? 0), focus?.Switches ?? 0, focus?.Evidence,
            (context.WindowHints ?? []).Where(h => Id(h.Handle) != 0)
                .Select(h => new RecordedWindowHint(Id(h.Handle), h.PassiveVisual, h.MediaCompanion, h.Anchor, h.ProtectedRegions, h.Role, h.FloatingAffinity, h.TextFocus, h.InitialTextPlacement)).ToArray(),
            (context.Placement ?? []).Where(p => Id(p.Window) != 0)
                .Select(p => new RecordedPlacement(Id(p.Window), Id(p.Partner), p.Center, p.Width, p.Order,
                    p.RevealedCell, p.Confidence, p.InnerOverlap, p.Height, p.PreferWidth, p.PreferCenter)).ToArray(),
            floors.Length > 0 ? floors : null, context.Activities?.Where(a => Id(a.Key) != 0)
                .ToDictionary(a => Id(a.Key), a => a.Value), context.Attention?.Where(a => Id(a.Key) != 0)
                .ToDictionary(a => Id(a.Key), a => a.Value), Id(context.GestureAnchor), video, context.GestureTarget, navigation);
    }

    // During offline replay the session-local ids are the simulated handles.
    internal TaskLayoutContext ApplyToReplay(TaskLayoutContext context) => context with
    {
        Focus = new(Main, Companion, Switches, FocusEvidence ?? "recent-switch-pair",
            Navigation?.Hops.Where(h => h.SourceRole is "waiting" or "return").Select(h => (nint)h.Source).ToArray(),
            Navigation?.Hops.Select(h => new NavigationHop(h.Source, h.Target, h.Evidence, h.AgeMilliseconds)).ToArray()),
        NavigationObservedAt = Navigation?.ObservedAt,
        WindowHints = Hints.Select(h => new TaskWindowHint(h.Id, h.Passive, ProtectedRegions: h.Regions,
            Anchor: h.Anchor, MediaCompanion: h.Media, Role: h.Role, FloatingAffinity: h.FloatingAffinity, TextFocus: h.TextFocus, InitialTextPlacement: h.InitialTextPlacement)).ToArray(),
        Placement = Placements.Select(p => new PlacementPreference(p.Id, p.Partner, p.Center, p.Width,
            p.Order, p.Cell, p.Confidence, p.Overlap, p.Height, p.PreferWidth, p.PreferCenter)).ToArray(),
        Activities = Activities?.ToDictionary(a => (nint)a.Key, a => a.Value),
        Attention = Attention?.ToDictionary(a => (nint)a.Key, a => a.Value),
        GestureAnchor = GestureAnchor, GestureTarget = GestureTarget,
        VideoHint = Video is { } v ? new(v.Id, v.Orientation, v.ContentAspectRatio, v.Viewport, v.Confidence, v.Tendency) : context.VideoHint,
        SizeFloors = SizeFloors?.Select(f => new WindowSizeFloor(f.Id, f.MinWidth, f.MinHeight)).ToArray()
    };
}
