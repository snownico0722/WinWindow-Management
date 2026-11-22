namespace NeatWin.Core;

internal enum PlacementAxis { X, Y, Width, Height }
internal enum ShadowChange { Rank, Generate }

internal sealed record ShadowAxis(nint Window, PlacementAxis Axis, int Value, int Rejected, int Tolerance, ShadowChange Change)
{
    internal static int Get(RectI rect, PlacementAxis axis) => axis switch
    {
        PlacementAxis.X => rect.X, PlacementAxis.Y => rect.Y, PlacementAxis.Width => rect.Width, _ => rect.Height
    };

    internal RectI Apply(RectI rect) => Axis switch
    {
        PlacementAxis.X => rect with { X = Value },
        PlacementAxis.Y => rect with { Y = Value },
        PlacementAxis.Width => rect with { Width = Value },
        _ => rect with { Height = Value }
    };

    internal bool Matches(RectI rect) => Math.Abs((long)Get(rect, Axis) - Value) <= Tolerance;
}

internal sealed class ShadowReplayProbe(ShadowAxis axis)
{
    internal bool Feasible { get; private set; }
    internal void Observe(IReadOnlyList<VisibleWindow> group, RectI[] rects, double feedback)
    {
        if (feedback != 0 || Feasible) return;
        for (int i = 0; i < group.Count; i++)
            if (group[i].Window.Handle == axis.Window && axis.Matches(rects[i])) Feasible = true;
    }
}

internal sealed class PlacementShadowReplay
{
    // 十分钟窗口用来汇总独立回拖。只缓存少量完整的求解输入。
    internal const int IntervalMs = 10 * 60_000;
    private const int MaxExamples = 6;
    private const int CorrectionWindowMs = 30_000;
    private const int RuleLifetimeMs = 3 * IntervalMs;
    private readonly List<Example> _queued = [];
    private Rule[] _rules = [];
    private Finding[] _lastFindings = [];
    private readonly List<(Finding Finding, long Tick)> _recent = [];
    private Submission? _submission;

    internal sealed record Frame(LayoutRoute Route, VisibleWindow[] Visible, TidyOptions Options,
        IntentReferenceDocument Reference, WindowSnapshot[] Desktop, TaskLayoutContext Context, DateTimeOffset Time)
    {
        internal IntentLayoutPlan Solve(ShadowAxis[]? axes = null, ShadowReplayProbe? probe = null) =>
            LayoutDispatch.Create(Route, Visible, Options, Reference, Desktop,
                Context with { ShadowAxes = axes, ShadowProbe = probe, ShadowTime = Time });
    }

    private sealed record Submission(Frame Frame, TidyMove[] Moves, WindowSnapshot[] Projected, long Tick);
    internal sealed record Example(Frame Frame, Scope Scope, ShadowAxis[] Axes);
    private sealed record Rule(Scope Scope, ShadowAxis Axis, long Expires);
    internal sealed record Finding(Scope Scope, ShadowAxis Axis, string Diagnosis, bool Won);
    internal sealed record Scope(WindowSnapshot Window, nint Main, nint Companion, WindowSnapshot[] Neighbors)
    {
        internal static Scope Capture(WindowSnapshot window, LayoutFocus? focus, IReadOnlyList<WindowSnapshot> desktop) =>
            new(window, focus?.Main ?? 0, focus?.Companion ?? 0, Related(window, desktop));

        internal bool Matches(IReadOnlyList<WindowSnapshot> desktop, LayoutFocus? focus)
        {
            if (Main != (focus?.Main ?? 0) || Companion != (focus?.Companion ?? 0) ||
                desktop.FirstOrDefault(w => w.Handle == Window.Handle) is not { } current ||
                !SameWindow(Window, current)) return false;
            return SameNeighbors(Related(current, desktop));
        }

        internal bool Includes(nint handle) => Window.Handle == handle || Neighbors.Any(w => w.Handle == handle);

        internal bool Matches(Scope other) => Main == other.Main && Companion == other.Companion &&
            SameWindow(Window, other.Window) && SameNeighbors(other.Neighbors);

        private bool SameNeighbors(WindowSnapshot[] neighbors) =>
            Neighbors.Length == neighbors.Length && Neighbors.Zip(neighbors).All(pair => SameWindow(pair.First, pair.Second));

        private static WindowSnapshot[] Related(WindowSnapshot window, IReadOnlyList<WindowSnapshot> desktop) =>
            desktop.Where(w => w.Handle != window.Handle && w.MonitorHandle == window.MonitorHandle &&
                (w.IsManageable || w.SurfaceState is "fullscreen" or "maximized")).OrderBy(w => w.Handle).ToArray();

        private static bool SameWindow(WindowSnapshot old, WindowSnapshot current) =>
            old.SurfaceState == current.SurfaceState && WindowStateRules.Matches(old, current, current.VisualRect);
    }

    internal void Submitted(Frame? frame, IntentLayoutPlan applied, long now)
    {
        if (applied.Moves.Count == 0 && applied.Layers.Count == 0) return;
        // 排除影子规则参加的排布，避免自身输出循环确认。
        _submission = frame != null && frame.Context.ShadowAxes is not { Length: > 0 } &&
            frame.Desktop.Length <= 32 ? new(frame, applied.Moves.ToArray(), TaskLayoutSession.Project(frame.Desktop, applied).ToArray(), now) : null;
    }

    internal void Observe(ManualWindowGesture gesture, WindowSnapshot moved, IReadOnlyList<WindowSnapshot> desktop, long now)
    {
        // 后续用户动作可以立即撤销本轮规则。这里只使证据失效，不从自己的输出补样本。
        _rules = _rules.Where(r => !Contradicts(r.Axis, gesture)).ToArray();
        _recent.RemoveAll(entry => Contradicts(entry.Finding.Axis, gesture));
        var submission = _submission;
        if (submission == null || now - submission.Tick > CorrectionWindowMs) return;
        var move = submission.Moves.FirstOrDefault(m => m.Window.Handle == moved.Handle);
        if (move == null) return;
        // 一次请求只对应一次真实松手。手势起点核对系统位置已落地，期间邻窗变动会使归因失效。
        _submission = null;
        long started = (gesture.ReleasedTick ?? now) - gesture.DurationMilliseconds;
        if (started < submission.Tick || !WindowStateRules.Matches(move.Window, moved, gesture.EndRect) ||
            !WindowStateRules.Near(gesture.StartRect, move.TargetVisualRect)) return;
        var scope = Scope.Capture(moved, submission.Frame.Context.Focus, submission.Projected);
        if (!scope.Matches(desktop, submission.Frame.Context.Focus) ||
            scope.Neighbors.Any(old => !WindowStateRules.Near(old.VisualRect, desktop.First(w => w.Handle == old.Handle).VisualRect))) return;
        var correction = PlacementCorrection.Observe(gesture.StartRect, gesture.EndRect, move.Window.VisualRect, move.TargetVisualRect, moved.Dpi);
        if (correction == null) return;
        var axes = new List<ShadowAxis>();
        int tolerance = (int)Math.Round(20 * Math.Max(96u, moved.Dpi) / 96d);
        Add(PlacementAxis.X, correction.X); Add(PlacementAxis.Y, correction.Y);
        Add(PlacementAxis.Width, correction.Width); Add(PlacementAxis.Height, correction.Height);
        if (axes.Count == 0) return;
        if (_queued.Count == MaxExamples) _queued.RemoveAt(0);
        _queued.Add(new(submission.Frame, scope, axes.ToArray()));

        void Add(PlacementAxis axis, AxisCorrection? evidence)
        {
            // 从左边或上边拉伸也会移动边缘。这个动作只教尺寸轴。
            if (!IsGestureAxis(gesture, axis)) return;
            if (evidence is { Confidence: >= .55 } e && Math.Abs((long)e.Released - e.Submitted) > tolerance)
                axes.Add(new(moved.Handle, axis, e.Released, e.Submitted, tolerance, ShadowChange.Rank));
        }
    }

    private static bool IsGestureAxis(ManualWindowGesture gesture, PlacementAxis axis) =>
        (gesture.Kind == ManualGestureKind.Move) == (axis is PlacementAxis.X or PlacementAxis.Y);

    internal static bool Contradicts(ShadowAxis axis, ManualWindowGesture gesture) =>
        axis.Window == gesture.WindowHandle && IsGestureAxis(gesture, axis.Axis) && !axis.Matches(gesture.EndRect) &&
        Math.Abs((long)ShadowAxis.Get(gesture.StartRect, axis.Axis) - ShadowAxis.Get(gesture.EndRect, axis.Axis)) > axis.Tolerance;

    internal Example[] Take()
    {
        var batch = _queued.ToArray();
        _queued.Clear();
        return batch;
    }

    internal static Finding[] Analyze(Example[] examples, CancellationToken stopping)
    {
        var findings = new List<Finding>();
        foreach (var example in examples)
        foreach (var axis in example.Axes)
        {
            stopping.ThrowIfCancellationRequested();
            if (example.Frame.Desktop.FirstOrDefault(w => w.Handle == axis.Window) is not { } old) continue;
            var probe = new ShadowReplayProbe(axis);
            var baseline = example.Frame.Solve(probe: probe);
            RectI Target(IntentLayoutPlan plan) => plan.Moves.FirstOrDefault(m => m.Window.Handle == axis.Window)?.TargetVisualRect ?? old.VisualRect;
            if (axis.Matches(Target(baseline)) || Math.Abs((long)ShadowAxis.Get(Target(baseline), axis.Axis) - axis.Rejected) > 3)
            {
                findings.Add(new(example.Scope, axis, "baseline-different", false));
                continue;
            }
            // 有可行候选就试排序。缺候选时另试一轴生成，仍用原有安全检查与收益门槛。
            var alternative = axis with { Change = probe.Feasible ? ShadowChange.Rank : ShadowChange.Generate };
            bool won = axis.Matches(Target(example.Frame.Solve([alternative])));
            findings.Add(new(example.Scope, alternative, probe.Feasible ? "lost-on-ranking" : "missing-feasible-candidate", won));
        }
        return findings.ToArray();
    }

    internal void Publish(Finding[] findings, long now)
    {
        _lastFindings = findings;
        _recent.RemoveAll(entry => now - entry.Tick >= RuleLifetimeMs);
        _recent.AddRange(findings.Select(f => (f, now)));
        if (_recent.Count > 48) _recent.RemoveRange(0, _recent.Count - 48);
        var rules = _rules.Where(r => r.Expires > now).ToList();
        foreach (var finding in findings)
        {
            System.Diagnostics.Trace.TraceInformation($"Placement shadow replay: {finding.Axis.Axis}, {finding.Diagnosis}, alternative selected={finding.Won}");
            var axis = finding.Axis;
            // 同一组窗口、同一轴至少两次接近的人工结果才发布。相反结果保留默认判断。
            var peers = _recent.Select(entry => entry.Finding).Where(f => f.Axis.Axis == axis.Axis && finding.Scope.Matches(f.Scope)).ToArray();
            if (peers.Any(f => Math.Abs((long)f.Axis.Value - axis.Value) > axis.Tolerance))
            {
                rules.RemoveAll(r => r.Axis.Axis == axis.Axis && r.Scope.Matches(finding.Scope));
                continue;
            }
            if (!finding.Won || peers.Count(f => f.Won && f.Axis.Change == axis.Change) < 2) continue;
            rules.RemoveAll(r => r.Axis.Axis == axis.Axis && r.Scope.Matches(finding.Scope));
            rules.Add(new(finding.Scope, axis, now + RuleLifetimeMs));
        }
        _rules = rules.TakeLast(16).ToArray();
    }

    internal ShadowAxis[] Bind(IReadOnlyList<WindowSnapshot> desktop, LayoutFocus? focus, long now) =>
        _rules.Where(r => now < r.Expires && r.Scope.Matches(desktop, focus)).Select(r => r.Axis).ToArray();

    internal RecordedShadowFinding[]? Report(IReadOnlyDictionary<nint, int> ids)
    {
        var report = _lastFindings.Where(f => ids.ContainsKey(f.Axis.Window)).Select(f =>
            new RecordedShadowFinding(ids[f.Axis.Window], f.Axis.Axis.ToString(), f.Diagnosis, f.Won)).ToArray();
        return report.Length == 0 ? null : report;
    }

    internal void Retire(nint handle)
    {
        if (_submission?.Projected.Any(w => w.Handle == handle) == true) _submission = null;
        _queued.RemoveAll(example => example.Scope.Includes(handle));
        _recent.RemoveAll(entry => entry.Finding.Scope.Includes(handle));
        _rules = _rules.Where(r => !r.Scope.Includes(handle)).ToArray();
        _lastFindings = _lastFindings.Where(f => !f.Scope.Includes(handle)).ToArray();
    }

    internal void Clear()
    {
        _queued.Clear();
        _submission = null;
        _rules = [];
        _lastFindings = [];
        _recent.Clear();
    }
}
