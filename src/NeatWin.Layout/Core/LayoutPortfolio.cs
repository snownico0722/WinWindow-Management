namespace NeatWin.Core;

// 矩形和整桌提案分别去重。即使两个算法交出同一份坐标，仍保留两个来源；窗口下标始终对应组内 Ids。
public sealed record SolverBallot(int[] Frames, int[] Order, string[] Sources, bool Feasible, bool Eligible, double? Rank);
public sealed record SolverPortfolio(string Context, int[][] Rectangles, SolverBallot[] Ballots,
    Dictionary<string, int> Representatives, int WithoutReputation, int Selected, double MaximumSpread,
    IntentAlternative[]? Hypotheses)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ContinuationDecision? Continuation { get; init; }
}
public sealed record IntentAlternative(string Intent, double Weight);
public sealed record SolverRating(string Context, string Family, double Balance, double Evidence, DateTimeOffset Updated)
{
    // 中性先验相当于四次无偏观察。按时间衰减证据量，新工作方式可以取代过去的反馈。
    internal double Decay(DateTimeOffset now) => Math.Pow(.5, Math.Max(0, (now - Updated).TotalDays) / 30);
    internal double Trust(DateTimeOffset now) => Balance * Decay(now) / (4 + Evidence * Decay(now));
}

internal static partial class IntentLayoutPlanner
{
    private sealed class GeometryEquality : IEqualityComparer<Candidate>
    {
        public bool Equals(Candidate? a, Candidate? b) => ReferenceEquals(a, b) || a != null && b != null &&
            a.Rects.AsSpan().SequenceEqual(b.Rects) && a.Order.AsSpan().SequenceEqual(b.Order);
        public int GetHashCode(Candidate c)
        {
            var hash = new HashCode();
            foreach (var r in c.Rects) hash.Add(r);
            foreach (var i in c.Order) hash.Add(i);
            return hash.ToHashCode();
        }
    }

    internal sealed class PortfolioBuilder
    {
        private sealed class Entry(Candidate candidate)
        {
            internal readonly Candidate Candidate = candidate;
            internal readonly HashSet<string> Sources = [];
            internal double? Rank;
            internal bool Eligible;
        }
        private readonly Dictionary<Candidate, int> _ids = new(new GeometryEquality());
        private readonly List<Entry> _entries = [];

        internal bool Offer(Candidate c)
        {
            bool fresh = !_ids.TryGetValue(c, out int id);
            if (fresh)
            {
                _ids[c] = id = _entries.Count;
                _entries.Add(new(c));
            }
            _entries[id].Sources.Add(c.Kind);
            return fresh;
        }

        internal void Scored(Candidate c, double rank, bool eligible)
        {
            Offer(c);
            var e = _entries[_ids[c]];
            e.Rank = Math.Min(e.Rank ?? double.PositiveInfinity, rank);
            e.Eligible |= eligible;
        }

        internal (Candidate Chosen, SolverPortfolio Portfolio) Finish(Candidate best, double bestRank, string context,
            double maxSpread, SolverRating[]? ratings, DateTimeOffset now, IntentAlternative[]? hypotheses,
            PairContinuation? continuation = null, double continuationBudget = 0)
        {
            int original = _ids[best], selected = original;
            var reps = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Rank == null) continue;
                foreach (var source in e.Sources)
                    if (!reps.TryGetValue(source, out int prev) || e.Rank < _entries[prev].Rank) reps[source] = i;
            }
            // 只比较已通过当前现场收益门槛的完整可行解。信誉不能发起另一轮整理，也不能撤销减扰细化的平局优先。
            if (best.Kind != "keep" && ratings is { Length: > 0 })
            {
                var trust = ratings.Where(r => r != null && r.Context == context && !string.IsNullOrEmpty(r.Family) &&
                    double.IsFinite(r.Evidence) && double.IsFinite(r.Balance) && r.Evidence >= Math.Abs(r.Balance))
                    .GroupBy(r => r.Family).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Updated)!.Trust(now));
                // 信誉的训练对象是每个来源独立选出的代表案。偶然也生成过这组坐标的其他来源没有投它一票。
                double Bonus(int i) => .5 * maxSpread * _entries[i].Sources.Where(s => reps.GetValueOrDefault(s, -1) == i)
                    .Select(s => trust.GetValueOrDefault(s)).DefaultIfEmpty().Average();
                double leaderBonus = Bonus(original), gain = 0;
                foreach (var i in reps.Values.Distinct())
                {
                    var e = _entries[i];
                    if (!e.Eligible || e.Rank == null) continue;
                    double improvement = Bonus(i) - leaderBonus - Math.Max(0, e.Rank.Value - bestRank);
                    if (improvement > gain + 1e-9) { selected = i; gain = improvement; }
                }
            }

            ContinuationDecision? future = null;
            if (continuation != null)
            {
                var placements = _entries.Select((entry, id) => (entry, id))
                    .Where(p => p.entry.Rank.HasValue && p.entry.Candidate.Order.AsSpan().SequenceEqual([0, 1]))
                    .Select(p => new ContinuationPlacement(p.id, p.entry.Candidate.Rects[continuation.Main],
                        p.entry.Candidate.Rects[continuation.Peer])).ToArray();
                // 超过预算时不截断候选，避免把漏算的后继当成不可达。
                if (placements.Length <= 128 && placements.Any(p => p.Id == selected))
                {
                    var reachable = continuation.Evaluate(placements);
                    var immediate = selected;
                    var originalMain = _entries[0].Candidate.Rects[continuation.Main];
                    bool frontAlreadyMoves = _entries[immediate].Candidate.Rects[continuation.Main] != originalMain;
                    // 预留后路只在近似平局时有权改变顺序。它不能让原位胜出的请求开始搬窗。
                    // 如果即时方案允许前台留在原位，展望后续也不能额外搬动它。
                    if (_entries[immediate].Eligible)
                        selected = placements.Where(p => _entries[p.Id].Eligible &&
                            (frontAlreadyMoves || p.Main == originalMain) &&
                            _entries[p.Id].Rank <= bestRank + continuationBudget &&
                            reachable[p.Id].Improves(reachable[immediate], p.Id, immediate))
                            .OrderByDescending(p => reachable[p.Id].Coverage(p.Id))
                            .ThenBy(p => _entries[p.Id].Rank)
                            .Select(p => p.Id).DefaultIfEmpty(immediate).First();
                    future = new(continuation.Main, continuation.Peer, immediate, selected, continuationBudget, reachable);
                }
            }

            var rectangles = new List<int[]>();
            var indices = new Dictionary<RectI, int>();
            int Intern(RectI r)
            {
                if (indices.TryGetValue(r, out int id)) return id;
                indices[r] = id = rectangles.Count;
                rectangles.Add([r.X, r.Y, r.Width, r.Height]);
                return id;
            }
            var ballots = _entries.Select(e => new SolverBallot(e.Candidate.Rects.Select(Intern).ToArray(), e.Candidate.Order,
                e.Sources.ToArray(), e.Rank.HasValue, e.Eligible, e.Rank)).ToArray();
            return (selected == original ? best : _entries[selected].Candidate,
                new SolverPortfolio(context, rectangles.ToArray(), ballots, reps, original, selected, maxSpread, hypotheses)
                    { Continuation = future });
        }
    }

    // 同一组有两种合理解释时，两种解释都在行动前确定权重。已指明的任务、导航和单窗授权优先。
    private sealed record AmbiguousPair(int Main, int Peer, IntentAlternative[] Alternatives)
    {
        internal static AmbiguousPair? Create(VisibleWindow[] group, TaskEvidence[] evidence, AttentionTarget[]? goals, TaskLayoutContext ctx)
        {
            if (goals == null || evidence is not [{ Purpose: null } pair] || group.Length != 2 ||
                ctx.GeometryOnlyWindow != 0 || ctx.GestureAnchor != 0 || ctx.Scene?.Lane != null || ctx.Focus?.Navigation is { Length: > 0 } ||
                ctx.Focus?.NavigationAncestors is { Length: > 0 } || ctx.Scene?.Settings?.Intent != null ||
                ctx.PairHints?.Any(h => h.Relation != TaskRelation.Automatic) == true) return null;
            int main = Array.FindIndex(goals, g => g.Role == AttentionRole.Main);
            if (main < 0 || goals[1-main].Role != AttentionRole.Reference || Math.Min(pair.Parked, pair.Joint) <= 0) return null;
            double read = pair.Joint / (pair.Joint + pair.Parked);
            return new(main, 1-main, [new("read-together", read), new("alternate", 1-read)]);
        }

        internal double ExpectedLoss(Candidate c, AttentionTarget[] goals, RectI area)
        {
            var main = c.Rects[Main].Intersect(area);
            var peer = c.Rects[Peer].Intersect(area);
            double covered = Array.IndexOf(c.Order, Main) < Array.IndexOf(c.Order, Peer) ? main.Intersect(peer).Area : 0;
            double peerArea = Math.Max(1, c.Rects[Peer].Area);
            var frame = c.Rects[Peer];
            int titleHeight = Math.Min(frame.Height, Math.Max(1, goals[Peer].AccessWidth / 4));
            var title = (frame with { Height = titleHeight }).Intersect(area);
            double titleVisible = title.Area - (covered > 0 ? main.Intersect(title).Area : 0);
            double needed = Math.Max(1, Math.Min(frame.Width, goals[Peer].AccessWidth) * (double)titleHeight);
            return Alternatives[0].Weight * (1 - (peer.Area - covered) / peerArea) +
                Alternatives[1].Weight * Math.Max(0, 1 - titleVisible / needed);
        }

        internal void Propose(List<Candidate> candidates, RectI[] original, AttentionTarget[] goals, RectI area, int[] order)
        {
            // 交替时只需露出返回入口；把这扇窗铺开会把“方便切回去”误读成“双窗都要读”。
            if (!goals[Main].Movable) return;
            var front = original[Main]; var peer = original[Peer];
            if (front.Intersect(peer).IsEmpty) return;
            foreach (int x in new[] { peer.Left + goals[Peer].AccessWidth, peer.Right - goals[Peer].AccessWidth - front.Width })
            {
                if (front.Width > area.Width) continue;
                var rects = original.ToArray();
                rects[Main] = front with { X = Math.Clamp(x, area.Left, area.Right - front.Width) };
                candidates.Add(new("alternate-access", rects, order));
            }
        }
    }
}
