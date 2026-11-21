using NeatWin.Core;

internal static class TextViewChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1040);
        WindowSnapshot Window(int id, RectI frame, int rank, bool foreground = false) =>
            new(id, frame, frame, area, 1, default, true, foreground, true, rank, 96, (uint)id);
        var reader = Window(1, new(580, 230, 650, 580), 0, true);
        var peer = Window(2, new(615, 250, 800, 680), 1);
        var scene = new[] { reader, peer };
        var context = new TaskLayoutContext(Focus: new(1, 2, 4), UseWorkingPairs: true, WindowHints: [new(1, TextFocus: true)],
            Attention: new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, .95, 0, true), [2] = new(.9, .8, .75, .8, false) });
        var options = new TidyOptions(SmartStrength: SmartTidyStrength.Assertive);
        var visibility = new VisibilityAnalyzer();
        IntentLayoutPlan Plan(WindowSnapshot[] desktop, TaskLayoutContext ctx) =>
            LayoutDispatch.Create(ctx.ExplicitArrange ? LayoutRoute.ManualAlgorithm : LayoutRoute.Smart,
                visibility.SelectForManualArrange(desktop, true), options, null, desktop, ctx);
        var planned = Plan(scene, context);
        var settled = TaskLayoutSession.Project(scene, planned);
        int BeforeWidth(WindowSnapshot[] desktop) => OcclusionMetrics.Measure(desktop[1].VisualRect, [desktop[0].VisualRect], 96).UsefulWidth;
        check(settled[0].VisualRect == reader.VisualRect && BeforeWidth(settled) > BeforeWidth(scene) + 96,
            "automatic arrangement keeps the reading frame exact while improving the reference around it");

        var manual = Plan(scene, context with { ExplicitArrange = true });
        check(manual.Moves.Any(m => m.Window.Handle == reader.Handle && m.TargetVisualRect != reader.VisualRect),
            "explicit arrangement can change a focused text frame");

        var resize = new TidyMove(reader, reader.VisualRect with { Width = reader.VisualRect.Width + 1 });
        var shift = new TidyMove(reader, reader.VisualRect with { X = reader.VisualRect.X + 1 });
        foreach (var (kind, tweak) in new[] { ("resize", resize), ("move", shift) })
        {
            var injected = planned with { Moves = [.. planned.Moves, tweak] };
            check(!IntentLayoutPlanner.TextViewAcceptsPlan(scene, injected, context) &&
                !IntentLayoutPlanner.TaskRefinementAcceptable(visibility.SelectForManualArrange(scene, true), planned,
                    injected.Moves, options, context, scene),
                $"post-processing and final application reject a one-pixel text frame change ({kind})");
        }

        var tool = Window(3, new(600, 265, 120, 80), 0);
        var interleaved = new[] { tool, reader with { ZOrder = 1 }, peer with { ZOrder = 2 } };
        IntentLayoutPlan MoveTool(RectI rect) => new([new(tool, rect)], [], []);
        check(!IntentLayoutPlanner.TextViewAcceptsPlan(interleaved, MoveTool(tool.VisualRect with { X = 640 }), context) &&
            IntentLayoutPlanner.TextViewAcceptsPlan(interleaved, MoveTool(tool.VisualRect with { X = 0 }), context),
            "a neighbor may uncover text but cannot cover a different line with the same visible area");
        var obstruction = Window(3, new(550, 205, 395, 460), 0);
        var blocked = new[] { obstruction, interleaved[1], interleaved[2] };
        var deoccluded = TaskLayoutSession.Project(blocked, Plan(blocked, context));
        check(deoccluded[1].VisualRect == reader.VisualRect &&
            deoccluded[0].VisualRect.Intersect(reader.VisualRect).Area < obstruction.VisualRect.Intersect(reader.VisualRect).Area,
            "a movable foreground obstruction yields while the active reading surface remains in place");

        var release = reader with { VisualRect = reader.VisualRect with { X = 10 } };
        var released = new[] { release, peer };
        var drag = context with { GestureAnchor = release.Handle,
            RecentGesture = new(release.Handle, reader.VisualRect, release.VisualRect, area, ManualGestureKind.Move, [], 570, 80) };
        var result = TaskLayoutSession.Project(released, Plan(released, drag));
        check(result[0].VisualRect.Left == area.Left && result[0].VisualRect != release.VisualRect,
            "a completed drag can assist the focused text frame once");
        var later = drag with { AllowGestureAssist = false, GestureTarget = result[0].VisualRect };
        var repeated = TaskLayoutSession.Project(result, Plan(result, later));
        var lateMove = new IntentLayoutPlan([new(release, result[0].VisualRect)], [], []);
        check(repeated[0].VisualRect == result[0].VisualRect &&
            !IntentLayoutPlanner.TextViewAcceptsPlan(released, lateMove, later) &&
            !Plan(released, later).Moves.Any(m => m.Window.Handle == release.Handle),
            "ordinary workspace events preserve the raw or assisted text view despite a recent gesture");
        var resized = reader with { VisualRect = reader.VisualRect with { X = area.Right - reader.VisualRect.Width - 9 } };
        var resizeGesture = new ManualWindowGesture(reader.Handle, resized.VisualRect with { Width = resized.VisualRect.Width - 85 },
            resized.VisualRect, area, ManualGestureKind.Resize, [], 430, 60);
        var snapped = TaskLayoutSession.Project([resized], Plan([resized], context with { Focus = new(1, 0, 0),
            GestureAnchor = reader.Handle, RecentGesture = resizeGesture }));
        check(snapped[0].VisualRect == resized.VisualRect with { X = resized.VisualRect.X + 9 },
            "after a text window resize, one assisted alignment honors the released wrap width");
        var switched = new[] { peer with { IsForeground = true, ZOrder = 0 }, reader with { IsForeground = false, ZOrder = 1 } };
        var media = context with { WindowHints = [new(1, PassiveVisual: true, MediaCompanion: true)] };
        check(!IntentLayoutPlanner.TextViewAcceptsPlan(scene, new([shift], [], []), media, reader.Handle) &&
            IntentLayoutPlanner.TextViewAcceptsPlan(scene, new([shift], [], []), media, peer.Handle),
            "a caret newly detected in the same foreground window protects the reading position at submission");
        check(IntentLayoutPlanner.TextViewAcceptsPlan(switched, new([shift], [], []), context) &&
            IntentLayoutPlanner.TextViewAcceptsPlan(scene, new([shift], [], []), media),
            "a stale background text hint and a foreground media surface allow ordinary geometric placement");
    }
}
