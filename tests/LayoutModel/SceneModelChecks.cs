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
        var score = new TaskCostBreakdown(1,1,1,1,1,1,1,1);
        check(rule.Weights!.Apply(score).Total == score.Total + 2, "contextual weights act on the named task cost alone");
    }
}
