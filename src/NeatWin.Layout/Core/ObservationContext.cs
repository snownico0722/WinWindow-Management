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
public sealed record LayoutRunObservation(int Version, string Session, string RequestId, string Trigger,
    WorkspaceFrame Before, PlannedWindow[] Targets, int[][] Layers, PlannedGroup[] Groups,
    WorkspaceFrame? Actual, string Outcome, string[] ApplyNotes, ProjectedPlan? BasePlan = null, ProjectedPlan? Proposal = null, ProjectedPlan? ScoredPlan = null,
    CompiledLayoutRules? Rules = null, RecordedTaskSignals? Signals = null, SmartTidyStrength? Strength = null, bool? FullDesktop = null, RejectedWindowMove[]? SubmissionFailures = null,
    int? MinimizeTarget = null, bool? MinimizeVerified = null);

/// <summary>Maps native identities only in memory; destroying a window retires its ID.</summary>
public sealed class ObservationIdentityTracker
{
    private readonly Dictionary<(nint Handle, uint Process), int> _ids = new();
    private int _sequence;

    public int Id(WindowSnapshot window)
    {
        var key = (window.Handle, window.ProcessId);
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

public sealed record RecordedWindowHint(int Id, bool Passive, bool Media, bool Anchor, TaskContentRegion[]? Regions);
public sealed record RecordedPlacement(int Id, int Partner, double Center, double Width, double Order,
    int Cell, double Confidence, double? Overlap,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    double? Height = null);
public sealed record RecordedSizeFloor(int Id, int MinWidth, int MinHeight);
public sealed record RecordedTaskSignals(int Main, int Companion, int Switches, string? FocusEvidence,
    RecordedWindowHint[] Hints, RecordedPlacement[] Placements,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RecordedSizeFloor[]? SizeFloors = null, Dictionary<int, string>? Activities = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<int, LayoutAttention>? Attention = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int GestureAnchor = 0)
{
    internal static RecordedTaskSignals From(TaskLayoutContext context, IReadOnlyDictionary<nint, int> ids)
    {
        int Id(nint handle) => ids.GetValueOrDefault(handle);
        var focus = context.Focus;
        var floors = (context.SizeFloors ?? []).Where(f => Id(f.Handle) != 0)
            .Select(f => new RecordedSizeFloor(Id(f.Handle), f.MinWidth, f.MinHeight)).ToArray();
        return new(Id(focus?.Main ?? 0), Id(focus?.Companion ?? 0), focus?.Switches ?? 0, focus?.Evidence,
            (context.WindowHints ?? []).Where(h => Id(h.Handle) != 0)
                .Select(h => new RecordedWindowHint(Id(h.Handle), h.PassiveVisual, h.MediaCompanion, h.Anchor, h.ProtectedRegions)).ToArray(),
            (context.Placement ?? []).Where(p => Id(p.Window) != 0)
                .Select(p => new RecordedPlacement(Id(p.Window), Id(p.Partner), p.Center, p.Width, p.Order,
                    p.RevealedCell, p.Confidence, p.InnerOverlap, p.Height)).ToArray(),
            floors.Length > 0 ? floors : null, context.Activities?.Where(a => Id(a.Key) != 0)
                .ToDictionary(a => Id(a.Key), a => a.Value), context.Attention?.Where(a => Id(a.Key) != 0)
                .ToDictionary(a => Id(a.Key), a => a.Value), Id(context.GestureAnchor));
    }

    // During offline replay the session-local ids are the simulated handles.
    internal TaskLayoutContext ApplyToReplay(TaskLayoutContext context) => context with
    {
        Focus = new(Main, Companion, Switches, FocusEvidence ?? "recent-switch-pair"),
        WindowHints = Hints.Select(h => new TaskWindowHint(h.Id, h.Passive, ProtectedRegions: h.Regions,
            Anchor: h.Anchor, MediaCompanion: h.Media)).ToArray(),
        Placement = Placements.Select(p => new PlacementPreference(p.Id, p.Partner, p.Center, p.Width,
            p.Order, p.Cell, p.Confidence, p.Overlap, p.Height)).ToArray(),
        Activities = Activities?.ToDictionary(a => (nint)a.Key, a => a.Value),
        Attention = Attention?.ToDictionary(a => (nint)a.Key, a => a.Value),
        GestureAnchor = GestureAnchor,
        SizeFloors = SizeFloors?.Select(f => new WindowSizeFloor(f.Id, f.MinWidth, f.MinHeight)).ToArray()
    };
}
