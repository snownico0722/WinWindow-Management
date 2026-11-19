namespace NeatWin.Core;

// Explicit preferences, not inferred satisfaction or a personality diagnosis.
// Defaults are engineering hypotheses; see docs/HUMAN_FACTORS_V3.md.
public sealed record TaskLayoutProfile(bool AllowUsefulResize = true, bool AllowPeripheralBleed = true,
    double MaximumBleedDip = 24, double JointVisibilityWeight = 1,
    double SpatialContinuityWeight = 1, double ComfortableWidthDip = 1120,
    double ComfortableHeightDip = 800, bool ProtectWindowEdges = false)
{
    public TaskLayoutProfile Normalize() => this with
    {
        MaximumBleedDip = Bound(MaximumBleedDip, 24, 0, 48),
        JointVisibilityWeight = Bound(JointVisibilityWeight, 1, .5, 2),
        SpatialContinuityWeight = Bound(SpatialContinuityWeight, 1, .5, 2),
        ComfortableWidthDip = Bound(ComfortableWidthDip, 1120, 480, 2400),
        ComfortableHeightDip = Bound(ComfortableHeightDip, 800, 320, 1600),
    };
    private static double Bound(double v, double fallback, double min, double max) =>
        Math.Clamp(double.IsFinite(v) ? v : fallback, min, max);
}

public sealed record TaskDisplay(nint Monitor, RectI WorkArea, RectI Bounds);

// Optional physical calibration, not inferred from DPI or another device's specifications.
public sealed record ViewingCalibration(RectI WorkArea, uint Dpi, double WidthMillimeters,
    double DistanceMillimeters)
{
    public bool Matches(RectI area, uint dpi) => area == WorkArea && dpi == Dpi &&
        double.IsFinite(WidthMillimeters) && WidthMillimeters is >= 150 and <= 4000 &&
        double.IsFinite(DistanceMillimeters) && DistanceMillimeters is >= 200 and <= 3000;
    public double HorizontalAngle(double x, RectI area) =>
        Math.Atan((x - (area.Left + area.Width / 2.0)) / area.Width * WidthMillimeters /
            DistanceMillimeters) * 180 / Math.PI;

    // Angle between two eye-to-content rays on a flat screen with square pixels.
    // Keeping both axes avoids treating vertically separated content as zero distance.
    public double AngularSeparation(double firstX, double firstY, double secondX, double secondY)
    {
        var mmPerPixel = WidthMillimeters / WorkArea.Width;
        var cx = WorkArea.Left + WorkArea.Width / 2.0;
        var cy = WorkArea.Top + WorkArea.Height / 2.0;
        var ax = (firstX - cx) * mmPerPixel; var ay = (firstY - cy) * mmPerPixel;
        var bx = (secondX - cx) * mmPerPixel; var by = (secondY - cy) * mmPerPixel;
        var z = DistanceMillimeters;
        var crossX = z * (ay - by); var crossY = z * (bx - ax);
        var crossZ = ax * by - ay * bx;
        var cross = Math.Sqrt(crossX * crossX + crossY * crossY + crossZ * crossZ);
        return Math.Atan2(cross, ax * bx + ay * by + z * z) * 180 / Math.PI;
    }
}

public enum TaskRelation { Automatic, JointView, Alternating, Integrated }
public sealed record TaskPairHint(nint First, nint Second, TaskRelation Relation);
public sealed record TaskWindowHint(nint Handle, bool PassiveVisual = false,
    bool ProtectPeriphery = false, double? UsefulWidthDip = null, double? UsefulHeightDip = null, TaskContentRegion[]? ProtectedRegions = null);
public sealed record TaskLayoutContext(TaskLayoutProfile? Profile = null, TaskDisplay[]? Displays = null,
    ViewingCalibration? Calibration = null, TaskPairHint[]? PairHints = null,
    TaskWindowHint[]? WindowHints = null, ManualWindowGesture? RecentGesture = null,
    bool VerifiedRepeat = false, string[]? RejectedLayouts = null, VideoBlackBarHint? VideoHint = null,
    bool PreferReversibleVerticalFill = false)
{
    public TaskLayoutProfile Preferences => (Profile ?? new()).Normalize();
}

/// <summary>Bounded session feedback, never satisfaction learning. No disk persistence.</summary>
public sealed class TaskLayoutSession
{
    private WindowSnapshot[]? _pending;
    private string? _verified;
    private FeedbackGroup[] _pendingGroups = [];
    private readonly Queue<string> _rejected = new();
    private ManualWindowGesture? _gesture;
    private DateTimeOffset _gestureTime;
    private DateTimeOffset _appliedTime;

    public void Gesture(ManualWindowGesture gesture, DateTimeOffset now)
    {
        _gesture = gesture; _gestureTime = now; _verified = null; _pending = null;
        // A follow-up may be a task change, not rejection.
        _pendingGroups = [];
    }

    public TaskLayoutContext Capture(IReadOnlyList<WindowSnapshot> windows, TaskLayoutProfile profile,
        TaskDisplay[] displays, ViewingCalibration? calibration, DateTimeOffset now) =>
        new(profile, displays, calibration, RecentGesture: now - _gestureTime <= TimeSpan.FromSeconds(20) ? _gesture : null,
            VerifiedRepeat: _verified == Fingerprint(windows) && now - _appliedTime < TimeSpan.FromMinutes(5),
            RejectedLayouts: _rejected.ToArray());

    public void Requested(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan, DateTimeOffset now)
    {
        if (plan.Moves.Count == 0 && plan.Layers.Count == 0) return;
        var placed = Project(before, plan);
        _pending = placed; _verified = null; _appliedTime = now;
        var changed = plan.Moves.Where(m => m.TargetVisualRect != m.Window.VisualRect)
            .Select(m => m.Window.Handle).Concat(plan.Layers.SelectMany(l => l.FrontToBack.Select(w => w.Handle))).ToHashSet();
        _pendingGroups = plan.Groups.Where(g => g.Handles.Any(changed.Contains))
            .Select(g => new FeedbackGroup(LayoutKey(placed.Where(w => g.Handles.Contains(w.Handle))),
                g.Handles.Where(changed.Contains).ToArray())).ToArray();
    }

    public void Verify(IReadOnlyList<WindowSnapshot> actual)
    {
        if (_pending is null) return; // A no-op verification must not erase a verified layout.
        // Match the native journal's three-pixel settling tolerance once, then anchor the
        // exact observed geometry. Later one-pixel user changes still invalidate reuse.
        var matches = _pending.Length == actual.Count && _pending.All(expected => actual.Any(w =>
            WindowStateRules.Matches(expected, w, expected.VisualRect) &&
            w.ZOrder == expected.ZOrder && w.IsForeground == expected.IsForeground));
        _verified = matches ? Fingerprint(actual) : null;
        _pending = null;
    }

    public void RejectExplicitly(IEnumerable<nint>? restoredHandles = null)
    {
        var restored = restoredHandles?.ToHashSet();
        foreach (var group in _pendingGroups)
        {
            // A skipped/partial undo must not label a complete, untouched group as rejected.
            if (restored is not null && !group.ChangedHandles.All(restored.Contains)) continue;
            if (!_rejected.Contains(group.Layout)) _rejected.Enqueue(group.Layout);
            while (_rejected.Count > 32) _rejected.Dequeue();
        }
        _pendingGroups = []; _pending = null; _verified = null;
    }

    private sealed record FeedbackGroup(string Layout, nint[] ChangedHandles);

    public void Invalidate() { _pending = null; _verified = null; }

    public static string LayoutKey(IEnumerable<WindowSnapshot> windows) => string.Join(";",
        windows.OrderBy(w => w.Handle).Select(w => $"{w.Handle}:{w.ProcessId}:{w.WorkArea}:{w.Dpi}:{w.VisualRect}:{w.ZOrder}"));

    private static string Fingerprint(IEnumerable<WindowSnapshot> windows) => string.Join(";",
        windows.OrderBy(w => w.Handle).Select(w =>
            $"{w.Handle}:{w.ProcessId}:{w.MonitorHandle}:{w.WorkArea}:{w.Dpi}:{w.VisualRect}:{w.ZOrder}:{w.IsForeground}:{w.IsTopmost}:{w.IsManageable}:{w.IsResizable}:{w.FrameInsets}"));

    private static WindowSnapshot[] Project(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan)
    {
        var rects = plan.Moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
        var ranks = before.ToDictionary(w => w.Handle, w => w.ZOrder);
        foreach (var layer in plan.Layers)
        {
            var slots = layer.FrontToBack.Select(w => ranks[w.Handle]).Order().ToArray();
            for (var i = 0; i < slots.Length; i++) ranks[layer.FrontToBack[i].Handle] = slots[i];
        }
        return before.Select(w => w with { VisualRect = rects.GetValueOrDefault(w.Handle, w.VisualRect),
            ZOrder = ranks[w.Handle] }).ToArray();
    }
}
