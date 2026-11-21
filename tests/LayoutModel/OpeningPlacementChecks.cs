using NeatWin.Core;

internal static class OpeningPlacementChecks
{
    internal static void Run(Action<bool, string> check)
    {
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
}
