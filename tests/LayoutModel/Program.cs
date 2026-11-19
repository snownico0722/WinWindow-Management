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

Check(ActivityHints.Classify(false, 1, .5, 0, true, null, true, uniqueMediaWindow: true) == "audible-media-session" &&
    ActivityHints.Classify(false, 1, .5, 0, true, null, true, uniqueMediaWindow: false) == "visible-background",
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

if (args.Length > 0) Replay(args[0], args.ElementAtOrDefault(1));

static void Replay(string path, string? rulesPath)
{
    CompiledLayoutRules? overrideRules = null;
    if (rulesPath != null)
    {
        overrideRules = JsonSerializer.Deserialize<CompiledLayoutRules>(File.ReadAllText(rulesPath), LayoutRuleFile.Json);
        if (overrideRules?.IsValid() != true) throw new InvalidDataException("Invalid candidate layout rules.");
    }
    using var archive = ZipFile.OpenRead(path);
    var seen = new HashSet<(string, string)>();
    var analyzer = new VisibilityAnalyzer();
    foreach (var entry in archive.Entries.Where(e => e.Name.StartsWith("plans-") && e.Name.EndsWith(".jsonl")))
    {
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            var run = JsonSerializer.Deserialize<LayoutRunObservation>(line);
            if (run is not { Trigger: "explicit", Actual: not null } || !seen.Add((run.Session, run.RequestId))) continue;
            var frame = run.Before;
            var areas = frame.Windows.Select(w => w.WorkArea).Distinct().ToArray();
            foreach (bool assumeResizable in new[] { true, false })
            {
                var desktop = frame.Windows.OrderBy(w => w.ZOrder).Select(w => new WindowSnapshot(w.Id, w.Rect, w.Rect,
                    w.WorkArea, Array.IndexOf(areas, w.WorkArea) + 1, default, w.Resizable ?? (assumeResizable && w.Manageable),
                    w.Foreground, w.Manageable, w.ZOrder, w.Dpi, IsTopmost: w.Topmost)).ToArray();
                var displays = areas.Select((a, i) => new TaskDisplay(i + 1, a,
                    frame.Displays?.FirstOrDefault(d => d.WorkArea == a)?.Bounds ?? a)).ToArray();
                var focus = desktop.FirstOrDefault(w => w.IsForeground);
                var ctx = new TaskLayoutContext(Displays: displays, ExplicitArrange: true, PreferReversibleVerticalFill: true,
                    Focus: new(focus?.Handle ?? 0, 0, 0));
                var activeRules = overrideRules ?? run.Rules ?? CompiledLayoutRules.Default;
                ctx = ctx with { Rules = activeRules, RulePairs = activeRules.Bind(desktop.Zip(frame.Windows.OrderBy(w => w.ZOrder),
                    (window, observed) => (window, observed.AppKey))) };
                ctx = run.Signals?.ApplyToReplay(ctx) ?? ctx;
                var plan = LayoutDispatch.Create(LayoutRoute.ManualAlgorithm, analyzer.SelectForManualArrange(desktop, true),
                    new(), null, desktop, ctx);
                Console.WriteLine(JsonSerializer.Serialize(new { run.RequestId, assumeResizable, frame.Truncated,
                    Moves = plan.Moves.Select(m => new { Id = (int)m.Window.Handle, Before = m.Window.VisualRect, After = m.TargetVisualRect }),
                    Selected = plan.Groups.Select(g => g.Selected) }));
            }
        }
    }
    Console.WriteLine($"Replayed {seen.Count} distinct explicit requests.");
}
