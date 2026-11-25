using NeatWin.Core;

internal static class SurfaceAndSolverV4Checks
{
    internal static void Run(Action<bool, string> check)
    {
        var work = new RectI(0, 0, 2560, 1380);
        WindowSnapshot Make(nint id, uint pid, RectI rect, bool fg = false, bool resizable = true, bool topmost = false,
            bool manageable = true, int z = 0) =>
            new(id, rect, rect, work, 1, default, resizable, fg, manageable, z, 120, pid, topmost, "windowed")
            {
                HasCaption = true,
                IndependentFrame = true,
            };

        // 1. CanonicalClassKey strips dynamic WPF GUIDs & ATL hex while preserving stable Chrome/Explorer keys
        check(SurfaceAutonomy.CanonicalRuleClass("HwndWrapper[DoubaoImeSettings.exe;;162d436c-f623-4b75-a559-1b5d083bd5fb]") ==
              "HwndWrapper[DoubaoImeSettings.exe;]" &&
              SurfaceAutonomy.CanonicalRuleClass("Chrome_WidgetWin_1") == "Chrome_WidgetWin_1" &&
              SurfaceAutonomy.CanonicalAutonomyClass("Qt51519QWindowIcon") == "Qt*QWindowIcon",
              "canonical class normalization strips random instance GUIDs without breaking installed Chrome/Explorer keys");

        // 2. Tier 0A & Tier 0B surface gates catch micro targets, Chromium empty hover strip, and CLaunch/Snap Bar
        check(SurfaceAutonomy.IsGhostSurface("cc-switch.exe", "Tao Thread Event Target", new(0, 0, 18, 18), new(0, 0, 18, 18),
                  work, 120, false, false, false, true, false, false, false, false) &&
              SurfaceAutonomy.IsGhostSurface("chrome.exe", "Chrome_WidgetWin_1", new(0, 1340, 389, 30), new(0, 1340, 389, 30),
                  work, 120, false, false, false, false, false, false, false, false) &&
              SurfaceAutonomy.IsTransientWorkspaceFreezeOverlay("CLaunch.exe", "CLaunchWndClass", new(2028, 476, 532, 904),
                  work, true, true) &&
              SurfaceAutonomy.IsTransientWorkspaceFreezeOverlay("explorer.exe", "XamlExplorerHostIslandWindow", new(0, 0, 2560, 450),
                  work, true, true),
              "Tier 0A/0B filters micro event targets, empty-title URL hover bars, CLaunch, and Win11 Snap Bar");

        // 3. Cross-process QQKit ('图片和视频') & full-height Weixin ('图片和视频') suppressed while detached QQ SubChat remains task
        var qqHost = Make(7, 100, new(1179, 41, 1381, 1123), fg: false, z: 1);
        var qqKitViewer = Make(1734, 200, new(905, 104, 750, 1173), fg: true, z: 0);
        var qqSubChat = Make(1527, 100, new(829, 156, 904, 1071), fg: true, z: 0);
        var events = new List<(nint Child, string Action, string Rule)>();
        var surfaces = new TaskSurfaces
        {
            Role = w => WindowUseRole.Chat,
            ParentProcessId = pid => pid == 200 ? 100u : 0u,
            ChatPurpose = w => w.Handle == 1734 ? WindowClassification.ChatCaption("图片和视频") : WindowClassification.ChatCaption("项目讨论组"),
            Changed = (m, act) => events.Add((m.Child.Handle, act, m.Rule)),
        };
        surfaces.Update([qqHost], null);
        surfaces.Update([qqKitViewer, qqHost], qqHost);
        check(surfaces.IsLocal(qqKitViewer) && events.Any(e => e.Child == 1734 && e.Action == "suppress"),
            "cross-process QQKit media viewer is linked via parent PID and suppressed despite exceeding 43% host area");

        surfaces.Update([qqSubChat, qqHost], qqHost);
        check(!surfaces.IsLocal(qqSubChat) && events.Any(e => e.Child == 1527 && e.Action == "task"),
            "detached resizable QQ subchat window remains an independent task eligible for side-by-side arrangement");

        // 只跳过有明确菜单证据的临时表面。相同几何的置顶窗会改变回拖上下文。
        var w1 = Make(10, 10, new(345, 128, 1169, 1252), fg: true, z: 0);
        var w2 = Make(7, 20, new(1179, 103, 1381, 1123), fg: false, z: 1);
        var portfolio = new SolverPortfolio("workspace",
            [[345, 57, 1169, 1252], [1179, 103, 1381, 1123], [345, 128, 1169, 1252]],
            [new([0, 1], [0, 1], ["keep"], true, false, .5), new([2, 1], [0, 1], ["attention-layout"], true, true, .4)],
            new() { ["keep"] = 0, ["attention-layout"] = 1 }, 1, 1, .1, null);
        var plan = new IntentLayoutPlan([new(w1, new(345, 128, 1169, 1252))], [],
            [new([10, 7], "attention-layout", 0, [], null, null, portfolio)]);
        var fbSession = new SolverFeedbackSession();
        var beforePlacement = new[] { w1 with { VisualRect = new(345, 57, 1169, 1252) }, w2 };
        var pinned = Make(999, 30, new(0, 0, 2560, 450), fg: false, resizable: false, topmost: true, manageable: false, z: 0);
        var gesture = new ManualWindowGesture(10, new(345, 128, 1169, 1252), new(345, 57, 1169, 1252), work,
            ManualGestureKind.Move, [], 600, 10, ReleasedTick: 2_500) { ProcessId = w1.ProcessId };
        SolverFeedback? WithSurface(WindowSnapshot surface)
        {
            fbSession.Submitted("req-115", beforePlacement, plan, 1_000);
            return fbSession.Observe(gesture, [surface, beforePlacement[0] with { ZOrder = 1 }, w2 with { ZOrder = 2 }],
                2_600, DateTimeOffset.UnixEpoch);
        }
        var feedback = WithSurface(pinned with { Owner = w1.Handle, IsTransientMenu = true });
        check(feedback != null && feedback.Votes["keep"] > feedback.Votes["attention-layout"],
            "an identified transient menu does not interrupt a real drag-back vote");
        var browserTool = pinned with
        {
            Owner = w1.Handle, NativeUtility = true, IsTopmost = false,
            IsPassiveBrowserTool = SurfaceAutonomy.IsPassiveBrowserTool("msedge", "Chrome_WidgetWin_1", ownedPopup: true,
                isToolWindow: true, isForeground: false, isTopmost: false, hasTitleText: false),
        };
        check(browserTool.IsPassiveBrowserTool && WithSurface(browserTool) is { } browserFeedback &&
            browserFeedback.Votes["keep"] > browserFeedback.Votes["attention-layout"],
            "an owned titleless non-topmost browser tool appearing at release preserves a drag-back vote");
        check(WithSurface(pinned) == null,
            "a newly visible fixed occluder makes a drag-back ambiguous");
        check(WithSurface(browserTool with { IsPassiveBrowserTool = false }) == null,
            "an owned persistent viewer is still considered when attributing a drag-back");

        // 6. Browser expanded bookmarks menu & Self-Occlusion Immunity (owned menus and sticky notes)
        check(!SurfaceAutonomy.IsGhostSurface("chrome.exe", "Chrome_WidgetWin_1", new(736, 214, 501, 1141), new(736, 214, 501, 1141),
                  work, 120, false, true, false, true, false, false, false, false, owner: 1100, noActivate: true) &&
              !SurfaceAutonomy.IsGhostSurface("firefox.exe", "MozillaWindowClass", new(736, 214, 501, 1141), new(736, 214, 501, 1141),
                  work, 120, false, true, false, true, false, false, false, true, owner: 1100, noActivate: true) &&
              SurfaceAutonomy.IsGhostSurface("netease_dd.exe", "Qt5152QWindowToolSaveBits", new(2460, 816, 778, 909), new(2460, 816, 778, 909),
                  work, 120, false, true, false, true, false, true, false, true, owner: 0, noActivate: false) &&
              !SurfaceAutonomy.IsGhostSurface("PaperTodo.exe", "HwndWrapper[PaperTodo;;334b16a3-a641-4e33-9ca6-a101d0a2a3a1]",
                  new(2435, 168, 125, 58), new(2435, 168, 125, 58), work, 120, false, true, false, false, false, true, false, false, owner: 0, noActivate: true),
              "owned browser menus retain their bounds, Qt decoration is discarded and a sticky note survives");
        var popupRect = new RectI(600, 200, 240, 180);
        bool ExcludesOwned(string windowClass) => SurfaceAutonomy.IsGhostSurface("chrome", windowClass, popupRect, popupRect,
            work, 120, false, true, false, true, false, false, false, false, owner: 1100, noActivate: true);
        check(new[] { "SysShadow", "tooltips_class32", "Qt5152QWindowToolTipSaveBits", "Qt5152QWindowToolSaveBits" }
            .All(ExcludesOwned), "known shadows and tooltips are still filtered when they have an owner");
        check(!ExcludesOwned("#32768") && !ExcludesOwned("Qt5152QWindowPopupSaveBits"),
            "owned Win32 and Qt menus keep their visible geometry");

        var chromeHost = Make(1100, 14608, new(478, 86, 1162, 1206), fg: true, z: 1);
        var ownedChildDialog = Make(3700, 14608, new(736, 214, 501, 1141), fg: false, resizable: false, topmost: true, manageable: false, z: 0)
            with { Owner = 1100, NativeUtility = true, HasCaption = true, IndependentFrame = false };
        WindowSnapshot[] selfOccludedDesktop = [ownedChildDialog, chromeHost];
        var selfVis = new VisibilityAnalyzer().SelectVisibleWorkingSet(selfOccludedDesktop, includeStackAccess: true);
        var selfPlan = IntentLayoutPlanner.CreateDetailedPlan(selfVis, new(), null, selfOccludedDesktop,
            new TaskLayoutContext(Focus: new(1100, 0, 0),
                Attention: new Dictionary<nint, LayoutAttention> { [1100] = new(1, 1, .9, 0, true) }) { IsWorkspaceObservation = true });
        check(selfVis.Count == 1 && Math.Abs(selfVis[0].VisibleRatio - 1.0) < 1e-6 && selfPlan.Moves.Count == 0,
            "Self-Occlusion Immunity prevents a host browser from jumping sideways to dodge its own owned child popup or dialog");

        // 菜单跨过宿主边缘时，右侧窗口仍然损失了这部分可见面积。
        var neighbor = Make(1101, 77, new(1000, 0, 1200, 1200), z: 2);
        var menu = ownedChildDialog with { VisualRect = new(850, 0, 400, 800) };
        var host = chromeHost with { VisualRect = new(0, 0, 900, 1100) };
        var observed = new VisibilityAnalyzer().SelectForManualArrange([menu, host, neighbor], true);
        check(observed[0].VisibleRatio == 1 &&
            observed[1].VisibleArea == neighbor.VisualRect.Area - menu.VisualRect.Intersect(neighbor.VisualRect).Area,
            "an owned menu protects its host and remains a real occluder for the adjacent task");

        var firstMenu = menu with { VisualRect = new(720, 0, 220, 800), IsTransientMenu = true };
        var secondMenu = menu with { Handle = 3701, Owner = firstMenu.Handle, IsTransientMenu = true };
        host = host with { ZOrder = 2 };
        neighbor = neighbor with { ZOrder = 3 };
        firstMenu = firstMenu with { ZOrder = 1 };
        WindowSnapshot[] openMenus = [secondMenu, firstMenu, host, neighbor];
        var layered = TaskSurfaces.ResolveLocalRoots(openMenus);
        var nestedVisibility = new VisibilityAnalyzer().SelectForManualArrange(layered, true);
        check(layered[0].Owner == firstMenu.Handle && layered[0].IsLocalTo(host) && nestedVisibility[0].VisibleRatio == 1 &&
            nestedVisibility[1].VisibleArea == neighbor.VisualRect.Area - secondMenu.VisualRect.Intersect(neighbor.VisualRect).Area,
            "nested bookmark menus keep their direct owner, exempt the root host and still cover the adjacent task");
        var independentOwner = firstMenu with { IndependentFrame = true };
        check(!TaskSurfaces.ResolveLocalRoots([secondMenu, independentOwner, host])[0].IsLocalTo(host),
            "submenu ownership does not cross an independently usable task frame");
        check(SurfaceAutonomy.IsTransientMenu("Chrome_WidgetWin_1", ownedMenu: true, noActivate: true, isToolWindow: true) &&
            !SurfaceAutonomy.IsTransientMenu("Chrome_WidgetWin_1", ownedMenu: false, noActivate: true, isToolWindow: true) &&
            !SurfaceAutonomy.IsTransientMenu("Qt51519QWindowIcon", ownedMenu: true, noActivate: false, isToolWindow: true) &&
            !SurfaceAutonomy.IsTransientMenu("HwndWrapper[PaperTodo;]", ownedMenu: true, noActivate: true, isToolWindow: true),
            "a shared Chromium or Electron menu requires owner evidence, and an interactive viewer or note stays distinct");

        var scheduler = new AutomaticLayoutScheduler();
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [host, neighbor], 0);
        check(scheduler.Observe(openMenus, 50, false) == null && !scheduler.HasPending,
            "opening unactivated bookmark menus keeps the observed workspace stable");
        var resizedNeighbor = neighbor with { VisualRect = neighbor.VisualRect with { Width = 960 } };
        WindowSnapshot[] changedAlongsideMenu = [secondMenu, firstMenu, host, resizedNeighbor];
        check(scheduler.Observe(changedAlongsideMenu, 100, false) == null &&
            scheduler.Observe(changedAlongsideMenu, 800, false) == null && scheduler.HasPending &&
            scheduler.Observe([host, resizedNeighbor], 850, false) is { Cause: WorkspaceChangeCause.Geometry },
            "a pending real change waits for the menu to close before dispatch");
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [host, neighbor], 1000);
        var closedWithoutChange = scheduler.Observe(openMenus, 1050, false) == null &&
            scheduler.Observe([host, neighbor], 1800, false) == null && !scheduler.HasPending;
        check(closedWithoutChange, "dismissal of an unactivated menu does not dispatch on its own");
        var secondDisplay = neighbor with { MonitorHandle = 2, IsForeground = true };
        var backgroundHost = host with { IsForeground = false };
        var otherDisplayResized = secondDisplay with { VisualRect = resizedNeighbor.VisualRect };
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [backgroundHost, secondDisplay], 2000);
        WindowSnapshot[] otherDisplayWorking = [secondMenu, firstMenu, backgroundHost, otherDisplayResized];
        check(scheduler.Observe(otherDisplayWorking, 2050, false) == null &&
            scheduler.Observe(otherDisplayWorking, 2550, false) is { Cause: WorkspaceChangeCause.Geometry },
            "a menu on another display does not block real changes on the working display");

        // 独立穷举作对照。原先固定不相交的 A/C 顺序，会丢掉 C、B、A 这个独有的可见结果。
        var frames = new RectI[] { new(0, 0, 300, 400), new(400, 0, 300, 400),
            new(610, 0, 300, 400), new(820, 0, 300, 400) };
        var group = frames.Select((r, i) => new VisibleWindow(Make(i + 1, 10, r with { X = 400 * i }, i == 0, z: i),
            r.Area, r.Area, 1)).ToArray();
        var orders = new List<IntentLayoutPlanner.Candidate>();
        IntentLayoutPlanner.AddOrders(orders, "stack", frames, group);
        (bool, bool) Signature(int[] order) => (Array.IndexOf(order, 1) < Array.IndexOf(order, 2),
            Array.IndexOf(order, 2) < Array.IndexOf(order, 3));
        var expected = (from a in new[] { 1, 2, 3 } from b in new[] { 1, 2, 3 } where a != b
            select Signature([0, a, b, 6 - a - b])).ToHashSet();
        check(orders.Count == 4 && orders.All(c => c.Order[0] == 0) &&
            expected.SetEquals(orders.Select(c => Signature(c.Order))), "all four visible outcomes survive overlap-chain deduplication");
        orders.Clear();
        IntentLayoutPlanner.AddOrders(orders, "columns", group.Select(g => g.Window.VisualRect).ToArray(), group);
        check(orders.Count == 1, "disjoint target frames only need their natural order");

        var first = group[0].Window;
        var back = group[1].Window;
        var overlapped = back.VisualRect with { X = 150 };
        var groupTrace = new LayoutGroupTrace([first.Handle, back.Handle], "pair", 0, []);
        var overlapPlan = new IntentLayoutPlan([new(back, overlapped)], [], [groupTrace]);
        var returned = back with { VisualRect = overlapped, IsForeground = true, ZOrder = 0 };
        WindowSnapshot[] raised = [returned, first with { IsForeground = false, ZOrder = 1 }];
        var watch = LayerReturnObservation.FromSubmission("sample", 1000, [first, back], overlapPlan)!;
        check(watch.Observe(raised, returned, 2800) is { ElapsedMs: 1800, CoveredBefore: 0, CoveredAfter: .5 } &&
            watch.Observe(raised, returned, 12000) == null, "layer-return observation measures added overlap and expires");
        var alreadyOverlapped = back with { VisualRect = overlapped };
        var oldStack = LayerReturnObservation.FromSubmission("sample", 1000, [first, alreadyOverlapped],
            new([new(first, first.VisualRect with { Height = first.VisualRect.Height + 20 })], [], [groupTrace]))!;
        raised[1] = raised[1] with { VisualRect = first.VisualRect with { Height = first.VisualRect.Height + 20 } };
        check(oldStack.Observe(raised, returned, 2800) is { CoveredBefore: .5, CoveredAfter: .5 },
            "normal activation in an existing stack is recorded with zero added overlap");
    }
}
