namespace NeatWin.Core;

// Session-local identifiers only. These DTOs deliberately have no title, process or HWND fields.
public sealed record ObservedWindow(int Id, RectI Rect, RectI WorkArea, uint Dpi,
    int ZOrder, bool Foreground, bool Topmost, bool Manageable, double VisibleRatio, WindowExposure? Exposure = null,
    string? AppKey = null, bool? Resizable = null, string? SurfaceState = null);
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
public sealed record LayoutRunObservation(int Version, string Session, string RequestId, string Trigger,
    WorkspaceFrame Before, PlannedWindow[] Targets, int[][] Layers, PlannedGroup[] Groups,
    WorkspaceFrame? Actual, string Outcome, string[] ApplyNotes, ProjectedPlan? BasePlan = null, ProjectedPlan? Proposal = null, ProjectedPlan? ScoredPlan = null);

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
        foreach (var window in ordered.Take(64))
        {
            var front = ordered.TakeWhile(w => w.ZOrder < window.ZOrder).Select(w => w.VisualRect);
            var exposure = WindowExposure.Measure(window.VisualRect, screens, front);
            result.Add(new(Id(window), window.VisualRect, window.WorkArea, window.Dpi, window.ZOrder,
                window.IsForeground, window.IsTopmost, window.IsManageable, exposure.Visible, exposure,
                window.IsManageable || window.SurfaceState != null ? appKey?.Invoke(window) : null, window.IsResizable, window.SurfaceState));
        }
        return new(DateTimeOffset.UtcNow, result.ToArray(), snapshot.Count > 64, displays);
    }

    public static bool SameState(WorkspaceFrame? first, WorkspaceFrame second) =>
        first is not null && first.Truncated == second.Truncated && first.Windows.SequenceEqual(second.Windows) &&
        (first.Displays ?? []).SequenceEqual(second.Displays ?? []);
}
