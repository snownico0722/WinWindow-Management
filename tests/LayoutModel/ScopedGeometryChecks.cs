using NeatWin.Core;

internal static class ScopedGeometryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1080);
        WindowSnapshot Make(int id, RectI rect) => new(id, rect, rect, area, 1, default, true, id == 1, true, id - 1, 96, (uint)id);
        var main = Make(1, new(170, 125, 900, 730));
        var peer = Make(2, new(835, 230, 750, 620));
        var app = new string('A', 24);
        var entries = new[] { (main, (string?)app), (peer, (string?)app) };
        var focus = new LayoutFocus(1, 2, 3);
        var uses = new Dictionary<nint, string> { [1] = "reading", [2] = "playback" };
        var rule = new AppPairRule(app, app, "right", OnlyWhenPaired: true, SecondWidth: .40, FirstUse: "reading", SecondUse: "playback");
        var rules = new CompiledLayoutRules(Version: 6, Pairs: [rule], AppSlots: [new(app, "right", Use: "reading"), new(app, "left", Use: "playback")]);
        var bound = rules.Bind(entries, focus: focus, uses: uses);
        var slots = rules.BindSlots(entries, focus, uses);
        check(rules.IsValid() && bound is [{ First: 1, Second: 2 }] && slots.Length == 2 &&
            slots.Single(s => s.Window == 1).Order == 1 && slots.Single(s => s.Window == 2) is { Width: .40, Order: -1 },
            "scoped imported pair, slot and width use actual independent surfaces in one application");
        var ambiguous = new[] { entries[0], entries[1], (Make(3, main.VisualRect), (string?)app) };
        uses[3] = "reading";
        check(rules.Bind(ambiguous, focus: focus, uses: uses).Length == 0 &&
            rules.BindSlots(ambiguous, focus, uses) is [{ Window: 2, PreferWidth: false }] &&
            rules.BindSlots(entries, focus).Length == 0 && !(rules with { Version = 5 }).IsValid() &&
            !(rules with { Pairs = [rule with { FirstUse = "audio" }] }).IsValid(),
            "an absent use, duplicate role or incompatible file cannot select a twin by order");
        var layered = new CompiledLayoutRules(Version: 6, AppSlots: [new(app, Width: .55), new(app, "right", Use: "reading")]);
        check(layered.IsValid() && layered.BindSlots([entries[0]], focus, uses) is
            [{ Order: 1, Width: .55, PreferCenter: true, PreferWidth: true }], "a scoped side inherits a unique app's separately specified width");
        var signals = RecordedTaskSignals.From(new() { InstanceUses = uses },
            new Dictionary<nint, int> { [1] = 21, [2] = 22 });
        var replayed = signals.ApplyToReplay(new());
        check(replayed.InstanceUses?.Count == 2 && replayed.InstanceUses[21] == "reading" &&
            replayed.InstanceUses[22] == "playback", "recorded rule selectors retain observations for a later offline import");
        IntentLayoutPlan Plan(WindowSnapshot[] windows, LayoutFocus visit) => LayoutDispatch.Create(LayoutRoute.Smart,
            new VisibilityAnalyzer().SelectVisibleWorkingSet(windows, includeStackAccess: true), new(), null, windows,
            new(Focus: visit, UseWorkingPairs: true) { Diagnostics = new(TraceCandidateAxes: true) });
        var pair = Plan([main, peer], focus);
        check(pair.Groups.SelectMany(g => g.Candidates).Any(c => c.Kind == "reference-clearance" && c.Rejection == null) &&
            !Plan([main, peer], focus with { Evidence = "handoff" }).Groups.SelectMany(g => g.Candidates).Any(c => c.Kind == "reference-clearance"),
            "confirmed reading pair can expose a neighbor with one translation, while a handoff cannot authorize this proposal");
        var clipped = Make(2, new(930, 150, 1100, 780));
        var repair = Plan([main, clipped], focus);
        check(repair.Groups.SelectMany(g => g.Candidates).Any(c => c.Kind == "clipped-edge-repair" && c.Rejection == null),
            "a window extending past one screen edge has a feasible repair keeping its opposite edge in place");
    }
}
