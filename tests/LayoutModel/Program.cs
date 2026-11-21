using System.IO.Compression;
using System.Text.Json;
using NeatWin.Core;
using NeatWin.Reference;

var area = new RectI(0, 0, 1920, 1040);
var home = new RectI(560, 100, 810, 700);
WindowSnapshot Window(int id, RectI rect) => new(id, rect, rect, area, 1, default, true, id == 1, true, id - 1, 96, (uint)id);
WindowSnapshot[] windows = [Window(1, home), Window(2, home with { X = 600 })];
var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}

var rect = new RectI(0, 0, 400, 400);
var corner = WindowExposure.Measure(rect, [rect, rect], [new(0, 100, 400, 300), new(100, 0, 300, 100)]);
Check(corner.Visible == 1d / 16 && corner.Cells == "F000000000000000" &&
    corner.LargestFragment == new RectI(0, 0, 100, 100), "only the exposed corner contributes");
var split = WindowExposure.Measure(rect, [new(0, 0, 100, 400), new(200, 0, 200, 400)], []);
Check(split.Visible == .75 && split.Offscreen == .25, "the gap between displays is offscreen");
Check(!WindowExposure.Measure(new(0, 0, 4, 4), [rect], [rect]).Cells.Contains('F'), "fully covered cells cannot supply visual samples");

var history = new ForegroundHistory();
history.Visit(1, 1, 1000); history.Visit(2, 2, 2000);
Check(history.Resolve(windows, 2100).Companion == 0, "one switch does not establish a companion");
history.Visit(1, 1, 3000);
var focus = history.Resolve(windows, 3100);
Check(focus == new LayoutFocus(1, 2, 2), "switching back identifies the main and companion");
history.Retire(2);
Check(history.Resolve(windows, 3200).Companion == 0, "destroyed handles lose their history");

var tracker = new ObservationIdentityTracker();
WorkspaceFrame Frame(WindowSnapshot[] items) => tracker.Capture(items, [new(area, area)], w => "app-" + w.Handle);
var start = Frame(windows);
var left = home with { X = 0 };
var moved = Frame([windows[0] with { VisualRect = left }, windows[1]]);
var gesture = new WindowAdjustmentObservation(now, "session", 1, home, left, area, area, 96, 900, [], "stable",
    3, start, moved, moved, "arrange-request");
var edit = LayoutHabits.Observe(gesture);
Check(edit is { Partner: "app-2", Order: -1, Corrected: true }, "a stable manual correction learns the left/right pair");
Check(LayoutHabits.Observe(gesture with { Outcome = "automation" }) == null &&
    LayoutHabits.Observe(gesture with { SettledContext = null }) == null, "automated and legacy incomplete gestures are excluded");
var hiddenPeer = moved.Windows[1] with { VisibleRatio = 0, Exposure = new(0, 0, new string('0', 16), default) };
var cornerPeer = hiddenPeer with { VisibleRatio = 1d / 16, Exposure = corner };
var cornerEdit = gesture with { StartContext = start with { Windows = [start.Windows[0], hiddenPeer] },
    SettledContext = moved with { Windows = [moved.Windows[0], cornerPeer] } };
Check(LayoutHabits.Observe(cornerEdit) is { RevealedCell: 0 }, "exposing just one sixteenth of a companion is eligible evidence");
var samples = LayoutHabits.Add([], edit!);
samples = LayoutHabits.Add(samples, edit! with { Time = now.AddMinutes(2) });
Check(samples.Length == 1 && LayoutHabits.Resolve(samples, windows[0], "app-1", "app-2", 2, now.AddHours(1)) == null,
    "a burst of dragging gives one sample and no layout preference");
for (int day = 1; day <= 8; day++) samples = LayoutHabits.Add(samples, edit! with { Time = now.AddDays(day) });
var habit = LayoutHabits.Resolve(samples, windows[0], "app-1", "app-2", 2, now.AddDays(9))!;
Check(habit is { Order: -1, Confidence: > .5 }, "independent edits establish a placement preference");
var context = new TaskLayoutContext(ExplicitArrange: true, Focus: focus);
IntentLayoutPlan Arrange(TaskLayoutContext ctx) => LayoutDispatch.Create(LayoutRoute.ManualAlgorithm,
    new VisibilityAnalyzer().SelectForManualArrange(windows, true), new(), null, windows, ctx);
RectI Target(IntentLayoutPlan plan, int index) =>
    plan.Moves.FirstOrDefault(m => m.Window.Handle == windows[index].Handle)?.TargetVisualRect ?? windows[index].VisualRect;
Check(Arrange(new(ExplicitArrange: true, Placement: [habit])).Groups.Any(g =>
    g.Task?.Relations.Any(r => r.Source == "learned-working-pair") == true),
    "a learned companion can be selected when live switch history is missing");
var mainLeft = Arrange(context with { Placement = [habit] });
var mainRight = Arrange(context with { Placement = [habit with { Order = 1, Center = 1 - habit.Center }] });
Check(Target(mainLeft, 0).X < Target(mainLeft, 1).X && Target(mainRight, 0).X > Target(mainRight, 1).X,
    "opposite learned preferences produce opposite actual layouts through manual Smart");
Check(Target(mainLeft, 0).Height == home.Height && Target(mainRight, 0).Height == home.Height,
    "side-by-side arrangements retain useful window height");
Check(ActivityHints.Classify(false, .5, .5, 2, true, null) == "media-with-motion" &&
    ActivityHints.Classify(false, .5, .5, 0, true, 2) == "visible-background" &&
    ActivityHints.Classify(false, .1, .5, 2, false, null) == "peripheral-motion",
    "visible motion, peripheral motion and uncorroborated app-level playback stay distinct");

for (int day = 9; day <= 100; day++)
    samples = LayoutHabits.Add(samples, edit! with { Time = now.AddDays(day), InnerOverlap = 0 });
var longHabit = LayoutHabits.Resolve(samples, windows[0], "app-1", "app-2", 2, now.AddDays(101));
Check(longHabit is { Order: < -.99, InnerOverlap: < .01, Confidence: > .8 },
    "100 independent left placements strengthen the same ordering and zero-overlap preference");

var oneCorner = ActivityHints.FragmentPatch(corner.LargestFragment);
var cornerRole = ActivityHints.Classify(false, corner.Visible, .4, 0, false, null, false, 2);
var anchor = ActivityHints.ToLayoutHint(3, rect, cornerRole, oneCorner);
Check(anchor is { Anchor: true } && anchor.ProtectedRegions!.Single().Project(rect) == new RectI(0, 0, 100, 100),
    "repeated changes in one sixteenth protect exactly that exposed corner");
Check(ActivityHints.Classify(false, .7, .5, 2, false, null, true) == "audible-with-motion" &&
    ActivityHints.Classify(false, .7, .5, 0, false, null, true) == "visible-background",
    "process audio supports visible motion and alone cannot label a window as video");

Check(ActivityHints.Classify(false, 1, .5, 0, true, null, true, processWindowCount: 1) == "audible-media-session" &&
    ActivityHints.Classify(false, 1, .5, 0, true, null, true, processWindowCount: 2) == "visible-background",
    "audible playback can support a single visible app window without pixel sampling, and abstains for siblings");

var workspace = new RectI(0, 0, 2100, 1000);
var fixture = new[] { Window(1, new(200, 100, 1200, 800)) with { WorkArea = workspace },
    Window(2, new(600, 100, 1200, 800)) with { WorkArea = workspace } };
IntentLayoutPlan Run(WindowSnapshot[] items, TaskLayoutContext ctx) => LayoutDispatch.Create(LayoutRoute.ManualAlgorithm,
    new VisibilityAnalyzer().SelectForManualArrange(items, true), new(), null, items, ctx);
RectI Placed(IntentLayoutPlan plan, WindowSnapshot w) => plan.Moves.FirstOrDefault(m => m.Window.Handle == w.Handle)?.TargetVisualRect ?? w.VisualRect;
var asVideo = new TaskLayoutContext(ExplicitArrange: true, Focus: new(1, 2, 3), WindowHints: [new(2, PassiveVisual: true, MediaCompanion: true)]);
var overlapping = Run(fixture, asVideo);
var separated = Run(fixture, asVideo with {Placement = [longHabit!]});
var first = Placed(overlapping, fixture[0]); var second = Placed(overlapping, fixture[1]);
Check(first.HorizontalOverlapRatio(second) is > .10 and <= .32 && first.Height == 800 && second.Height == 800,
    "the pressured main/media pair can keep height and share part of the inner edge");
Check(Placed(separated, fixture[0]).Intersect(Placed(separated, fixture[1])).IsEmpty,
    "a learned zero-overlap preference overrides the media default in the actual selected plan");
var watched = Window(3, rect) with { WorkArea = workspace };
var trio = new[] { fixture[0], fixture[1], watched };
var result = Run(trio, asVideo with { WindowHints = [new(2, PassiveVisual: true, MediaCompanion: true), anchor!] });
Check(Placed(result, watched) == watched.VisualRect && IntentLayoutPlanner.DesktopRegionsPreserved(trio,
    trio.Select(w => w with { VisualRect = Placed(result, w) }).ToArray(), asVideo with { WindowHints = [anchor!] }),
    "the recorder's corner hint survives candidate selection for the whole desktop");

Check(!IntentLayoutPlanner.DesktopRegionsPreserved(trio,
    [trio[0] with { VisualRect = rect }, trio[1], watched], asVideo with { WindowHints = [anchor!] }),
    "a proposed move that covers the observed patch is rejected");
var partial = WindowExposure.Measure(new(-900, 0, 1000, 400), [workspace], []);
Check(partial.Visible == .1 && partial.Offscreen == .9, "90 percent offscreen records ten percent visible");
var recordedContext = asVideo with { WindowHints = [anchor!], Placement = [longHabit!] };
var ids = new Dictionary<nint, int> { [1] = 11, [2] = 12, [3] = 13 };
var signals = RecordedTaskSignals.From(recordedContext, ids);
var replay = JsonSerializer.Deserialize<RecordedTaskSignals>(JsonSerializer.Serialize(signals))!.ApplyToReplay(new());
Check(replay.Focus is { Main: 11, Companion: 12 } && replay.WindowHints!.Single() is { Handle: 13, Anchor: true } &&
    replay.Placement!.Single() is { Window: 11, Partner: 12, InnerOverlap: 0 },
    "replay restores decisions from anonymous ids including local evidence and learned overlap");

var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
try
{
    var store = new PlacementHistoryStore(directory);
    Check(store.Key("private identity", false) == null && !Directory.Exists(directory), "reading preferences does not create an identity");
    string? key = store.Key("private identity", true);
    store.Learn(gesture);
    var reopened = new PlacementHistoryStore(directory);
    Check(key != null && reopened.Key("private identity", false) == key && reopened.Read().Length == 1,
        "anonymous identity and manual placement survive restart");
    var file = new LayoutRuleFile(directory);
    file.EnsureTemplate();
    var originalRules = file.Read();
    var path = Path.Combine(directory, LayoutRuleFile.Name);
    var pair = new AppPairRule(new string('A', 24), new string('B', 24), "right", 0);
    File.WriteAllText(path, JsonSerializer.Serialize(originalRules with { Pairs = [pair] }));
    var updated = file.Read();
    var bound = updated.Bind([(fixture[0], pair.FirstApp), (fixture[1], pair.SecondApp)]);
    var fromRule = Run(fixture, asVideo with { Rules = updated, RulePairs = bound });
    var leftTarget = Placed(fromRule, fixture[0]); var rightTarget = Placed(fromRule, fixture[1]);
    Check(leftTarget.Left > rightTarget.Left && leftTarget.Intersect(rightTarget).IsEmpty,
        "installing a JSON pair rule switches the actual windows to the requested sides and spacing");
    Check(updated.Bind([(fixture[0], pair.FirstApp), (fixture[1], pair.SecondApp), (watched, pair.SecondApp)]).Length == 0,
        "a pair rule abstains when a second same-app window makes the match ambiguous");
    File.WriteAllText(path, "{");
    Check(ReferenceEquals(file.Read(), updated), "a partial rules edit retains the last valid model");
    File.Delete(path);
    Check(file.Read() == CompiledLayoutRules.Default, "removing custom rules restores the default at the next read");
    reopened.Clear();
    Check(reopened.Key("private identity", false) == null && reopened.Read().Length == 0, "clear forgets identities and examples");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }


// --- v5 Algorithmic & Architectural Regression Checks ---
// 1. Transient Alt+Tab pass-through (<450ms) does not pollute ForegroundHistory; recency breaks ties.
var markov = new ForegroundHistory();
markov.Visit(1, 1, 10_000);
markov.Visit(3, 3, 10_200); // 200ms transient Alt+Tab icon pass-through on the way to window 2
markov.Visit(2, 2, 10_400);
markov.Visit(1, 1, 12_000);
markov.Visit(2, 2, 14_000);
var threeWins = new[] { Window(1, home), Window(2, home with { X = 600 }), Window(3, home with { X = 640 }) };
Check(markov.Resolve(threeWins, 14_500) is { Main: 1, Companion: 2 },
    "transient Alt+Tab pass-throughs are collapsed and recent active pair wins");

// 2. Bidirectional habit learning + >0.40 overlap clamping (no cliff drop to null).
var stationaryLeft = Window(1, new(0, 100, 1120, 780));
var draggedRightBefore = Window(2, new(450, 100, 1120, 780));
var draggedRightAfter = Window(2, new(800, 100, 1120, 780)); // ~28.6% overlap, later test 42% clamped to 40%
var biStart = Frame([stationaryLeft, draggedRightBefore]);
var biEnd = Frame([stationaryLeft, draggedRightAfter]);
var biGesture = new WindowAdjustmentObservation(now, "bi", 2, draggedRightBefore.VisualRect, draggedRightAfter.VisualRect,
    area, area, 96, 850, [], "stable", 3, biStart, biEnd, biEnd, "arrange-request");
var biHabit = LayoutHabits.Observe(biGesture)!;
Check(biHabit is { App: "app-2", Partner: "app-1", Order: 1, PartnerCenter: not null, PartnerWidth: not null },
    "dragging only the right companion records stationary left partner geometry");
var biSamples = Array.Empty<LayoutHabit>();
for (int d = 1; d <= 4; d++)
    biSamples = LayoutHabits.Add(biSamples, biHabit with { Time = now.AddDays(d), InnerOverlap = .43 });
var mirrorPref = LayoutHabits.Resolve(biSamples, stationaryLeft, "app-1", "app-2", 2, now.AddDays(5));
var directClamped = LayoutHabits.Resolve(biSamples, draggedRightAfter, "app-2", "app-1", 1, now.AddDays(5));
Check(mirrorPref is { Order: < -.9, InnerOverlap: .40 } && directClamped is { Order: > .9, InnerOverlap: .40 },
    "mirror habit resolves stationary main side/width and clamps 43% overlap to 40% instead of dropping");

// 3. Authorized high overlap (0.35..0.40) beats zero-overlap columns without slope inversion.
var wideArea = new RectI(0, 0, 2560, 1380);
var hdPair = new[]
{
    Window(1, new(500, 120, 1520, 1080)) with { WorkArea = wideArea, Dpi = 120 },
    Window(2, new(560, 120, 1520, 1080)) with { WorkArea = wideArea, Dpi = 120, IsForeground = false }
};
var highOverlapRule = new CompiledLayoutRules(Version: 2, MediaOverlap: .38,
    RolePairs: [new RolePairRule("foreground", "media", "left", .38)]);
var roleBound = highOverlapRule.Bind([(hdPair[0], "000000000000000000000001"), (hdPair[1], "000000000000000000000002")],
    [new(2, PassiveVisual: true, MediaCompanion: true)]);
var hdPlan = Run(hdPair, new(ExplicitArrange: true, Focus: new(1, 2, 3),
    WindowHints: [new(2, PassiveVisual: true, MediaCompanion: true)], Rules: highOverlapRule, RulePairs: roleBound));
var hdLeft = Placed(hdPlan, hdPair[0]); var hdRight = Placed(hdPlan, hdPair[1]);
Check(hdLeft.X < hdRight.X && hdLeft.HorizontalOverlapRatio(hdRight) is >= .34 and <= .40,
    "v2 RolePairRule binds new apps by role and achieves 38% authorized overlap without flipping to 0% columns");

// 4. Three-window working-overlap tucks the 3rd auxiliary window's top access band instead of failing TaskSafe.
var topPairA = hdPair[0] with { VisualRect = new(500, 0, 1520, 1080), OuterRect = new(500, 0, 1520, 1080) };
var topPairB = hdPair[1] with { VisualRect = new(560, 0, 1520, 1080), OuterRect = new(560, 0, 1520, 1080), ZOrder = 1 };
var auxThird = Window(3, new(40, 60, 1100, 760)) with { WorkArea = wideArea, Dpi = 120, IsForeground = false, ZOrder = 2 };
var threeDesktop = new[] { topPairA, topPairB, auxThird };
var threePlan = LayoutDispatch.Create(LayoutRoute.Smart,
    new VisibilityAnalyzer().SelectVisibleWorkingSet(threeDesktop, includeStackAccess: true), new(), null, threeDesktop,
    new(UseWorkingPairs: true, Focus: new(1, 2, 3),
        WindowHints: [new(2, PassiveVisual: true, MediaCompanion: true)], Rules: highOverlapRule, RulePairs: roleBound));
Check(threePlan.Groups.Any(g => g.Selected is "working-overlap" or "working-pair") &&
    Placed(threePlan, topPairA).Top >= 40 && Placed(threePlan, auxThird).Top == 0 &&
    Placed(threePlan, topPairA).HorizontalOverlapRatio(Placed(threePlan, topPairB)) >= .20,
    "FullAssist (UseWorkingPairs) arranges a 3-window desktop with working-overlap while keeping the 3rd window's title band accessible");

// 5. Small Topmost floating widget (<15% work area) does not trigger fixed-occluder veto.
var miniPinned = new WindowSnapshot(99, new(2220, 1100, 290, 240), new(2220, 1100, 290, 240),
    wideArea, 1, default, false, false, false, 0, 120, 99, IsTopmost: true);
var withMiniOverlay = new[] { miniPinned, hdPair[0] with { ZOrder = 1 }, hdPair[1] with { ZOrder = 2 } };
var overlayPlan = LayoutDispatch.Create(LayoutRoute.ManualAlgorithm,
    new VisibilityAnalyzer().SelectForManualArrange(withMiniOverlay, true), new(), null, withMiniOverlay,
    new(ExplicitArrange: true, Focus: new(1, 2, 3), WindowHints: [new(2, PassiveVisual: true, MediaCompanion: true)]));
Check(overlayPlan.Moves.Count >= 2 && overlayPlan.Groups.All(g => g.Selected != "keep"),
    "a small pinned Topmost widget (<15% area) acts as soft exposure rather than a fixed-occluder brick wall");

// 6. WindowLayerSafety allows reordering across an other-monitor or disjoint normal window, blocks overlapping unmanageable dialogs.
var disjointMid = new WindowSnapshot(50, new(10, 10, 200, 150), new(10, 10, 200, 150), wideArea, 1, default, true, false, true, 1, 120, 50);
var reorderedA = hdPair[0] with { VisualRect = new(400, 200, 900, 700), ZOrder = 0 };
var reorderedB = hdPair[1] with { VisualRect = new(900, 200, 900, 700), ZOrder = 2 };
Check(WindowLayerSafety.CanReorder([reorderedB, reorderedA], [reorderedA, disjointMid, reorderedB]),
    "WindowLayerSafety permits reordering past a completely non-intersecting normal window");
var modalMid = disjointMid with { VisualRect = new(700, 300, 400, 300), IsManageable = false };
Check(!WindowLayerSafety.CanReorder([reorderedB, reorderedA], [reorderedA, modalMid, reorderedB]),
    "WindowLayerSafety still blocks reordering across an unmanageable dialog on the same monitor");

// 7. WindowSizeMemory learns empirical WM_GETMINMAXINFO clamp floors and prevents unachievable shrinks.
var sizeMem = new WindowSizeMemory();
sizeMem.Observe(new(100, 100, 1400, 900), new(100, 100, 800, 900), new(100, 100, 1160, 900), 96, 2, 2, "app-clamped");
var floors = sizeMem.Bind([(fixture[0], "app-normal"), (fixture[1], "app-clamped")]);
Check(floors.Single() is { Handle: 2, MinWidth: 1160 },
    "WindowSizeMemory records clamped minimum width and binds it for subsequent plans");
var clampedPlan = Run(fixture, new(ExplicitArrange: true, Focus: new(1, 2, 3), SizeFloors: floors));
Check(Placed(clampedPlan, fixture[1]).Width >= 1160,
    "planner respects learned native minimum width floor instead of proposing an impossible shrink");

// The same desk adjustment must not become two independent votes when the user moves both windows.
var directLeft = biHabit with { App = "app-1", Partner = "app-2", Center = biHabit.PartnerCenter!.Value,
    Width = biHabit.PartnerWidth!.Value, PartnerCenter = biHabit.Center, PartnerWidth = biHabit.Width, Order = -1, Time = now };
var twoPeriods = new[] { directLeft, biHabit with { Time = now.AddMinutes(1) }, directLeft with { Time = now.AddMinutes(31) } };
Check(LayoutHabits.Resolve(twoPeriods, stationaryLeft, "app-1", "app-2", 2, now.AddHours(1)) == null &&
    LayoutHabits.Resolve([.. twoPeriods, biHabit with { Time = now.AddMinutes(61) }], stationaryLeft, "app-1", "app-2", 2,
        now.AddMinutes(62)) is { Order: < -.9 }, "mirrored observations count once per pair and period, including older records");

var gameRule = highOverlapRule with { RolePairs = [new("game", "media", "left", .38)] };
var mediaContext = new TaskLayoutContext(ExplicitArrange: true, Focus: new(1, 2, 3), Rules: gameRule,
    WindowHints: [new(2, MediaCompanion: true)], Activities: new Dictionary<nint,string> { [1] = "game-like-interaction" });
var roleExport = RecordedTaskSignals.From(mediaContext, new Dictionary<nint,int> { [1] = 11, [2] = 12 });
var restored = JsonSerializer.Deserialize<RecordedTaskSignals>(JsonSerializer.Serialize(roleExport))!.ApplyToReplay(new(ExplicitArrange: true));
var replayDesktop = hdPair.Select(w => w with { Handle = w.Handle + 10 }).ToArray();
WorkingPairRule[] BindGame(WindowSnapshot[] desk, TaskLayoutContext ctx) =>
    gameRule.Bind(desk.Select(w => (w, (string?)null)), ctx.WindowHints, ctx.Activities);
var runtimeGame = Run(hdPair, mediaContext with { RulePairs = BindGame(hdPair, mediaContext) });
var replayGame = Run(replayDesktop, restored with { Rules = gameRule, RulePairs = BindGame(replayDesktop, restored) });
Check(BindGame(hdPair, mediaContext).Length == 1 && BindGame(replayDesktop, restored).Single().First == 11 &&
    hdPair.Select(w => Placed(runtimeGame, w)).SequenceEqual(replayDesktop.Select(w => Placed(replayGame, w))),
    "resizable game and media bind the same role rule and coordinates after anonymous JSON replay");
var missingRole = restored with { Activities = null };
Check(BindGame(replayDesktop, missingRole).Length == 0, "older logs with no game evidence abstain");

IntentLayoutPlan Automatic(WindowSnapshot[] desk, TaskLayoutContext ctx) => LayoutDispatch.Create(LayoutRoute.Smart,
    new VisibilityAnalyzer().SelectVisibleWorkingSet(desk, includeStackAccess: true), new(), null, desk, ctx);
var noPair = Automatic(threeDesktop, new(UseWorkingPairs: true, Focus: new(1, 0, 0)));
var fullyCovered = Automatic([topPairA, topPairB with { VisualRect = topPairA.VisualRect }, auxThird],
    new(UseWorkingPairs: true, Focus: new(1, 2, 3)));
Check(noPair.Groups.All(g => !g.Handles.Contains((nint)2)) && fullyCovered.Groups.All(g => !g.Handles.Contains((nint)2)),
    "unknown slivers and fully covered companions stay outside the automatic working set");
var anchorPlan = Automatic(threeDesktop, new(UseWorkingPairs: true, Focus: new(1, 2, 3), WindowHints: [new(2, Anchor: true)]));
Check(anchorPlan.Groups.All(g => !g.Handles.Contains((nint)2)), "an anchored companion stays put during automatic layout");

var focusArea = new RectI(0, 0, 2560, 1380);
WindowSnapshot AttentionWindow(int id, RectI bounds) => Window(id, bounds) with { WorkArea = focusArea, Dpi = 120 };
var browsingDesktop = new[]
{
    AttentionWindow(1, new(160, 452, 663, 340)),
    AttentionWindow(2, new(160, 0, 1128, 442)) with { ProcessId = 1 },
    AttentionWindow(3, new(1113, 280, 1472, 928)),
};
foreach (int active in new[] { 1, 2 })
{
    var other = 3 - active;
    var visits = new ForegroundHistory();
    visits.Visit(active, 1, 1_000);
    visits.Visit(3, 3, 3_000);
    visits.Visit(active, 1, 5_000);
    visits.Visit(3, 3, 7_000);
    visits.Visit(active, 1, 9_000);
    visits.Visit(other, 1, 11_000);
    visits.Visit(active, 1, 15_000);
    var current = browsingDesktop.Select(w => w with { IsForeground = w.Handle == active }).ToArray();
    var recent = visits.Resolve(current, 15_100, out var recentAttention);
    Check(recent.Main == active && recentAttention[other].Weight > ForegroundHistory.WorkingAttention,
        $"focus history distinguishes active window {active} from its same-process sibling");
    visits.Resolve(current, 21_200, out var quietAttention);
    visits.Interact(active, 1, 21_100);
    visits.Resolve(current, 21_200, out var handoffAttention);
    Check(quietAttention[other].Weight >= ForegroundHistory.WorkingAttention &&
        handoffAttention[other].Weight < ForegroundHistory.WorkingAttention,
        $"continued recent input in window {active} accelerates the older task's decay");

    var currentFocus = visits.Resolve(current, 45_000, out var agedAttention);
    var attentionContext = new TaskLayoutContext(UseWorkingPairs: true, Focus: currentFocus, Attention: agedAttention,
        RulePairs: [new(active, other, "left", null)], Placement: [new(active, 3, .25, .5, -1, -1, .9)],
        WindowHints: [new(3, PassiveVisual: true, MediaCompanion: true)]);
    var arranged = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(current, true),
        new(SmartStrength: SmartTidyStrength.Assertive), null, current, attentionContext);
    var original = current.Single(w => w.Handle == active);
    var target = Placed(arranged, original);
    Check(currentFocus.Companion == 0 && agedAttention[active].Weight == 1 && agedAttention[other].Weight < ForegroundHistory.WorkingAttention &&
        agedAttention[3].Weight < ForegroundHistory.WorkingAttention && arranged.Groups is [{ Handles: [var only] }] && only == active &&
        target.Width >= original.VisualRect.Width && target.Height >= original.VisualRect.Height,
        $"expired windows do not crowd or join active window {active} during assertive automatic arrangement");
    var exported = RecordedTaskSignals.From(attentionContext, new Dictionary<nint, int> { [1] = 11, [2] = 12, [3] = 13 });
    var replayAttention = JsonSerializer.Deserialize<RecordedTaskSignals>(JsonSerializer.Serialize(exported))!
        .ApplyToReplay(new()).Attention!;
    Check(replayAttention[active + 10].Weight == 1 && replayAttention[other + 10] == agedAttention[other],
        $"anonymous replay preserves attention for window {active} and its background sibling");

    visits.Interact(other, 1, 45_010);
    var paired = visits.Resolve(current, 45_050, out var directInput);
    var adjacent = attentionContext with { Focus = paired, Attention = directInput, RulePairs = [],
        Placement = [new(active, other, .15, .35, -1, -1, .8)] };
    var directPlan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(current, true),
        new(SmartStrength: SmartTidyStrength.Assertive), null, current, adjacent);
    Check(directPlan.Groups.SelectMany(g => g.Handles).ToHashSet().SetEquals([(nint)active, (nint)other]) &&
        Placed(directPlan, original).Height >= original.VisualRect.Height,
        $"direct input can bring window {other} back without giving the media surface a layout slot");

    var manualWindow = new ManualWindowGesture(active, original.VisualRect with { X = 0 }, original.VisualRect,
        focusArea, ManualGestureKind.Move, [], 500, 0);
    var session = new TaskLayoutSession();
    session.Gesture(manualWindow, now);
    var gestureContext = session.Capture(current, new(), [], null, now.AddSeconds(1)) with
    {
        UseWorkingPairs = true, Focus = currentFocus, Attention = agedAttention,
        Placement = [new(active, 0, .35, .45, 0, -1, .95)],
    };
    var gesturePlan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(current, true),
        new(SmartStrength: SmartTidyStrength.Assertive), null, current, gestureContext);
    Check(gestureContext.GestureAnchor == active && Placed(gesturePlan, original) == original.VisualRect &&
        session.Capture(current, new(), [], null, now.AddSeconds(5)).GestureAnchor == 0 &&
        session.Capture(current, new(), [], null, now.AddSeconds(1), explicitArrange: true).GestureAnchor == 0,
        $"automatic arrangement preserves the completed gesture for window {active} and releases it afterward");
}

var coveredTask = new[]
{
    Window(1, new(100, 120, 930, 700)),
    Window(2, new(220, 140, 710, 720)),
};
var workingAttention = new Dictionary<nint, LayoutAttention> { [1] = new(1, 1, 1, 0, true), [2] = new(.8, .8, .8, .9, false) };
var workingContext = new TaskLayoutContext(UseWorkingPairs: true, Focus: new(1, 2, 4), Attention: workingAttention);
var workingPlan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(coveredTask, true),
    new(SmartStrength: SmartTidyStrength.Assertive), null, coveredTask, workingContext);
Check(workingPlan.Moves.Count > 0 && Placed(workingPlan, coveredTask[0]).Height >= coveredTask[0].VisualRect.Height &&
    Placed(workingPlan, coveredTask[0]).Intersect(Placed(workingPlan, coveredTask[1])).Area <
    coveredTask[0].VisualRect.Intersect(coveredTask[1].VisualRect).Area,
    "automatic arrangement still repairs real occlusion between two task windows");
var soloContext = workingContext with { Focus = new(1, 0, 0),
    Attention = new Dictionary<nint, LayoutAttention> { [1] = workingAttention[1] } };
var soloPlan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange(coveredTask, true),
    new(SmartStrength: SmartTidyStrength.Assertive), null, coveredTask, soloContext);
Check(soloPlan.Groups is [{ Handles: [1] }] && soloPlan.Moves.All(m => m.Window.Handle == 1),
    "a covered background window stays put when only the foreground belongs to the current task");

var observedStack = new[]
{
    AttentionWindow(1, new(1656, 156, 904, 1071)),
    AttentionWindow(2, new(1113, 280, 1472, 928)),
    AttentionWindow(3, focusArea) with { IsManageable = false, IsResizable = false },
};
var stackVisible = new VisibilityAnalyzer().SelectForManualArrange(observedStack, true);
var gentle = new TidyOptions(SmartStrength: SmartTidyStrength.Gentle);
var opened = LayoutDispatch.Create(LayoutRoute.Smart, stackVisible, gentle, null, observedStack, workingContext);
Check(opened.Moves is [{ Window.Handle: 2, TargetVisualRect: var separatedRect }] &&
    separatedRect.Width == observedStack[1].VisualRect.Width && separatedRect.Height == observedStack[1].VisualRect.Height &&
    separatedRect.Intersect(observedStack[0].VisualRect).IsEmpty && separatedRect.Intersect(focusArea) == separatedRect,
    "conservative Smart opens a severely covered active pair at full size over a maximized background");
var parked = LayoutDispatch.Create(LayoutRoute.Smart, stackVisible, gentle, null, observedStack, soloContext);
Check(parked.Moves.Count == 0, "the same severe stack stays in place if the rear window is outside the task");

var videoWindow = Window(1, new(100, 100, 1200, 800));
var videoHint = new VideoBlackBarHint(1, VideoBlackBarOrientation.Horizontal, 2, new(150, 190, 1100, 620), .95);
var videoContext = workingContext with { Focus = new(1, 0, 0), VideoHint = videoHint };
var videoDesktop = new[] { videoWindow };
var videoVisible = new VisibilityAnalyzer().SelectForManualArrange(videoDesktop, true);
var videoOptions = new TidyOptions();
var videoBaseline = LayoutDispatch.Create(LayoutRoute.Smart, videoVisible, videoOptions, null, videoDesktop, videoContext);
var videoPlan = VideoAspectPostProcessor.Refine(videoVisible, videoBaseline.Moves, videoOptions,
    new(RemoveVideoBlackBars: true, VideoBlackBarTendency: VideoBlackBarTendency.Shrink), videoHint);
Check(videoPlan.Single().TargetVisualRect.Height == 730 &&
    IntentLayoutPlanner.TaskRefinementAcceptable(videoVisible, videoBaseline, videoPlan, videoOptions, videoContext, videoDesktop),
    "attention protection preserves the picture while allowing a confirmed letterbox to be removed");

var sizeTest = new WindowSizeMemory();
var wide = new RectI(0, 0, 1400, 900); var narrow = wide with { Width = 800 };
var stopped = wide with { Width = 1160 };
var sameApp = new[] { (Window(1, wide), (string?)"app"), (Window(2, wide), (string?)"app"), (Window(3, wide), (string?)"app") };
sizeTest.Observe(wide, narrow, wide, 96, 1, 1, "app");
Check(sizeTest.Bind(sameApp).Length == 0, "a refused resize does not teach any minimum size");
sizeTest.Observe(wide, narrow, stopped, 96, 1, 1, "app");
sizeTest.Observe(wide, narrow, stopped, 96, 1, 1, "app");
Check(sizeTest.Bind(sameApp) is [{ Handle: 1, MinWidth: 1160 }], "repeating a clamp on one instance stays window-local");
sizeTest.Observe(wide, narrow, stopped, 96, 2, 2, "app");
Check(sizeTest.Bind(sameApp).Length == 3, "two independent matching clamps support an application floor");
sizeTest.Observe(stopped, narrow, narrow, 96, 1, 1, "app");
Check(sizeTest.Bind(sameApp) is [{ Handle: 2 }] && sizeTest.Bind([(Window(2, narrow), "app")]).Length == 0,
    "successful smaller geometry withdraws the floor and a smaller sibling is never inflated");
sizeTest.Retire(2);
Check(sizeTest.Bind(sameApp).Length == 0, "retired handles cannot donate a floor to a reused handle");
Check(ActivityHints.Classify(false, .8, .4, 0, true, .5, true, processWindowCount: 2) == "visible-background" &&
    ActivityHints.Classify(false, .8, .4, 2, true, .5, true, processWindowCount: 2) == "media-with-motion",
    "a static browser sibling needs local evidence even when shared audio and playback are active");

var behaviorDiscovery = new WindowBehaviorDiscovery();
long BehaviorUtc(long elapsed) => now.UtcTicks + elapsed * TimeSpan.TicksPerMillisecond;
var helper = new BehaviorWindow(70, "utility-test.exe", BehaviorUtc(-20000), true);
var nextTask = new BehaviorWindow(71, "task-test.exe", BehaviorUtc(1600), true);
behaviorDiscovery.Observe(0, BehaviorUtc(0), [helper], new Dictionary<int, BehaviorDeparture>(), new HashSet<int>());
behaviorDiscovery.Observe(2000, BehaviorUtc(2000), [helper with { Foreground = false }, nextTask],
    new Dictionary<int, BehaviorDeparture>(), new HashSet<int>());
behaviorDiscovery.Observe(6000, BehaviorUtc(6000), [nextTask],
    new Dictionary<int, BehaviorDeparture> { [helper.Id] = BehaviorDeparture.Closed }, new HashSet<int>());
var discoveredRule = behaviorDiscovery.Observe(16000, BehaviorUtc(16000), [nextTask],
    new Dictionary<int, BehaviorDeparture>(), new HashSet<int>()).Single();
Check(discoveredRule.Rule.SourceApp == helper.App && discoveredRule.Rule.NextApps!.Single() == nextTask.App &&
    discoveredRule.Rule.RequireNewProcess && discoveredRule.Rule.Action == WindowBehaviorAction.Close &&
    discoveredRule.DelayMilliseconds == 4000, "behavior discovery learns a generic fresh-task handoff from observed window actions");

var sequence = new WindowBehaviorSequence(discoveredRule.Rule);
var fit = new WindowBehaviorFit().ObserveDismissal(4000).ObserveDismissal(4000);
BehaviorUpdate BehaviorTick(long tick, BehaviorWindow[] current, bool enabled = true, bool dismissed = false) =>
    sequence.Observe(tick, BehaviorUtc(tick), current,
        dismissed ? new Dictionary<int, BehaviorDeparture> { [helper.Id] = BehaviorDeparture.Closed } : new Dictionary<int, BehaviorDeparture>(),
        fit, learn: true, canAct: enabled);
BehaviorTick(0, [helper]);
BehaviorTick(2400, [helper with { Foreground = false }, nextTask with { ProcessStartedUtc = BehaviorUtc(-10) }]);
Check(BehaviorTick(8000, [helper with { Foreground = false }, nextTask with { ProcessStartedUtc = BehaviorUtc(-10) }]).Dismiss is null,
    "a previously running task does not trigger a learned fresh-launch action");
sequence.Reset();
BehaviorTick(0, [helper]);
BehaviorTick(2000, [helper with { Foreground = false }, nextTask]);
Check(BehaviorTick(6900, [helper with { Foreground = false }, nextTask], enabled: false).Dismiss is null &&
    BehaviorTick(7000, [helper with { Foreground = false }, nextTask]).Dismiss == helper.Id,
    "a fitted action waits for the task and requires the individual rule to be enabled");
BehaviorTick(7600, [nextTask], dismissed: true);
Check(BehaviorTick(8100, [helper with { Id = 72 }, nextTask with { Foreground = false }]).Fit?.Reopens == 1,
    "reopening the helper after an automatic dismissal is negative feedback");

var routine = new WindowBehaviorSequence(discoveredRule.Rule with { RequireNewProcess = false });
var reopenedHelper = helper with { Id = 72 };
BehaviorUpdate RoutineTick(long tick, BehaviorWindow[] current, Dictionary<int, BehaviorDeparture>? departures = null) =>
    routine.Observe(tick, BehaviorUtc(tick), current, departures ?? [], fit, learn: false, canAct: true);
RoutineTick(0, [helper]);
RoutineTick(2000, [helper with { Foreground = false }, nextTask]);
RoutineTick(6500, [helper with { Foreground = false }, nextTask]);
RoutineTick(7000, [nextTask], new() { [helper.Id] = BehaviorDeparture.Closed });
RoutineTick(7500, [reopenedHelper, nextTask with { Foreground = false }]);
RoutineTick(8000, [reopenedHelper with { Foreground = false }, nextTask]);
RoutineTick(14_000, [reopenedHelper, nextTask with { Foreground = false }]);
RoutineTick(16_000, [reopenedHelper with { Foreground = false }, nextTask]);
Check(RoutineTick(22_000, [reopenedHelper with { Foreground = false }, nextTask]).Dismiss is null,
    "reopening blocks repeated dismissal in the same task even with personalization disabled");

if (args is ["--behavior", var zip, var behaviorRules, ..])
    BehaviorReplay.Run(zip, behaviorRules, args.ElementAtOrDefault(3));
else if (args.Length > 0)
    Replay(args[0], args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));

static void Replay(string path, string? rulesPath, string? sessionFilter)
{
    CompiledLayoutRules? overrideRules = null;
    if (rulesPath != null)
    {
        overrideRules = JsonSerializer.Deserialize<CompiledLayoutRules>(File.ReadAllText(rulesPath), LayoutRuleFile.Json);
        if (overrideRules?.IsValid() != true) throw new InvalidDataException("Invalid candidate layout rules.");
    }
    using var archive = ZipFile.OpenRead(path);
    var corrections = new Dictionary<(string Session, string Request), List<WindowAdjustmentObservation>>();
    foreach (var adjEntry in archive.Entries.Where(e => e.Name.StartsWith("adjustments-") && e.Name.EndsWith(".jsonl")))
    {
        using var adjReader = new StreamReader(adjEntry.Open());
        while (adjReader.ReadLine() is { } line)
        {
            var obs = JsonSerializer.Deserialize<WindowAdjustmentObservation>(line);
            if (obs is { Outcome: "stable", AfterRequest: string req } && !string.IsNullOrEmpty(req))
            {
                if (!corrections.TryGetValue((obs.Session, req), out var list)) corrections[(obs.Session, req)] = list = [];
                list.Add(obs);
            }
        }
    }
    var seen = new HashSet<(string, string)>();
    var analyzer = new VisibilityAnalyzer();
    int correctedRuns = 0;
    double totalNormalizedRegret = 0;
    int regretSamples = 0;
    foreach (var entry in archive.Entries.Where(e => e.Name.StartsWith("plans-") && e.Name.EndsWith(".jsonl")))
    {
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            var run = JsonSerializer.Deserialize<LayoutRunObservation>(line);
            if (run is not { Trigger: "explicit" or "automatic-gesture" or "automatic-workspace", Actual: not null } ||
                (sessionFilter is not null && run.Session != sessionFilter) || !seen.Add((run.Session, run.RequestId))) continue;
            var frame = run.Before;
            var areas = frame.Windows.Select(w => w.WorkArea).Distinct().ToArray();
            bool explicitRun = run.Trigger == "explicit";
            foreach (bool assumeResizable in new[] { true, false })
            {
                var desktop = frame.Windows.OrderBy(w => w.ZOrder).Select(w => new WindowSnapshot(w.Id, w.Rect, w.Rect,
                    w.WorkArea, Array.IndexOf(areas, w.WorkArea) + 1, default, w.Resizable ?? (assumeResizable && w.Manageable),
                    w.Foreground, w.Manageable, w.ZOrder, w.Dpi, IsTopmost: w.Topmost)).ToArray();
                var displays = areas.Select((a, i) => new TaskDisplay(i + 1, a,
                    frame.Displays?.FirstOrDefault(d => d.WorkArea == a)?.Bounds ?? a)).ToArray();
                var focus = desktop.FirstOrDefault(w => w.IsForeground);
                var ctx = new TaskLayoutContext(Displays: displays, ExplicitArrange: explicitRun, UseWorkingPairs: true,
                    PreferReversibleVerticalFill: true, Focus: new(focus?.Handle ?? 0, 0, 0));
                ctx = run.Signals?.ApplyToReplay(ctx) ?? ctx;
                var activeRules = overrideRules ?? run.Rules ?? CompiledLayoutRules.Default;
                var keyed = desktop.Zip(frame.Windows.OrderBy(w => w.ZOrder), (window, observed) => (window, observed.AppKey)).ToArray();
                var slotPrefs = activeRules.BindSlots(keyed);
                var mergedPrefs = CompiledLayoutRules.ApplySlots(ctx.Placement ?? [], slotPrefs);
                ctx = ctx with { Rules = activeRules, RulePairs = activeRules.Bind(keyed, ctx.WindowHints, ctx.Activities), Placement = mergedPrefs };
                var fullDesktop = run.FullDesktop ?? explicitRun;
                var workingSet = fullDesktop ? analyzer.SelectForManualArrange(desktop, true) : analyzer.SelectVisibleWorkingSet(desktop, includeStackAccess: true);
                var plan = LayoutDispatch.Create(explicitRun ? LayoutRoute.ManualAlgorithm : LayoutRoute.Smart,
                    workingSet, new(SmartStrength: run.Strength ?? SmartTidyStrength.Balanced), null, desktop, ctx);
                var projected = TaskLayoutSession.Project(desktop, plan);
                bool? regionsPreserved = !frame.Truncated && ctx.WindowHints?.Any(h => h.ProtectedRegions is { Length: > 0 }) == true
                    ? IntentLayoutPlanner.DesktopRegionsPreserved(desktop, projected, ctx) : null;
                double? runRegret = null, verticalError = null;
                if (corrections.TryGetValue((run.Session, run.RequestId), out var edits))
                {
                    double horizontal = 0, vertical = 0;
                    int matched = 0;
                    foreach (var edit in edits)
                    {
                        if (edit.WorkArea.IsEmpty || projected.FirstOrDefault(w => w.Handle == edit.WindowId) is not { } predicted)
                            continue;
                        var r = predicted.VisualRect;
                        horizontal += (Math.Abs(r.X + r.Width / 2d - edit.End.X - edit.End.Width / 2d) +
                            .5 * Math.Abs(r.Width - edit.End.Width)) / edit.WorkArea.Width;
                        vertical += (Math.Abs(r.Y + r.Height / 2d - edit.End.Y - edit.End.Height / 2d) +
                            .5 * Math.Abs(r.Height - edit.End.Height)) / edit.WorkArea.Height;
                        matched++;
                    }
                    if (matched > 0)
                    {
                        runRegret = Math.Round(horizontal / matched, 4);
                        verticalError = Math.Round(vertical / matched, 4);
                        if (assumeResizable) { correctedRuns++; totalNormalizedRegret += horizontal; regretSamples += matched; }
                    }
                }
                Console.WriteLine(JsonSerializer.Serialize(new { run.RequestId, run.Trigger, assumeResizable, frame.Truncated,
                    PostArrangeRegret = runRegret, PostArrangeVerticalError = verticalError, ProtectedRegionsPreserved = regionsPreserved,
                    Moves = plan.Moves.Select(m => new { Id = (int)m.Window.Handle, Before = m.Window.VisualRect, After = m.TargetVisualRect }),
                    Selected = plan.Groups.Select(g => g.Selected) }));
            }
        }
    }
    Console.WriteLine($"Replayed {seen.Count} distinct requests ({correctedRuns} with post-arrange corrections, mean normalized regret={(regretSamples > 0 ? (totalNormalizedRegret / regretSamples).ToString("F4") : "n/a")}).");
}
