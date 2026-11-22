using NeatWin.Core;
using NeatWin.Reference;

internal static class SolverPortfolioChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckChain(check);
        CheckLearning(check);
        CheckRouting(check);
    }

    private static void CheckChain(Action<bool, string> check)
    {
        int verified = 0;
        // 三窗、负屏幕坐标、固定任意一端。独立穷举整组整数位置，防止局部挪窗把代价推到更远的窗口。
        foreach (int a in new[] { -4, -2, 0 })
        foreach (int b in new[] { -1, 1, 3 })
        foreach (int c in new[] { 1, 3, 5 })
        foreach (int mask in Enumerable.Range(0, 8))
        foreach (int gap in new[] { 0, 1 })
        {
            var slots = new[] { a, b, c }.Select((x, i) => new HorizontalSlot(x, 2, 2,
                (mask & (1 << i)) != 0, 1 + 2 * i)).ToArray();
            var xs = OrderedWindowChain.Place(-3, 6, slots, gap);
            double cost(int[] positions) => positions.Select((x, i) => slots[i].Resistance * Math.Pow(x - slots[i].X, 2)).Sum();
            double optimum = double.PositiveInfinity;
            for (int i = -3; i <= 4; i++)
            for (int j = i + 2 + gap; j <= 4; j++)
            for (int k = j + 2 + gap; k <= 4; k++)
            {
                int[] trial = [i, j, k];
                if (slots.Where((s, n) => !s.Movable && s.X != trial[n]).Any()) continue;
                optimum = Math.Min(optimum, cost(trial));
            }
            if (xs == null ? double.IsFinite(optimum) : cost(xs) != optimum ||
                xs[0] < -3 || xs[2] > 4 || xs[1] < xs[0]+2+gap || xs[2] < xs[1]+2+gap ||
                slots.Where((s, n) => !s.Movable && s.X != xs[n]).Any())
                throw new Exception($"chain failed enumeration at {a},{b},{c} mask={mask} gap={gap}");
            verified++;
        }
        check(verified == 432, "order-preserving solver agrees with independent exhaustive three-window placement in 432 scenes");
        var far = OrderedWindowChain.Place(0, 1100,
            [new(0,200,200,false,1), new(150,200,200,true,1), new(850,200,200,true,1)], 0);
        check(far != null && far.SequenceEqual(new[] {0,200,850}), "a distant reference stays put when the first collision can be solved locally");
    }

    private static void CheckLearning(Action<bool, string> check)
    {
        var time = DateTimeOffset.UnixEpoch;
        var work = new RectI(-200,0,1500,900);
        var r = new RectI(10,100,500,460);
        var peer = r with { X = 760 };
        WindowSnapshot first = new(1,r,r,work,1,default,true,true,true,0,96,1001);
        WindowSnapshot second = first with { Handle=2, ProcessId=1002, VisualRect=peer, OuterRect=peer, ZOrder=1, IsForeground=false };
        var ballots = new IntentLayoutPlanner.PortfolioBuilder();
        var hold = new IntentLayoutPlanner.Candidate("keep", [r, peer], [0,1]);
        var left = hold with { Kind="left", Rects=[r with { X=-180 },peer] };
        var right = hold with { Kind="right", Rects=[r with { X=250, Width=650 },peer] };
        ballots.Scored(hold, 2, false); ballots.Scored(left,.5,true); ballots.Scored(right,.504,true);
        ballots.Offer(right with { Kind="same-right" });
        ballots.Offer(right with { Kind="blocked", Order=[1,0] });
        SolverPortfolio Decide(IntentLayoutPlanner.Candidate leader, double rank, SolverRating[]? ratings) =>
            ballots.Finish(leader, rank,"pair",.01,ratings,time,null).Portfolio;
        var p = Decide(hold,2,null);
        check(p.Representatives.Count == 4 && p.Ballots.Length == 4 && p.Ballots.Single(b => b.Order[0] == 1).Feasible == false &&
            p.Ballots[p.Representatives["right"]].Sources.Contains("same-right"), "archive retains all geometry, Z orders and identical proposals from independent sources");
        var session = new SolverFeedbackSession();
        void Submit() => session.Submitted("round",[first,second],new([],[],[new([1,2],"keep",0,[],Portfolio:p)]),1000);
        var target = r with { X=250 };
        ManualWindowGesture gesture = new(1,r,target,work,ManualGestureKind.Move,[],200,0,2000) { ProcessId=1001 };
        SolverFeedback? Read(ManualWindowGesture g, WindowSnapshot other, long now = 2000) =>
            session.Observe(g,[first with { VisualRect=g.EndRect },other],now,time);
        Submit();
        var sample = Read(gesture,second);
        check(sample is { Kept: true, Edits: [{ Axis: "X" }] } && sample.Votes["right"] > 0 &&
            sample.Votes["right"] == sample.Votes["same-right"] && sample.Votes["left"] < 0 && !sample.Votes.ContainsKey("blocked") &&
            Read(gesture,second) == null, "a manual move after keep labels only the observed axis once and compares feasible alternatives");
        Submit();
        check(Read(gesture with { Kind=ManualGestureKind.Resize, EndRect=r with { X=-140, Width=650 } },second) is { Edits: [{Axis:"Width"}] },
            "resizing by the left border credits width and leaves side preference unknown");
        Submit(); check(Read(gesture,second with { ProcessId=9999 }) == null, "a replaced neighbor invalidates attribution");
        Submit(); check(Read(gesture with { ProcessId=8888 },second) == null, "a reused subject cannot supply feedback for its predecessor");
        Submit(); check(Read(gesture,second,32000) == null, "late edits carry no reputational evidence");
        var action = Decide(left,.5,null);
        var shifted = left.Rects[0];
        var applied = new IntentLayoutPlan([new(first,shifted)],[],[new([1,2],"left",0,[],Portfolio:action)]);
        session.Submitted("moved",[first,second],applied,1000);
        check(Read(gesture with { StartRect=shifted, EndRect=r },second) is { Kept:false, Edits:[{Axis:"X"}] } undo &&
            undo.Votes["keep"] > undo.Votes["left"], "a manual return after an actual move credits the solver that would have kept that axis");
        session.Submitted("interrupted",[first,second],applied with { Moves=[] },1000);
        check(Read(gesture,second) == null, "a projected winner that was not submitted cannot receive feedback");
        Submit(); session.Retire(2); check(Read(gesture,second) == null, "retiring a member closes the feedback window");
        Submit(); check(Read(gesture with { EndRect = r },second) == null, "unmodified geometry does not reward any solver");
        var ratings = SolverFeedbackSession.Ratings(Enumerable.Range(0,32).Select(i => sample! with { RequestId=i.ToString() }));
        check(Decide(left,.5,null).Selected == p.Representatives["left"] &&
            Decide(left,.5,ratings).Selected == p.Representatives["right"] && Decide(hold,2,ratings).Selected == p.Selected,
            "repeated independent corrections break a close tie and cannot turn a keep decision into a move");
        var inOtherTask = ratings.Select(rating => rating with { Context="other" }).ToArray();
        var otherWay = ratings.Select(rating => rating with { Balance=-rating.Balance }).ToArray();
        check(Decide(left,.5,inOtherTask).Selected == p.Representatives["left"] &&
            Decide(left,.5,otherWay).Selected == p.Representatives["left"], "the same scene retains its default under unrelated or opposing feedback");
        ballots.Scored(left,.46,true);
        check(Decide(left,.46,ratings).Selected == p.Representatives["left"], "large current-layout benefit prevails even with opposing history");
        check(ratings.All(q => Math.Abs(q.Trust(time.AddDays(150))) < Math.Abs(q.Trust(time))) &&
            SolverFeedbackSession.Ratings([sample!,sample!]).All(q => q.Evidence == 1), "old evidence decays and repeating a request supplies only one vote");
        var path = Path.Combine(Path.GetTempPath(),"winwindow-solver-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PlacementHistoryStore(path);
            store.LearnFeedback(sample!,true); store.LearnFeedback(sample!,true);
            store.LearnFeedback(sample! with { RequestId="paused" },false);
            store.FlushFeedback();
            var restored = new PlacementHistoryStore(path);
            check(store.Ratings().All(q => q.Evidence == 2) && restored.Ratings().All(q => q.Evidence == 1) &&
                restored.DurableFeedback().Single().RequestId == "round", "durable votes survive restart and paused-session evidence stays in memory");
            store.Clear(); store.FlushFeedback();
            check(new PlacementHistoryStore(path).Ratings().Length == 0 && store.Ratings().Length == 0, "clearing the record also clears current and durable reputation");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path,true); }
    }

    private static void CheckRouting(Action<bool, string> check)
    {
        var area = new RectI(-2520,0,2520,1080);
        WindowSnapshot Window(int i, int x, int width)
        {
            var r = new RectI(area.X+x,60 + i*12,width,780);
            return new(i,r,r,area,1,default,true,i==1,true,i-1,96,(uint)(1000+i));
        }
        var before = new[] { Window(1,20,860), Window(2,790,850), Window(3,1770,740) };
        var ctx = new TaskLayoutContext(UseWorkingPairs:true,Focus:new(1,2,0,"visible-reference"),
            Attention:before.ToDictionary(w=>w.Handle,w=>new LayoutAttention(.8,.6,.8,.4,w.IsForeground))) { IsWorkspaceObservation=true };
        IntentLayoutPlan Run(WindowSnapshot[] windows, TaskLayoutContext request) => LayoutDispatch.Create(LayoutRoute.Smart,
            new VisibilityAnalyzer().SelectForManualArrange(windows,true),new(),null,windows,request);
        var multiple = Run(before,ctx).Groups.Single();
        check(multiple.Portfolio!.Representatives.ContainsKey("ordered-chain"), "normal Smart dispatch admits a feasible three-reader horizontal chain");
        var narrow = new RectI(0, 0, 1920, 1080);
        var compact = before.Select((w,i) =>
        {
            var box = new RectI(new[] {30,570,1330}[i],110,570,710);
            return w with { VisualRect=box, OuterRect=box, WorkArea=narrow };
        }).ToArray();
        check(Run(compact,ctx).Groups.Single().Portfolio!.Representatives.ContainsKey("ordered-chain"),
            "ordinary 16:9 three-window desktops have a feasible order-preserving proposal");
        var pair = Run(before[..2],ctx).Groups.Single().Portfolio!;
        check(pair.Hypotheses?.Length == 2 && Math.Abs(pair.Hypotheses.Sum(h=>h.Weight)-1) < 1e-9 &&
            pair.Ballots.Any(b=>b.Sources.Contains("alternate-access")) && pair.Ballots.Any(b=>b.Sources.Contains("weighted-pair-projection")),
            "an ambiguous pair compares reading and return-access proposals in the same desktop solve");
        var scoped = Run(before[..2],ctx with {LocalPlacementWindow=1});
        check(scoped.Groups.All(g=>g.Portfolio?.Hypotheses==null) && scoped.Moves.All(m=>m.Window.Handle==1),
            "single-window authorization keeps its scope during semantic competition");
    }
}
