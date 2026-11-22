using System.Text.Json;
using NeatWin.Core;

internal static class DesktopInteractionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1040);
        WindowSnapshot Host(int handle, RectI r) => new(handle, r, r, area, 1, default, false, false, true, 1, 96, 17);
        var host = Host(1, new(1240, 250, 126, 52));
        var front = Host(2, new(100, 30, 900, 850)) with { IsForeground = true, ProcessId = 2, ZOrder = 0 };
        var popup = Host(3, new(1350, 284, 270, 360));
        var evidence = new DesktopInteractionHistory();
        long now = 1000;
        var surfaces = new TaskSurfaces { ReadContextPress = () => evidence.ContextAt(now) };
        var recorded = new List<(long Id, string Change)>();
        var scheduler = new AutomaticLayoutScheduler
        {
            Surfaces = surfaces,
            RecordChange = (_, change) => { long id = recorded.Count + 1; recorded.Add((id, change)); return id; }
        };
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [front, host], now);
        evidence.Pointer("right-down", 1, 17, now += 30, 10, new(1350, 280));
        evidence.Pointer("right-up", popup.Handle, 17, now += 1000, 11, new(1350, 281));
        check(evidence.ContextAt(now) is { Root: 1, Sequence: 10, ReleaseSequence: 11 },
            "release on the newly opened menu retains the original right-click host");
        var dialog = popup with { Handle = 4, HasCaption = true };
        var dialogSurfaces = new TaskSurfaces { ReadContextPress = () => evidence.ContextAt(now) };
        dialogSurfaces.Update([front, host], null);
        dialogSurfaces.Update([front, host, dialog], host);
        check(!dialogSurfaces.IsLocal(dialog), "a fixed-size captioned window beside the click keeps its task identity");
        scheduler.Observe([front, host, popup], ++now, false);
        check(surfaces.IsLocal(popup) && !scheduler.HasPending && recorded.Count == 0,
            "a context popup larger than a background note consumes the release without scheduling a workspace change");
        scheduler.Observe([front, host, popup], now += 1500, false);
        check(surfaces.IsLocal(popup), "a menu stays local beyond the click evidence lifetime");
        var editor = popup with { IsResizable = true, IndependentFrame = true, IsForeground = true, ZOrder = 0 };
        scheduler.Observe([front with { IsForeground = false }, host, editor], ++now, false);
        var request = scheduler.Observe([front with { IsForeground = false }, host, editor], now += 600, false);
        check(!surfaces.IsLocal(editor) && request?.ChangeEvents?.Contains(recorded.First(r => r.Change == "added").Id) == true,
            "promotion to an independent editor records the specific observed change on the request");
        check(scheduler.Observe([front with { IsForeground = false }, host, editor], now += 2000, false) == null,
            "an unchanged desktop does not generate further requests");

        evidence.Pointer("right-down", 1, 17, now, 15, new(100, 100));
        evidence.Pointer("right-up", 1, 17, ++now, 16, new(250, 270));
        check(evidence.ContextAt(now) == null, "right-button dragging does not supply popup evidence");
        evidence.Pointer("wheel", 1, 17, now, 17, new(100, 100));
        evidence.Pointer("wheel", 1, 17, now += 120, 18, new(100, 100));
        var physical = new RectI(40, 20, 1, 18);
        evidence.CaretMoved(2, 2, now, 19, physical);
        evidence.CaretMoved(2, 2, now += 80, 20, physical);
        check(!evidence.Read([front], now).ContainsKey(2), "a static text caret cannot imply continuing edits");
        evidence.CaretMoved(2, 2, ++now, 21, physical with { X = 60 });
        var inputs = evidence.Read([host, front], now);
        var ctx = new TaskLayoutContext { IsWorkspaceObservation = true, ReadingInput = inputs };
        var visible = new VisibleWindow(host, host.VisualRect.Area, host.VisualRect.Area, 1);
        var target = host.VisualRect with { X = 130 };
        double Cost(TaskLayoutContext c) => IntentLayoutPlanner.AttentionInterruption(visible, target, c, null);
        var ids = new Dictionary<nint, int> { [1] = 1, [2] = 2 };
        var stored = RecordedTaskSignals.From(ctx, ids);
        var replay = JsonSerializer.Deserialize<RecordedTaskSignals>(JsonSerializer.Serialize(stored))!.ApplyToReplay(ctx with { ReadingInput = null });
        check(inputs[1].RepeatedScroll && inputs[2].CaretEvent == 21 && Cost(ctx) > Cost(ctx with { ReadingInput = null }) &&
            Cost(ctx) == Cost(replay), "recent background scrolling costs more to relocate and reproduces from the recording");
        check(evidence.Read([host with { ProcessId = 99 }], now).Count == 0 && evidence.Read([host, front], now + 1900).Count == 0,
            "input evidence expires and cannot cross a reused HWND");
        var use = new PlacementUseObservation(2, 0, false, [], [1]);
        check(use.SupportsPair(2, 1) && use.For(2, 1) == PlacementUsage.BackgroundReading &&
            use.For(2, 4) == null, "only the scrolled reference participates in background reading habits");
        var identity = new RawWindowIdentity("0xAB", 17, "Test App", @"C:\Tools\Test App.exe", null, "TestClass", "原始标题 文档.md");
        var frame = new ObservationIdentityTracker().Capture([host], rawIdentity: _ => identity);
        check(JsonSerializer.Deserialize<WorkspaceFrame>(JsonSerializer.Serialize(frame))!.Windows.Single().Identity == identity,
            "raw identity, including case, full path and title, survives the recording round trip");
    }
}
