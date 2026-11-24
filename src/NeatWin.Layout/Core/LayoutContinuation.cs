namespace NeatWin.Core;

// A witness is a ballot ID. Null means no safe successor in the evaluated set.
// A ballot that is its own witness needs no background translation.
public sealed record ContinuationReachability(bool Alternation, int? CompareFromMain, int? CompareFromPeer)
{
    internal int Coverage(int ballot) => (Alternation ? 1 : 0) +
        Level(CompareFromMain, ballot) + Level(CompareFromPeer, ballot);

    internal bool Improves(ContinuationReachability other, int ballot, int otherBallot)
    {
        int main = Level(CompareFromMain, ballot), otherMain = Level(other.CompareFromMain, otherBallot);
        int peer = Level(CompareFromPeer, ballot), otherPeer = Level(other.CompareFromPeer, otherBallot);
        return (Alternation || !other.Alternation) && main >= otherMain && peer >= otherPeer &&
            (Alternation != other.Alternation || main > otherMain || peer > otherPeer);
    }

    private static int Level(int? witness, int ballot) => witness == ballot ? 2 : witness.HasValue ? 1 : 0;
}

public sealed record ContinuationDecision(int MainIndex, int PeerIndex, int Immediate, int Selected,
    double RankBudget, Dictionary<int, ContinuationReachability> Reachability);

internal readonly record struct ContinuationPlacement(int Id, RectI Main, RectI Peer);

internal static partial class IntentLayoutPlanner
{
    internal sealed class PairContinuation(int main, int peer, RectI area,
        (int Width, int Height)[] minimum, RectI[] abovePair, RectI[] aboveBackground)
    {
        internal int Main => main;
        internal int Peer => peer;

        internal static PairContinuation? Create(int main, int peer, VisibleWindow[] group,
            AttentionTarget[] goals, RectI area, WindowSnapshot[] obstacles, TaskLayoutContext context)
        {
            // 只对已在使用的双窗做反事实。开窗、导航、显式规则和手势继续由对应的求解入口负责。
            if (context.DisableContinuation || !context.IsWorkspaceObservation || context.ExplicitArrange ||
                context.AllowGestureAssist || context.FirstPlacementWindow != 0 ||
                group.Any(v => MeasuredContent(v.Window, context) != null) ||
                context.Focus?.Evidence != "recent-switch-pair" || context.Focus.Switches < 2 ||
                context.Focus.Companion != group[peer].Window.Handle ||
                !group[main].Window.IsForeground || main != 0 ||
                context.Scene?.References.Length > 0 ||
                group.Any(w => w.Window.IsTopmost || w.Window.Owner != 0 || w.Window.SurfaceState is "minimized" or "maximized" or "fullscreen"))
                return null;

            var pair = group.Select(w => w.Window).ToArray();
            // 阅读下限来自求解前的现场。当前较小的窗可继续使用已有尺寸，候选自己的缩窗不降低下限。
            var minimum = pair.Select((w, i) => (
                Math.Max(1, Math.Min(w.VisualRect.Width, goals[i].Width)),
                Math.Max(1, Math.Min(w.VisualRect.Height, goals[i].Height)))).ToArray();
            var abovePair = obstacles.Where(w => w.ZOrder < pair[0].ZOrder).Select(w => w.VisualRect).ToArray();
            var aboveBackground = obstacles.Where(w => w.ZOrder < pair[1].ZOrder).Select(w => w.VisualRect).ToArray();
            // 忙碌桌面连同遮挡物超出首版验证范围时，继续使用现有单步选择。
            if (aboveBackground.Length > 12) return null;
            return new(main, peer, area, minimum, abovePair, aboveBackground);
        }

        internal Dictionary<int, ContinuationReachability> Evaluate(IReadOnlyList<ContinuationPlacement> placements)
        {
            bool Readable(RectI rect, int index, IEnumerable<RectI> blockers)
            {
                var floor = minimum[index];
                return HasClearRectangle(rect.Intersect(area), blockers, floor.Width, floor.Height);
            }

            var alternation = new bool[placements.Count];
            var comparison = new bool[placements.Count];
            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                alternation[i] = Readable(p.Main, main, abovePair) && Readable(p.Peer, peer, abovePair);
                // 终点必须在两种前后关系下都能并读，随后切回正文才无需再动任一窗。
                comparison[i] = alternation[i] && Readable(p.Main, main, aboveBackground.Append(p.Peer)) &&
                    Readable(p.Peer, peer, aboveBackground.Append(p.Main));
            }
            return BackwardReachability(placements, alternation, comparison);
        }
    }

    // 连续正文要求同一块矩形满足宽和高。零碎面积不能充当两者同时满足的证据。
    // 可行阅读块往上推到窗口顶边或障碍底边时仍可用，只需检查这些高度。
    internal static bool HasClearRectangle(RectI bounds, IEnumerable<RectI> obstacles, int width, int height)
    {
        if (width <= 0 || height <= 0 || bounds.Width < width || bounds.Height < height) return false;
        var blockers = obstacles.Select(bounds.Intersect).Where(r => !r.IsEmpty).OrderBy(r => r.X).ToArray();
        foreach (int top in blockers.Select(r => r.Bottom).Prepend(bounds.Top).Distinct())
        {
            if (top > bounds.Bottom - height) continue;
            int left = bounds.Left;
            foreach (var r in blockers)
            {
                if (r.Top >= top + height || r.Bottom <= top || r.Right <= left) continue;
                if (r.Left - left >= width) return true;
                left = r.Right;
            }
            if (bounds.Right - left >= width) return true;
        }
        return false;
    }

    // 对合格终点按「锁定的前台外框 + 后台尺寸」做逆索引。每条分支可选不同终点，
    // 每一条边只允许保尺寸平移后台，前台切换由用户发起。索引对有限候选集是精确的。
    internal static Dictionary<int, ContinuationReachability> BackwardReachability(
        IReadOnlyList<ContinuationPlacement> placements, IReadOnlyList<bool> alternation, IReadOnlyList<bool> comparison)
    {
        var mainAnchored = new Dictionary<(RectI Frame, int Width, int Height), int>();
        var peerAnchored = new Dictionary<(RectI Frame, int Width, int Height), int>();
        for (int i = 0; i < placements.Count; i++)
        {
            if (!comparison[i]) continue;
            var p = placements[i];
            mainAnchored.TryAdd((p.Main, p.Peer.Width, p.Peer.Height), p.Id);
            peerAnchored.TryAdd((p.Peer, p.Main.Width, p.Main.Height), p.Id);
        }

        var result = new Dictionary<int, ContinuationReachability>(placements.Count);
        for (int i = 0; i < placements.Count; i++)
        {
            var p = placements[i];
            int? mainWitness = null, peerWitness = null;
            if (comparison[i])
            {
                mainWitness = p.Id;
                peerWitness = p.Id;
            }
            else if (alternation[i])
            {
                // 切入时如果自己的工作尺寸已不足，就不能假定该窗会在使用中重新变宽。
                if (mainAnchored.TryGetValue((p.Main, p.Peer.Width, p.Peer.Height), out int a)) mainWitness = a;
                if (peerAnchored.TryGetValue((p.Peer, p.Main.Width, p.Main.Height), out int b)) peerWitness = b;
            }
            result.Add(p.Id, new(alternation[i], mainWitness, peerWitness));
        }
        return result;
    }
}
