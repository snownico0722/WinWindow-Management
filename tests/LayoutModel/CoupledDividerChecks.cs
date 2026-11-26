using NeatWin.Core;

internal static class CoupledDividerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1040);
        foreach (bool mirror in new[] { false, true })
        foreach (int delta in new[] { -90, 110 })
        {
            RectI Flip(RectI r) => mirror ? r with { X = area.Right - r.Right } : r;
            var start = Flip(new(18, 80, 934, 850));
            var end = Flip(new(18, 80, 934 + delta, 850));
            var other = Flip(new(960, 80, 936, 850));
            WindowSnapshot Make(int id, RectI r) => new(id, r, r, area, 1, default, true, id == 1, true, id - 1, 96, (uint)id);
            var main = Make(1, end);
            var peer = Make(2, other);
            var gesture = new ManualWindowGesture(1, start, end, area, mirror ? ManualGestureKind.MoveAndResize : ManualGestureKind.Resize, [], 400, 0) { ProcessId = 1 };
            var ctx = new TaskLayoutContext(RecentGesture: gesture, GestureAnchor: 1, Focus: new(1, 2, 2),
                Attention: new Dictionary<nint, LayoutAttention>(), UseWorkingPairs: true) { Diagnostics = new(TraceCandidateAxes: true) };
            IntentLayoutPlan Plan(TaskLayoutContext c, params WindowSnapshot[] windows) => LayoutDispatch.Create(LayoutRoute.Smart,
                new VisibilityAnalyzer().SelectVisibleWorkingSet(windows, includeStackAccess: true), new(), null, windows, c);
            var plan = Plan(ctx, main, peer);
            var expected = Flip(new(960 + delta, 80, 936 - delta, 850));
            check(plan.Moves is [{ Window.Handle: 2 } local] && local.TargetVisualRect == expected && plan.Layers.Count == 0,
                $"shared divider release moves just its peer's inner edge, mirror={mirror}, delta={delta}");
            check(Plan(ctx with { SizeFloors = [new(2, 940, 0)] }, main, peer).Moves.Count == (delta > 0 ? 0 : 1) &&
                Plan(ctx, main, peer with { IsResizable = false }).Moves.Count == 0,
                "an independent native minimum or a fixed peer cannot be squeezed");
            if (delta > 0)
            {
                int floor = expected.Width + 1;
                var clamped = Plan(ctx with { SizeFloors = [new(2, floor, 0)] }, main, peer);
                check(clamped.Moves is [{ Window.Handle: 2 } partial] &&
                    partial.TargetVisualRect == Flip(new(960 + delta - 1, 80, floor, 850)) &&
                    clamped.Layers.Count == 0, "crossing the readable divider limit by one pixel retains all safe travel");
            }
            var ids = new Dictionary<nint, int> { [1] = 1, [2] = 2 };
            var replay = RecordedTaskSignals.From(ctx, ids).ApplyToReplay(new()) with { Diagnostics = new(TraceCandidateAxes: true) };
            check(Plan(replay, main with { ProcessId = 0 }, peer with { ProcessId = 0 }).Moves.Single().TargetVisualRect == expected,
                "the exported resize endpoints reproduce the same divider without native handles");
            var unrelated = Make(3, new(870, 950, 300, 80));
            check(Plan(ctx, main, peer, unrelated).Moves.All(m => m.Window.Handle == 2) &&
                Plan(ctx with { AllowGestureAssist = false }, main, peer).Groups.All(g => g.Selected != "coupled-divider"),
                "a distant third surface stays put and a focus-only event cannot use the divider");
            var translated = end with { X = end.X + 32 };
            var translatedMain = Make(1, translated);
            var bothEdges = ctx with { RecentGesture = gesture with { EndRect = translated, Kind = ManualGestureKind.MoveAndResize } };
            check(Plan(bothEdges, translatedMain, peer).Groups.All(g => g.Selected != "coupled-divider"),
                "combined move and resize that changes the opposite edge must not drive the divider");
            var fromMove = ctx with { RecentGesture = gesture with { Kind = ManualGestureKind.Move } };
            check(Plan(fromMove, main, peer).Groups.All(g => g.Selected != "coupled-divider") &&
                Plan(ctx with { RecentGesture = gesture with { EndRect = end with { Height = end.Height + 50 } } }, main, peer)
                    .Groups.All(g => g.Selected != "coupled-divider"), "move and mismatched release cannot masquerade as an inner-edge resize");
        }
    }
}
