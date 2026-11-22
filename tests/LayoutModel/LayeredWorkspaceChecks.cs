using NeatWin.Core;

internal static class LayeredWorkspaceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (width, height, dpi) in new[] { (1920, 1080, 96u), (2880, 1620, 144u), (2520, 1080, 96u) })
        foreach (bool mirror in new[] { false, true })
        {
            var area = new RectI(-width, 32, width, height);
            RectI Frame(double x, double y, double w, double h) => new(
                area.X + (int)Math.Round((mirror ? 1 - x - w : x) * width), area.Y + (int)Math.Round(y * height),
                (int)Math.Round(w * width), (int)Math.Round(h * height));
            WindowSnapshot W(int id, RectI r, bool resizable = true) =>
                new(id, r, r, area, 1, default, resizable, id == 1, true, id - 1, dpi, (uint)(3000 + id));
            var task = new TaskLayoutContext(UseWorkingPairs: true, Focus: new(1, 2, 3),
                Attention: new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, .9, .9, true), [2] = new(.9, .8, .9, .8, false) })
                { IsWorkspaceObservation = true, TraceCandidateAxes = true };
            IntentLayoutPlan Plan(WindowSnapshot[] ws, TaskLayoutContext ctx) => LayoutDispatch.Create(LayoutRoute.Smart,
                new VisibilityAnalyzer().SelectVisibleWorkingSet(ws, includeStackAccess: true), new(), null, ws, ctx);
            var front = W(1, Frame(0, .08, .64, .83), false);
            var rear = W(2, Frame(.03, .12, .60, .76), false);
            var windows = new[] { front, rear };
            var plan = Plan(windows, task);
            var projected = TaskLayoutSession.Project(windows, plan);
            long Visible(WindowSnapshot[] ws) => new VisibilityAnalyzer().SelectForManualArrange(ws, false)
                .Single(v => v.Window.Handle == 2).VisibleArea;
            string display = $"{width}x{height}/{dpi}, mirrored={mirror}";
            check(projected[0].VisualRect == front.VisualRect && projected[1].VisualRect.Width == rear.VisualRect.Width &&
                projected[1].VisualRect.Height == rear.VisualRect.Height && Visible(projected) > Visible(windows) + area.Area * .15 &&
                !projected[0].VisualRect.Intersect(projected[1].VisualRect).IsEmpty && plan.Layers.Count == 0,
                $"a buried recent companion exposes spare screen without shrinking either frame or shifting the reader ({display})");
            var switched = new[] { projected[1] with { ZOrder = 0, IsForeground = true },
                projected[0] with { ZOrder = 1, IsForeground = false } };
            var next = Plan(switched, task with { Focus = new(2, 1, 4) });
            check(next.Moves.Count == 0 && next.Layers.Count == 0,
                $"switching to the exposed companion opens its original working frame in place ({display})");
            var unrelated = rear with { Handle = 30, ProcessId = 30030 };
            check(!Plan([front, unrelated], task with { Focus = new(1, 0, 0) }).Moves.Any(m => m.Window.Handle == 30),
                $"an unrelated covered window stays in the background ({display})");
            var reference = new ReferenceExposure(rear, windows, "visible-background", .1);
            check(reference.ExposurePrice(.4) < reference.ExposurePrice(.35) &&
                Math.Abs(reference.ExposurePrice(reference.Before)) < 1e-9,
                $"two legal layouts can differ in the amount of visible external reference ({display})");
            var full = W(1, area, false);
            var fullPlan = Plan([full, rear], task with { WindowHints = [new(1, UsefulWidthDip: 3200)] });
            check(fullPlan.Groups.Single().Task?.Objectives?[0].WorkingSurface?.Width > area.Width,
                $"a single task may request more logical width than this monitor can expose ({display})");
            check(fullPlan.Moves.Count == 0 && fullPlan.Layers.Count == 0,
                $"one immovable reading surface consumes the budget without reshuffling the hidden peer ({display})");
        }

        var surface = new WorkingSurfaceObjective(1200, 800, 1, .2);
        var screen = new RectI(0, 0, 1920, 1080);
        var background = new RectI(560, 50, 1200, 800);
        var covered = new RectI(0, 50, 1000, 800);
        var shrunk = new RectI(1000, 50, 760, 800);
        var seen = IntentLayoutPlanner.ScreenExposure(background, [covered, covered], screen, 96);
        var exposed = IntentLayoutPlanner.ScreenExposure(shrunk, [], screen, 96);
        check(Math.Abs(surface.ExposureLoss(background, seen) - surface.ExposureLoss(shrunk, exposed)) < 1e-8 &&
            surface.FrameLoss(background) < surface.FrameLoss(shrunk),
            "equal visible pixels receive equal exposure credit while the wider return frame retains separate value");
    }
}
