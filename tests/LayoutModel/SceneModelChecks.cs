using NeatWin.Core;

internal static class SceneModelChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string chat = "AAAAAAAAAAAAAAAAAAAAAAAA", game = "BBBBBBBBBBBBBBBBBBBBBBBB";
        var area = new RectI(0, 0, 1920, 1040);
        WindowSnapshot W(int id, RectI r, int rank, uint pid, bool active = false) =>
            new(id, r, r, area, 1, default, true, active, true, rank, 96, pid, SurfaceState: "windowed");
        var host = W(1, new(100, 50, 1050, 880), 1, 80, true);
        var card = W(2, new(370, 180, 310, 460), 0, 80, true) with { IsResizable = false };
        var other = W(3, new(930, 40, 960, 920), 2, 90);
        var local = new TaskSurfaces { Role = _ => WindowUseRole.Chat, JustClicked = _ => true };
        var scheduler = new AutomaticLayoutScheduler { Surfaces = local };
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [host, other], 0);
        WindowSnapshot[] inspecting = [card, host with { IsForeground = false }, other];
        var opened = scheduler.Observe(inspecting, 100, false);
        var during = scheduler.Observe(inspecting, 2000, false);
        var closed = scheduler.Observe([host, other], 2100, false);
        check(opened == null && during == null && closed == null && !scheduler.HasPending &&
            scheduler.Observe([host, other], 4000, false) == null, "opening, browsing and dismissing a same-process chat profile never schedules a desktop reflow");
        var article = card with { Handle = 4, VisualRect = new(120, 90, 840, 760), IsResizable = true, IndependentFrame = true };
        WindowSnapshot[] reading = [article, host with { IsForeground = false }, other];
        scheduler.Observe(reading, 4100, false);
        check(scheduler.Observe(reading, 5000, false) != null && !local.IsLocal(article),
            "an independent article from the same chat still starts a workspace decision");
        var tool = card with { ProcessId = 92, Owner = host.Handle, NativeUtility = true };
        local.Update([tool, host, other], host);
        check(local.IsLocal(tool), "owner evidence can attach a cross-process utility surface to its host");
        var enlarged = tool with { VisualRect = host.VisualRect, IsResizable = true };
        local.Update([enlarged, host, other], host);
        check(!local.IsLocal(enlarged), "a card promoted to a workspace-sized window regains task status");
        var overrides = new TaskSurfaces { Role = _ => WindowUseRole.Chat, JustClicked = _ => true,
            AppKey = w => w.Handle == host.Handle ? chat : game, Rules = () => [new("inspect-as-task", chat, .5, "task", game)] };
        overrides.Update([tool, host, other], host);
        check(!overrides.IsLocal(tool), "a scoped task override can reverse the default owned-card classification");

        var chatTypes = new TaskSurfaces { Role = _ => WindowUseRole.Chat, JustClicked = _ => true,
            ChatPurpose = _ => TaskSurfaces.ChatCaption("微信音视频通话 "), AppKey = _ => chat,
            Rules = () => [new("same-qt-class", chat, .45, "local", chat)] };
        chatTypes.Update([tool with { ProcessId = host.ProcessId }, host, other], host);
        check(!chatTypes.IsLocal(tool with { ProcessId = host.ProcessId }),
            "a voice call sharing the chat card's process, window class and size retains its task status");
        var pendingCard = card with { IsForeground = false };
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [host,other], 5100);
        scheduler.Observe([pendingCard,host,other], 5200, false);
        scheduler.Observe(inspecting, 5400, false);
        scheduler.Observe([host,other], 6000, false);
        check(!scheduler.HasPending, "show-before-focus popup event order cannot leave a delayed workspace request");
        var selection = W(31, area, 0, 210, true) with { IsManageable = false, NativeUtility = true, IsTopmost = true };
        var translation = selection with { Handle = 32, VisualRect = new(530, 210, 450, 300), IsResizable = false };
        var popup = new TaskSurfaces { CaptureOverlay = w => w.ProcessId == 210 &&
            TaskSurfaces.IsCaptureTool(w, w.Handle == 32 ? TaskSurfaces.ChatCaption("截图翻译") : SurfacePurpose.Unknown) };
        var capture = new AutomaticLayoutScheduler { Surfaces = popup };
        capture.Reset(AutomaticLayoutMode.AutoFullAssist, [host, other], 0);
        var background = host with { IsForeground = false };
        foreach (var (sequence, tick) in new (WindowSnapshot[], int)[]
        {
            ([selection with { IsForeground = false }, host, other], 50), ([selection, background, other], 70),
            ([translation, background, other], 110), ([translation with { VisualRect = translation.VisualRect with { X = 750 } }, background, other], 160)
        })
            check(capture.Observe(sequence, tick, false) == null && !capture.HasPending, "capture and translation events do not enqueue work or add a quiet interval");
        check(capture.Observe([host, other], 190, false) == null && !capture.HasPending &&
            capture.Observe([article, background, other], 200, false, openingReady: true) != null,
            "closing translation preserves attention and a real opening can dispatch immediately");
        capture.Reset(AutomaticLayoutMode.AutoFullAssist, [host, other], 1000);
        var changedOther = other with { VisualRect = other.VisualRect with { Width = 720 } };
        capture.Observe([selection, background, changedOther], 1050, false);
        check(capture.Observe([selection, background, changedOther], 1500, false) is { Cause: var cause } &&
            cause.HasFlag(WorkspaceChangeCause.Geometry) && capture.WorkingForeground == host.Handle,
            "an independent geometry event can dispatch on time while a screenshot surface owns native focus");
        WindowSnapshot[] capturing = [selection, background, changedOther];
        var working = popup.PrepareForLayout(capturing, capture.WorkingForeground);
        var screenshotWorkingSet = new VisibilityAnalyzer().SelectVisibleWorkingSet(working);
        check(capturing[0].IsForeground && working.Count == 2 && working[0].IsForeground &&
            screenshotWorkingSet.Any(w => w.Window.Handle == host.Handle && w.VisibleRatio == 1),
            "the physical screenshot stays observable while planning uses the visible working desktop beneath it");
        capture.ApplicationRequested(capturing, new([], [], []), 1500);
        check(popup.IsCaptureOverlay(selection) && capture.WorkingForeground == host.Handle &&
            !popup.PrepareForLayout(capturing, capture.WorkingForeground).Contains(selection),
            "submitting against the observed desktop keeps screenshot recognition through the native preflight");
        check(!TaskSurfaces.IsCaptureTool(selection with { IndependentFrame = true }, TaskSurfaces.ChatCaption("截图")) &&
            !TaskSurfaces.IsCaptureTool(translation, SurfacePurpose.IndependentTask),
            "a pinned independent window or call stays a real reference");
        var rule = new LayoutScene("watch", "workspace", [chat], game, MaxVisibleLoss: .03, Weights: new(Continuity: 3));
        var rules = new CompiledLayoutRules(Version: 4, Scenes: [rule], SurfaceRules: [new("peek", chat, .4)]);
        check(rules.IsValid() && !(rules with { Version = 3 }).IsValid() &&
            !(rules with { Scenes = [rule, rule] }).IsValid() && !(rules with { Scenes = [rule with { Weights = new(Intervention: 0) }] }).IsValid(),
            "v4 scene validation rejects unsupported version, conflicting identity and invalid weight");
        var ctx = new TaskLayoutContext(Rules: rules) { IsWorkspaceObservation = true, AppKeys = new Dictionary<nint,string?> { [1] = chat, [3] = game } };
        var scene = ActiveLayoutScene.Resolve([host, other], ctx);
        check(scene?.Settings == rule && ActiveLayoutScene.Resolve([host, other], ctx with { ExplicitArrange = true }) == null,
            "rules bind on current foreground, background reference and trigger; explicit arrangement has its own policy");
        var clone = other with { Handle = 5 };
        check(ActiveLayoutScene.Resolve([host, other, clone],ctx with { AppKeys = new Dictionary<nint,string?> { [1] = chat, [3] = game, [5] = game } }) == null,
            "two candidate reference windows with the same anonymous key do not receive an arbitrary instance policy");
        var cross = other with { MonitorHandle = 2 };
        check(ActiveLayoutScene.Resolve([host,cross],ctx) == null, "the same reference app on another display cannot change this monitor");
        var cover = host with { Handle = 7, IsForeground = false };
        var reference = new ReferenceExposure(other, [host,cover,other], "test", .02);
        var swap = host with { VisualRect = new(1300,50,620,880) };
        var result = reference.Measure([swap,cover,other], w => w.VisualRect, w => w.ZOrder);
        var same = reference.Measure([cover,host,other], w => w.VisualRect, w => w.ZOrder);
        check(same.Lost < 1e-9 && result.Lost > .3 && result.After < reference.Before,
            "unchanged duplicate blockers cost zero, newly covered reference pixels count exactly once");
        var tiny = other with { VisualRect = new(1300,100,100,100) };
        var reduced = reference.Measure([host,tiny], w => w.VisualRect, w => w.ZOrder);
        check(reduced.After < .02 && reduced.Lost > .4, "shrinking a reference cannot improve its exposure merely by changing the denominator");
        var origin = host.VisualRect;
        var end = origin with { X = 600 };
        var date = DateTimeOffset.UnixEpoch;
        WorkspaceFrame Frame(RectI r, bool popup) => new(date, [new(1, r, area, 96, 0, true, false, true, 1, AppKey: chat, LocalSurface: popup)]);
        var adjustment = new WindowAdjustmentObservation(date, "chat", 1, origin, end, area, area, 96, 400, [], "stable",
            StartContext: Frame(origin, false), EndContext: Frame(end, false), SettledContext: Frame(end, false));
        check(LayoutHabits.Observe(adjustment) != null &&
            LayoutHabits.Observe(adjustment with { StartContext = Frame(origin, true) }) == null &&
            LayoutHabits.Observe(adjustment with { Outcome = "manual-release", EndContext = Frame(end, true) }, byInstance: true) == null,
            "card gestures remain observable without teaching the chat application or instance a placement preference");
        CheckSituatedHabits(check, host, other, adjustment, chat, game);
        var score = new TaskCostBreakdown(1,1,1,1,1,1,1,1);
        check(rule.Weights!.Apply(score).Total == score.Total + 2, "contextual weights act on the named task cost alone");
    }

    private static void CheckSituatedHabits(Action<bool, string> check, WindowSnapshot window, WindowSnapshot peer,
        WindowAdjustmentObservation adjustment, string app, string partner)
    {
        var companion = new ObservedWindow(3, peer.VisualRect, peer.WorkArea, 96, 1, false, false, true, .65,
            AppKey: partner);
        var start = adjustment.StartContext! with { Windows = [.. adjustment.StartContext.Windows, companion] };
        var end = adjustment.EndContext! with { Windows = [.. adjustment.EndContext.Windows, companion] };
        var joint = new PlacementUseObservation(1, 3, false, []);
        var playback = new PlacementUseObservation(1, 0, false, [3]);
        var observed = adjustment with { StartContext = start, EndContext = end, SettledContext = end, StartUsage = joint };
        var vote = LayoutHabits.Observe(observed)!;
        check(vote is { Usage: PlacementUsage.Joint } && vote.Partner == partner &&
            LayoutHabits.Observe(observed with { StartUsage = null }) is { Usage: null } &&
            LayoutHabits.Observe(observed with { StartUsage = playback }) is { Usage: PlacementUsage.BackgroundPlayback } &&
            playback.For(3, 1) == PlacementUsage.BackgroundPlayback && playback.For(3) == null &&
            (playback with { Playing = [3, 4] }).For(1, 3) == null &&
            (new PlacementUseObservation(1, 0, false, [1])).For(1, 3) == PlacementUsage.ForegroundPlayback &&
            (new PlacementUseObservation(1, 0, true, [])).For(1, 3) == PlacementUsage.TextFocus,
            "only usage observed before an adjustment labels its context, shared symmetrically by the two windows");
        LayoutHabit[] history = [];
        var date = observed.Time;
        for (int day = 0; day < 8; day++)
        {
            var time = date.AddDays(day);
            history = LayoutHabits.Add(history, vote with { Time = time, Center = .30, RevealedCell = 1, InnerOverlap = .05 });
            history = LayoutHabits.Add(history, vote with { Time = time, Center = .72, Usage = PlacementUsage.BackgroundPlayback,
                RevealedCell = 13, InnerOverlap = .25 });
            history = LayoutHabits.Add(history, vote with { Time = time, Width = .52, Edited = HorizontalEdit.Width, Usage = null });
        }
        var historyJson = System.Text.Json.JsonSerializer.Serialize(history);
        history = System.Text.Json.JsonSerializer.Deserialize<LayoutHabit[]>(historyJson)!;
        var entries = new[] { (window, (string?)app, "1"), (peer, (string?)partner, "3") };
        var focus = new LayoutFocus(window.Handle, peer.Handle, 3);
        PlacementPreference? Select(PlacementUseObservation? use) => PlacementPreferences.Bind(entries, focus, history, [],
            date.AddDays(8), (w, p) => use?.For((int)w, (int)p)).FirstOrDefault(p => p.Window == window.Handle);
        var jointPlacement = Select(joint);
        var backgroundPlacement = Select(playback);
        var unknown = Select(null);
        check(history.Length == 24 &&
            jointPlacement is { Center: < .35, Width: > .50 and < .54, RevealedCell: 1, InnerOverlap: < .1, Usage: PlacementUsage.Joint } &&
            backgroundPlacement is { Center: > .67, Width: > .50 and < .54, RevealedCell: 13, InnerOverlap: > .20, Usage: PlacementUsage.BackgroundPlayback } &&
            unknown is { PreferCenter: false, PreferWidth: true, Usage: null } &&
            Select(new PlacementUseObservation(1, 0, true, [])) == unknown,
            "the live binder separates opposite outcomes for the same pair and backs off each unsupported axis independently");
        var regionVotes = Enumerable.Range(0, 8).Select(i => vote with
        {
            Time = date.AddDays(i / 2).AddHours(i % 2),
            Center = i % 2 == 0 ? .25 : .75,
            Order = i % 2 == 0 ? -1 : 1,
            RevealedCell = 5,
        }).ToArray();
        var region = PlacementPreferences.Bind(entries, focus, regionVotes, [], date.AddDays(5),
            (w, p) => joint.For((int)w, (int)p)).Single(p => p.Window == window.Handle);
        check(region is { Partner: 3, PreferCenter: false, PreferWidth: false, RevealedCell: 5, Order: 0 } &&
            region.PositionConfidence == 0 && region.RegionConfidence >= .40 &&
            PlacementPreferences.Bind(entries, focus, regionVotes, [], date.AddDays(5)).Length == 0,
            "repeatably revealing the same region transfers no side preference, and missing cues apply neither contextual vote");
        var covering = window with { VisualRect = peer.VisualRect with { X = peer.VisualRect.X - 160 }, ZOrder = 0 };
        WindowSnapshot[] desktop = [covering, peer with { ZOrder = 1 }];
        var visible = new VisibilityAnalyzer().SelectForManualArrange(desktop, false);
        var plan = LayoutDispatch.Create(LayoutRoute.Smart, visible, new(SmartStrength: SmartTidyStrength.Assertive), null,
            desktop, new TaskLayoutContext(ExplicitArrange: true, Focus: focus, Placement: [region]) { UseWorkingPairs = true });
        check(plan.Groups.SelectMany(g => g.Candidates).Any(c => c.Breakdown?.PersonalPlacement > 0),
            "the live solver charges for covering an independently learned region");
        var signals = RecordedTaskSignals.From(new(Placement: [jointPlacement!, region]), new Dictionary<nint, int> { [1] = 1, [3] = 3 });
        var replay = System.Text.Json.JsonSerializer.Deserialize<RecordedTaskSignals>(System.Text.Json.JsonSerializer.Serialize(signals))!;
        check(replay.ApplyToReplay(new()).Placement is { Length: 2 } restored &&
            restored[0].Usage == PlacementUsage.Joint && restored[1].RegionConfidence == region.RegionConfidence,
            "recorded replay retains the selected usage and the separate region confidence with anonymous window IDs");
    }
}
