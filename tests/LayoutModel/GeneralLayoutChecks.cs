using NeatWin.Core;

// Metamorphic checks use fractions of each work area. They compare visible work space and
// input history across transformations rather than expecting a particular candidate or slot.
internal static class GeneralLayoutChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (width, height, dpi) in new[] { (1366, 768, 96), (1920, 1080, 96), (1920, 1080, 144), (2560, 1440, 144), (2560, 1080, 120) })
        {
            var area = new RectI(-width, 40, width, height - 40 * dpi / 96);
            string screen = $"{width}x{height} at {dpi} DPI";
            foreach (int active in new[] { 0, 1 })
            {
                double? originalShare = null;
                foreach (bool mirror in new[] { false, true })
                {
                    var first = Shape(area, .10, .22, .48, .64, mirror);
                    var second = Shape(area, .35, .19, .47, .70, mirror);
                    var windows = new[] { Window(56, first), Window(17, second) };
                    windows = windows.Select((w, i) => w with { IsForeground = i == active, ZOrder = i == active ? 0 : 1 })
                        .OrderBy(w => w.ZOrder).ToArray();
                    var equal = PlanPair(windows, peerActive: true);
                    var reading = PlanPair(windows, peerActive: false);
                    var noPulse = PlanPair(windows, peerActive: true, peerInput: 0);
                    check(equal.Select(w => w.VisualRect).SequenceEqual(noPulse.Select(w => w.VisualRect)),
                        $"{screen}: a pointer pulse alone does not change a working pair's allocation");
                    var renamed = new[] { windows[0] with { Handle = 203, ProcessId = 7783 }, windows[1] with { Handle = 91, ProcessId = 7759 } };
                    var renamedPlan = PlanPair(renamed, peerActive: true, rename: true);
                    var foreground = equal[0].VisualRect;
                    var background = equal[1].VisualRect;
                    var exposure = OcclusionMetrics.Measure(background, [foreground], (uint)dpi);
                    double readableShare = foreground.Width / (double)(foreground.Width + exposure.UsefulWidth);
                    bool withinScreen = equal.Concat(reading).All(w => w.VisualRect.Intersect(area) == w.VisualRect);
                    check(withinScreen && readableShare is >= .35 and <= .65 && exposure.CenterVisible >= .65 &&
                        exposure.AccessWidth >= 64 * dpi / 96 && foreground.Width > width * .32,
                        $"{screen}: both equally attended windows retain usable content and access (foreground {active}, mirror {mirror})");
                    if (originalShare.HasValue) check(Math.Abs(readableShare - originalShare.Value) <= .07,
                        $"{screen}: reflecting the scene preserves the usable width budget (foreground {active})");
                    originalShare ??= readableShare;
                    check(equal.Select(w => w.VisualRect).SequenceEqual(renamedPlan.Select(w => w.VisualRect)),
                        $"{screen}: renaming both unknown programs and their window identities preserves the chosen geometry");
                    // 停留与共同使用确实改变时可以重分宽；孤立的点击／滚轮脉冲不能改变同一任务的预算。
                    if (area.Width * 96d / dpi <= 1450)
                        check(reading[0].VisualRect.Width >= foreground.Width + width * .01,
                            $"{screen}: a task with stronger dwell and association receives more width under contention (foreground {active}, mirror {mirror})");
                    if (area.Width * 96d / dpi <= 1450 && !mirror)
                    {
                        var switched = new[] { reading[1] with { IsForeground = true, ZOrder = 0 },
                            reading[0] with { IsForeground = false, ZOrder = 1 } };
                        var resumed = PlanPair(switched, peerActive: false);
                        var formerlyVisible = OcclusionMetrics.Measure(reading[1].VisualRect, [reading[0].VisualRect], (uint)dpi);
                        var stillUsable = OcclusionMetrics.Measure(resumed[1].VisualRect, [resumed[0].VisualRect], (uint)dpi);
                        check(resumed[0].VisualRect.Width > formerlyVisible.UsefulWidth + 8 * dpi / 96 &&
                            stillUsable.UsefulWidth >= width * .30,
                            $"{screen}: switching the task on a previously solved desktop redistributes useful width");
                    }
                }
            }

            var starter = Window(101, Shape(area, .13, .18, .46, .62));
            var automatic = new TaskLayoutContext(Focus: new(starter.Handle, 0, 0), Attention: new Dictionary<nint, LayoutAttention>
                { [starter.Handle] = new(1, 1, .9, 0, true) }, UseWorkingPairs: true);
            var learned = automatic with { Placement = [new(starter.Handle, 0, .70, .34, 0, -1, .94, Height: .56)] };
            var preferred = Solve([starter], learned)[0].VisualRect;
            var ordinary = Solve([starter], automatic)[0].VisualRect;
            check(Math.Abs(preferred.Width - area.Width * .34) < width * .04 &&
                Math.Abs(preferred.Height - area.Height * .56) < area.Height * .035 &&
                preferred.Width < ordinary.Width && preferred.Left + preferred.Width / 2d > area.Left + area.Width * .58,
                $"{screen}: a well established personal side and size override the default center and reading size");

            // The same endpoint near a side can result from a deliberate long drag or a fine
            // correction away from the edge. Only the first path supplies docking evidence.
            var release = starter with { VisualRect = Shape(area, .012, .18, .38, .60) };
            TaskLayoutContext Drag(int startingX) => automatic with { GestureAnchor = release.Handle,
                RecentGesture = new(release.Handle, release.VisualRect with { X = startingX }, release.VisualRect, area,
                    ManualGestureKind.Move, [], 500, 95) };
            var docked = Solve([release], Drag(area.Left + width / 3))[0].VisualRect;
            var corrected = Solve([release], Drag(release.VisualRect.Left - 3))[0].VisualRect;
            check(docked.Left == area.Left && corrected == release.VisualRect,
                $"{screen}: an approaching drag and a fine correction at the same endpoint remain distinct");

            foreach (bool leftEdge in new[] { false, true })
            {
                int inset = (int)Math.Round(12d * dpi / 96);
                var frame = release.VisualRect with { X = leftEdge ? area.Left + inset : area.Right - release.VisualRect.Width - inset };
                var resized = release with { VisualRect = frame };
                int change = 80 * dpi / 96;
                var start = frame with { Width = frame.Width - change, X = frame.X + (leftEdge ? change : 0) };
                var gesture = new ManualWindowGesture(resized.Handle, start, frame, area,
                    leftEdge ? ManualGestureKind.MoveAndResize : ManualGestureKind.Resize, [], 450, 65 * dpi / 96);
                var target = Solve([resized], automatic with { RecentGesture = gesture, GestureAnchor = resized.Handle })[0].VisualRect;
                check(target == frame with { X = frame.X + (leftEdge ? -inset : inset) },
                    $"{screen}: changing the {(leftEdge ? "left" : "right")} edge preserves the user's width and closes the remaining small gap");
            }

            WindowSnapshot Window(int id, RectI rect) =>
                new(id, rect, rect, area, 1, default, true, true, true, 0, (uint)dpi, (uint)(200 + id));
        }

        CheckSeveralWindows(check);

        var crowded = new RectI(0, 0, 1600, 860);
        var frames = new[] { Shape(crowded, 0, .1, .48, .80), Shape(crowded, .5, .1, .45, .65), Shape(crowded, .49, .05, .50, .85) };
        foreach (var idleRole in new[] { WindowUseRole.Unknown, WindowUseRole.Browser, WindowUseRole.FileManager })
        {
            var desktop = frames.Select((r, i) => new WindowSnapshot(i + 1, r, r, crowded, 1, default, true, i == 0, true, i)).ToArray();
            var tracker = new IdleWindowReclaim();
            var history = new ForegroundHistory();
            var task = new TaskLayoutContext(Focus: new(1, 3, 3), WindowHints: [new(2, Role: idleRole)]);
            IntentLayoutPlan unchanged = new([], [], []);
            WindowSnapshot? Choose(WindowSnapshot[] scene, long time) =>
                tracker.Choose(scene, unchanged, task, SmartTidyStrength.Balanced, time, w => (int)w.Handle, history);
            var parked = desktop.ToArray();
            parked[1] = parked[1] with { VisualRect = Shape(crowded, .06, .1, .36, .65) };
            check(Choose(desktop, 1000) == null && Choose(parked, 601000) == null &&
                Choose(desktop, 602000)?.Handle == desktop[1].Handle,
                $"idle reclaim follows elapsed time and useful space recovered for an ordinary {idleRole} window");
        }

        static WindowSnapshot[] PlanPair(WindowSnapshot[] windows, bool peerActive, bool rename = false, double peerInput = .95)
        {
            var main = windows[0].Handle;
            var peer = windows[1].Handle;
            var focus = new LayoutFocus(main, peer, 4);
            var attention = new Dictionary<nint, LayoutAttention> { [main] = new(1, 1, 1, 1, true),
                [peer] = peerActive ? new(.99, 1, peerInput, .9, false) : new(.65, .55, .08, .65, false) };
            var hints = WindowUseRoles.Bind(windows, [], focus, attention, [], w => WindowUseRoles.Identify(
                rename ? $"custom-view-{w.Handle}.exe" : $"sample-tool-{w.Handle}.exe", "AppWindow"));
            return Solve(windows, new(Focus: focus, Attention: attention, WindowHints: hints, UseWorkingPairs: true));
        }
    }

    private static void CheckSeveralWindows(Action<bool, string> check)
    {
        foreach (var (dpi, area) in new[] { (96u, new RectI(0, 28, 1366, 730)), (144u, new RectI(1920, -720, 2560, 1380)) })
        foreach (bool mirror in new[] { false, true })
        {
            string screen = $"{area.Width}px/{dpi} DPI, mirror {mirror}";
            WindowSnapshot Make(int id, RectI frame, bool canResize = true) =>
                new(id, frame, frame, area, 1, default, canResize, id == 1, true, id - 1, dpi, (uint)(100 + id));
            var windows = new[]
            {
                Make(1, Shape(area, .10, .13, .48, .64, mirror)),
                Make(2, Shape(area, .39, .08, .54, .62, mirror), canResize: false),
                Make(3, Shape(area, .34, .34, .55, .58, mirror)),
                Make(4, Shape(area, .01, .73, .17, .22, mirror)),
            };
            // Newly opened reading surface, a fixed-size source to return to, a frequently
            // used reference, and an unrelated utility parked behind the task.
            var focus = new LayoutFocus(1, 2, 1, "handoff");
            var attention = new Dictionary<nint, LayoutAttention>
            {
                [1] = new(1, .96, .93, .8, true), [2] = new(.76, .65, .2, .5, false),
                [3] = new(.9, .8, .86, .8, false),
            };
            var hints = WindowUseRoles.Bind(windows, [], focus, attention, [], w => w.Handle switch
            {
                1 => WindowUseRole.Browser, 2 => WindowUseRole.FileManager, 3 => WindowUseRole.Editor, _ => WindowUseRole.Unknown
            });
            var context = new TaskLayoutContext(Focus: focus, Attention: attention, WindowHints: hints, UseWorkingPairs: true);
            var plan = Plan(windows, context);
            var final = TaskLayoutSession.Project(windows, plan);
            var fixedReturn = final.Single(w => w.Handle == 2);
            var source = windows[1].VisualRect;
            var main = Exposed(final, 1);
            var back = Exposed(final, 2);
            var reference = Exposed(final, 3);
            double scale = dpi / 96d;
            check(main.CenterVisible >= .80 && main.UsefulWidth >= Math.Min(650 * scale, area.Width * .43),
                $"{screen}: under three-way contention the current reading task has a visible reading area");
            check(back.AccessWidth >= 64 * scale && fixedReturn.VisualRect.Width == source.Width && fixedReturn.VisualRect.Height == source.Height,
                $"{screen}: a fixed-size source keeps its dimensions and a visible way back");
            check(reference.CenterVisible >= .35 && reference.UsefulWidth >= Math.Min(280 * scale, area.Width * .23),
                $"{screen}: a third recently used reference keeps a usable reading area");
            check(final.Single(w => w.Handle == 4).VisualRect == windows[3].VisualRect &&
                plan.Layers.All(order => order.FrontToBack.All(w => w.Handle != 4)),
                $"{screen}: an unrelated unobstructing window stays where the user parked it");

            // A cold process has no verified-session shortcut. Recreating the planner from
            // each observed geometry should also settle under unchanged user evidence.
            var projected = final;
            var seen = new HashSet<string> { TaskLayoutSession.LayoutKey(final) };
            bool converged = false;
            for (int i = 0; i < 6; i++)
            {
                var recomputed = Plan(projected, context);
                if (recomputed.Moves.Count == 0 && recomputed.Layers.Count == 0) { converged = true; break; }
                projected = TaskLayoutSession.Project(projected, recomputed);
                if (!seen.Add(TaskLayoutSession.LayoutKey(projected))) break;
            }
            check(converged, $"{screen}: restarting the solver on its own geometry converges without cycling");

            var session = new TaskLayoutSession();
            var time = DateTimeOffset.UnixEpoch;
            session.Requested(windows, plan, time);
            session.Verify(final);
            var next = session.Capture(final, new(), [], null, time.AddSeconds(1));
            var unchanged = Plan(final, context with { VerifiedRepeat = next.VerifiedRepeat });
            check(next.VerifiedRepeat && unchanged.Moves.Count == 0 && unchanged.Layers.Count == 0,
                $"{screen}: verified geometry with the same task evidence settles without a second placement");
        }

        static OcclusionMetrics.Exposure Exposed(WindowSnapshot[] desktop, nint id)
        {
            var window = desktop.Single(w => w.Handle == id);
            return IntentLayoutPlanner.ScreenExposure(window.VisualRect,
                desktop.Where(w => w.ZOrder < window.ZOrder).Select(w => w.VisualRect), window.WorkArea, window.Dpi);
        }
    }

    private static RectI Shape(RectI area, double x, double y, double width, double height, bool mirror = false)
    {
        int w = (int)Math.Round(width * area.Width);
        int left = (int)Math.Round(x * area.Width);
        return new(area.Left + (mirror ? area.Width - left - w : left), area.Top + (int)Math.Round(y * area.Height),
            w, (int)Math.Round(height * area.Height));
    }

    private static WindowSnapshot[] Solve(WindowSnapshot[] windows, TaskLayoutContext context) =>
        TaskLayoutSession.Project(windows, Plan(windows, context));

    private static IntentLayoutPlan Plan(WindowSnapshot[] windows, TaskLayoutContext context) =>
        LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(windows, true),
            new(), null, windows, context);
}
