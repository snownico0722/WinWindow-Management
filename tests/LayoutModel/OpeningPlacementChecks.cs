using NeatWin.Core;

internal static class OpeningPlacementChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckCentralSwitches(check);
        CheckCoveredHandoff(check);
        CheckOccupiedSide(check);
        CheckOpeningMemory(check);
        CheckRestoreAndPartialReturn(check);
        CheckPairWidth(check);
        CheckRepeatedReturn(check);
        foreach (bool left in new[] { true, false })
        {
            var work = new RectI(-1920, 0, 1920, 1040);
            int x = left ? work.Left : work.Right - 1020;
            var rect = new RectI(x, 100, 1020, 850);
            WindowSnapshot front = new(1, rect, rect, work, 1, default, true, true, true, 0, 96, 11, SurfaceState: "windowed");
            var resident = front with { Handle = 2, ProcessId = 22, IsForeground = false, ZOrder = 1,
                VisualRect = rect with { Y = 15, Height = 790 } };
            WindowSnapshot[] scene = [front, resident];
            var history = new ForegroundHistory();
            history.Visit(2, 22, 1000);
            history.Visit(9, 99, 5000); // Shell in between, outside the captured working set.
            history.Visit(1, 11, 5600);
            var opening = history.OpeningFor(scene, 5620);
            check(opening?.ResidentLeft == left && history.OpeningStillIdle(opening!, 5620, 5599),
                "entering a surface over an established task recognizes its prior side across the shell");
            check(history.OpeningFor([front with { Owner = 2 }, resident], 5620) == null &&
                history.OpeningFor([front, resident with { MonitorHandle = 2 }], 5620) == null &&
                history.OpeningFor([front, resident with { Handle = 3, IsTopmost = true, ZOrder = 1 }, resident with { ZOrder = 2 }], 5620) == null &&
                history.OpeningFor(scene, 6900) == null, "owned, cross-display, already obscured and stale surfaces do not acquire an opening pair");
            var focus = new LayoutFocus(1, 2, 0, left ? "opening-resident-left" : "opening-resident-right");
            history.Resolve(scene, 5620, out var attention);
            var hints = WindowUseRoles.Bind(scene, [], focus, attention, [], _ => WindowUseRole.Editor, 1, keyboardRoot: 1, initialPlacement: 1);
            var context = new TaskLayoutContext(Focus: focus, Attention: attention, WindowHints: hints, UseWorkingPairs: true)
                { IsWorkspaceObservation = true };
            var set = new VisibilityAnalyzer().SelectForManualArrange(scene, false);
            var plan = LayoutDispatch.Create(LayoutRoute.Smart, set, new(), null, scene, context);
            var after = TaskLayoutSession.Project(scene, plan);
            var a = after.Single(w => w.Handle == 1).VisualRect;
            var b = after.Single(w => w.Handle == 2).VisualRect;
            check(AttentionLayout.HorizontalSide(a, work) == (left ? -1 : 1) &&
                AttentionLayout.HorizontalSide(b, work) == (left ? -1 : 1),
                "automatic first placement keeps reading on the incoming and resident working side across the shell");
            var held = WindowUseRoles.Bind(scene, [], focus, attention, [], _ => WindowUseRole.Unknown);
            var heldPlan = LayoutDispatch.Create(LayoutRoute.Smart, set, new(), null, scene, context with { WindowHints = held });
            var stay = TaskLayoutSession.Project(scene, heldPlan).Single(w => w.Handle == 2).VisualRect;
            check(held.Any(h => h.Handle == 1 && h.TextFocus && !h.InitialTextPlacement) &&
                heldPlan.Moves.All(m => m.Window.Handle != 1) && (left ? stay.Left < work.Left + work.Width / 2 : stay.Right > work.Left + work.Width / 2),
                "input at opening protects the incoming frame without evicting the established surface");
            var replay = RecordedTaskSignals.From(context, new Dictionary<nint, int> { [1] = 10, [2] = 20 }).ApplyToReplay(new());
            check(replay.Focus?.OpeningResidentLeft == left && replay.Focus.Companion == 20 &&
                replay.WindowHints?.Any(h => h.Handle == 10 && h.InitialTextPlacement) == true,
                "the opening orientation, peer and guarded first placement survive anonymous replay");
            var scheduler = new AutomaticLayoutScheduler();
            var prior = resident with { IsForeground = true };
            scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [prior], 5000);
            scheduler.ApplicationRequested([prior], new([], [], []), 5500);
            check(scheduler.Observe(scene, 5620, false, navigationSettleUntil: 5700, openingReady: true) == null &&
                scheduler.Observe(scene, 5640, true, openingReady: true) == null &&
                scheduler.Observe(scene, 5660, false, openingReady: true)?.Trigger == LayoutTrigger.WorkspaceChange,
                "an opening can dispatch on the first free observation while navigation and pointer ownership still gate it");
            history.Dispatched(opening!);
            check(history.OpeningFor(scene, 5630) == null && !history.OpeningStillIdle(opening!, 5630, 5610),
                "an opening is single-use and a later keyboard pulse withdraws placement");
            history.Visit(2, 22, 6400);
            history.Visit(1, 11, 7000);
            check(history.OpeningFor(scene, 7020) == null, "quick round trips do not reopen a placed pair");
        }
    }
    private static void CheckOccupiedSide(Action<bool, string> check)
    {
        // 正反例共用同一对窗口、强度和个人偏好；仅改变另一栏的实际可见内容。
        var area = new RectI(-1920, 60, 1920, 1040);
        foreach (int side in new[] { -1, 1 })
        {
            WindowSnapshot Window(int id, int lane, int y, int width, int height)
            {
                int x = lane < 0 ? area.Left : area.Right - width;
                var rect = new RectI(x, area.Top + y, width, height);
                return new(id, rect, rect, area, 2, default, true, id == 1, true, id - 1, 96, (uint)id);
            }
            var article = Window(1, side, 120, 990, 850);
            var source = Window(2, side, 70, 980, 870);
            var independent = Window(3, -side, 52, 900, 900);
            var focus = new LayoutFocus(1, 2, 0, side < 0 ? "opening-resident-left" : "opening-resident-right");
            var task = new TaskLayoutContext(Focus: focus, Attention: new Dictionary<nint, LayoutAttention>
                { [1] = new(1, 1, 1, 0, true), [2] = new(1, 1, 1, 0, false) }, UseWorkingPairs: true,
                Placement: [new(1, 0, side < 0 ? .72 : .28, .52, 0, -1, .68)])
                { IsWorkspaceObservation = true, AllowGestureAssist = false };
            IntentLayoutPlan Solve(WindowSnapshot[] desktop, TaskLayoutContext input) => LayoutDispatch.Create(LayoutRoute.Smart,
                new VisibilityAnalyzer().SelectForManualArrange(desktop, false), new(SmartStrength: SmartTidyStrength.Assertive), null, desktop, input);
            WindowSnapshot[] stack = [article, source, independent];
            check(Solve(stack, task).Groups.Single().Task?.Limits.Contains("existing-occupied-side-stack") == true &&
                Solve(stack, task with { Focus = new(1, 2, 3), GestureAnchor = 1 }).Moves.Count == 0,
                "opening and subsequent focus or post-gesture events inherit a readable occupied side, in either direction");
            var space = Solve([article, source], task);
            check(space.Groups.Single().Task?.Limits.Contains("inherited-side-stack") == true &&
                TaskLayoutSession.Project([article, source], space)[0].VisualRect == article.VisualRect,
                "free space alone does not pull the newly opened foreground away from the user's reading side");
            var wide = article with { VisualRect = new(area.Left + area.Width / 6, area.Top + 80, area.Width * 2 / 3, 880) };
            check(ActiveLayoutScene.Resolve([wide, source], task)?.Lane == null,
                "a wide centered opening with an offset resident and substantial occlusion can expand the existing order");
            var split = Solve([article, source], task with { ExplicitArrange = true });
            check(split.Moves.Count > 0 && AttentionLayout.HorizontalSide(TaskLayoutSession.Project([article, source], split)[0].VisualRect, area) == -side,
                "deliberate arrangement can still use the free opposite side");
            var usedPair = task with { Focus = new(1, 2, 4) };
            check(ActiveLayoutScene.Resolve([article, source], usedPair)?.Lane == null &&
                ActiveLayoutScene.Resolve([article, source], task with { Focus = new(1, 2, 2) })?.Lane != null,
                "repeated bilateral interaction can release a side while a focus bounce alone cannot");
            var small = article with { VisualRect = article.VisualRect with { X = article.VisualRect.X + 180, Width = 480, Height = 420 } };
            var enlarged = TaskLayoutSession.Project([small, source], Solve([small, source], task))[0].VisualRect;
            check((enlarged.Width > small.VisualRect.Width || enlarged.Height > small.VisualRect.Height) &&
                AttentionLayout.HorizontalSide(enlarged, area) == side, "an incoming small window can reach a useful size on the inherited side");
            var hidden = independent with { Handle = 4, ZOrder = 2, IsManageable = false, IsTopmost = true };
            check(ActiveLayoutScene.Resolve([article, source, hidden, independent with { ZOrder = 3 }], task)?.WorkingSide is null or 0 &&
                ActiveLayoutScene.Resolve([article, source, independent with { MonitorHandle = 3 }], task)?.WorkingSide is null or 0,
                "a fully hidden resident and another monitor do not occupy this display's opposite reading area");

            var history = new ForegroundHistory();
            history.Visit(1, 1, 1000);
            var dragged = new ManualWindowGesture(1, independent.VisualRect with { Width = article.VisualRect.Width },
                article.VisualRect, area, ManualGestureKind.Move, [], 300, 0);
            history.Placed(article, dragged, 1200, stack);
            var adjusted = article with { VisualRect = article.VisualRect with { Y = area.Top + 80 } };
            history.Resolve([adjusted, source with { ZOrder = 3, VisualRect = source.VisualRect with { Y = area.Top } }], 3500, out var evidence);
            var corrected = Solve([article, source], task with { Attention = evidence });
            check(evidence[1].ManualSide == side &&
                AttentionLayout.HorizontalSide(TaskLayoutSession.Project([article, source], corrected)[0].VisualRect, area) == side,
                "a recent horizontal release survives a neighbor closing, a new order and same-side assistance");
            var reverse = article with { VisualRect = dragged.StartRect };
            history.Placed(reverse, dragged with { EndRect = reverse.VisualRect, StartRect = article.VisualRect }, 4000, [reverse, source]);
            history.Resolve([reverse, source], 4050, out var reversed);
            history.Resolve([reverse, source], 44000, out var expired);
            check(reversed[1].ManualSide == -side && expired[1].ManualSide == 0 &&
                RecordedTaskSignals.From(task with { Attention = reversed }, new Dictionary<nint,int> { [1] = 9 })
                    .ApplyToReplay(new()).Attention?[9].ManualSide == -side,
                "a later drag reverses the side immediately, the evidence replays, and it expires without new gestures");
        }
    }

    private static void CheckOpeningMemory(Action<bool, string> check)
    {
        var area = new RectI(-1920, 0, 1920, 1040);
        var right = new RectI(-920, 80, 900, 800);
        var center = new RectI(-1200, 180, 590, 460);
        var source = new WindowSnapshot(31, right, right, area, 1, default, true, true, true, 0, 96, 31031);
        var child = source with { Handle = 32, ProcessId = 32032, VisualRect = center, OuterRect = center };
        var history = new ForegroundHistory();
        history.Visit(31, 31031, 1000);
        history.ObserveWorkingSide([source], 1400);
        history.Retire(31);
        history.Created(32, 32032, 4100);
        history.Visit(32, 32032, 4110);
        int origin = history.ObserveWorkingSide([child], 4200);
        var ctx = new TaskLayoutContext(Focus: new(32, 0, 0)) { IsWorkspaceObservation = true, OriginSide = origin };
        var scene = ActiveLayoutScene.Resolve([child], ctx);
        check(origin == 1 && scene?.Lane?.Allows(child, center with { X = area.Left }) == false &&
            scene.Lane.Allows(child, center) && history.ObserveWorkingSide([child], 18000) == 0 &&
            RecordedTaskSignals.From(ctx, new Dictionary<nint, int> { [32] = 2 }).ApplyToReplay(new()).OriginSide == 1,
            "recently closed launcher contributes an expiring and replayable side without moving a centered child");
        check(ActiveLayoutScene.Resolve([child with { VisualRect = center with { X = area.Left } }], ctx)?.Lane == null &&
            history.ObserveWorkingSide([child with { MonitorHandle = 2 }], 4500) == 0,
            "native placement in the opposite lane and a display switch keep the application's own landing point");
    }

    private static void CheckRestoreAndPartialReturn(Action<bool, string> check)
    {
        foreach (int side in new[] { -1, 1 })
        {
            var work = new RectI(-1920, 0, 1920, 1040);
            var old = new RectI(side > 0 ? -900 : -1860, 80, 820, 760);
            var a = new WindowSnapshot(71, old, old, work, 1, default, true, true, true, 0, 96, 111);
            var otherRect = old with { X = side < 0 ? -900 : -1860 };
            var b = a with { Handle = 72, ProcessId = 112, VisualRect = otherRect, OuterRect = otherRect };
            var h = new ForegroundHistory();
            h.Visit(a.Handle, a.ProcessId, 1000);
            h.ObserveWorkingSide([a], 1600);
            h.MinimizeChanged(a.Handle, a.ProcessId, 2000, false);
            h.Visit(b.Handle, b.ProcessId, 2020);
            h.ObserveWorkingSide([b], 3400);
            h.MinimizeChanged(a.Handle, a.ProcessId, 8020, true);
            h.Shown(a.Handle, a.ProcessId, 8020);
            h.Visit(a.Handle, a.ProcessId, 8050);
            var desktop = new[] { a, b with { IsForeground = false, ZOrder = 1 } };
            int source = h.ObserveWorkingSide(desktop, 8100);
            var focus = h.Resolve(desktop, 8100, out var evidence);
            var ctx = new TaskLayoutContext(Focus: focus, Attention: evidence, UseWorkingPairs: true)
                { IsWorkspaceObservation = true, AllowGestureAssist = false, OriginSide = source };
            var lane = ActiveLayoutScene.Resolve(desktop, ctx)?.Lane;
            var plan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(desktop, true),
                new(SmartStrength: SmartTidyStrength.Assertive), null, desktop, ctx);
            check(source == -side && lane?.Restore == old && !lane.Allows(a, otherRect) &&
                plan.Moves.All(m => m.Window.Handle != a.Handle || AttentionLayout.HorizontalSide(m.TargetVisualRect, work) == side) &&
                h.OpeningFor(desktop, 8110) == null, "restore owns the last working side even after using the opposite side");
            var misplaced = a with { VisualRect = otherRect, OuterRect = otherRect };
            var obstructed = new[] { misplaced, desktop[1] };
            var repaired = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(obstructed, true),
                new(SmartStrength: SmartTidyStrength.Balanced), null, obstructed, ctx);
            check(repaired.Moves.Any(m => m.Window.Handle == a.Handle &&
                AttentionLayout.HorizontalSide(m.TargetVisualRect, work) == side),
                "restoring on top of the opposite task can recover the old empty side in the global solver");
            var saved = RecordedTaskSignals.From(ctx, new Dictionary<nint,int> { [71] = 4, [72] = 5 }).ApplyToReplay(new());
            h.Resolve([a with { MonitorHandle = 3 }], 8150, out var changed);
            check(saved.Attention?[4].RestoreFrame == old && changed[a.Handle].RestoreFrame == null,
                "restore frame replays but never crosses a changed display");
            var moved = a with { VisualRect = otherRect };
            h.Placed(moved, new(a.Handle, old, otherRect, work, ManualGestureKind.Move, [], 200, 0), 8200);
            h.Resolve([moved], 8250, out var revised);
            check(revised[a.Handle].RestoreFrame == null, "a newer manual gesture immediately replaces the restore expectation");
            h.Retire(a.Handle);
            h.MinimizeChanged(a.Handle, a.ProcessId, 8300, true);
            h.Visit(a.Handle, a.ProcessId, 8310);
            h.Resolve([a], 8350, out var reused);
            check(reused[a.Handle].RestoreFrame == null, "a reused handle cannot inherit the retired instance location");

            var initial = old with { X = old.X - side * 200 };
            var release = old with { X = old.X - side * 60 };
            var atRelease = a with { VisualRect = release };
            h.Submitted(new([new(a with { VisualRect = initial }, old)], [], []), [a], 9000);
            h.Placed(atRelease, new(a.Handle, old, release, work, ManualGestureKind.Move, [], 200, 0), 9250, [atRelease]);
            h.Resolve([atRelease], 9300, out var votes);
            var context = new TaskLayoutContext(Attention: votes);
            var target = old with { Height = old.Height + 15 };
            var limited = context.LimitReturn(atRelease, target);
            check(side * (limited.X - release.X) is > 0 and < 30 && limited.Height == target.Height &&
                h.LimitLiveReturn(atRelease, target, 9350) == limited &&
                context.LimitReturn(atRelease with { VisualRect = limited }, target) == limited &&
                (context with { ExplicitArrange = true }).LimitReturn(atRelease, target) == target,
                "first partial return limits cumulative automatic travel only on the corrected axis");
            var correctionGesture = new ManualWindowGesture(a.Handle, old, release, work, ManualGestureKind.Move, [], 200, 0);
            var gestureContext = context with { GestureAnchor = a.Handle, GestureTarget = target, RecentGesture = correctionGesture,
                UseWorkingPairs = true, Focus = new(a.Handle, b.Handle, 3) };
            WindowSnapshot[] withNeighbor = [atRelease, desktop[1]];
            var assisted = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(withNeighbor, true),
                new(SmartStrength: SmartTidyStrength.Assertive), null, withNeighbor, gestureContext);
            check(gestureContext.GeometryOnlyWindow == a.Handle && assisted.Layers.Count == 0 &&
                assisted.Moves.All(m => m.Window.Handle == a.Handle && context.LimitReturn(m.Window, m.TargetVisualRect) == m.TargetVisualRect),
                "a corrective release cannot use its preview to undo the correction or make another window pay for it");
            check(h.LimitLiveReturn(atRelease, target, 40000) == target, "first return expires without creating a new layout event");
        }
    }

    private static void CheckPairWidth(Action<bool, string> check)
    {
        const string a = "123456781234567812345678", b = "ABCDEF12ABCDEF12ABCDEF12";
        var box = new RectI(0, 0, 960, 800);
        var w = new WindowSnapshot(41, box, box, new(0, 0, 1920, 1040), 1, default, true, true, true, 0);
        (WindowSnapshot Window, string? Key)[] entries = [(w, a), (w with { Handle = 42, IsForeground = false }, b)];
        var rules = new CompiledLayoutRules(Version: 5, Pairs: [new(a, b, OnlyWhenPaired: true, FirstWidth: .66)]);
        var opening = new LayoutFocus(42, 41, 0, "opening-resident-left");
        check(rules.IsValid() && !(rules with { Version = 4 }).IsValid() &&
            rules.BindSlots(entries, opening) is [{ Window: 41, Width: .66, PreferCenter: false, Partner: 0 }] &&
            rules.Bind(entries, focus: opening).Length == 0 && rules.BindSlots(entries, new(41, 0, 0)).Length == 0 &&
            rules.BindSlots(entries, new(41, 42, 2, "handoff")).Length == 0,
            "pair-specific width works in either focus order and cannot establish a pair or borrow a horizontal position");
    }

    private static void CheckRepeatedReturn(Action<bool, string> check)
    {
        var rect = new RectI(900, 100, 800, 800);
        var w = new WindowSnapshot(45, rect, rect, new(0, 0, 1920, 1040), 1, default, true, true, true, 0, 96, 45045);
        var submitted = rect with { X = 80 };
        var other = w with { Handle = 46, IsForeground = false, ZOrder = 1 };
        var h = new ForegroundHistory();
        h.Visit(w.Handle, w.ProcessId, 1000);
        for (var i = 0; i < 2; i++)
        {
            h.Submitted(new([new(w, submitted)], [], []), [w, other], 1500 + i * 5000);
            h.Placed(w, new(w.Handle, submitted, rect, w.WorkArea, ManualGestureKind.Move, [], 250, 0), 2000 + i * 5000, [w]);
            h.Resolve([w], 2050 + i * 5000, out var current);
            check((current[w.Handle].RepeatedReversal != null) == (i == 1),
                "two confirmed returns count independently of other windows disappearing during the gesture");
        }
        h.Resolve([w, other], 60000, out var votes);
        var task = new TaskLayoutContext(Attention: votes);
        var replay = RecordedTaskSignals.From(task, new Dictionary<nint,int> { [45] = 45 }).ApplyToReplay(new());
        check(replay.RepeatsReversal(w, submitted) && !task.RepeatsReversal(w, rect with { Height = 890 }) &&
            !(task with { ExplicitArrange = true }).RepeatsReversal(w, submitted), "repeated reversal veto is per axis and survives replay");
        h.Interact(w.Handle, w.ProcessId, 125000);
        h.Resolve([w], 128000, out var expired);
        check(expired[w.Handle].RepeatedReversal == null, "typing does not renew a repeated reversal");
    }

    private static void CheckCoveredHandoff(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1024, 720);
        var box = new RectI(87, 90, 482, 413);
        var main = new WindowSnapshot(1, box, box, area, 1, default, true, true, true, 0, 96, 1001);
        var source = main with { Handle = 2, ProcessId = 1002, IsForeground = false, ZOrder = 1 };
        WindowSnapshot[] desktop = [main, source];
        var visits = new Dictionary<nint, LayoutAttention> { [1] = new(1, .1, .9, 0, true), [2] = new(.97, .05, .9, 1, false) };
        foreach (bool repeated in new[] { false, true })
        {
            var ctx = new TaskLayoutContext(Focus: new(1, 2, repeated ? 3 : 1, repeated ? "recent-switch-pair" : "handoff"),
                Attention: visits, UseWorkingPairs: true) { IsWorkspaceObservation = true, AllowGestureAssist = false };
            var plan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(desktop, true),
                new(SmartStrength: SmartTidyStrength.Balanced), null, desktop, ctx);
            var result = TaskLayoutSession.Project(desktop, plan);
            var exposure = IntentLayoutPlanner.ScreenExposure(result[1].VisualRect, [result[0].VisualRect], area, 96);
            check(repeated ? exposure.Visible >= .50 && exposure.AccessWidth >= 80 :
                exposure.AccessWidth >= 80 && AttentionLayout.HorizontalSide(result[0].VisualRect, area) != 1,
                repeated ? "two repeatedly used, completely covered windows become readable" : "a first handoff exposes a return target without sending the foreground across the screen");
        }
    }

    private static void CheckCentralSwitches(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1040);
        var rect = new RectI(400, 80, 1120, 850);
        WindowSnapshot a = new(1, rect, rect, area, 1, default, true, true, true, 0, 96, uint.MaxValue - 1, SurfaceState: "windowed");
        var b = a with { Handle = 2, ProcessId = uint.MaxValue - 2, IsForeground = false, ZOrder = 1,
            VisualRect = rect with { X = rect.X + 7, Y = rect.Y + 14 } };
        var history = new ForegroundHistory();
        history.Visit(2, b.ProcessId, 1000);
        history.Visit(1, a.ProcessId, 6800);
        check(history.OpeningFor([a, b], 6810) == null, "activating an overlapping centered surface does not establish a side from a few pixels");
        history.Shown(1, a.ProcessId, 6780);
        check(history.OpeningFor([a, b], 6810) is { ResidentLeft: true },
            "a real restore can place a new surface beside that same centered resident");
        var visit = new Dictionary<nint, LayoutAttention> { [1] = new(1, .2, 0, 0, true), [2] = new(.9, 1, .8, 0, false) };
        var task = new TaskLayoutContext(Focus: new(1, 0, 0), Attention: visit, UseWorkingPairs: true)
            { IsWorkspaceObservation = true, AllowGestureAssist = false };
        IntentLayoutPlan Solve(WindowSnapshot[] ws, TaskLayoutContext context) =>
            LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(ws, true),
                new(SmartStrength: SmartTidyStrength.Assertive), null, ws, context);
        var returned = new[] { b with { IsForeground = true, ZOrder = 0 }, a with { IsForeground = false, ZOrder = 1 } };
        check(Solve([a, b], task).Moves.Count == 0 && Solve(returned, task with { Focus = new(2, 1, 2) }).Moves.Count == 0,
            "a passive round trip between centered windows preserves both frames in sequence");
        var paired = task with { Focus = new(1, 2, 4), Attention = new Dictionary<nint, LayoutAttention>(visit) { [1] = new(1, 1, .9, 1, true) } };
        check(Solve([a, b], paired).Moves.Count > 0 &&
            Solve([a, b], task with { Focus = new(1, 2, 0, "opening-resident-left") }).Moves.Count > 0,
            "independent opening or two engaged surfaces can establish a split from the same geometry");
        foreach (bool mirror in new[] { true, false })
        {
            var side = returned[0] with { VisualRect = new(mirror ? 0 : 1430, 90, 480, 850) };
            var peer = returned[1] with { VisualRect = new(mirror ? 450 : 0, 60, 1400, 900) };
            WindowSnapshot[] narrow = [side, peer];
            var repaired = Solve(narrow, paired with { Focus = new(2, 1, 4) });
            var placed = TaskLayoutSession.Project(narrow, repaired);
            check(repaired.Moves.Count > 0 && placed[0].VisualRect.Width > side.VisualRect.Width &&
                (placed[0].VisualRect.Left < placed[1].VisualRect.Left) == mirror,
                "automatic repacking can enlarge a cramped reader while preserving established left-right order");
        }
    }

}
