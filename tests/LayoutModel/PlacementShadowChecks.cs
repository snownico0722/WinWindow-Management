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
        check(findings is [{ Diagnosis: "missing-feasible-candidate" or "lost-on-ranking" }],
            "the worker replays the exact baseline and distinguishes feasibility from ranking");
        learner.Publish(findings, 3000);
        check(learner.Bind(desktop, focus, 3100).Length == 0,
            "a single corrected replay never affects the live solver");

        var confirmed = findings[0] with { Won = true };
        learner.Publish([confirmed], 3500);
        if (!findings[0].Won)
        {
            check(learner.Bind(desktop, focus, 3600).Length == 0,
                "one winning alternative cannot override an unsuccessful replay");
            learner.Publish([confirmed], 4000);
        }
        var bound = learner.Bind(desktop, focus, 4100);
        check(bound is [{ Axis: PlacementAxis.Width }] &&
            learner.Bind([main], focus, 4100).Length == 0 &&
            learner.Bind([main, peer with { ProcessId = 99 }], focus, 4100).Length == 0 &&
            learner.Bind(desktop, focus, 4000 + 3 * PlacementShadowReplay.IntervalMs).Length == 0,
            "corroborated rules bind only within the same live task and expire without new evidence");

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
        check(learner.Bind(desktop, focus, 4200).Length == 0 &&
            learner.Report(new Dictionary<nint, int> { [1] = 19 }) == null,
            "retiring a neighbor prevents old rules or diagnostics from following a recycled handle");
    }
}
