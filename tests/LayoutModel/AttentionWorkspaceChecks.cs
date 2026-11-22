using NeatWin.Core;

internal static class AttentionWorkspaceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(1920, 0, 1920, 1040);
        WindowSnapshot Window(int id, int x, int y, int width, int height) =>
            new(id, new(area.Left + x, y, width, height), new(area.Left + x, y, width, height),
                area, 2, default, true, id == 1, true, id - 1, 96, (uint)id);
        var document = Window(1, 0, 80, 860, 900);
        var peer = Window(2, 880, 100, 960, 760);
        var attention = new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, .9, 0, true) };
        var context = new TaskLayoutContext(Focus: new(1, 0, 0), Attention: attention, UseWorkingPairs: true);
        var shown = new VisibleWindow(document, document.VisualRect.Area, document.VisualRect.Area, 1);
        var wider = document.VisualRect with { Width = document.VisualRect.Width + 30 };
        var reading = new TaskWindowHint(document.Handle, Role: WindowUseRole.Document);
        double Perceived(TaskLayoutContext ctx, RectI target) =>
            IntentLayoutPlanner.AttentionInterruption(shown, target, ctx, reading);
        var afterRelease = context with { GestureAnchor = document.Handle, GestureTarget = wider };
        var fresh = context with { FirstPlacementWindow = document.Handle };
        var movingPeer = context with { GestureAnchor = peer.Handle, GestureTarget = wider };
        var repeated = context with { Attention = new Dictionary<nint, LayoutAttention>
            { [document.Handle] = attention[1] with { RecentSystemPlacement = 1 } } };
        check(Perceived(context, document.VisualRect) == 0 && Perceived(afterRelease, wider) < Perceived(context, wider) &&
            Perceived(fresh, wider) < Perceived(context, wider) && Perceived(movingPeer, wider) == Perceived(context, wider) &&
            Perceived(repeated, wider) > Perceived(context, wider), "same 30px correction costs less at release or first placement and more after a prior submit");
        var firstCost = new TaskCostBreakdown(0, 0, 0, 0, 0, 0, 0, 0, Goal: new(0, 0, 1));
        var tinyChange = firstCost with { Goal = default, Interruption = .03 };
        check(tinyChange.Total < firstCost.Total && new SceneWeights(Interruption: 6).Apply(tinyChange).Total > firstCost.Total &&
            new AttentionDeficit(0, 0, .01).DemandCost < new AttentionDeficit(0, 0, .02).DemandCost,
            "a small comfort improvement pays for a cheap adjustment and loses when its perceptual cost grows");
        var nearlyComfortable = document with { VisualRect = new(area.Left + 50, 40, 910, 710) };
        const string key = "AAAAAAAAAAAAAAAAAAAAAAAA";
        IntentLayoutPlan PriceMicroAdjustment(double price)
        {
            var scene = new LayoutScene("reader", "workspace", [key], Weights: new(Interruption: price), ReconsiderSettled: true);
            var ctx = context with { Rules = new(Version: 5, Scenes: [scene]), IsWorkspaceObservation = true,
                AppKeys = new Dictionary<nint, string?> { [document.Handle] = key } };
            return LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange([nearlyComfortable], true),
                new(), null, [nearlyComfortable], ctx);
        }
        check(PriceMicroAdjustment(1).Moves is [{ TargetVisualRect.Width: > 910 and < 960 }] &&
            PriceMicroAdjustment(8).Moves.Count == 0, "same readable window can gain a few pixels or stay put under an imported interruption weight");
        foreach (bool right in new[] { true, false })
        {
            var active = Window(1, right ? 40 : 980, 100, 900, 750);
            var returning = Window(2, right ? 1080 : -60, 100, 900, 750);
            var handoffWindows = new[] { active, returning };
            var weakPreference = new PlacementPreference(2, 0, right ? .28 : .72, .5, 0, -1, .44, PreferWidth: false);
            var handoff = context with { Focus = new(1, 2, 1, "handoff"), IsWorkspaceObservation = true,
                Attention = new Dictionary<nint, LayoutAttention>(attention) { [2] = new(.95, .55, .80, 1, false) },
                Placement = [weakPreference] };
            var result = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(handoffWindows, true),
                new(SmartStrength: SmartTidyStrength.Assertive), null, handoffWindows, handoff);
            var repaired = TaskLayoutSession.Project(handoffWindows, result)[1].VisualRect;
            check(AttentionLayout.HorizontalSide(repaired, area) == (right ? 1 : -1) && repaired.Intersect(area) == repaired,
                "a usable return window can be brought just inside the screen without following a weak opposite-side prior");
        }
        var clock = new ForegroundHistory();
        clock.Visit(document.Handle, document.ProcessId, 0);
        clock.Submitted(new([new(document, wider)], [], []), [document], 120);
        var landed = document with { VisualRect = wider, OuterRect = wider };
        clock.Resolve([landed], 240, out var recent);
        clock.Resolve([document], 250, out var refused);
        clock.Interact(document.Handle, document.ProcessId, 4500);
        clock.Resolve([landed], 4500, out var expired);
        var savedRecent = RecordedTaskSignals.From(context with { Attention = recent },
            new Dictionary<nint, int> { [document.Handle] = 7 }).ApplyToReplay(new());
        check(savedRecent.Attention![7].RecentSystemPlacement > .9 && refused[document.Handle].RecentSystemPlacement == 0 &&
            expired[document.Handle].RecentSystemPlacement == 0, "repeat cost survives replay only for a matching recent placement and input never renews it");
        static RectI[] Solve(WindowSnapshot[] windows, TaskLayoutContext context)
        {
            var visible = new VisibilityAnalyzer().SelectForManualArrange(windows, true);
            var plan = LayoutDispatch.Create(LayoutRoute.Smart, visible, new(SmartStrength: SmartTidyStrength.Assertive), null, windows, context);
            return TaskLayoutSession.Project(windows, plan).Select(w => w.VisualRect).ToArray();
        }
        var comparisonContext = context with { Focus = new(1, 2, 4), Attention = new Dictionary<nint, LayoutAttention>(attention)
            { [2] = new(1, 1, 1, 1, false), [3] = new(1, 1, 1, 1, false) } };
        IntentLayoutPlan Balanced(WindowSnapshot[] windows, TaskLayoutContext task) => LayoutDispatch.Create(LayoutRoute.Smart,
            new VisibilityAnalyzer().SelectForManualArrange(windows, true), new(SmartStrength: SmartTidyStrength.Balanced), null, windows, task);
        // 用陌生窗口、镜像及不同原点回放第六版的因果链。参考离开后，原位可读的窗口仍无需横移。
        foreach (bool right in new[] { false, true })
        foreach (string surface in new[] { "maximized", "fullscreen" })
        {
            var side = Window(1, right ? 860 : 0, 30, 1060, 980);
            var rear = Window(2, 0, 0, area.Width, area.Height) with { IsManageable = false, SurfaceState = surface };
            var workspace = context with { IsWorkspaceObservation = true, AllowGestureAssist = false };
            check(Balanced([side, rear], workspace).Moves.Count == 0 &&
                Balanced([side], workspace).Moves.All(m => m.TargetVisualRect.X == side.VisualRect.X),
                $"a single eligible window preserves a visible {surface} reference and retains its place after it leaves (right: {right})");
        }
        var left = Window(1, 0, 30, 1100, 990);
        var center = left.VisualRect with { X = area.X + 410 };
        var release = left.VisualRect with { X = area.X + 48 };
        var released = left with { VisualRect = release };
        var corrections = new ForegroundHistory();
        corrections.Visit(1, 1, 0);
        corrections.Submitted(new([new(left, center)], [], []), [left, peer], 1200);
        var manual = new ManualWindowGesture(1, center, release, area, ManualGestureKind.Move, [], 400, 0);
        corrections.Placed(released, manual, 2000, [released, peer]);
        corrections.Submitted(new([new(released, left.VisualRect)], [], []), [released, peer], 2050);
        corrections.Interact(1, 1, 16000);
        corrections.Resolve([left, peer], 30000, out var observedCorrection);
        var evidence = observedCorrection[1].Correction;
        check(evidence is { X.Confidence: > .8, Y: null, Width: null, Height: null } &&
            evidence.Cost(left.VisualRect, center) > .8 && evidence.Cost(left.VisualRect, left.VisualRect with { Height = 800 }) == 0,
            "a partial reversal and subsequent edge assist revise only the returned axis after the gesture expires");
        var oldHabit = new PlacementPreference(1, 0, .50, .55, 0, -1, .9, AxisConfidence: new(.75, .9, 0));
        var countered = context with { Attention = observedCorrection, Placement = [oldHabit], WorkspaceCause = WorkspaceChangeCause.Focus };
        var logged = RecordedTaskSignals.From(countered, new Dictionary<nint, int> { [1] = 1 });
        var replayed = System.Text.Json.JsonSerializer.Deserialize<RecordedTaskSignals>(
            System.Text.Json.JsonSerializer.Serialize(logged))!.ApplyToReplay(new());
        check(replayed.WorkspaceCause == WorkspaceChangeCause.Focus &&
            replayed.WithCurrentCorrections([left, peer]).Placement is [{ PreferCenter: false, PreferWidth: true, WidthConfidence: .9 }],
            "replay preserves current counterevidence, the independent width prior and the dispatch cause");
        corrections.Resolve([left], 31000, out var differentTask);
        check(differentTask[1].Correction == null, "closing the former companion ends the situational correction");

        var dockedWindow = Window(1, 1240, 150, 600, 500);
        var opposite = Window(2, 180, 100, 740, 710);
        var drag = new ManualWindowGesture(1, dockedWindow.VisualRect with { X = area.Left + 610 },
            dockedWindow.VisualRect, area, ManualGestureKind.Move, [], 450, 50);
        var split = context with { GestureAnchor = 1, RecentGesture = drag };
        var splitPlan = Balanced([dockedWindow, opposite], split);
        check(splitPlan.Moves.Any(m => m.Window.Handle == 2) && splitPlan.Moves.Any(m => m.Window.Handle == 1 &&
            m.TargetVisualRect.Right == area.Right && m.TargetVisualRect.Width > dockedWindow.VisualRect.Width),
            "a deliberate side release completes both halves with one unvisited visible reading window");
        var palette = Window(2, 100, 170, 140, 100);
        check(!Balanced([dockedWindow, palette], split).Moves.Any(m => m.Window.Handle == 2),
            "an entirely visible utility palette is too small to become the opposite reading window");

        var hidden = Window(1, 100, 70, 1000, 800);
        var strip = Window(4, 125, 70, 1000, 800);
        var layered = new[] { hidden, Window(2, 1310, 0, 200, 230), Window(3, 1270, 260, 240, 220), strip };
        var tracker = new IdleWindowReclaim();
        var history = new ForegroundHistory();
        WindowSnapshot? Reclaim(WindowSnapshot[] scene, long tick) =>
            tracker.Choose(scene, new([], [], []), context, SmartTidyStrength.Balanced, tick, w => (int)w.Handle, history);
        check(Reclaim(layered, 1000) == null && Reclaim(layered, 301000) == null &&
            Reclaim(layered, 961000)?.Handle == strip.Handle &&
            Reclaim([.. layered.Take(3), strip with { VisualRect = hidden.VisualRect }], 962000) == null,
            "a long-idle rear strip can clear visible desktop space while a fully buried window stays put");

        var uneven = new[] { Window(1, 20, 80, 1400, 800), Window(2, 1040, 80, 420, 800) };
        var shared = TaskLayoutSession.Project(uneven, Balanced(uneven, comparisonContext));
        check(shared[0].VisualRect.Width >= 900 && shared[1].VisualRect.Width >= 900 &&
            shared[0].VisualRect.Intersect(shared[1].VisualRect).IsEmpty, "a crowded balanced pair redistributes excess width without overlap");
        var observed = comparisonContext with { IsWorkspaceObservation = true, AllowGestureAssist = false };
        var ready = new[] { Window(1, 0, 80, 960, 900), Window(2, 960, 60, 960, 950) };
        var popup = Window(3, 940, 875, 120, 30) with { IsTopmost = true, IsManageable = false, ZOrder = -1 };
        foreach (bool mainOnRight in new[] { true, false })
        {
            var columns = new[] { Window(1, mainOnRight ? 900 : 0, 85, 1020, 855),
                Window(2, mainOnRight ? 0 : 900, 40, 1020, 920) };
            var unrelatedHabit = new PlacementPreference(2, 99, .67, .52, 0, -1, .8);
            var quietPeer = context with { IsWorkspaceObservation = true, Placement = [unrelatedHabit] };
            foreach (var visit in new[] { observed with { Placement = [unrelatedHabit] }, quietPeer })
            {
                var plan = Balanced([popup, .. columns], visit);
                check(plan.Moves.Count == 0 && plan.Layers.Count == 0 &&
                    plan.Groups.Single().Task?.Limits.Contains("existing-readable-columns") == true,
                    $"readable columns keep their frames through input and peer expiry (main on right: {mainOnRight}, companion: {visit.Focus?.Companion})");
            }
            var alone = Balanced([columns[0]], quietPeer);
            check(alone.Moves.All(m => m.Window.Handle != 1 || m.TargetVisualRect.X == columns[0].VisualRect.X),
                $"removing the visible peer preserves the usable remaining column (main on right: {mainOnRight})");
        }
        foreach (var frame in new[] { ready, new[] { popup, ready[0], ready[1] }, ready })
            check(Balanced(frame, observed).Moves.Count == 0,
                "a readable pair stays settled as a peripheral surface appears and disappears");
        var viewport = ready[1].VisualRect with { X = ready[1].VisualRect.X + 30, Y = 180, Width = 880, Height = 620 };
        var video = new VideoBlackBarHint(2, VideoBlackBarOrientation.Horizontal, 1.78, viewport, .95);
        var ordinarySwitch = observed with { Focus = new(1, 2, 1, "handoff"),
            Placement = [new(2, 0, .77, .3, 0, -1, .48, PreferWidth: false)] };
        foreach (var hints in new[] { ordinarySwitch, ordinarySwitch with { VideoHint = video },
            ordinarySwitch with { VideoHint = video with { Tendency = VideoBlackBarTendency.Expand } },
            context with { IsWorkspaceObservation = true, VideoHint = video } })
        {
            var result = Balanced(ready, hints);
            check(result.Layers.Count == 0 && (hints.VideoHint == null ? result.Moves.Count == 0 :
                result.Moves is [{ Window.Handle: 2 } resize] && resize.TargetVisualRect.X == ready[1].VisualRect.X &&
                resize.TargetVisualRect.Width == ready[1].VisualRect.Width && resize.TargetVisualRect.Height < ready[1].VisualRect.Height),
                "a focus handoff keeps readable columns and horizontal bars shorten only the browser on its existing column");
        }
        var atFloor = ordinarySwitch with { VideoHint = video, SizeFloors = [new(2, 1, ready[1].VisualRect.Height)] };
        check(Balanced(ready, atFloor).Moves.Count == 0,
            "when cropping reaches a size floor and growing covers the neighbor, the entire pair stays put");
        var withSpace = ready.Select(w => w with { WorkArea = area with { Width = 2500 },
            VisualRect = w.Handle == 2 ? w.VisualRect with { X = w.VisualRect.X + 240 } : w.VisualRect }).ToArray();
        var positive = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(withSpace, true),
            new(), null, withSpace, atFloor with { VerifiedRepeat = true,
                VideoHint = video with { ViewportVisualRect = viewport with { X = viewport.X + 240 } } });
        check(positive.Moves is [{ Window.Handle: 2 } growth] && growth.TargetVisualRect.Width > ready[1].VisualRect.Width &&
            positive.Layers.Count == 0, "with free space the same shrink preference permits expansion when shrinking hits a floor");
        var contrary = ordinarySwitch with { Placement = [new(2, 0, .2, .5, 0, -1, .85)] };
        check(Balanced(ready, contrary).Groups.Single().Candidates.Length > 1,
            "a learned position on the opposite side still reopens the search");
        var underMain = ready[1] with { VisualRect = ready[1].VisualRect with { X = ready[1].VisualRect.X - 260 } };
        check(!IntentLayoutPlanner.DesktopRegionsPreserved(ready, [ready[0], underMain], observed with { VideoHint = video }) &&
            IntentLayoutPlanner.DesktopRegionsPreserved([ready[0], underMain], ready, observed with { VideoHint = video }, ready),
            "moving a detected viewport under the foreground loses content while moving it back reveals it");
        var covered = ready[1] with { VisualRect = ready[1].VisualRect with { X = area.Left + 15 } };
        check(Balanced([ready[0], covered], observed).Moves.Count > 0 && Balanced(uneven, observed).Moves.Count > 0,
            "a real collision or a narrow reference immediately reopens workspace arrangement");
        var utilityFocus = ready.Select(w => w with { IsForeground = false }).ToArray();
        check(Balanced(utilityFocus, observed).Groups.Count == 0 &&
            Balanced(utilityFocus, observed with { ExplicitArrange = true }).Groups.Count > 0,
            "an uncaptured foreground suspends automatic work while an explicit arrangement remains available");
        var spacious = uneven.Select(w => w with { WorkArea = area with { Width = 2560, Height = 1440 } }).ToArray();
        var generous = TaskLayoutSession.Project(spacious, Balanced(spacious, comparisonContext));
        check(generous[0].VisualRect.Width >= uneven[0].VisualRect.Width && generous[1].VisualRect.Width >= 900,
            "on a wider display an expanded reference does not require the primary to shrink");
        var largePrint = comparisonContext with { Profile = new(ComfortableWidthDip: 1680) };
        var readingBudgets = AttentionLayout.Targets(new VisibilityAnalyzer().SelectForManualArrange(spacious, true).ToArray(),
            largePrint, SmartTidyStrength.Balanced)!;
        check(readingBudgets.All(t => t.FrameWidth > 0) && readingBudgets.Sum(t => t.Width) < spacious[0].WorkArea.Width,
            "horizontal reading contention is budgeted even when both reading heights fit vertically");
        var rows = spacious.Select((w, i) => w with { VisualRect = new(area.Left, i * 720, 1500, 670) }).ToArray();
        var rowTargets = AttentionLayout.Targets(new VisibilityAnalyzer().SelectForManualArrange(rows, true).ToArray(),
            largePrint, SmartTidyStrength.Balanced)!;
        check(rowTargets.All(t => t.FrameWidth == 0), "a pair already reading in separate rows keeps its full reading width");
        var trio = new[] { Window(1, 120, 80, 940, 780), Window(2, 680, 140, 800, 585), Window(3, 860, 250, 800, 590) };
        var stacked = Balanced(trio, comparisonContext);
        var frames = TaskLayoutSession.Project(trio, stacked).Select(w => w.VisualRect).ToArray();
        check(stacked.Groups.Any(g => g.Candidates.Any(c => c.Kind == "attention-side-stack" && c.Rejection == null)) &&
            frames[1].Left == frames[2].Left && frames[1].Bottom <= frames[2].Top &&
            frames[0].Intersect(frames[1]).IsEmpty && frames[0].Intersect(frames[2]).IsEmpty &&
            frames[0].Width >= 900 && Math.Min(frames[1].Width, frames[2].Width) >= 800,
            "two active references can share a readable side column alongside the main window");
        var navigationTask = comparisonContext with { Focus = new(1, 2, 0, "handoff", [3, 2]) };
        check(!Balanced(trio, navigationTask).Groups.SelectMany(g => g.Candidates).Any(c => c.Kind == "attention-side-stack"),
            "a three-hop navigation task never becomes a main window with two tiled references");

        var grown = Solve([document], context with { IsWorkspaceObservation = true })[0];
        check(grown.X == document.VisualRect.X && grown.Width > document.VisualRect.Width,
            "a narrow side window can gain readable width into the empty space in place");
        foreach (var request in new[] { context with { ExplicitArrange = true }, context with { FirstPlacementWindow = 1, IsWorkspaceObservation = true } })
        {
            var adequate = Window(1, 0, 80, 1200, 900);
            check(Solve([adequate], request)[0] == adequate.VisualRect,
                "geometric centering alone cannot pay for moving an already readable window");
            var goals = AttentionLayout.Targets([new(document, document.VisualRect.Area, document.VisualRect.Area, 1)], request, SmartTidyStrength.Assertive);
            AttentionLayout.SetRestingCenter([new(document, document.VisualRect.Area, document.VisualRect.Area, 1)], goals, [], request);
            check(goals is [{ DefaultCenter: true }], "a fresh or explicitly arranged window may compare a central landing");
        }
        var parked = Solve([document, peer], context);
        check(parked[0].Intersect(peer.VisualRect).Area < peer.VisualRect.Area * .35,
            "solo centering respects another visible surface on the destination side");
        var keyed = new[] { (document, (string?)new string('A', 24)), (peer, (string?)new string('B', 24)) };
        var scoped = new CompiledLayoutRules(Version: 3, Pairs: [new(keyed[0].Item2!, keyed[1].Item2!, "left", OnlyWhenPaired: true)]);
        check(scoped.IsValid() && scoped.Bind(keyed, focus: new(1, 0, 0)).Length == 0 &&
            scoped.Bind(keyed, focus: new(2, 1, 1, "handoff")).Length == 0 &&
            scoped.Bind(keyed, focus: new(2, 1, 3)) is [{ First: 1, Second: 2, FirstSide: "left" }],
            "a learned side order binds in either focus direction only for an observed working pair");
        var pairContext = context with { Focus = new(1, 0, 1),
            Attention = new Dictionary<nint, LayoutAttention>(attention) { [2] = new(.95, .4, .2, .95, false) } };
        var pair = Solve([document, peer], pairContext);
        var overlap = pair[0].Intersect(pair[1]).Width;
        var remaining = OcclusionMetrics.Measure(pair[1], [pair[0]], peer.Dpi);
        check(pair.All(r => r.Intersect(area) == r) && pair[0].Width > document.VisualRect.Width &&
            overlap < Math.Min(pair[0].Width, pair[1].Width) * OcclusionMetrics.HorizontalInsetFraction &&
            remaining.CenterVisible > .98 && remaining.UsefulWidth >= area.Width * .40,
            "a working pair gains reading space while retaining its reference content and title");
        var elsewhere = Window(3, -1500, 100, 1260, 770) with { WorkArea = area with { X = 0 }, MonitorHandle = 1 };
        var twoDisplays = Solve([document, peer, elsewhere], pairContext with
        {
            Attention = new Dictionary<nint, LayoutAttention>(pairContext.Attention!) { [3] = new(.95, .4, .2, .95, false) }
        });
        check(pair.SequenceEqual(twoDisplays.Take(2)) && twoDisplays[2] == elsewhere.VisualRect,
            "a recent task on another display cannot displace the local side-by-side reference");
        var note = Window(1, 540, 280, 720, 510);
        var editing = pairContext with { Focus = new(1, 2, 3), WindowHints = [new(1, Role: WindowUseRole.FloatingEditor)] };
        var writing = Solve([note, peer], editing);
        check(writing[0] == note.VisualRect && writing[1] == peer.VisualRect,
            "writing over a reference keeps both windows at the user's working positions");

        // The same overlay can still escape a fixed occluder. Keep it in the test selection
        // through a visible strip as a real automatic capture would.
        var blocker = Window(3, 500, 270, 660, 460) with { IsTopmost = true, IsManageable = false, ZOrder = -1 };
        var escaped = Solve([blocker, note, peer], editing);
        var before = OcclusionMetrics.Measure(note.VisualRect, [blocker.VisualRect], note.Dpi);
        var after = OcclusionMetrics.Measure(escaped[1], [blocker.VisualRect], note.Dpi);
        check(after.CenterVisible > before.CenterVisible + .30 && escaped[0] == blocker.VisualRect,
            "seriously obstructed content takes precedence over floating stability");

        var almost = Solve([note, peer], editing with { WindowHints = [new(1, FloatingAffinity: .701)] });
        var near = Solve([note, peer], editing with { WindowHints = [new(1, FloatingAffinity: .700)] });
        check(almost.Zip(near).All(pair => Math.Abs(pair.First.X - pair.Second.X) <= 8 &&
            Math.Abs(pair.First.Width - pair.Second.Width) <= 8),
            "a near-identical floating preference preserves the projected desktop geometry");

        // Perturb the observation itself and pass through the role inference as well as
        // the solver. An otherwise identical app with an unknown name follows the same rule.
        foreach (var role in new[] { WindowUseRole.Unknown, WindowUseRole.Editor })
        {
            var source = Window(2, 80, 60, 1450, 920);
            RectI[] Write(int width)
            {
                var overlay = Window(1, 420, 250, width, (int)Math.Round(area.Height * .64));
                var desktop = new[] { overlay, source };
                var focus = new LayoutFocus(1, 2, 1, "handoff");
                var signals = new Dictionary<nint, LayoutAttention> { [1] = new(.95, .8, .85, .7, true),
                    [2] = new(.8, .9, .2, .8, false) };
                var hints = WindowUseRoles.Bind(desktop, [], focus, signals, [], w => w.Handle == 1 ? role : WindowUseRole.Unknown);
                return Solve(desktop, pairContext with { Focus = focus, Attention = signals, WindowHints = hints });
            }
            int width = (int)Math.Round(area.Width * .56);
            var ordinary = Write(width);
            var enlarged = Write(width + 1);
            check(ordinary.Zip(enlarged).All(pair => Math.Abs(pair.First.X - pair.Second.X) +
                Math.Abs(pair.First.Y - pair.Second.Y) + Math.Abs(pair.First.Width - pair.Second.Width) +
                Math.Abs(pair.First.Height - pair.Second.Height) < 12),
                $"a one-pixel geometry change preserves the projected desktop for a transient {role} surface");
        }

        foreach (bool reflect in new[] { false, true })
        {
            WindowSnapshot Surface(int id, double x, double width, double height)
            {
                double left = reflect ? 1 - x - width : x;
                return Window(id, (int)(left * area.Width), 100, (int)(width * area.Width), (int)(height * area.Height));
            }
            var input = Surface(1, .27, .48, .71);
            var material = Surface(2, .43, .55, .81);
            RectI[] Visit(double dwell, int switches) => Solve([input, material], pairContext with
            {
                Displays = [new(2, area, area)], Focus = new(1, 2, switches),
                Attention = new Dictionary<nint, LayoutAttention> { [1] = new(1, dwell, .96, 0, true),
                    [2] = new(.98, dwell, .85, .85, false) }
            });
            var glance = Visit(.12, 2);
            var comparison = Visit(1, 6);
            double Travel(RectI r) => Math.Abs((input.VisualRect.X + input.VisualRect.Width / 2d) - (r.X + r.Width / 2d));
            var referenceBefore = OcclusionMetrics.Measure(material.VisualRect, [input.VisualRect], 96);
            var referenceAfter = OcclusionMetrics.Measure(comparison[1], [comparison[0]], 96);
            check(Travel(glance[0]) <= input.VisualRect.Width * .22 && glance[0].Intersect(area) == glance[0] &&
                Travel(glance[0]) < Travel(comparison[0]) && referenceAfter.UsefulWidth > referenceBefore.UsefulWidth,
                $"a quick visit preserves the local input position while sustained comparison can open up the reference ({reflect})");
        }

        var leaf = Window(1, 250, 100, 550, 580);
        var parent = Window(2, 250, 100, 550, 580);
        var ancestor = Window(3, 60, 60, 980, 820);
        var chain = Solve([leaf, parent, ancestor], pairContext with { Focus = new(1, 2, 1, "handoff", [3, 2]),
            Attention = new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, .9, .8, true),
                [2] = new(.9, 1, .8, .8, false), [3] = new(.9, 1, .8, .8, false) } });
        check(chain[2] == ancestor.VisualRect && OcclusionMetrics.Measure(chain[1], [chain[0]], 96).AccessWidth >= 96 &&
            chain[0].Width > leaf.VisualRect.Width,
            "a nested handoff leaves its earlier ancestor fixed and the direct source reachable");

        var narrow = new RectI(-1500, 0, 1500, 820);
        var crowded = new[] { Window(1, 0, 0, 1080, 730), Window(2, 150, 60, 920, 640), Window(3, 300, 40, 980, 690) }
            .Select(w => w with { VisualRect = w.VisualRect with { X = narrow.X + (int)w.Handle * 80 }, WorkArea = narrow }).ToArray();
        var scarce = Solve(crowded, pairContext with { Focus = new(1, 2, 3),
            Attention = new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, 1, 1, true),
                [2] = new(.9, 1, .8, 1, false), [3] = new(.9, 1, .8, 1, false) },
            WindowHints = crowded.Select(w => new TaskWindowHint(w.Handle, UsefulWidthDip: 960)).ToArray() });
        check(scarce.Select((rect, i) => rect.Width >= Math.Min(960, crowded[i].VisualRect.Width)).All(kept => kept) &&
            OcclusionMetrics.Measure(scarce[1], [scarce[0]], 96).AccessWidth >= 96,
            "when three reading surfaces cannot fit, preserve usable app frames and a reachable reference");

    }
}
