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
    reopened.Clear();
    Check(reopened.Key("private identity", false) == null && reopened.Read().Length == 0, "clear forgets identities and examples");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

if (args.Length == 1) Replay(args[0]);

static void Replay(string path)
{
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
