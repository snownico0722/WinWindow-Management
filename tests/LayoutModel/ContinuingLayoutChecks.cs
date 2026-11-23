using System.Text.Json;
using NeatWin.Core;

internal static class ContinuingLayoutChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckReadableRectangle(check);
        CheckBackwardIndex(check);
        CheckPortfolio(check);
        CheckDispatch(check);
    }

    private static void CheckReadableRectangle(Action<bool, string> check)
    {
        var area = new RectI(-4, 2, 6, 5);
        RectI[] blockers = [new(-5, 2, 2, 3), new(-2, 1, 3, 2), new(-1, 4, 3, 2),
            new(-3, 4, 1, 3), new(0, 3, 3, 1), new(1, 6, 3, 3)];
        int trials = 0;
        for (int mask = 0; mask < (1 << blockers.Length); mask++)
        for (int width = 1; width <= area.Width; width++)
        for (int height = 1; height <= area.Height; height++)
        {
            var obstacles = blockers.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
            bool oracle = false;
            for (int y = area.Top; y <= area.Bottom - height; y++)
            for (int x = area.Left; x <= area.Right - width; x++)
                oracle |= obstacles.All(r => r.Intersect(new(x, y, width, height)).IsEmpty);
            if (IntentLayoutPlanner.HasClearRectangle(area, obstacles, width, height) != oracle)
                throw new InvalidOperationException($"Reading rectangle differs from exhaustive pixels: {mask}, {width}×{height}");
            trials++;
        }
        check(trials == 1920, "continuation reading rectangles agree with 1,920 exhaustive pixel cases");
    }

    private static void CheckBackwardIndex(Action<bool, string> check)
    {
        int tested = 0, both = 0, neither = 0;
        for (int width = 5; width <= 11; width++)
        {
            var rectangles = (from size in new[] { 2, 3, 4 }
                              from x in Enumerable.Range(-2, width - size + 1)
                              select new RectI(x, 0, size, 2)).ToArray();
            var placements = rectangles.SelectMany(a => rectangles.Select(b => (a, b)))
                .Select((pair, i) => new ContinuationPlacement(i + 4, pair.a, pair.b)).ToArray();
            bool Alternation(ContinuationPlacement p) => p.Main.Width >= 3 && p.Peer.Width >= 3;
            bool Joint(ContinuationPlacement p) => Alternation(p) && p.Main.Intersect(p.Peer).IsEmpty;
            var actual = IntentLayoutPlanner.BackwardReachability(placements, placements.Select(Alternation).ToArray(),
                placements.Select(Joint).ToArray());
            foreach (var p in placements)
            {
                var outcome = actual[p.Id];
                // 独立枚举「保持当前这个外框」之后的全部同尺寸落点，再逐一检查是否能并读。
                bool Valid(ContinuationPlacement q, bool mainActive) => Joint(q) && (mainActive
                    ? q.Main == p.Main && q.Peer.Width == p.Peer.Width && q.Peer.Height == p.Peer.Height
                    : q.Peer == p.Peer && q.Main.Width == p.Main.Width && q.Main.Height == p.Main.Height);
                bool left = placements.Any(q => Valid(q, true)), right = placements.Any(q => Valid(q, false));
                bool ValidWitness(int? witness, bool mainActive) => witness == null || placements.Any(q =>
                    q.Id == witness && Valid(q, mainActive));
                if (outcome.Alternation != Alternation(p) || outcome.CompareFromMain.HasValue != left ||
                    outcome.CompareFromPeer.HasValue != right || !ValidWitness(outcome.CompareFromMain, true) ||
                    !ValidWitness(outcome.CompareFromPeer, false) ||
                    (outcome.CompareFromMain == p.Id) != Joint(p) || (outcome.CompareFromPeer == p.Id) != Joint(p))
                    throw new InvalidOperationException($"Backwards index differs from enumerated continuations: {width}, {p}");
                if (left && right) both++;
                if (!left && !right) neither++;
                tested++;
            }
        }
        check(both > 0 && neither > 0, $"indexed reachability agrees with direct search for all {tested:N0} states, including both feasible and infeasible branches");
    }

    private static void CheckPortfolio(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1500, 900);
        RectI main = new(350, 10, 650, 750), peer = new(510, 10, 625, 750);
        RectI left = main with { X = 60 }, right = peer with { X = 720 };
        WindowSnapshot Window(int i, RectI r) => new(i, r, r, area, 1, default, true, i == 1, true, i - 1, 96, (uint)i);
        var windows = new[] { Window(1, main), Window(2, peer) };
        var visible = new VisibilityAnalyzer().SelectForManualArrange(windows, true).ToArray();
        var ctx = new TaskLayoutContext(Focus: new(1, 2, 3), UseWorkingPairs: true)
            { IsWorkspaceObservation = true, AllowGestureAssist = false };
        AttentionTarget[] goals = [new(AttentionRole.Main, 640, 700, 100, true, true),
            new(AttentionRole.Reference, 620, 700, 100, true, true)];
        var continuation = IntentLayoutPlanner.PairContinuation.Create(0, 1, visible, goals, area, [], ctx)!;
        var portfolio = new IntentLayoutPlanner.PortfolioBuilder();
        IntentLayoutPlanner.Candidate Build(string kind, RectI a, RectI b) => new(kind, [a, b], [0, 1]);
        var keep = Build("keep", main, peer);
        var immediate = Build("overlap", main with { X = 355 }, peer);
        var twoRoutes = Build("offset", left, peer);
        var joint = Build("joint", left, right);
        var oneWitness = Build("main-fixed", left, peer with { X = 780 });
        portfolio.Scored(keep, 2, false);
        portfolio.Scored(immediate, .5, true);
        portfolio.Scored(twoRoutes, .506, true);
        portfolio.Scored(joint, .7, true);
        portfolio.Scored(oneWitness, .7, true);
        var a = portfolio.Finish(immediate, .5, "test", .01, null, DateTimeOffset.UnixEpoch, null, continuation, .025);
        var b = portfolio.Finish(immediate, .5, "test", .01, null, DateTimeOffset.UnixEpoch, null, continuation, .005);
        var c = portfolio.Finish(keep, 2, "test", .01, null, DateTimeOffset.UnixEpoch, null, continuation);
        var trace = a.Portfolio.Continuation!;
        check(a.Chosen == twoRoutes && trace.Immediate != trace.Selected &&
            trace.Reachability[trace.Selected].CompareFromMain == a.Portfolio.Representatives["joint"] &&
            trace.Reachability[trace.Immediate].CompareFromMain == null && b.Chosen == immediate,
            "backwards witnesses change a close eligible decision and lose to a larger benefit today");
        check(c.Chosen == keep && c.Portfolio.Continuation is { Immediate: 0, Selected: 0 },
            "future reachability cannot authorize a move when the original decision keeps the desktop");
        var roundTrip = JsonSerializer.Deserialize<SolverPortfolio>(JsonSerializer.Serialize(a.Portfolio));
        check(roundTrip?.Continuation?.Reachability[trace.Selected] == trace.Reachability[trace.Selected] &&
            RecordedTaskSignals.From(ctx, new Dictionary<nint, int> { [1] = 1, [2] = 2 }).ApplyToReplay(ctx, 17).DisableContinuation,
            "witness indices survive JSON and pre-v18 replay opts out by default");
    }

    private static void CheckDispatch(Action<bool, string> check)
    {
        int measured = 0, forecasts = 0, changed = 0, improved = 0;
        foreach (int screen in new[] { 1366, 1600, 1920, 2560 })
        foreach (bool mirror in new[] { false, true })
        foreach (double frontWidth in new[] { .38, .49, .58 })
        foreach (double offset in new[] { .02, .10, .20, .30 })
        foreach (double affinity in new[] { .25, .55, .85 })
        {
            var area = new RectI(-screen, 30, screen, 1000);
            WindowSnapshot W(int id, double x, double w, int y)
            {
                var r = new RectI(area.X + (int)Math.Round(screen * (mirror ? 1 - x - w : x)), y,
                    (int)Math.Round(screen * w), 700);
                return new(id, r, r, area, 1, default, true, id == 1, true, id - 1, 96, (uint)id);
            }
            var desktop = new[] { W(1, .15, frontWidth, 70), W(2, .15 + offset, .44, 95) };
            var ctx = new TaskLayoutContext(UseWorkingPairs: true, Focus: new(1, 2, 3),
                Attention: new Dictionary<nint, LayoutAttention> { [1] = new(.85, .7, affinity, .5, true),
                    [2] = new(.7, .5, affinity, .25, false) })
                { IsWorkspaceObservation = true, AllowGestureAssist = false };
            var visible = new VisibilityAnalyzer().SelectVisibleWorkingSet(desktop, includeStackAccess: true);
            IntentLayoutPlan Run(TaskLayoutContext context) => LayoutDispatch.Create(LayoutRoute.Smart, visible, new(), null, desktop, context);
            var baseline = Run(ctx with { DisableContinuation = true });
            var proposed = Run(ctx);
            var trace = proposed.Groups.SingleOrDefault()?.Portfolio?.Continuation;
            measured++;
            if (trace == null) continue;
            forecasts++;
            var baselineFrames = TaskLayoutSession.Project(desktop, baseline).Select(w => w.VisualRect);
            var newFrames = TaskLayoutSession.Project(desktop, proposed).Select(w => w.VisualRect);
            bool movedDifferently = !baselineFrames.SequenceEqual(newFrames);
            if (movedDifferently)
            {
                changed++;
                var before = trace.Reachability[trace.Immediate];
                var after = trace.Reachability[trace.Selected];
                Console.WriteLine($"CONTINUATION {screen}px mirror={mirror} width={frontWidth} offset={offset} affinity={affinity} " +
                    $"alternation {before.Alternation}->{after.Alternation}, main {before.CompareFromMain}->{after.CompareFromMain}, " +
                    $"peer {before.CompareFromPeer}->{after.CompareFromPeer}, choices {trace.Immediate}->{trace.Selected}, " +
                    $"move count {baseline.Moves.Count}->{proposed.Moves.Count}");
            }
            bool gained = trace.Reachability[trace.Selected].Improves(trace.Reachability[trace.Immediate],
                trace.Selected, trace.Immediate);
            if (gained) improved++;
            if (movedDifferently != gained || proposed.Layers.Count != baseline.Layers.Count ||
                Run(ctx with { LocalPlacementWindow = 1 }).Groups.Any(g => g.Portfolio?.Continuation != null) ||
                Run(ctx with { AllowGestureAssist = true }).Groups.Any(g => g.Portfolio?.Continuation != null))
                throw new InvalidOperationException("Continuation changed geometry outside its recorded benefit or allowed route.");
        }
        check(forecasts > 0 && improved > 0,
            $"Smart sampled {measured} paired desktops, assessed {forecasts}, changed {changed}, improved modeled continuation {improved}");
    }
}
