namespace NeatWin.Core;

// 当前默认取舍及可推翻它们的案例见 docs/DECISIONS.md 与 docs/LAYOUT_CASES.md。
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
    bool ProtectPeriphery = false, double? UsefulWidthDip = null, double? UsefulHeightDip = null, TaskContentRegion[]? ProtectedRegions = null, bool Anchor = false, bool MediaCompanion = false,
    WindowUseRole Role = WindowUseRole.Unknown, double FloatingAffinity = 0, bool TextFocus = false, bool InitialTextPlacement = false);
public sealed record WindowSizeFloor(nint Handle, int MinWidth, int MinHeight);

public sealed record TaskLayoutContext(TaskLayoutProfile? Profile = null, TaskDisplay[]? Displays = null,
    ViewingCalibration? Calibration = null, TaskPairHint[]? PairHints = null,
    TaskWindowHint[]? WindowHints = null, ManualWindowGesture? RecentGesture = null,
    bool VerifiedRepeat = false, string[]? RejectedLayouts = null, VideoBlackBarHint? VideoHint = null,
    bool PreferReversibleVerticalFill = false, bool ExplicitArrange = false, LayoutFocus? Focus = null,
    PlacementPreference[]? Placement = null, CompiledLayoutRules? Rules = null, WorkingPairRule[]? RulePairs = null,
    bool UseWorkingPairs = false, WindowSizeFloor[]? SizeFloors = null,
    IReadOnlyDictionary<nint, string>? Activities = null, IReadOnlyDictionary<nint, LayoutAttention>? Attention = null,
    nint GestureAnchor = default, RectI? GestureTarget = null)
{
    // A workspace event can still carry the last gesture as an anchor without granting
    // another placement adjustment. Live and replay callers set this from the event kind.
    internal IReadOnlyDictionary<nint, string?>? AppKeys { get; init; }
    internal ActiveLayoutScene? Scene { get; init; }
    internal ShadowAxis[]? ShadowAxes { get; init; }
    internal ShadowReplayProbe? ShadowProbe { get; init; }
    internal DateTimeOffset? ShadowTime { get; init; }
    internal bool AllowGestureAssist { get; init; } = true;
    internal IReadOnlyDictionary<nint, VideoViewportFit>? VideoFits { get; init; }
    internal int OriginSide { get; init; }
    internal bool IsWorkspaceObservation { get; init; }
    internal nint FirstPlacementWindow { get; init; }
    internal nint LocalPlacementWindow { get; init; }
    // 这次松手直接推翻了系统刚做的动作。辅助最多再动用户接手的窗，免得一次试摆牵动整组。
    internal nint GeometryOnlyWindow => LocalPlacementWindow != 0 ? LocalPlacementWindow :
        !ExplicitArrange && AllowGestureAssist && RecentGesture is { } g && GestureAnchor == g.WindowHandle &&
        Attention?.GetValueOrDefault(GestureAnchor)?.RecentReturn?.MatchesRelease(g) == true ? GestureAnchor : 0;
    internal WorkspaceChangeCause WorkspaceCause { get; init; }

    // 这些系数只调节同等可读性下的动作门槛。救回屏外窗口或满足明确手势仍有自己可核对的空间收益。
    internal double InterventionWeight(bool followsGesture) =>
        ExplicitArrange ? 1 : followsGesture ? .85 :
        Focus?.OpeningResidentLeft != null || WorkspaceCause.HasFlag(WorkspaceChangeCause.Membership) ? .55 :
        IsWorkspaceObservation || GestureAnchor != 0 ? .35 : 1;

    internal DateTimeOffset? NavigationObservedAt { get; init; }
    public TaskLayoutProfile Preferences => (Profile ?? new()).Normalize();
    public bool PairPlanningEnabled => ExplicitArrange || UseWorkingPairs;
    internal bool HoldsTextView(WindowSnapshot window) => !ExplicitArrange && window.IsForeground &&
        WindowHints?.Any(h => h.Handle == window.Handle && h.TextFocus) == true;

    internal static RectI LimitReturn(RectI frame, RectI target, PlacementCorrection? recent, PlacementCorrection? repeated)
    {
        var limited = recent?.Limit(frame, target, false) ?? target;
        return repeated?.Limit(frame, limited, true) ?? limited;
    }

    internal RectI LimitReturn(WindowSnapshot window, RectI target)
    {
        var a = Attention?.GetValueOrDefault(window.Handle);
        return ExplicitArrange ? target : LimitReturn(window.VisualRect, target, a?.RecentReturn, a?.RepeatedReversal);
    }

    internal bool RepeatsReversal(WindowSnapshot window, RectI target) => !ExplicitArrange &&
        Attention?.GetValueOrDefault(window.Handle)?.RepeatedReversal?.RepeatsRejectedAxis(window.VisualRect, target) == true;

    internal bool PreservesReleasedSide(WindowSnapshot window, RectI target)
    {
        // 只保护刚选的半屏；在同侧改变大小、贴边和回收屏外部分仍由各自的空间收益决定。
        int side = Attention?.GetValueOrDefault(window.Handle)?.ManualSide ?? 0;
        if (ExplicitArrange || window.VisualRect == target || side is not (-1 or 1)) return true;
        return AttentionLayout.HorizontalSide(window.VisualRect, window.WorkArea) != side ||
            AttentionLayout.HorizontalSide(target, window.WorkArea) == side;
    }

    internal TaskLayoutContext WithCurrentCorrections(IReadOnlyList<WindowSnapshot> desktop)
    {
        if (ExplicitArrange || Placement is not { Length: > 0 } || Attention == null ||
            !Attention.Values.Any(a => a.Correction != null)) return this;
        var revised = new List<PlacementPreference>();
        foreach (var preference in Placement)
        {
            var w = desktop.FirstOrDefault(w => w.Handle == preference.Window);
            if (w == null || Attention.GetValueOrDefault(w.Handle)?.Correction is not { } correction)
            {
                revised.Add(preference);
                continue;
            }
            var rect = w.VisualRect;
            var area = w.WorkArea;
            int width = preference.PreferWidth ? (int)Math.Round(preference.Width * area.Width) : rect.Width;
            int x = area.X + (int)Math.Round(preference.Center * area.Width - width / 2d);
            int height = (int)Math.Round((preference.Height ?? 0) * area.Height);
            // 一次回拖修正当前任务对旧习惯的信任。保存的样本保留原貌，其他轴和以后独立任务仍可使用。
            double position = preference.PositionConfidence * (1 - (correction.X?.Cost(rect.X, x) ?? 0));
            double horizontal = preference.WidthConfidence * (1 - (correction.Width?.Cost(rect.Width, width) ?? 0));
            double vertical = preference.HeightConfidence * (1 - (correction.Height?.Cost(rect.Height, height) ?? 0));
            double confidence = Math.Max(position, Math.Max(horizontal, vertical));
            if (confidence >= .40)
                revised.Add(preference with { Confidence = confidence, AxisConfidence = new(position, horizontal, vertical),
                    PreferCenter = position >= .40, PreferWidth = horizontal >= .40, Height = vertical >= .40 ? preference.Height : null,
                    Partner = position >= .40 ? preference.Partner : 0, Order = position >= .40 ? preference.Order : 0,
                    RevealedCell = position >= .40 ? preference.RevealedCell : -1, InnerOverlap = position >= .40 ? preference.InnerOverlap : null });
        }
        return this with { Placement = revised.ToArray() };
    }

    internal bool HasRecentInput(nint handle) => Attention?.GetValueOrDefault(handle)?.Input >= .65;
    public (int Width, int Height) MinimumSize(nint handle) =>
        SizeFloors?.FirstOrDefault(f => f.Handle == handle) is { } floor ? (floor.MinWidth, floor.MinHeight) : (0, 0);
}

/// <summary>Empirical, session-local lower bounds from observed partial resizes.</summary>
public sealed class WindowSizeMemory
{
    private sealed record Limit(string? App, double Width, double Height);
    private readonly Dictionary<(nint Handle, uint Pid), Limit> _windows = [];

    public void Clear() { lock (_windows) _windows.Clear(); }

    public void Retire(nint handle)
    {
        lock (_windows)
            foreach (var key in _windows.Keys.Where(k => k.Handle == handle).ToArray()) _windows.Remove(key);
    }

    public void Observe(RectI before, RectI target, RectI actual, uint dpi, nint handle, uint pid, string? appKey)
    {
        if (handle == 0 || pid == 0 || actual.IsEmpty) return;
        double scale = dpi == 0 ? 1 : dpi / 96d;
        double ObserveAxis(int start, int request, int result, double previous)
        {
            if (result > request + 6 && result < start - 6 && request < start)
                return result / scale;
            return result + 4 < previous * scale ? 0 : previous;
        }

        lock (_windows)
        {
            var key = (handle, pid);
            var previous = _windows.GetValueOrDefault(key);
            double width = ObserveAxis(before.Width, target.Width, actual.Width, previous?.Width ?? 0);
            double height = ObserveAxis(before.Height, target.Height, actual.Height, previous?.Height ?? 0);
            if (width <= 0 && height <= 0) { _windows.Remove(key); return; }
            if (_windows.Count >= 256 && previous == null) _windows.Remove(_windows.Keys.First());
            _windows[key] = new(appKey, width, height);
        }
    }

    private static double ConfirmedAxis(IEnumerable<double> values)
    {
        // Two distinct live instances must independently stop at the same DIP dimension.
        // If limits disagree, match the lower corroborated pair.
        double previous = 0;
        foreach (double value in values.Where(v => v > 0).Order())
        {
            if (previous > 0 && value - previous <= 4) return previous;
            previous = value;
        }
        return 0;
    }

    public WindowSizeFloor[] Bind(IEnumerable<(WindowSnapshot Window, string? Key)> entries)
    {
        lock (_windows)
        {
            if (_windows.Count == 0) return [];
            var shared = _windows.Values.Where(v => v.App != null).GroupBy(v => v.App!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (Width: ConfirmedAxis(g.Select(v => v.Width)),
                    Height: ConfirmedAxis(g.Select(v => v.Height))), StringComparer.OrdinalIgnoreCase);
            var result = new List<WindowSizeFloor>();
            foreach (var (w, key) in entries.Where(e => e.Window.IsManageable))
            {
                var own = _windows.GetValueOrDefault((w.Handle, w.ProcessId));
                var app = key == null ? default : shared.GetValueOrDefault(key);
                double scale = w.Dpi == 0 ? 1 : w.Dpi / 96d;
                int Floor(double local, double common, int actual, int available)
                {
                    double dip = local > 0 ? local : common;
                    int pixels = (int)Math.Ceiling(dip * scale);
                    return pixels <= actual + 4 ? Math.Min(pixels, available) : 0;
                }
                int width = Floor(own?.Width ?? 0, app.Width, w.VisualRect.Width, w.WorkArea.Width);
                int height = Floor(own?.Height ?? 0, app.Height, w.VisualRect.Height, w.WorkArea.Height);
                if (width > 0 || height > 0) result.Add(new(w.Handle, width, height));
            }
            return result.ToArray();
        }
    }
}

/// <summary>Bounded session feedback, never satisfaction learning. No disk persistence.</summary>
public sealed class TaskLayoutSession
{
    private WindowSnapshot[]? _pending;
    private string? _verified;
    private bool _lastRequestWasExplicit;
    private FeedbackGroup[] _pendingGroups = [];
    private readonly Queue<string> _rejected = new();
    private ManualWindowGesture? _gesture;
    private DateTimeOffset _gestureTime;
    private static readonly TimeSpan GestureLifetime = TimeSpan.FromSeconds(4);
    private RectI? _assistedGestureFrame;
    private bool _lastRequestUsedGesture;
    private DateTimeOffset _appliedTime;

    public void Gesture(ManualWindowGesture gesture, DateTimeOffset now)
    {
        _gesture = gesture; _gestureTime = now; _assistedGestureFrame = null; _verified = null; _pending = null;
        // A follow-up may be a task change, not rejection.
        _pendingGroups = [];
    }

    public TaskLayoutContext Capture(IReadOnlyList<WindowSnapshot> windows, TaskLayoutProfile profile,
        TaskDisplay[] displays, ViewingCalibration? calibration, DateTimeOffset now, bool explicitArrange = false)
    {
        var foreground = windows.FirstOrDefault(w => w.IsForeground);
        if (_gesture is { } previous && (now - _gestureTime > GestureLifetime ||
            foreground != null && foreground.Handle != previous.WindowHandle ||
            previous.ProcessId != 0 && !windows.Any(w => w.Handle == previous.WindowHandle && w.ProcessId == previous.ProcessId)))
        {
            _gesture = null;
            _assistedGestureFrame = null;
        }
        var recent = explicitArrange ? null : _gesture;
        var anchor = recent is { } gesture && gesture.StartRect != gesture.EndRect &&
            windows.Any(w => w.Handle == gesture.WindowHandle && w.IsManageable && w.WorkArea == gesture.WorkArea &&
                (w.VisualRect == gesture.EndRect || w.VisualRect == _assistedGestureFrame)) ? gesture.WindowHandle : 0;
        return new(profile, displays, calibration, RecentGesture: recent,
            VerifiedRepeat: (!explicitArrange || _lastRequestWasExplicit) && (!_lastRequestUsedGesture || anchor != 0) &&
                _verified == Fingerprint(windows) && now - _appliedTime < TimeSpan.FromMinutes(5),
            RejectedLayouts: _rejected.ToArray(), ExplicitArrange: explicitArrange, GestureAnchor: anchor);
    }

    public void Requested(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan, DateTimeOffset now, bool explicitArrange = false)
    {
        if (plan.Moves.Count == 0 && plan.Layers.Count == 0) return;
        _lastRequestWasExplicit = explicitArrange;
        var placed = Project(before, plan);
        _lastRequestUsedGesture = !explicitArrange && _gesture is { } g && now - _gestureTime <= GestureLifetime &&
            before.Any(w => w.Handle == g.WindowHandle && (g.ProcessId == 0 || g.ProcessId == w.ProcessId) && w.IsForeground &&
                (w.VisualRect == g.EndRect || w.VisualRect == _assistedGestureFrame));
        if (_lastRequestUsedGesture) _assistedGestureFrame = placed.First(w => w.Handle == _gesture!.WindowHandle).VisualRect;
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

    internal static WindowSnapshot[] Project(IReadOnlyList<WindowSnapshot> before, IntentLayoutPlan plan)
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
