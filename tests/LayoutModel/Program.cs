using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NeatWin.Core;
using NeatWin.Reference;

var area = new RectI(0, 0, 1920, 1040);
PlacementShadowChecks.Run(Check);
SceneModelChecks.Run(Check);
AttentionWorkspaceChecks.Run(Check);
VideoFitChecks.Run(Check);
ReadingFocusChecks.Run(Check);
OpeningPlacementChecks.Run(Check);
TextViewChecks.Run(Check);
NavigationChecks.Run(Check);
PlacementPreferenceChecks.Run(Check);
GeneralLayoutChecks.Run(Check);
GesturePreviewChecks.Run(Check);
var home = new RectI(560, 100, 810, 700);
WindowSnapshot Window(int id, RectI rect) => new(id, rect, rect, area, 1, default, true, id == 1, true, id - 1, 96, (uint)id);
WindowSnapshot[] windows = [Window(1, home), Window(2, home with { X = 600 })];
var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
}

{
    var scheduler = new AutomaticLayoutScheduler();
    scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, windows, 0);
    scheduler.RequestCurrent(10);
    Check(scheduler.Observe(windows, 500, busy: false, desktopFocused: true) is null && !scheduler.HasPending,
        "focusing the desktop cancels queued app layout even with the mouse released");
    var returned = windows.Select(w => w with { IsForeground = w.Handle == 2 }).ToArray();
    scheduler.Observe(returned, 700, busy: true);
    var held = scheduler.Observe(returned, 3000, busy: true);
    var justReleased = scheduler.Observe(returned, 3010, busy: false);
    var settled = scheduler.Observe(returned, 3550, busy: false);
    Check(held is null && justReleased is null && settled?.Trigger == LayoutTrigger.WorkspaceChange &&
        scheduler.Observe(returned, 5000, false) is null,
        "returning from desktop during a file drag waits for drop and settling, then runs one fresh pass");
}

{
    var focusPath = new LayoutFocus(30303, 20202, 1, "handoff", [10101, 20202],
        [new(10101, 20202, "click-open", 2400), new(20202, 30303, "owner-open", 350)]);
    var task = new TaskLayoutContext(Focus: focusPath) { NavigationObservedAt = now };
    var navigationIds = new Dictionary<nint, int> { [10101] = 1, [20202] = 2, [30303] = 3 };
    string json = JsonSerializer.Serialize(RecordedTaskSignals.From(task, navigationIds));
    var recorded = JsonSerializer.Deserialize<RecordedTaskSignals>(json)!;
    var replayedNavigation = recorded.ApplyToReplay(new());
    Check(recorded.Navigation?.ObservedAt == now && recorded.Navigation.Hops is
            [{ Source: 1, Target: 2, Evidence: "click-open", AgeMilliseconds: 2400, SourceRole: "waiting" },
             { Source: 2, Target: 3, Evidence: "owner-open", AgeMilliseconds: 350, SourceRole: "return" }] &&
        replayedNavigation.Focus?.NavigationAncestors?.SequenceEqual([(nint)1, (nint)2]) == true &&
        replayedNavigation.Focus.Navigation?[1].Target == 3 && !json.Contains("10101") && !json.Contains("30303"),
        "export and replay preserve each navigation hop, anonymous endpoints, roles and event age");
    navigationIds.Remove(20202);
    Check(RecordedTaskSignals.From(task, navigationIds).Navigation == null &&
        (recorded with { Navigation = null }).ApplyToReplay(new()).Focus?.Navigation == null,
        "missing or legacy navigation evidence cannot form a synthetic source-to-leaf link");
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

{
    Check(WindowUseRoles.Identify("msedge.exe", "Chrome_WidgetWin_1") == WindowUseRole.Browser &&
        WindowUseRoles.Identify("client.exe", "Chrome_WidgetWin_1") == WindowUseRole.Unknown &&
        WindowUseRoles.Identify("explorer.exe", "CabinetWClass") == WindowUseRole.FileManager &&
        WindowUseRoles.Identify("explorer.exe", "Progman") == WindowUseRole.Unknown &&
        WindowUseRoles.Identify("typora.exe", "#32770") == WindowUseRole.Unknown,
        "common application evidence distinguishes browser, embedded Chromium, files and shell dialogs");
    var work = new RectI(0, 0, 2560, 1380);
    var note = Window(1, new(830, 433, 1185, 841)) with { WorkArea = work, Dpi = 120 };
    var sourceWindow = Window(2, new(0, 95, 1462, 1114)) with { WorkArea = work, Dpi = 120 };
    var noted = WindowUseRoles.Bind([note, sourceWindow], [], new(1, 2, 1, "handoff"), null, null,
        w => w.Handle == 1 ? WindowUseRoles.Identify("Notepad.exe", "Notepad") : WindowUseRole.Browser);
    var roleSignals = RecordedTaskSignals.From(new(WindowHints: noted), new Dictionary<nint, int> { [1] = 1, [2] = 2 });
    var replayNote = roleSignals.ApplyToReplay(new()).WindowHints!.Single(h => h.Handle == 1);
    Check(replayNote.Role == WindowUseRole.Editor && replayNote.FloatingAffinity is > 0 and < .5 &&
        replayNote.FloatingAffinity == noted.Single(h => h.Handle == 1).FloatingAffinity,
        "a known editor supplies a weak, replayable prior for a recent compact overlay");

    var overlay = Window(1, new(235, 130, 1076, 668));
    var reader = Window(2, area);
    var handoff = new LayoutFocus(1, 2, 1, "handoff");
    var typing = new LayoutAttention(1, .7, .7, 0, true);
    double Floating(WindowSnapshot target, WindowUseRole role, LayoutAttention use, LayoutFocus? focus = null,
        PlacementPreference? habit = null, WindowSnapshot? material = null) =>
        WindowUseRoles.FloatingAffinity(role, target, [target, material ?? reader], focus ?? handoff,
            new Dictionary<nint, LayoutAttention> { [1] = use }, habit == null ? null : [habit]);
    double known = Floating(overlay, WindowUseRole.Editor, typing);
    double generic = Floating(overlay, WindowUseRole.Unknown, typing);
    var noSourceHistory = new LayoutFocus(1, 0, 0);
    Check(Floating(overlay, WindowUseRole.Unknown, typing, noSourceHistory) > 0 &&
        Floating(overlay, WindowUseRole.Unknown, typing, noSourceHistory) == Floating(overlay, WindowUseRole.Editor, typing, noSourceHistory),
        "an application name alone cannot change overlay evidence when the underlying source lacks task history");
    Check(generic > 0 && known - generic is > 0 and < .06 &&
        Math.Abs(known - Floating(overlay with { VisualRect = overlay.VisualRect with { Width = 1077 } }, WindowUseRole.Editor, typing)) < .01 &&
        Math.Abs(generic - Floating(overlay with { VisualRect = overlay.VisualRect with { Width = 1077 } }, WindowUseRole.Unknown, typing)) < .01,
        "a one-pixel footprint change has a continuous effect for recognized and unknown apps");
    var compact = Window(1, new(235, 130, 620, 420));
    double placed = Floating(compact, WindowUseRole.Unknown, typing with { ManualFloatPreference = 1 });
    var floatingBrowser = WindowUseRoles.Bind([compact with { Owner = 2 }, reader], [], handoff, null, null,
        _ => WindowUseRole.Browser).Single(h => h.Handle == 1);
    Check(placed == 1 && Floating(compact, WindowUseRole.Unknown, typing with { SustainedForeground = 1, ManualFloatPreference = 1 }) == 1 &&
        Floating(compact with { Owner = 2 }, WindowUseRole.Unknown, typing) == 1 &&
        floatingBrowser is { Role: WindowUseRole.Browser, FloatingAffinity: 1 } &&
        Floating(compact, WindowUseRole.Unknown, typing, material: reader with { VisualRect = new(920, 0, 800, 700) }) == 0 &&
        Floating(compact, WindowUseRole.Unknown, typing, material: reader with { IsManageable = false }) == 0,
        "deliberate placement or a small true owned tool over accessible material gives unknown apps floating intent");
    Check(Floating(compact, WindowUseRole.Editor, typing with { ManualFloatPreference = -1 }) == 0 &&
        Floating(compact, WindowUseRole.Editor, typing with { SustainedForeground = 1 }) == 0 &&
        Floating(compact, WindowUseRole.Editor, typing, new(1, 2, 4)) == 0 &&
        Floating(compact, WindowUseRole.Editor, typing, habit: new(1, 2, .5, .8, 0, -1, .8)) == 0 &&
        Floating(compact, WindowUseRole.Editor, typing, habit: new(1, 2, .5, .8, 0, -1, .8, PreferWidth: false)) > 0 &&
        Floating(overlay with { VisualRect = area }, WindowUseRole.Editor, typing) == 0,
        "manual expansion, sustained reading, recurring co-use, or a learned large size supersedes floating intent");
    var rear = compact with { IsForeground = false, ZOrder = 2 };
    var front = reader with { IsForeground = true, ZOrder = 0 };
    Check(Floating(rear, WindowUseRole.Editor, typing with { Foreground = false }, new(2, 0, 0), material: front) > 0 &&
        Floating(rear, WindowUseRole.Editor, new(.1, 1, 0, 0, false), new(2, 0, 0), material: front) == 0,
        "only recent editing supports a background overlay during a return to the source");

    var manualHistory = new ForegroundHistory();
    manualHistory.Visit(1, 1, 1000);
    var drag = new ManualWindowGesture(1, compact.VisualRect with { X = 800 }, compact.VisualRect, area,
        ManualGestureKind.Move, [], 450, 0);
    manualHistory.Placed(compact, drag, 2000);
    double Manual(WindowSnapshot w, long tick)
    {
        manualHistory.Resolve([w, reader], tick, out var signals);
        return signals.GetValueOrDefault(w.Handle)?.ManualFloatPreference ?? 0;
    }
    var secondTool = Window(2, compact.VisualRect with { X = 950 });
    manualHistory.Placed(secondTool, drag with { WindowHandle = 2, EndRect = secondTool.VisualRect }, 2050);
    manualHistory.Resolve([compact, secondTool], 2100, out var twoReleases);
    Check(twoReleases.Count(pair => pair.Value.ManualFloatPreference == 1) == 2,
        "placing a second tool preserves each window's own recent manual gesture");
    var docked = compact with { VisualRect = compact.VisualRect with { X = 0 } };
    Check(Manual(compact, 2100) == 1 && Manual(compact, 26000) is > 0 and < 1 &&
        Manual(compact, 39000) == 0 && Manual(compact with { ProcessId = 9 }, 2100) == 0,
        "a true interior manual release fades, expires and cannot transfer to a reused handle");
    manualHistory.Placed(compact, drag with { StartRect = compact.VisualRect with { X = compact.VisualRect.X + 1 } }, 40000);
    Check(Manual(compact, 40100) is > 0 and < .05, "a one-pixel drag supplies proportionally weak placement evidence");
    manualHistory.Placed(docked, drag with { StartRect = compact.VisualRect, EndRect = docked.VisualRect }, 40500);
    Check(Manual(docked, 41000) == -1, "a subsequent manual docking replaces the earlier overlay intent");
}
{
    using var memory = new MemoryStream();
    using (var captureZip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
    {
        using (var writer = new StreamWriter(captureZip.CreateEntry("layout-decisions.jsonl").Open()))
            writer.WriteLine("""{"Session":"A","RequestId":"1"}""");
        using var timeline = new StreamWriter(captureZip.CreateEntry("timeline.jsonl").Open());
        timeline.WriteLine(JsonSerializer.Serialize(new { Start = now }));
        timeline.WriteLine(JsonSerializer.Serialize(new { ElapsedMs = 0, Windows = new[] {
            new { App = "msedge.exe", Class = "Chrome_WidgetWin_1", Frame = (RectI?)home, Dpi = (uint?)96,
                Visible = true, Minimized = false, Cloaked = false, Foreground = true },
            new { App = "", Class = "", Frame = (RectI?)null, Dpi = (uint?)null,
                Visible = false, Minimized = false, Cloaked = false, Foreground = false } } }));
    }
    memory.Position = 0;
    using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
    var roles = new SnapshotRoles(archive);
    Check(roles.Enrich("A", "1", now, windows, new()).Any(h => h.Role == WindowUseRole.Browser) &&
        roles.Enrich("B", "1", now, windows, new()).Length == 0 &&
        roles.Enrich("A", "2", now, windows, new()).Length == 0 &&
        roles.Enrich("A", "1", now.AddSeconds(-1), windows, new()).Length == 0,
        "legacy role enrichment requires the same capture session and request, a prior sample and matching geometry");
}
var tracker = new ObservationIdentityTracker();
WorkspaceFrame Frame(WindowSnapshot[] items) => tracker.Capture(items, [new(area, area)], w => "app-" + w.Handle);
var start = Frame(windows);
var left = home with { X = 0 };
var moved = Frame([windows[0] with { VisualRect = left }, windows[1]]);
var gesture = new WindowAdjustmentObservation(now, "session", 1, home, left, area, area, 96, 900, [], "stable",
    3, start, moved, moved, "arrange-request");
{
    WorkspaceFrame SameBrowser(WorkspaceFrame f) => f with
        { Windows = f.Windows.Select(w => w with { AppKey = "browser" }).ToArray() };
    var shared = gesture with { StartContext = SameBrowser(start), EndContext = SameBrowser(moved),
        SettledContext = SameBrowser(moved), AfterRequest = null };
    var release = shared with { Outcome = "manual-release", SettledContext = null };
    var firstVote = LayoutHabits.Observe(release, byInstance: true)!;
    var repeat = firstVote with { Time = now.AddMinutes(3) };
    LayoutHabit[] votes = [firstVote, repeat];
    var live = LayoutHabits.Resolve(votes, windows[0], "1", "2", 2, now.AddMinutes(4), sessionOnly: true);
    var mirror = LayoutHabits.Resolve(votes, windows[1], "2", "1", 1, now.AddMinutes(4), sessionOnly: true);
    Check(LayoutHabits.Observe(shared) == null && firstVote is { App: "1", Partner: "2", Order: -1 } &&
        live is { Order: -1, Confidence: >= .4 } && mirror is { Order: 1, Confidence: >= .4 },
        "same-app documents learn separate session sides from deliberate releases, without polluting app-wide history");
    Check(LayoutHabits.Observe(shared with { Outcome = "automation" }, byInstance: true) == null &&
        LayoutHabits.Resolve(votes, windows[0], "1", null, 0, now.AddMinutes(4), sessionOnly: true) == null &&
        LayoutHabits.Resolve(votes, windows[0], "1", "2", 2, now.AddHours(3), sessionOnly: true) == null &&
        LayoutHabits.Resolve([firstVote, repeat with { Time = now.AddSeconds(25) }], windows[0], "1", "2", 2,
            now.AddSeconds(30), sessionOnly: true) == null,
        "assisted frames, rapid tweaks, absent partners and stale sessions cannot recreate a paired placement");
}
{
    var resized = left with { Width = left.Width + 60 };
    var final = resized with { X = home.X + 45 };
    var width = gesture with { Start = left, End = resized, StartContext = moved,
        EndContext = Frame([windows[0] with { VisualRect = resized }, windows[1]]) };
    var position = gesture with { Start = resized, End = final, Outcome = "assisted-release",
        StartContext = width.EndContext, EndContext = Frame([windows[0] with { VisualRect = final }, windows[1]]) };
    var axes = LayoutHabits.Coalesce([gesture, width, position]);
    Check(axes.SequenceEqual([HabitAxes.None, HabitAxes.Width, HabitAxes.Position]) &&
        LayoutHabits.Observe(position, false, axes[2]) is { Edited: HorizontalEdit.Position } vote &&
        Math.Abs(vote.Center - (final.Left + final.Width / 2d - area.Left) / area.Width) < .001,
        "rapid same-window edits retain the final deliberate position and independent width");
    var restoredPosition = left with { X = home.X };
    var returnToStart = gesture with { Start = left, End = restoredPosition };
    Check(LayoutHabits.Coalesce([gesture, returnToStart]).All(a => a == HabitAxes.None),
        "an exploratory move returning to its starting position contributes no session or persistent vote");

    var reversal = gesture with { PriorPlacement = new("verified", 950, left, home) };
    var unrelatedResize = left with { Height = left.Height - 90 };
    var mixed = reversal with { End = unrelatedResize,
        SettledContext = Frame([windows[0] with { VisualRect = unrelatedResize }, windows[1]]) };
    var afterReversal = position with { Start = left, End = left with { X = 24 } };
    Check(LayoutHabits.Coalesce([reversal, afterReversal]).All(a => a == HabitAxes.None) &&
        LayoutHabits.Observe(reversal) == null &&
        LayoutHabits.Observe(reversal with { Outcome = "manual-release" }, byInstance: true) == null &&
        LayoutHabits.Observe(mixed) is { Edited: HorizontalEdit.None, Height: not null } &&
        LayoutHabits.Observe(reversal with { PriorPlacement = reversal.PriorPlacement! with { ElapsedMs = 31_000 } }) != null,
        "a linked reversal rejects automatic placement without teaching an endpoint, while deliberate independent axes can still learn");
}
var edit = LayoutHabits.Observe(gesture);
Check(edit is { Partner: "app-2", Order: -1, Corrected: true }, "a stable manual correction learns the left/right pair");
Check(LayoutHabits.Observe(gesture with { Outcome = "automation" }) == null &&
    LayoutHabits.Observe(gesture with { SettledContext = null }) == null, "automated and legacy incomplete gestures are excluded");
{
    var observed = LayoutHabits.Observe(gesture)!;
    var positionVotes = Enumerable.Range(0, 12).Select(i => observed with { Time = now.AddHours(i) }).ToArray();
    PlacementPreference? Learned(IEnumerable<LayoutHabit> source) =>
        LayoutHabits.Resolve(source, windows[0], "app-1", "app-2", 2, now.AddHours(13));
    Check(Learned(positionVotes) is { PreferCenter: true, PreferWidth: false } &&
        LayoutHabits.Resolve(positionVotes, windows[1], "app-2", "app-1", 1, now.AddHours(13)) is { PreferWidth: false },
        "moving a window never learns an incidental width for it or its stationary companion");
    var resizes = positionVotes.Select(s => s with { Edited = HorizontalEdit.Width, Width = .3 }).ToArray();
    var widthOnly = Learned(resizes)!;
    var recorded = RecordedTaskSignals.From(new(Placement: [widthOnly]), new Dictionary<nint, int> { [1] = 1 });
    Check(widthOnly is { PreferWidth: true, PreferCenter: false, Width: > .299 and < .301 } &&
        recorded.ApplyToReplay(new()).Placement!.Single() is { PreferWidth: true, PreferCenter: false } &&
        Learned(resizes.Select((s, i) => s with { Width = i % 2 == 0 ? .30 : .80 })) == null &&
        Learned(positionVotes.Select((s, i) => s with { Center = i % 2 == 0 ? .35 : .65 })) == null,
        "only a repeated selected axis becomes a preference; incompatible uses do not create a fictitious intermediate position or width");
    Check(Learned(positionVotes.Select(s => s with { Edited = null })) is { PreferCenter: true, PreferWidth: false } &&
        Learned(positionVotes.Select(s => s with { Corrected = false })) == Learned(positionVotes),
        "legacy observations inform placement; a gesture near an earlier plan has no extra voting power by timing alone");
    var sideRule = new CompiledLayoutRules(AppSlots: [new("browser", "right")]).BindSlots([(windows[0], "browser")]).Single();
    var sizeRule = new CompiledLayoutRules(AppSlots: [new("browser", Width: .35)]).BindSlots([(windows[0], "browser")]).Single();
    Check(sideRule is { PreferCenter: true, PreferWidth: false } && sizeRule is { PreferCenter: false, PreferWidth: true } &&
        CompiledLayoutRules.ApplySlots([widthOnly], [sideRule]) is [{ PreferCenter: true, PreferWidth: true, Width: > .299 and < .301, Center: > .55 }],
        "an imported side or width changes only its requested dimension and keeps independent personal choices");
    var assisted = gesture with { Outcome = "assisted-release", SettledContext = start };
    var assistedVote = LayoutHabits.Observe(assisted);
    Check(assistedVote == edit && LayoutHabits.Observe(assisted with { Outcome = "changed-after-release" }) == null,
        "verified assistance learns the original hand-picked endpoint, never the solver's final rectangle");
    var chosen = left with { Height = area.Height / 2 };
    var settled = Frame([windows[0] with { VisualRect = chosen }, windows[1]]);
    var resize = LayoutHabits.Observe(gesture with { End = chosen, SettledContext = settled })!;
    var positionOnly = JsonSerializer.Deserialize<LayoutHabit>(JsonSerializer.Serialize(edit!))!;
    var votes = Array.Empty<LayoutHabit>();
    for (int hour = 0; hour < 6; hour++)
    {
        votes = LayoutHabits.Add(votes, resize with { Time = now.AddHours(hour) });
        votes = LayoutHabits.Add(votes, positionOnly with { Time = now.AddHours(hour).AddMinutes(1) });
    }
    var preferred = LayoutHabits.Resolve(votes, windows[0], "app-1", "app-2", 2, now.AddHours(7))!;
    var legacy = LayoutHabits.Resolve(votes.Select(h => h with { Height = null }), windows[0], "app-1", "app-2", 2, now.AddHours(7))!;
    Check(votes.Length == 6 && positionOnly.Height is null && preferred.Height == .5 && legacy.Height is null &&
        LayoutHabits.Resolve(votes.Take(2), windows[0], "app-1", "app-2", 2, now.AddHours(7)) is null,
        "only independent manual vertical resizes teach height; legacy and move-only samples retain defaults");
    var ctx = new TaskLayoutContext(Focus: new(1, 0, 0), Attention: new Dictionary<nint, LayoutAttention>
        { [1] = new(1, 1, 1, 0, true) }, Placement: [preferred]);
    var saved = RecordedTaskSignals.From(ctx, new Dictionary<nint, int> { [1] = 1, [2] = 2 });
    var heightReplay = JsonSerializer.Deserialize<RecordedTaskSignals>(JsonSerializer.Serialize(saved))!.ApplyToReplay(new());
    var visible = new VisibleWindow(windows[0], home.Area, home.Area, 1);
    var target = AttentionLayout.Targets([visible], heightReplay, SmartTidyStrength.Assertive)![0];
    Check(target.LearnedHeight && target.Height == chosen.Height &&
        !AttentionLayout.Targets([visible], ctx with { Placement = [legacy] }, SmartTidyStrength.Assertive)![0].LearnedHeight,
        "attention target honors a normalized learned height in replay and still handles older records");
    foreach (int startingHeight in new[] { 350, 710 })
    {
        var w = Window(1, home with { Height = startingHeight });
        var plan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange([w], true),
            new(SmartStrength: SmartTidyStrength.Assertive), null, [w], heightReplay with { UseWorkingPairs = true });
        Check(plan.Moves.Single().TargetVisualRect.Height == chosen.Height,
            $"Smart restores the independently learned height from {startingHeight}px");
    }
}
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
var auxAccess = OcclusionMetrics.Measure(Placed(threePlan, auxThird),
    [Placed(threePlan, topPairA), Placed(threePlan, topPairB)], auxThird.Dpi);
Check(auxAccess.AccessWidth >= 140 * auxThird.Dpi / 96d &&
    Placed(threePlan, topPairA).HorizontalOverlapRatio(Placed(threePlan, topPairB)) is >= .20 and <= .40,
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

{
    var window = Window(1, new(360, 120, 580, 620));
    var move = new TidyMove(window, window.VisualRect with { X = 230, Width = 720 });
    var feedback = new WindowPlacementFeedback();
    var hint = new TaskWindowHint(window.Handle, UsefulWidthDip: 760);
    var changed = window with { VisualRect = move.TargetVisualRect };
    Check(!feedback.BlockUnchanged(move, changed, 0) && feedback.BlockUnchanged(move, window, 0) &&
        !feedback.BlockUnchanged(move, window, 500) &&
        feedback.Bind([window], [hint], 1000).Single() == (hint with { Anchor = true }),
        "only an unchanged refused target creates one temporary anchor and preserves content hints");
    Check(feedback.Bind([changed], [hint], 1100).Single() == hint &&
        feedback.BlockUnchanged(move, window, 2000) && feedback.Bind([window], [], 17001).Length == 0 &&
        feedback.BlockUnchanged(move, window, 18000) &&
        feedback.Bind([window with { ProcessId = window.ProcessId + 1 }], [], 18001).Length == 0,
        "placement feedback expires when the window moves, its identity changes, or the retry interval elapses");

    var fixedMain = Window(1, new(1270, 90, 700, 760));
    var peer = Window(2, new(1230, 130, 580, 580));
    WindowSnapshot[] desktop = [fixedMain, peer];
    var hintFeedback = new WindowPlacementFeedback();
    hintFeedback.BlockUnchanged(new(fixedMain, fixedMain.VisualRect with { X = 1100 }), fixedMain, 0);
    var retryContext = new TaskLayoutContext(ExplicitArrange: true, Focus: new(1, 2, 4), UseWorkingPairs: true,
        WindowHints: hintFeedback.Bind(desktop, [], 1000), Attention: new Dictionary<nint, LayoutAttention>
        { [1] = new(1, 1, 1, 0, true), [2] = new(.8, .8, .8, .9, false) });
    var alternative = LayoutDispatch.Create(LayoutRoute.ManualAlgorithm,
        new VisibilityAnalyzer().SelectForManualArrange(desktop, true), new(SmartStrength: SmartTidyStrength.Assertive),
        null, desktop, retryContext);
    Check(alternative.Moves.Any(m => m.Window.Handle == peer.Handle) &&
        alternative.Moves.All(m => m.Window.Handle != fixedMain.Handle),
        "manual Smart exposes a companion around the verified stationary foreground even when its edge is off-screen");
}

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
    var current = browsingDesktop.OrderByDescending(w => w.Handle == active)
        .Select((w, rank) => w with { IsForeground = w.Handle == active, ZOrder = rank }).ToArray();
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

var spaciousPair = new[]
{
    AttentionWindow(1, new(119, 227, 627, 725)),
    AttentionWindow(2, new(756, 246, 1249, 888)),
    AttentionWindow(3, focusArea) with { IsManageable = false, IsResizable = false },
};
var pairVisible = new VisibilityAnalyzer().SelectForManualArrange(spaciousPair, true);
IntentLayoutPlan GrowWorkingPair(TaskLayoutContext c, SmartTidyStrength strength) =>
    LayoutDispatch.Create(LayoutRoute.Smart, pairVisible, new(SmartStrength: strength), null, spaciousPair, c);
var growthPlan = GrowWorkingPair(workingContext, SmartTidyStrength.Assertive);
var grownMain = Placed(growthPlan, spaciousPair[0]);
var grownPeer = Placed(growthPlan, spaciousPair[1]);
var visiblePairArea = grownMain.Area + grownPeer.Area - grownMain.Intersect(grownPeer).Area;
Check(growthPlan.Moves.Count == 2 && grownMain.Width > spaciousPair[0].VisualRect.Width &&
    grownMain.Height > spaciousPair[0].VisualRect.Height && grownMain.Left < grownPeer.Left &&
    grownMain.Intersect(focusArea) == grownMain && grownPeer.Intersect(focusArea) == grownPeer &&
    OcclusionMetrics.Measure(grownPeer, [grownMain], 120).UsefulWidth >= spaciousPair[1].VisualRect.Width * .8 &&
    visiblePairArea > spaciousPair[0].VisualRect.Area + spaciousPair[1].VisualRect.Area,
    "assertive Smart makes the focal window larger and increases the total readable area with the reference still in reach");
var restrained = GrowWorkingPair(workingContext, SmartTidyStrength.Balanced);
var gentlePair = GrowWorkingPair(workingContext, SmartTidyStrength.Gentle);
Check(Placed(gentlePair, spaciousPair[0]).Width < Placed(restrained, spaciousPair[0]).Width &&
    Placed(restrained, spaciousPair[0]).Width < grownMain.Width,
    "from the same narrow starting frame, each stronger mode grants more width to the focal window");
var growthOff = GrowWorkingPair(workingContext with { Profile = new(AllowUsefulResize: false) }, SmartTidyStrength.Assertive);
Check(growthOff.Moves.All(m => m.TargetVisualRect.Width == m.Window.VisualRect.Width && m.TargetVisualRect.Height == m.Window.VisualRect.Height) &&
    GrowWorkingPair(workingContext with { GestureAnchor = 1 }, SmartTidyStrength.Assertive).Moves.All(m => m.Window.Handle != 1),
    "working-pair growth respects the resizing option and a protected manual placement");

foreach (var display in new[] { new RectI(0, 0, 1920, 1080), new RectI(-2560, 0, 2560, 1080) })
{
    var work = display with { Height = display.Height - 40 };
    var narrowRect = new RectI(work.Left + 160, work.Top + 130, 480, 440);
    var conversation = Window(21, narrowRect) with
        { WorkArea = work, IsForeground = true, ZOrder = 0, ProcessId = (uint)Environment.ProcessId + 1 };
    var reader = conversation with { Handle = 22 };
    var visits = new ForegroundHistory();
    visits.Visit(conversation.Handle, conversation.ProcessId, 1000);

    IntentLayoutPlan Smart(WindowSnapshot[] desk, long tick, LayoutRoute route = LayoutRoute.Smart)
    {
        var current = visits.Resolve(desk, tick, out var attention);
        return LayoutDispatch.Create(route, new VisibilityAnalyzer().SelectForManualArrange(desk, true),
            new(), null, desk, new(UseWorkingPairs: true, ExplicitArrange: route == LayoutRoute.ManualAlgorithm,
                Displays: [new(1, work, display)], Focus: current, Attention: attention));
    }

    var firstPlan = Smart([conversation], 2300);
    var coldTarget = Placed(firstPlan, conversation);
    Check(coldTarget.Width >= narrowRect.Width + 96 && coldTarget.Intersect(work) == coldTarget &&
        Placed(Smart([conversation], 2300, LayoutRoute.ManualAlgorithm), conversation).Width >= narrowRect.Width + 96,
        $"cold-start automatic and manual Smart provide useful width to the only task on {display.Width}x1080");
    var uncovered = conversation with { IsForeground = false };
    var noFocus = new TaskLayoutContext(ExplicitArrange: true, Focus: new(0, 0, 0),
        Attention: new Dictionary<nint, LayoutAttention>());
    var fromWorkbench = LayoutDispatch.Create(LayoutRoute.ManualAlgorithm,
        new VisibilityAnalyzer().SelectForManualArrange([uncovered], true), new(), null, [uncovered], noFocus);
    Check(Placed(fromWorkbench, uncovered).Width >= narrowRect.Width + 96,
        $"an explicit arrange after foreground history expires still sizes the front external window on {display.Width}x1080");
    var comfortable = conversation with { VisualRect = coldTarget, OuterRect = coldTarget };
    Check(Smart([comfortable], 2350).Moves.Count == 0,
        $"a repeated solve keeps the comfortable foreground frame on {display.Width}x1080");

    visits.Interact(conversation.Handle, conversation.ProcessId, 2400);
    visits.Visit(reader.Handle, reader.ProcessId, 2500);
    visits.Interact(reader.Handle, reader.ProcessId, 3000);
    WindowSnapshot[] openedDesk = [reader, conversation with { IsForeground = false, ZOrder = 1 }];
    var openedPlan = Smart(openedDesk, 3100);
    var afterOpening = TaskLayoutSession.Project(openedDesk, openedPlan);
    var readingRect = afterOpening[0].VisualRect;
    var returnRect = afterOpening[1].VisualRect;
    var returnExposure = OcclusionMetrics.Measure(returnRect, [readingRect], conversation.Dpi);
    Check(readingRect.Width >= narrowRect.Width + 96 && returnExposure.AccessWidth >= 96 &&
        afterOpening.All(w => w.VisualRect.Intersect(work) == w.VisualRect) && afterOpening[0].ZOrder < afterOpening[1].ZOrder,
        $"first same-process handoff expands the reader and uncovers a clickable return target on {display.Width}x1080");
    var repeat = Smart(afterOpening, 3150);
    Check(repeat.Moves.Count == 0 && repeat.Layers.Count == 0,
        $"the same handoff stays settled when another workspace event arrives on {display.Width}x1080");

    visits.Retire(reader.Handle);
    visits.Visit(conversation.Handle, conversation.ProcessId, 7000);
    var returned = conversation with { VisualRect = returnRect, OuterRect = returnRect };
    var resumed = Placed(Smart([returned], 8000), returned);
    Check(resumed.Width >= coldTarget.Width - 8 && resumed.Intersect(work) == resumed,
        $"closing the reader returns useful width to the conversation on {display.Width}x1080");
}

var occludedReference = new[]
{
    Window(1, new(0, 180, 1000, 840)) with { IsResizable = false },
    Window(2, new(1080, 0, 650, 445)),
    Window(3, new(1030, 0, 890, 1040)) with { IsResizable = false },
};
var referenceHistory = new ForegroundHistory();
referenceHistory.Visit(1, 1, 1000);
referenceHistory.Visit(3, 3, 3000);
referenceHistory.Visit(1, 1, 5000);
var referenceFocus = referenceHistory.Resolve(occludedReference, 5800, out var referenceAttention);
var beforeReference = OcclusionMetrics.Measure(occludedReference[2].VisualRect,
    occludedReference.Take(2).Select(w => w.VisualRect), 96);
var revealReference = LayoutDispatch.Create(LayoutRoute.Smart,
    new VisibilityAnalyzer().SelectForManualArrange(occludedReference, true), new(), null, occludedReference,
    new(UseWorkingPairs: true, Focus: referenceFocus, Attention: referenceAttention));
var revealed = TaskLayoutSession.Project(occludedReference, revealReference);
var afterReference = OcclusionMetrics.Measure(revealed[2].VisualRect,
    revealed.Where(w => w.ZOrder < revealed[2].ZOrder).Select(w => w.VisualRect), 96);
Check(revealed[1].VisualRect != occludedReference[1].VisualRect && afterReference.Visible > beforeReference.Visible + .1 &&
    revealed.All(w => w.VisualRect.Intersect(area) == w.VisualRect),
    "automatic Smart moves an idle ordinary window that obstructs the active reference on a 16:9 display");

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

sequence.Reset();
BehaviorTick(0, [helper]);
BehaviorTick(2000, [helper with { Foreground = false }, nextTask]);
BehaviorTick(3000, [helper, nextTask with { Foreground = false }]);
BehaviorTick(4600, [helper with { Foreground = false }, nextTask]);
Check(BehaviorTick(10000, [helper with { Foreground = false }, nextTask]).Dismiss == null,
    "returning to the launcher invalidates the earlier fresh-process handoff");
foreach (var action in new[] { WindowBehaviorAction.Minimize, WindowBehaviorAction.Close })
{
    var traySequence = new WindowBehaviorSequence(discoveredRule.Rule with { Action = action });
    traySequence.Observe(0, BehaviorUtc(0), [helper], new Dictionary<int, BehaviorDeparture>(), fit, true, false);
    traySequence.Observe(2000, BehaviorUtc(2000), [helper with { Foreground = false }, nextTask], new Dictionary<int, BehaviorDeparture>(), fit, true, false);
    var disappearance = new Dictionary<int, BehaviorDeparture> { [helper.Id] = BehaviorDeparture.Hidden };
    var trayResult = traySequence.Observe(6500, BehaviorUtc(6500), [nextTask], disappearance, fit, true, false);
    Check(trayResult.Fit == null, $"a tray hide does not confirm an observed {action} action");
}
behaviorDiscovery.Reset();
behaviorDiscovery.Observe(0, BehaviorUtc(0), [helper], new Dictionary<int, BehaviorDeparture>(), new HashSet<int>());
behaviorDiscovery.Observe(2000, BehaviorUtc(2000), [helper with { Foreground = false }, nextTask],
    new Dictionary<int, BehaviorDeparture>(), new HashSet<int>());
behaviorDiscovery.Observe(5000, BehaviorUtc(5000), [nextTask],
    new Dictionary<int, BehaviorDeparture> { [helper.Id] = BehaviorDeparture.Hidden }, new HashSet<int>());
Check(behaviorDiscovery.Observe(16000, BehaviorUtc(16000), [nextTask],
    new Dictionary<int, BehaviorDeparture>(), new HashSet<int>()).Count == 0,
    "discovery records no inferred minimize or close example for a tray hide");

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

{
    var usage = new RecordedUsage();
    Check(usage.Sample(1_000, 1, true) == null, "first usage sample establishes a boundary");
    usage.Foreground(2_000, 2);
    usage.Input(2);
    usage.Foreground(2_400, 1);
    var interval = usage.Sample(6_000, 1, true)!;
    Check(interval.Foreground.SequenceEqual([new(1, 0, 1000), new(2, 1000, 400), new(1, 1400, 3600)]) &&
        interval.PointerActions is [{ Id: 2, Count: 1 }], "usage intervals retain sub-sample round trips and aggregate real pointer actions");
    var oldFrame = Frame([Window(1, home), Window(2, home with { X = 0 })]);
    var recordedIds = oldFrame.Windows.Select(w => w.Id).ToArray();
    var shared = RecordedUsage.CoUse(new([new(recordedIds[0], 0, 5000)], null), oldFrame, oldFrame);
    Check(shared is [{ ForegroundMsWithPeerVisibleAtBothEnds: 5000 }] &&
        RecordedUsage.CoUse(interval, oldFrame with { Truncated = true }, oldFrame) == null,
        "co-use joins stable visible peers and excludes truncated endpoint evidence");
    Check(usage.Sample(11_000, 2, true) == null && usage.Sample(22_000, 2, true) == null &&
        usage.Sample(26_000, 2, false)?.PointerActions == null,
        "missing foreground events, sampling gaps, and unavailable input keep unknown evidence distinct from zero");
    var targets = new[] { Window(1, new(0, 0, 400, 400)), Window(2, new(0, 0, 800, 400)) };
    var video = new VideoBlackBarHint(2, VideoBlackBarOrientation.Horizontal, 2, new(0, 0, 800, 400), .95, VideoBlackBarTendency.Expand);
    var data = RecordedTaskSignals.From(new(VideoHint: video) { BrowserBarSample = (2, new("confirmed-picture", 2, .95)) }, targets.ToDictionary(w => w.Handle, w => (int)w.Handle), targets);
    Check(data.Video?.Exposure?.Visible == .5 && data.ApplyToReplay(new()).VideoHint == video,
        "record and replay the detected video viewport independently of full-window exposure");
    Check(data.ApplyToReplay(new(), 9).VideoHint == null &&
        (data with { BrowserBars = null }).ApplyToReplay(new()).VideoHint == null &&
        (data with { BrowserBars = data.BrowserBars! with { WindowId = 1 } }).ApplyToReplay(new()).VideoHint == null &&
        (data with { BrowserBars = data.BrowserBars! with { Decision = "unconfirmed-picture" } }).ApplyToReplay(new()).VideoHint == null,
        "legacy bars, rejected picture evidence and a different window cannot authorize a replayed resize");
}

// A dormant window lies above a still-relevant reference, while the main window stays active.
{
    var idleTracker = new IdleWindowReclaim();
    var visits = new ForegroundHistory();
    var mainWindow = Window(101, new(0, 0, 780, 900)) with { IsForeground = true, ZOrder = 0 };
    var reference = Window(102, new(800, 30, 780, 800)) with { IsForeground = false, ZOrder = 2 };
    var dormant = Window(103, new(920, 100, 540, 500)) with { ProcessId = uint.MaxValue, IsForeground = false, ZOrder = 1 };
    WindowSnapshot[] desktop = [mainWindow, dormant, reference];
    var task = new TaskLayoutContext(Focus: new(101, 102, 3));
    IntentLayoutPlan empty = new([], [], []);
    WindowSnapshot? Pick(long tick, TaskLayoutContext? context = null, IntentLayoutPlan? plan = null,
        WindowSnapshot[]? current = null) =>
        idleTracker.Choose(current ?? desktop, plan ?? empty, context ?? task, SmartTidyStrength.Assertive,
            tick, w => (int)w.Handle, visits);
    Check(Pick(0) is null && Pick(121_000)?.Handle == dormant.Handle,
        "idle reclaim needs elapsed time and frees useful reference content");
    var headerCover = dormant with { VisualRect = dormant.VisualRect with { X = 1100, Y = 30 } };
    Check(Pick(122_000, task with { Focus = new(101, 102, 1, "handoff") },
        current: [mainWindow, headerCover, reference]) is null,
        "an exposed return target does not need additional reading area");
    visits.Interact(dormant.Handle, dormant.ProcessId, 123_000);
    Check(Pick(130_000) is null, "background input between layout rounds restarts the idle clock");
    visits.Visit(dormant.Handle, dormant.ProcessId, 200_000);
    visits.Visit(mainWindow.Handle, mainWindow.ProcessId, 209_000);
    Check(Pick(300_000) is null && Pick(330_000)?.Handle == dormant.Handle,
        "a completed return visit postpones reclaim for the full idle interval");
    var movedAside = new TidyMove(reference, reference.VisualRect with { Y = 700, Height = 320 });
    Check(Pick(331_000, plan: new([movedAside], [], [])) is null,
        "a layout that already frees the reference has no residual reclaim pressure");
    var fixedCover = dormant with { Handle = 104, IsManageable = false, IsTopmost = true, ZOrder = 0 };
    Check(Pick(332_000, current: [.. desktop, fixedCover]) is null &&
        Pick(333_000, task with { WindowHints = [new(dormant.Handle, PassiveVisual: true)] }) is null,
        "duplicate occlusion and passive monitoring do not justify minimization");
}

{
    var prior = new SubmittedPlacement(27, "recording-fixture", 1000, new(32, 50, 600, 480), new(480, 50, 900, 480));
    var late = prior.At(27, 91_000);
    var change = RecordedGeometryChange.From(prior.Submitted, new(34, 50, 750, 480), late);
    Check(late?.ElapsedMs == 90_000 && change is { Dx: -446, DWidth: -150, StartedAtSubmitted: true } &&
        change.ReturnedAxes!.SequenceEqual(["x"]) && prior.At(28, 91_000) == null && prior.At(27, 999) == null,
        "late hand adjustment preserves signed axes and a partial return only on the original window instance");
    var usage = new RecordedUsage();
    usage.Sample(100, 1, true);
    usage.Foreground(120, 2); usage.Input(2); usage.Foreground(145, 1);
    var preview = usage.Snapshot(160, 1, true, true);
    var gap = usage.Sample(10_000, 1, true, true, out var coverageGap);
    Check(preview.Status == "observed" && preview.Events.Select(e => e.Id).SequenceEqual([2, 1]) &&
        gap == null && coverageGap.Status == "sampling-gap" && coverageGap.Events.SequenceEqual(preview.Events) &&
        coverageGap.PointerActions?.Single().Count == 1, "export is non-consuming and a sampling gap retains a witnessed leave and return");
    for (int i = 0; i < 260; i++) usage.Foreground(10_001 + i, 2 + i % 2);
    var over = usage.Sample(10_500, 3, false, true, out var clipped);
    Check(over == null && clipped.OmittedEvents == 4 && clipped.Events.Length == 256 && clipped.PointerActions == null,
        "a clipped focus timeline reports the number omitted and never claims continuous dwell");
    using var bytes = new MemoryStream();
    using (var output = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    using (var writer = new StreamWriter(output.CreateEntry("gestures-fixture.jsonl").Open()))
    {
        var release = new GestureEvent(1, 27, "release", 91_100, new(34, 50, 750, 480), area, 96, false, late, Change: change);
        var trimmed = (release with { StartTick = 91_000, OuterPath = [new(10, 0, 0, 600, 480), new(80, 4, 4, 600, 480)] })
            .WithinLookback(91_040);
        Check(trimmed.Prior == null && trimmed.Change == null && trimmed.OuterPath is [{ OffsetMs: 80 }],
            "a snapshot beginning mid-gesture excludes earlier geometry and its historical layout");
        var batch = new GestureBatch(1, "fixture", now, [release with { Stage = "start" }, release], 0);
        writer.WriteLine(JsonSerializer.Serialize(batch));
    }
    bytes.Position = 0;
    using var input = new ZipArchive(bytes, ZipArchiveMode.Read);
    var releaseReplay = ReplayEffort.Read(input, "fixture");
    Check(releaseReplay.Starts == 1 && releaseReplay.CompletedReleases == 1 && releaseReplay.Total == null &&
        releaseReplay.Releases[("fixture", "recording-fixture")].Single().Prior == late,
        "replay links a raw late release while leaving confirmed manual-effort counts unknown");
}

if (args is ["--validate", var candidatePath])
{
    var candidate = ReadRules(candidatePath)!;
    Console.WriteLine(JsonSerializer.Serialize(new { candidate.Version, Scenes = candidate.Scenes?.Select(r => r.Id),
        SurfaceRules = candidate.SurfaceRules?.Select(r => r.Id), Pairs = candidate.Pairs?.Length ?? 0 }));
}
else if (args is ["--behavior", var zip, var behaviorRules, ..])
    BehaviorReplay.Run(zip, behaviorRules, args.ElementAtOrDefault(3));
else if (args.Length > 0)
    Replay(args[0], args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3));

static CompiledLayoutRules? ReadRules(string? path)
{
    if (path == null) return null;
    using var file = File.OpenRead(path);
    if (file.Length is <= 0 or > 65536) throw new InvalidDataException("Invalid layout rule file size.");
    var rules = JsonSerializer.Deserialize<CompiledLayoutRules>(file, LayoutRuleFile.Json);
    return rules?.IsValid() == true ? rules : throw new InvalidDataException("Invalid layout rules.");
}

static void Replay(string path, string? rulesPath, string? sessionFilter, string? evaluationPath)
{
    var overrideRules = ReadRules(rulesPath);
    var evaluation = ReadRules(evaluationPath);
    if (sessionFilter == "*") sessionFilter = null;
    using var archive = ZipFile.OpenRead(path);
    var snapshotRoles = new SnapshotRoles(archive);
    var edits = ReplayEffort.Read(archive, sessionFilter);
    var planEntries = archive.Entries.Where(e => e.Name == "layout-decisions.jsonl" ||
        e.Name.StartsWith("plans-") && e.Name.EndsWith(".jsonl")).ToArray();
    edits.ReadUndo(planEntries, sessionFilter);
    var seen = new HashSet<(string, string)>();
    var analyzer = new VisibilityAnalyzer();
    int correctedRuns = 0;
    double totalNormalizedRegret = 0;
    int regretSamples = 0;
    foreach (var entry in planEntries)
    {
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            var run = JsonSerializer.Deserialize<LayoutRunObservation>(line);
            // Only the before-frame and the evidence collected at request time feed the solver.
            // A snapshot may end before the last native request has been verified.
            if (run is not { Trigger: "explicit" or "automatic-gesture" or "automatic-workspace" or "automatic-opening" or "offline-activity-probe" } ||
                (sessionFilter is not null && run.Session != sessionFilter) || !seen.Add((run.Session, run.RequestId))) continue;
            var frame = run.Before;
            // Fingerprint only request-time inputs shared by A/B. Later frames and
            // gestures label the observation and never enter this prediction.
            var inputFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                new { run.Before, run.Trigger, run.Signals, run.Strength, run.FullDesktop })));
            var areas = frame.Windows.Select(w => w.WorkArea).Distinct().ToArray();
            bool explicitRun = run.Trigger == "explicit";
            bool localOpening = run.Trigger == "automatic-opening";
            if (localOpening && run.Signals is not { FirstPlacementWindow: > 0 }) continue;
            foreach (bool assumeResizable in new[] { true, false })
            {
                var desktop = frame.Windows.OrderBy(w => w.ZOrder).Select(w => new WindowSnapshot(w.Id, w.Rect, w.Rect,
                    w.WorkArea, Array.IndexOf(areas, w.WorkArea) + 1, default, w.Resizable ?? (assumeResizable && w.Manageable),
                    w.Foreground, w.Manageable && !w.LocalSurface, w.ZOrder, w.Dpi, IsTopmost: w.Topmost,
                    SurfaceState: w.LocalSurface ? null : w.SurfaceState) { Owner = w.OwnerId }).ToArray();
                var displays = areas.Select((a, i) => new TaskDisplay(i + 1, a,
                    frame.Displays?.FirstOrDefault(d => d.WorkArea == a)?.Bounds ?? a)).ToArray();
                var focus = desktop.FirstOrDefault(w => w.IsForeground);
                var ctx = new TaskLayoutContext(Displays: displays, ExplicitArrange: explicitRun, UseWorkingPairs: true,
                    PreferReversibleVerticalFill: true, Focus: new(focus?.Handle ?? 0, 0, 0));
                ctx = (run.Signals?.ApplyToReplay(ctx, run.Version) ?? ctx) with
                {
                    AllowGestureAssist = run.Trigger == "automatic-gesture",
                    // 活动快照探针只复用工作区求解，缺少运行时授权、注意力和触发置信度。
                    IsWorkspaceObservation = run.Trigger is "automatic-workspace" or "automatic-opening" or "offline-activity-probe",
                    LocalPlacementWindow = localOpening ? run.Signals!.FirstPlacementWindow : 0,
                };
                var activeRules = overrideRules ?? run.Rules ?? CompiledLayoutRules.Default;
                var keyed = desktop.Zip(frame.Windows.OrderBy(w => w.ZOrder), (window, observed) => (window, observed.AppKey)).ToArray();
                var observedPair = ctx.GestureAnchor != 0 && ctx.AllowGestureAssist && ctx.GestureAnchor != ctx.Focus?.Main ? null : ctx.Focus;
                var slotPrefs = activeRules.BindSlots(keyed, observedPair);
                var mergedPrefs = CompiledLayoutRules.ApplySlots(ctx.Placement ?? [], slotPrefs);
                ctx = ctx with { AppKeys = keyed.ToDictionary(k => k.window.Handle, k => k.AppKey), Rules = activeRules, RulePairs = activeRules.Bind(keyed, ctx.WindowHints, ctx.Activities, observedPair), Placement = mergedPrefs };
                ctx = ctx with { WindowHints = snapshotRoles.Enrich(run.Session, run.RequestId, frame.Time, desktop, ctx) };
                var fullDesktop = run.FullDesktop ?? explicitRun;
                var workingSet = fullDesktop ? analyzer.SelectForManualArrange(desktop, true) : analyzer.SelectVisibleWorkingSet(desktop, includeStackAccess: true);
                var plan = LayoutDispatch.Create(explicitRun ? LayoutRoute.ManualAlgorithm : localOpening ? LayoutRoute.LocalOpening : LayoutRoute.Smart,
                    workingSet, new(SmartStrength: run.Strength ?? SmartTidyStrength.Balanced), null, desktop, ctx);
                var projected = TaskLayoutSession.Project(desktop, plan);
                // 衡量 A/B 两个方案时使用完全相同的观察人群。评估规则只看决策前的状态，不参与生成任何候选。
                var evaluator = evaluation == null || frame.Truncated ? null :
                    ActiveLayoutScene.Resolve(desktop, ctx with { Rules = evaluation }, includeDefaults: false);
                bool? regionsPreserved = !frame.Truncated && ctx.WindowHints?.Any(h => h.ProtectedRegions is { Length: > 0 }) == true
                    ? IntentLayoutPlanner.DesktopRegionsPreserved(desktop, projected, ctx) : null;
                double? runRegret = null, verticalError = null;
                if (edits.Linked.TryGetValue((run.Session, run.RequestId), out var followups))
                {
                    double horizontal = 0, vertical = 0;
                    int matched = 0;
                    foreach (var edit in followups)
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
                Console.WriteLine(JsonSerializer.Serialize(new { run.Session, run.RequestId, run.Trigger, assumeResizable, frame.Truncated,
                    BeforeTime = frame.Time, InputFingerprint = inputFingerprint,
                    BarEvidence = run.Signals?.BrowserBars?.Decision ?? (run.Signals?.Video != null ? "legacy-bars-only" : null),
                    VerifiedTaskRepeat = run.Signals?.VerifiedTaskRepeat == true,
                    ProjectedWindows = desktop.Zip(projected, (before, after) =>
                        new { Id = (int)before.Handle, Before = before.VisualRect, Rect = after.VisualRect }),
                    run.GestureTiming, run.ShadowReplay, ObservedReleaseDetails = edits.Releases.GetValueOrDefault((run.Session, run.RequestId))?
                        .OrderBy(g => g.Tick).Select(g => new { g.GestureId, g.WindowId, g.AutomationMarked, ElapsedMs = g.Prior!.ElapsedMs, g.Change }),
                    RecordedManualFollowups = followups?.Count, RecordedUndoOfThisRequest =
                        edits.Undone.Contains((run.Session, run.RequestId)) ? true : (bool?)null,
                    PostArrangeRegret = runRegret, PostArrangeVerticalError = verticalError, ProtectedRegionsPreserved = regionsPreserved,
                    Layers = plan.Layers.Select(l => l.FrontToBack.Select(w => (int)w.Handle)),
                    SceneEffects = plan.Scene?.Effects(projected, h => (int)h),
                    EvaluationEffects = evaluation == null || frame.Truncated ? null : evaluator?.Effects(projected, h => (int)h) ?? [],
                    Roles = (ctx.WindowHints ?? []).Where(h => h.Role != WindowUseRole.Unknown || h.FloatingAffinity > 0 || h.TextFocus)
                        .Select(h => new { Id = (int)h.Handle, Role = h.Role.ToString(), h.FloatingAffinity, h.TextFocus }),
                    Moves = plan.Moves.Select(m => new { Id = (int)m.Window.Handle, Before = m.Window.VisualRect, After = m.TargetVisualRect }),
                    Selected = plan.Groups.Select(g => g.Selected) }));
            }
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { RecordedManualGestures = edits.Total,
        ObservedGestureStarts = edits.Starts, ObservedReleases = edits.Starts.HasValue ? edits.CompletedReleases : (int?)null,
        AutomationMarkedReleases = edits.Starts.HasValue ? edits.MarkedReleases : (int?)null,
        DroppedGestureEvents = edits.Starts.HasValue ? edits.DroppedEvents : (int?)null,
        WithoutLinkedRequest = edits.Total.HasValue ? edits.Unlinked : (int?)null, ExplicitUndoRequests = edits.UndoCount, UndoWithKnownSource = edits.Undone.Count }));
    Console.WriteLine($"Replayed {seen.Count} distinct requests ({correctedRuns} with post-arrange corrections, mean normalized regret={(regretSamples > 0 ? (totalNormalizedRegret / regretSamples).ToString("F4") : "n/a")}).");
}
