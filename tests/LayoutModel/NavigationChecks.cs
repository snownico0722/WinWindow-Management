using NeatWin.Core;

internal static class NavigationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var work = new RectI(0, 0, 1920, 1040);
        WindowSnapshot Window(int id, uint pid, bool front = false, nint owner = 0) =>
            new((nint)id, new(80, 80, 1200, 850), new(80, 80, 1200, 850), work, 1, default,
                true, front, true, front ? 0 : id, 96, pid) { Owner = owner };
        var a = Window(11, 300011, true); var b = Window(57, 300057, true);
        var c = Window(39, 300039, true, b.Handle); var d = Window(45, 300045, true);
        WindowSnapshot[] openedB = [b, a with { IsForeground = false }];
        WindowSnapshot[] openedC = [c, b with { IsForeground = false }, a with { IsForeground = false }];
        var history = new ForegroundHistory();
        history.Visit(a.Handle, a.ProcessId, 10);
        history.Interact(a.Handle, a.ProcessId, 950, click: true);
        history.Created(b.Handle, b.ProcessId, 980);
        history.Visit(b.Handle, b.ProcessId, 1000);
        var first = history.NavigationFor(openedB, 1100);
        check(first is { Hops: [{ Evidence: "click-open" }] } && first.ProtectsAction(a.Handle, 1100) &&
            !first.ProtectsAction(a.Handle, first.SettleUntil),
            "first open from a clicked source defers workspace layout and recovery through its quiet interval");

        var scheduler = new AutomaticLayoutScheduler();
        AutomaticLayoutRequest? Observe(WindowSnapshot[] visible, long now) =>
            scheduler.Observe(visible, now, false, navigationSettleUntil: history.NavigationFor(visible, now)?.SettleUntil ?? 0);
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [a], 10);
        Observe(openedB, 1100);
        var interim = Observe(openedB, 1600);
        history.Created(c.Handle, c.ProcessId, 1630);
        history.Visit(c.Handle, c.ProcessId, 1650);
        Observe(openedC, 1700);
        var path = history.NavigationFor(openedC, 2450);
        var whileOpening = Observe(openedC, 2450);
        var focus = history.Resolve(openedC, 2450);
        check(first != null && path != null && path.SettleUntil > first.SettleUntil && interim == null && whileOpening == null &&
            scheduler.HasPending && focus.Companion == b.Handle && focus.Evidence == "handoff" &&
            focus.NavigationAncestors!.SequenceEqual([a.Handle, b.Handle]) &&
            focus.Navigation is [{ Evidence: "click-open" }, { Evidence: "owner-open" }] &&
            path.ProtectsAction(a.Handle, 2450) && path.ProtectsAction(b.Handle, 2450) && !path.ProtectsAction(d.Handle, 2450),
            "two fast anonymous cross-process opens extend the quiet interval, keep both ancestors and expose only the immediate return");
        check(Observe(openedC, path!.SettleUntil)?.Trigger == LayoutTrigger.WorkspaceChange &&
            Observe(openedC, path.SettleUntil + 1000) == null && !scheduler.HasPending &&
            history.Resolve(openedC, 10_000).IsNavigationAncestor(a.Handle),
            "silent endpoint reading releases one finite automatic arrangement and preserves the return path");

        history.Visit(b.Handle, b.ProcessId, 12_000);
        var back = history.Resolve([b, c with { IsForeground = false }, a with { IsForeground = false }], 12_700);
        history.Visit(c.Handle, c.ProcessId, 13_000);
        var compared = history.Resolve(openedC, 13_650);
        check(back is { Evidence: "handoff" } && back.Companion == a.Handle &&
            compared is { Evidence: "recent-switch-pair", Switches: >= 2 } && compared.Companion == b.Handle &&
            compared.NavigationAncestors == null,
            "returning one level preserves the remaining path; revisiting the same existing endpoint promotes actual comparison");

        var cross = new ForegroundHistory();
        var fixedSource = a with { MonitorHandle = 2, IsManageable = false, SurfaceState = "maximized", IsForeground = false };
        cross.Visit(a.Handle, a.ProcessId, 10);
        cross.Interact(a.Handle, a.ProcessId, 610, click: true);
        cross.Created(b.Handle, b.ProcessId, 620);
        cross.Visit(b.Handle, b.ProcessId, 650);
        cross.Created(c.Handle, c.ProcessId, 1150);
        cross.Visit(c.Handle, c.ProcessId, 1170);
        WindowSnapshot[] screens = [c, b with { IsForeground = false }, fixedSource];
        var crossPath = cross.NavigationFor(screens, 1400)!;
        check(crossPath.Hops.Length == 2 && crossPath.ProtectsAction(a.Handle, 1400) &&
            cross.Resolve(screens, 1400).IsNavigationAncestor(a.Handle),
            "native navigation evidence crosses screens and includes a maximized ancestor without requiring movable geometry");
        cross.PointerQuietUntil(cross.NavigationFor(screens, 1450)!, new(100, 100), c, 1450);
        long extended = cross.PointerQuietUntil(cross.NavigationFor(screens, crossPath.SettleUntil - 100)!,
            new(150, 110), c, crossPath.SettleUntil - 100);
        long limit = crossPath.SettleUntil + 2_000;
        for (long time = extended - 100; time < limit + 150; time += 150)
            extended = cross.PointerQuietUntil(cross.NavigationFor(screens, time)!, new((int)(time % 190) + 120, 100), c, time);
        check(extended == limit, "pointer motion inside a new endpoint extends settling only within the finite navigation budget");
        cross.Retire(c.Handle);
        cross.Visit(b.Handle, b.ProcessId, 5800);
        var afterClose = cross.Resolve([b, fixedSource], 5880);
        check(afterClose.Main == b.Handle && afterClose.Companion == a.Handle && afterClose.Navigation is [{ Evidence: "click-open" }],
            "closing a leaf and restoring its parent retains the live return route, including across screens");

        var switches = new ForegroundHistory();
        switches.Visit(a.Handle, a.ProcessId, 20);
        switches.Interact(a.Handle, a.ProcessId, 1000, click: true);
        switches.Visit(b.Handle, b.ProcessId, 1050);
        switches.Visit(c.Handle, c.ProcessId, 1800);
        check(switches.NavigationFor(openedC, 1850) == null, "old-window switching and an owner alone cannot manufacture a new-open chain");

        var fresh = new ForegroundHistory();
        fresh.Visit(a.Handle, a.ProcessId, 100);
        var sameProcess = b with { ProcessId = a.ProcessId };
        fresh.Created(b.Handle, a.ProcessId, 700);
        fresh.Visit(b.Handle, a.ProcessId, 750);
        check(fresh.NavigationFor([sameProcess, a with { IsForeground = false }], 910)?.Hops is [{ Evidence: "foreground-open" }],
            "fresh sibling documents in an established process may use foreground continuity without a pointer click");
        fresh.Retire(b.Handle);
        fresh.Visit(b.Handle, a.ProcessId, 1180);
        check(fresh.NavigationFor([sameProcess, a with { IsForeground = false }], 2100) == null,
            "destroying the endpoint clears creation evidence even when the same HWND and PID reappear");

        var foreign = new ForegroundHistory();
        foreign.Visit(a.Handle, a.ProcessId, 10);
        foreign.Created(b.Handle, b.ProcessId, 600);
        foreign.Interact(a.Handle, a.ProcessId, 620); // wheel, not a click
        foreign.Visit(b.Handle, b.ProcessId, 650);
        check(foreign.NavigationFor(openedB, 800) == null,
            "an unrelated foreground-stealing process with no owner or source click does not delay layout");

        var rule = new WindowBehaviorRule("test", "test", "alpha.exe", ["beta.exe"], DelayMilliseconds: 1_500);
        var firstUse = new WindowBehaviorFrame("test", 0, DateTimeOffset.UnixEpoch, [
            new(31, "alpha.exe", 0, "appeared", "observed", null), new(31, "alpha.exe", 0, "foreground", "observed", null)]);
        var secondUse = firstUse with { Tick = 1500, Time = firstUse.Time.AddMilliseconds(1500), Events = [
            new(79, "beta.exe", 100, "appeared", "observed", null), new(79, "beta.exe", 100, "foreground", "observed", null)], NavigationSources = [31] };
        var protectedFrame = secondUse with { Tick = 3850, Time = firstUse.Time.AddMilliseconds(3850), Events = [] };
        var releasedFrame = protectedFrame with { Tick = 4200, Time = firstUse.Time.AddMilliseconds(4200), NavigationSources = null };
        check(BehaviorReplay.Predict([firstUse, secondUse, protectedFrame, releasedFrame], [rule]) is [{ Tick: 4200 }],
            "offline behavior replay preserves navigation guards across sparse samples and releases the matching source at the recorded frame");

        foreach (var action in new[] { WindowBehaviorAction.Minimize, WindowBehaviorAction.Close })
        {
            var sequence = new WindowBehaviorSequence(new("test", "test", "alpha.exe", ["beta.exe"], action, DelayMilliseconds: 1_500));
            BehaviorWindow source = new(31, "alpha.exe", 0, true), target = new(79, "beta.exe", 10, true);
            sequence.Observe(0, 0, [source], new Dictionary<int, BehaviorDeparture>(), new(), false, true);
            var pair = new[] { source with { Foreground = false }, target };
            sequence.Observe(1000, 10_000, pair, new Dictionary<int, BehaviorDeparture>(), new(), false, true);
            var hold = sequence.Observe(3100, 31_000, pair, new Dictionary<int, BehaviorDeparture>(), new(), false, true,
                preservedSources: new HashSet<int> { source.Id });
            var released = sequence.Observe(3600, 36_000, pair, new Dictionary<int, BehaviorDeparture>(), new(), false, true,
                preservedSources: new HashSet<int> { 99 });
            check(hold.Dismiss == null && released.Dismiss == source.Id,
                $"a navigation ancestor blocks an enabled {action} without blocking the same rule on unrelated windows");
        }
    }
}
