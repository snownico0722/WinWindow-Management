namespace NeatWin.Core;

/// <summary>Evaluate what becomes visible if one long-idle window leaves the proposed layout.</summary>
internal sealed class IdleWindowReclaim
{
    private readonly Dictionary<int, long> _lastUse = [];

    internal void Reset() => _lastUse.Clear();

    internal WindowSnapshot? Choose(IReadOnlyList<WindowSnapshot> snapshot, IntentLayoutPlan plan,
        TaskLayoutContext context, SmartTidyStrength strength, long tick, Func<WindowSnapshot, int> identity, ForegroundHistory history)
    {
        var present = snapshot.Where(w => w.IsManageable && !w.IsTopmost).ToDictionary(identity);
        foreach (var id in _lastUse.Keys.Where(id => !present.ContainsKey(id)).ToArray()) _lastUse.Remove(id);
        var main = context.Focus?.Main ?? 0;
        var peer = context.Focus?.Companion ?? 0;
        var protectedHandles = new HashSet<nint> { main, peer, context.GestureAnchor, context.VideoHint?.WindowHandle ?? 0 };
        var verifiedMembers = context.VerifiedTaskMembers ?? [];
        protectedHandles.UnionWith(verifiedMembers);
        if (plan.Scene != null) protectedHandles.UnionWith(plan.Scene.References.Select(g => g.Window.Handle));
        if (context.Focus?.NavigationAncestors is { } ancestors) protectedHandles.UnionWith(ancestors);
        foreach (var hint in context.WindowHints ?? [])
            if (hint.Anchor || hint.PassiveVisual || hint.MediaCompanion || hint.ProtectPeriphery || hint.ProtectedRegions is { Length: > 0 })
                protectedHandles.Add(hint.Handle);
        void ProtectPair(nint first, nint second)
        {
            if (first == main) protectedHandles.Add(second);
            if (second == main) protectedHandles.Add(first);
        }
        foreach (var p in context.Placement ?? [])
            if (p.Confidence >= .5) ProtectPair(p.Window, p.Partner);
        foreach (var pair in context.RulePairs ?? []) ProtectPair(pair.First, pair.Second);
        foreach (var pair in context.PairHints ?? []) ProtectPair(pair.First, pair.Second);

        var idle = new HashSet<nint>();
        var age = strength switch
        {
            SmartTidyStrength.Assertive => TimeSpan.FromMinutes(2),
            SmartTidyStrength.Gentle => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(5),
        };
        foreach (var (id, w) in present)
        {
            if (!_lastUse.ContainsKey(id) || w.IsForeground ||
                protectedHandles.Contains(w.Handle) || context.Attention?.GetValueOrDefault(w.Handle)?.Weight > .20)
                _lastUse[id] = tick;
            else
            {
                _lastUse[id] = Math.Max(_lastUse[id], history.LastUse(w, tick));
                if (tick - _lastUse[id] >= age.TotalMilliseconds) idle.Add(w.Handle);
            }
        }

        if (main == 0 || idle.Count == 0) return null;
        idle.ExceptWith(plan.Moves.Select(m => m.Window.Handle));
        idle.ExceptWith(plan.Layers.SelectMany(l => l.FrontToBack.Select(w => w.Handle)));
        if (idle.Count == 0) return null;

        // 按本轮摆完的位置评估回收收益。若主窗移动后自己就能露出来，原先挡住它的窗口可以留着。
        var projected = TaskLayoutSession.Project(snapshot, plan);
        WindowSnapshot? best = null;
        double bestGain = .12;
        var monitor = projected.FirstOrDefault(w => w.Handle == main)?.MonitorHandle;
        bool IsTask(WindowSnapshot w) => w.Handle == main || w.Handle == peer || verifiedMembers.Contains(w.Handle) || context.HasRecentInput(w.Handle);
        foreach (var task in projected.Where(w => w.MonitorHandle == monitor && IsTask(w)))
        {
            var front = projected.Where(w => w.ZOrder < task.ZOrder && !task.VisualRect.Intersect(w.VisualRect).IsEmpty).ToArray();
            var candidates = front.Where(w => w.MonitorHandle == monitor && idle.Contains(w.Handle)).ToArray();
            if (candidates.Length == 0) continue;
            bool returnAccess = task.Handle == peer && context.Focus?.Evidence == "handoff" ||
                context.PairHints?.Any(p => p.Relation == TaskRelation.Alternating &&
                    (p.First == main && p.Second == task.Handle || p.Second == main && p.First == task.Handle)) == true;
            double Visibility(IEnumerable<WindowSnapshot> blockers)
            {
                var exposure = IntentLayoutPlanner.ScreenExposure(task.VisualRect, blockers.Select(w => w.VisualRect), task.WorkArea, task.Dpi);
                if (returnAccess)
                {
                    double wanted = Math.Max(1, Math.Min(task.VisualRect.Width, 140 * task.Dpi / 96d));
                    return Math.Min(1, exposure.AccessWidth / wanted);
                }
                return .65 * exposure.CenterVisible + .35 * exposure.Visible;
            }
            double before = Visibility(front);
            if (1 - before <= bestGain) continue;
            foreach (var possible in candidates)
            {
                // Every other occluder stays in place. A return window with a usable title
                // already has full access and contributes no reason to reclaim its neighbor.
                double gain = Visibility(front.Where(w => w.Handle != possible.Handle)) - before;
                if (gain <= bestGain) continue;
                bestGain = gain;
                best = possible;
            }
        }
        if (best != null || strength == SmartTidyStrength.Gentle || monitor == null) return best;

        // An old window behind the task can leave a narrow, unusable strip. Count pixels
        // that actually return to the desktop; hiding one strip to reveal another gains nothing.
        var local = projected.Where(w => w.MonitorHandle == monitor).ToArray();
        if (local.Count(w => w.IsManageable) < 4) return null;
        var taskOrder = local.Where(IsTask).Max(w => w.ZOrder);
        long idleLimit = Math.Max((long)TimeSpan.FromMinutes(15).TotalMilliseconds, (long)age.TotalMilliseconds * 3);
        double bestCleared = 0;
        foreach (var w in local.Where(w => w.ZOrder > taskOrder && idle.Contains(w.Handle)))
        {
            if (w.Owner != 0 || tick - _lastUse[identity(w)] < idleLimit || w.WorkArea.IsEmpty) continue;
            var front = local.Where(o => o.ZOrder < w.ZOrder).Select(o => o.VisualRect);
            var view = IntentLayoutPlanner.ScreenExposure(w.VisualRect, front, w.WorkArea, w.Dpi);
            if (view.Visible is <= 0 or > .18 || view.CenterVisible > .08) continue;
            var others = local.Where(o => o.Handle != w.Handle).Select(o => o.VisualRect);
            var cleared = IntentLayoutPlanner.ScreenExposure(w.VisualRect, others, w.WorkArea, w.Dpi).Visible *
                w.VisualRect.Area / w.WorkArea.Area;
            if (cleared < .003 || cleared <= bestCleared) continue;
            bestCleared = cleared;
            best = w;
        }
        return best;
    }

    internal void Used(int id, long tick) => _lastUse[id] = tick;
}
