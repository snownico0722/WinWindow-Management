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

        var projected = TaskLayoutSession.Project(snapshot, plan);
        WindowSnapshot? best = null;
        double bestGain = .12;
        var monitor = projected.FirstOrDefault(w => w.Handle == main)?.MonitorHandle;
        foreach (var task in projected.Where(w => w.MonitorHandle == monitor &&
            (w.Handle == main || w.Handle == peer || context.HasRecentInput(w.Handle))))
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
        return best;
    }

    internal void Used(int id, long tick) => _lastUse[id] = tick;
}
