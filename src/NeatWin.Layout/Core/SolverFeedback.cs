namespace NeatWin.Core;

public sealed record SolverAxisFeedback(int GroupWindow, string Axis, int From, int To, int Tolerance);
public sealed record SolverFeedback(string RequestId, DateTimeOffset Time, string Context, bool Kept,
    SolverAxisFeedback[] Edits, Dictionary<string, double> Votes, int GroupIndex = 0);

internal sealed class SolverFeedbackSession
{
    private sealed record Round(string Id, long Tick, WindowSnapshot[] Projected, LayoutGroupTrace[] Groups, IReadOnlyList<LayoutGroupTrace> AllGroups, HashSet<nint> Changed);
    private Round? _round;
    internal void Clear() => _round = null;
    internal void Retire(nint handle)
    {
        if (_round?.Projected.Any(w => w.Handle == handle) == true) Clear();
    }

    internal void Submitted(string? id, IReadOnlyList<WindowSnapshot> desktop, IntentLayoutPlan? applied, long now)
    {
        Clear();
        if (id == null || applied == null) return;
        var groups = applied.Groups.Where(g => g.Portfolio is { Representatives.Count: > 1 }).ToArray();
        if (groups.Length == 0) return;
        var projected = TaskLayoutSession.Project(desktop, applied).ToArray();
        // 后处理、截断或输入保护可能改变实际提交。只有完整胜出提案确实进入执行，才可把后来的补摆归到本轮。
        groups = groups.Where(g =>
        {
            var p = g.Portfolio!;
            var winner = p.Ballots[p.Selected];
            var members = g.Handles.Select(h => projected.FirstOrDefault(w => w.Handle == h)).ToArray();
            return members.All(w => w != null) && members.Select((w, i) => w!.VisualRect == Box(p, winner, i)).All(v => v) &&
                winner.Order.Select(i => members[i]!.ZOrder).SequenceEqual(members.Select(w => w!.ZOrder).Order());
        }).ToArray();
        if (groups.Length > 0) _round = new(id, now, projected, groups, applied.Groups,
            applied.Moves.Select(m => m.Window.Handle).Concat(applied.Layers.SelectMany(l => l.FrontToBack.Select(w => w.Handle))).ToHashSet());
    }

    private static RectI Box(SolverPortfolio p, SolverBallot b, int i)
    {
        var r = p.Rectangles[b.Frames[i]];
        return new(r[0], r[1], r[2], r[3]);
    }

    internal SolverFeedback? Observe(ManualWindowGesture gesture, IReadOnlyList<WindowSnapshot> live, long now, DateTimeOffset time)
    {
        var round = _round;
        Clear(); // 新的人工操作终结上一个任务的归因窗口，包括无法明确归因的松手。
        if (round == null || now - round.Tick is < 0 or > 30_000 ||
            (gesture.ReleasedTick ?? now) - gesture.DurationMilliseconds < round.Tick) return null;
        var g = round.Groups.FirstOrDefault(g => g.Handles.Contains(gesture.WindowHandle));
        var old = round.Projected.FirstOrDefault(w => w.Handle == gesture.WindowHandle);
        var end = live.FirstOrDefault(w => w.Handle == gesture.WindowHandle);
        if (g?.Portfolio == null || old == null || end == null || !end.IsForeground || end.ProcessId != gesture.ProcessId ||
            !WindowStateRules.Matches(old, end, gesture.EndRect) || !WindowStateRules.Near(old.VisualRect, gesture.StartRect) ||
            gesture.WorkArea != old.WorkArea) return null;
        var peers = round.Projected.Where(w => w.MonitorHandle == old.MonitorHandle && w.Handle != old.Handle)
            .OrderBy(w => w.ZOrder).ToArray();
        var current = live.Where(w => w.MonitorHandle == old.MonitorHandle && w.Handle != old.Handle).OrderBy(w => w.ZOrder).ToArray();
        if (peers.Length != current.Length || peers.Where((w,i) => !WindowStateRules.Matches(w, current[i], w.VisualRect)).Any()) return null;

        int index = Array.IndexOf(g.Handles, old.Handle);
        int tolerance = Math.Max(4, (int)Math.Round(12 * old.Dpi / 96d));
        var edits = new List<SolverAxisFeedback>();
        bool resized = gesture.Kind == ManualGestureKind.Resize || Math.Abs(gesture.StartRect.Width - gesture.EndRect.Width) > tolerance ||
            Math.Abs(gesture.StartRect.Height - gesture.EndRect.Height) > tolerance;
        // 拉左边框同时改变 X 与 Width；只拿用户实际拉过的尺寸轴评判算法，避免把副作用学成靠左偏好。
        foreach (var axis in Enum.GetValues<PlacementAxis>())
        {
            bool size = axis is PlacementAxis.Width or PlacementAxis.Height;
            if (resized != size) continue;
            int from = ShadowAxis.Get(gesture.StartRect, axis), to = ShadowAxis.Get(gesture.EndRect, axis);
            if (Math.Abs((long)from - to) > tolerance) edits.Add(new(index, axis.ToString(), from, to, tolerance));
        }
        var feedback = Compare(round.Id, time, g.Portfolio, !g.Handles.Any(round.Changed.Contains), edits.ToArray());
        return feedback == null ? null : feedback with { GroupIndex = round.AllGroups.ToList().IndexOf(g) };
    }

    internal static SolverFeedback? Compare(string request, DateTimeOffset time, SolverPortfolio p, bool kept, SolverAxisFeedback[] edits)
    {
        var votes = p.Representatives.Keys.ToDictionary(k => k, _ => 0d);
        int observedAxes = 0;
        foreach (var edit in edits)
        {
            var axis = Enum.Parse<PlacementAxis>(edit.Axis);
            var errors = p.Representatives.ToDictionary(kv => kv.Key, kv => Math.Max(0,
                Math.Abs((double)ShadowAxis.Get(Box(p, p.Ballots[kv.Value], edit.GroupWindow), axis) - edit.To) - edit.Tolerance));
            double best = errors.Values.Min(), worst = errors.Values.Max();
            if (worst - best <= edit.Tolerance) continue;
            // 一次回拖不证明某算法在其他轴上也更好。相同预测同分；超过这次改动幅度的误差不提供额外票数。
            double scale = Math.Max(Math.Abs((double)edit.To - edit.From), edit.Tolerance);
            var quality = errors.ToDictionary(kv => kv.Key, kv => 1 - Math.Min(1, kv.Value / scale));
            double center = (quality.Values.Min() + quality.Values.Max()) / 2;
            if (quality.Values.Max() == quality.Values.Min()) continue;
            foreach (var (family, value) in quality) votes[family] += 2 * (value - center);
            observedAxes++;
        }
        if (observedAxes == 0) return null;
        return new(request, time, p.Context, kept, edits, votes.ToDictionary(x => x.Key, x => x.Value / observedAxes));
    }

    internal static SolverRating[] Ratings(IEnumerable<SolverFeedback> feedback)
    {
        var result = new Dictionary<(string Context, string Family), SolverRating>();
        foreach (var f in feedback.OrderBy(f => f.Time).DistinctBy(f => f.RequestId))
        foreach (var (family, vote) in f.Votes)
        {
            if (!double.IsFinite(vote) || Math.Abs(vote) > 1) continue;
            var key = (f.Context, family);
            var r = result.GetValueOrDefault(key);
            double decay = r?.Decay(f.Time) ?? 0;
            result[key] = new(f.Context, family, (r?.Balance ?? 0) * decay + vote, (r?.Evidence ?? 0) * decay + 1, f.Time);
        }
        return result.Values.ToArray();
    }
}
