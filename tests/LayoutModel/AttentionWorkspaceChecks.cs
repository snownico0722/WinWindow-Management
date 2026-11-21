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
        var uneven = new[] { Window(1, 20, 80, 1400, 800), Window(2, 1040, 80, 420, 800) };
        var shared = TaskLayoutSession.Project(uneven, Balanced(uneven, comparisonContext));
        check(shared[0].VisualRect.Width >= 900 && shared[1].VisualRect.Width >= 900 &&
            shared[0].VisualRect.Intersect(shared[1].VisualRect).IsEmpty, "a crowded balanced pair redistributes excess width without overlap");
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

        double Distance(RectI r) => Math.Abs((r.Left + r.Width / 2d) - (area.Left + area.Width / 2d));
        var centered = Solve([document], context)[0];
        check(Distance(centered) <= area.Width * .05 && centered.Width >= document.VisualRect.Width,
            "a lone reading window occupies the display's central viewing area");
        var parked = Solve([document, peer], context);
        check(parked[0].Intersect(peer.VisualRect).Area < peer.VisualRect.Area * .35,
            "solo centering respects another visible surface on the destination side");
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
