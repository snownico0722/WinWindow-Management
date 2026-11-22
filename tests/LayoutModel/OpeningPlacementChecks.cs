using NeatWin.Core;

internal static class OpeningPlacementChecks
{
    internal static void Run(Action<bool, string> check)
    {
        CheckCentralSwitches(check);
        CheckOccupiedSide(check);
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
            var context = new TaskLayoutContext(Focus: focus, Attention: attention, WindowHints: hints, UseWorkingPairs: true);
            var set = new VisibilityAnalyzer().SelectForManualArrange(scene, false);
            var plan = LayoutDispatch.Create(LayoutRoute.Smart, set, new(), null, scene, context);
            var after = TaskLayoutSession.Project(scene, plan);
            var a = after.Single(w => w.Handle == 1).VisualRect;
            var b = after.Single(w => w.Handle == 2).VisualRect;
            check((left ? a.Left > b.Left : a.Left < b.Left) && a.Intersect(b).Area < b.Area * .25 &&
                Math.Abs((left ? b.Left - resident.VisualRect.Left : b.Right - resident.VisualRect.Right)) <= 24,
                "opening separates the new reading surface and retains the existing task on its original side");
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
            check(space.Moves.Count > 0 && AttentionLayout.HorizontalSide(TaskLayoutSession.Project([article, source], space)[0].VisualRect, area) == -side,
                "an independent opening can still take the free opposite side");
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

    private static void CheckCentralSwitches(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1040);
        var rect = new RectI(400, 80, 1120, 850);
        WindowSnapshot a = new(1, rect, rect, area, 1, default, true, true, true, 0, 96, 101, SurfaceState: "windowed");
        var b = a with { Handle = 2, ProcessId = 202, IsForeground = false, ZOrder = 1,
            VisualRect = rect with { X = rect.X + 7, Y = rect.Y + 14 } };
        var history = new ForegroundHistory();
        history.Visit(2, 202, 1000);
        history.Visit(1, 101, 6800);
        check(history.OpeningFor([a, b], 6810) == null, "activating an overlapping centered surface does not establish a side from a few pixels");
        history.Shown(1, 101, 6780);
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
