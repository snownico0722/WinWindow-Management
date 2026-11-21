using NeatWin.Core;

internal static class AttentionWorkspaceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(1920, 0, 1920, 1040);
        WindowSnapshot Window(int id, int x, int y, int width, int height) =>
            new(id, new(area.Left + x, y, width, height), new(area.Left + x, y, width, height),
                area, 2, default, true, id == 1, true, id - 1, 96, (uint)id);
        var browser = Window(1, 0, 80, 860, 900);
        var peer = Window(2, 880, 100, 960, 760);
        var attention = new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, .9, 0, true) };
        var context = new TaskLayoutContext(Focus: new(1, 0, 0), Attention: attention, UseWorkingPairs: true,
            WindowHints: [new(1, Role: WindowUseRole.Browser)]);
        static RectI[] Solve(WindowSnapshot[] windows, TaskLayoutContext context)
        {
            var visible = new VisibilityAnalyzer().SelectForManualArrange(windows, true);
            var plan = LayoutDispatch.Create(LayoutRoute.Smart, visible, new(SmartStrength: SmartTidyStrength.Assertive), null, windows, context);
            return TaskLayoutSession.Project(windows, plan).Select(w => w.VisualRect).ToArray();
        }
        double Distance(RectI r) => Math.Abs((r.Left + r.Width / 2d) - (area.Left + area.Width / 2d));
        var centered = Solve([browser], context)[0];
        check(Distance(centered) <= area.Width * .05 && centered.Width >= browser.VisualRect.Width,
            "a lone reading window occupies the display's central viewing area");
        var parked = Solve([browser, peer], context);
        check(parked[0].Intersect(peer.VisualRect).Area < peer.VisualRect.Area * .35,
            "solo centering respects another visible surface on the destination side");
        var pairContext = context with { Focus = new(1, 0, 1),
            Attention = new Dictionary<nint, LayoutAttention>(attention) { [2] = new(.95, .4, .2, .95, false) } };
        var pair = Solve([browser, peer], pairContext);
        var overlap = pair[0].Intersect(pair[1]).Width;
        var remaining = OcclusionMetrics.Measure(pair[1], [pair[0]], peer.Dpi);
        check(pair[0].Width > browser.VisualRect.Width && pair[0].Right > peer.VisualRect.Left &&
            pair[1].Right >= area.Right - 12 && overlap > 0 && overlap < pair[1].Width * .12 &&
            remaining.CenterVisible > .98 && remaining.UsefulWidth >= area.Width * .40,
            "two reading surfaces use the outer work-area edges and only share a narrow inner margin");
        var elsewhere = Window(3, -1500, 100, 1260, 770) with { WorkArea = area with { X = 0 }, MonitorHandle = 1 };
        var twoDisplays = Solve([browser, peer, elsewhere], pairContext with
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
    }
}
