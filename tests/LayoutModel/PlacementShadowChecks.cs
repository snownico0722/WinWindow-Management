using NeatWin.Core;

internal static class PlacementShadowChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1040);
        WindowSnapshot Window(int id, RectI rect) =>
            new(id, rect, rect, area, 1, default, true, id == 1, true, id - 1, 96, (uint)id);
        var main = Window(1, new(20, 80, 1400, 800));
        var peer = Window(2, new(1040, 80, 420, 800));
        WindowSnapshot[] desktop = [main, peer];
        var focus = new LayoutFocus(1, 2, 4);
        var context = new TaskLayoutContext(Focus: focus, Attention: new Dictionary<nint, LayoutAttention>
            { [1] = new(1, 1, 1, 1, true), [2] = new(1, 1, 1, 1, false) }, UseWorkingPairs: true);
        var visible = new VisibilityAnalyzer().SelectForManualArrange(desktop, true).ToArray();
        var frame = new PlacementShadowReplay.Frame(LayoutRoute.Smart, visible, new(), IntentReferenceDocument.Empty,
            desktop, context, DateTimeOffset.UnixEpoch);
        var plan = frame.Solve();
        var submitted = plan.Moves.Single(m => m.Window.Handle == 1).TargetVisualRect;
        var corrected = submitted with { Width = submitted.Width + (main.VisualRect.Width - submitted.Width) * 2 / 3,
            X = submitted.X + 90 };
        var placed = TaskLayoutSession.Project(desktop, plan);
        var released = placed.Select(w => w.Handle == 1 ? w with { VisualRect = corrected } : w).ToArray();
        var learner = new PlacementShadowReplay();
        var gesture = new ManualWindowGesture(1, submitted, corrected, area, ManualGestureKind.Resize,
            [], 400, 0, 2100) { ProcessId = 1 };
        learner.Submitted(frame, plan, 1000);
        learner.Observe(gesture, released[0], released, 2100);
        learner.Observe(gesture, released[0], released, 2100);
        var examples = learner.Take();
        check(examples is [{ Axes: [{ Axis: PlacementAxis.Width }] }],
            "one distinct release supplies only its resize axis even when the edge moved");
        var findings = PlacementShadowReplay.Analyze(examples, CancellationToken.None);
        check(findings is [{ Diagnosis: "missing-feasible-candidate" or "lost-on-ranking" or "candidate-rejected" }],
            "the worker replays the exact baseline and distinguishes feasibility from ranking");
        learner.Publish(findings, 3000);
        check(learner.Bind(desktop, focus, 3100).Length == 0,
            "a single corrected replay never affects the live solver");

        var confirmed = findings[0] with { Won = true };
        learner.Publish([confirmed, confirmed], 3500);
        check(learner.Bind(desktop, focus, 3600).Length == 0,
            "reprocessing the same release cannot manufacture independent votes");
        learner.Submitted(frame, plan, 3600);
        learner.Observe(gesture with { ReleasedTick = 4200 }, released[0], released, 4200);
        var second = PlacementShadowReplay.Analyze(learner.Take(), CancellationToken.None).Single();
        learner.Publish([second with { Won = true }], 4300);
        if (!findings[0].Won)
        {
            check(learner.Bind(desktop, focus, 4300).Length == 0, "unsuccessful replays supply no positive vote");
            learner.Submitted(frame, plan, 4400);
            learner.Observe(gesture with { ReleasedTick = 5000 }, released[0], released, 5000);
            learner.Publish([PlacementShadowReplay.Analyze(learner.Take(), CancellationToken.None).Single() with { Won = true }], 5100);
        }
        var band = confirmed.Axis with { Axis = PlacementAxis.Width, Value = 900, Tolerance = 20 };
        check(band.Apply(corrected with { Width = 895 }).Width == 895 &&
            band.Apply(corrected with { Width = 1100 }) == (corrected with { Width = 920 }),
            "a shadow rule keeps acceptable geometry and only repairs its own axis to the nearest boundary");
        var bound = learner.Bind(desktop, focus, 5200);
        check(bound is [{ Axis: PlacementAxis.Width }] &&
            learner.Bind([main], focus, 5200).Length == 0 &&
            learner.Bind([main, peer with { ProcessId = 99 }], focus, 5200).Length == 0 &&
            learner.Bind(desktop, focus, 5100 + 3 * PlacementShadowReplay.IntervalMs).Length == 0,
            "corroborated rules bind only within the same live task and expire without new evidence");

        var far = Window(8, new(1780, 960, 100, 60));
        var near = far with { VisualRect = main.VisualRect };
        check(learner.Bind([.. desktop, far], focus, 5200).Length == 1 &&
            learner.Bind([.. desktop, near], focus, 5200).Length == 0,
            "only a new window intersecting the corrected region invalidates a shadow preference");

        var self = frame with { Context = context with { ShadowAxes = bound } };
        learner.Submitted(self, plan, 1000);
        learner.Observe(gesture, released[0], released, 2100);
        check(learner.Take().Length == 0, "the worker never trains on its own output");

        var wrongFocus = context with { Focus = new(2, 1, 4) };
        var report = learner.Report(new Dictionary<nint, int> { [1] = 19 });
        var signals = RecordedTaskSignals.From(context with { ShadowAxes = bound }, new Dictionary<nint, int> { [1] = 19, [2] = 22 });
        check(report is [{ WindowId: 19, Axis: "Width", AlternativeSelected: true }, ..] &&
            signals.ApplyToReplay(wrongFocus).ShadowAxes is [{ Window: 19, Axis: PlacementAxis.Width }],
            "recorded diagnostics and rules use session-local identity and can be replayed");
        learner.Retire(peer.Handle);
        check(learner.Bind(desktop, focus, 5500).Length == 0 &&
            learner.Report(new Dictionary<nint, int> { [1] = 19 }) == null,
            "retiring a neighbor prevents old rules or diagnostics from following a recycled handle");
    }
}
