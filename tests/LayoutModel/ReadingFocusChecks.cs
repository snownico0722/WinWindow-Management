using System.Text.Json;
using NeatWin.Core;

internal static class ReadingFocusChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var work = new RectI(0, 0, 1920, 1040);
        var rect = new RectI(180, 150, 850, 650);
        WindowSnapshot main = new(1, rect, rect, work, 1, default, true, true, true, 0, 96, 100);
        WindowSnapshot source = main with { Handle = 2, IsForeground = false, ZOrder = 1, ProcessId = 200 };
        WindowUseRole Role(string exe, string className, FocusedOfficeSurface surface = 0) => WindowUseRoles.Identify(exe, className, surface);
        check(Role("WeChat.exe", "WeChatMainWndForPC") == WindowUseRole.Chat &&
            Role("Weixin.exe", "Qt51514QWindowIcon") == WindowUseRole.Chat &&
            Role("QQ.exe", "Chrome_WidgetWin_1") == WindowUseRole.Chat &&
            Role("WeChatAppEx.exe", "Chrome_WidgetWin_1") == WindowUseRole.Unknown &&
            Role("QQ.exe", "IME") == WindowUseRole.Unknown && Role("Weixin.exe", "Qt51514WxTrayIconMessageWindowClass") == WindowUseRole.Unknown,
            "chat priors match actual conversation frames and exclude embedded web, IME and tray helpers");
        check(Role("WINWORD.EXE", "OpusApp") == WindowUseRole.Document && Role("EXCEL", "XLMAIN") == WindowUseRole.Spreadsheet &&
            Role("POWERPNT", "PPTFrameClass") == WindowUseRole.Presentation && Role("POWERPNT", "screenClass") == WindowUseRole.Unknown &&
            Role("EXCEL", "#32770") == WindowUseRole.Unknown && Role("WINWORD", "NetUIHWND") == WindowUseRole.Unknown,
            "Word document, Excel grid and PowerPoint edit frames have distinct roles; slideshow and helpers have no content prior");
        const string qt = "Qt5QWindowIcon";
        check(Role("wps", "OpusApp") == WindowUseRole.Document && Role("wps.exe", qt, FocusedOfficeSurface.Document) == WindowUseRole.Document &&
            Role("et.exe", qt, FocusedOfficeSurface.Grid) == WindowUseRole.Spreadsheet &&
            Role("wpp.exe", qt, FocusedOfficeSurface.Document) == WindowUseRole.Presentation &&
            Role("wps.exe", qt, FocusedOfficeSurface.Grid) == WindowUseRole.Spreadsheet &&
            Role("wps.exe", qt, FocusedOfficeSurface.Canvas) == WindowUseRole.Presentation &&
            Role("wps.exe", qt) == WindowUseRole.Unknown && Role("et.exe", qt, FocusedOfficeSurface.Canvas) == WindowUseRole.Unknown &&
            Role("wpp.exe", "screenClass", FocusedOfficeSurface.Document) == WindowUseRole.Unknown &&
            Role("wpscloudsvr.exe", qt, FocusedOfficeSurface.Grid) == WindowUseRole.Unknown,
            "WPS distinguishes documents, workbook cells and slides using the focused surface; the launcher, mixed shell and slideshow gain no implicit text focus");

        var engaged = new LayoutAttention(1, .8, 0, 0, true);
        TaskWindowHint? Hint(WindowUseRole role, WindowSnapshot? target = null, LayoutAttention? use = null, nint caret = 0,
            string? activity = null, LayoutFocus? focus = null, WindowSnapshot? behind = null)
        {
            var w = target ?? main;
            var ctx = focus ?? new(1, 0, 0);
            return WindowUseRoles.Bind([w, behind ?? source], [], ctx, new Dictionary<nint, LayoutAttention> { [1] = use ?? engaged },
                null, s => s.Handle == 1 ? role : WindowUseRole.Unknown, caret,
                activity == null ? null : new Dictionary<nint, string> { [1] = activity }).FirstOrDefault(h => h.Handle == 1);
        }
        foreach (var role in new[] { WindowUseRole.Editor, WindowUseRole.Chat, WindowUseRole.Document, WindowUseRole.Spreadsheet, WindowUseRole.Presentation })
            check(Hint(role)?.TextFocus == true && Hint(role, use: new(1, .02, 0, 0, true))?.TextFocus == false &&
                Hint(role, target: main with { IsForeground = false })?.TextFocus == false &&
                Hint(role, target: main with { SurfaceState = "fullscreen" })?.TextFocus == false,
                $"{role} keeps an active reading position without a caret and releases it on focus or display-mode change");
        check(Hint(WindowUseRole.Unknown, caret: 1)?.TextFocus == true && Hint(WindowUseRole.Browser, caret: 1)?.TextFocus == true &&
            Hint(WindowUseRole.Browser, caret: 2)?.TextFocus == false &&
            Hint(WindowUseRole.Editor, activity: "game-like-interaction")?.TextFocus == false &&
            Hint(WindowUseRole.Presentation, activity: "media-with-motion")?.TextFocus == false &&
            Hint(WindowUseRole.Browser, caret: 1, activity: "media-with-motion")?.TextFocus == true,
            "a real foreground caret works across unknown applications and web forms; video or game evidence defeats the static reading prior");
        var navigation = new LayoutFocus(1, 2, 1, "handoff", [2], [new(2, 1, "click-open", 500)]);
        check(Hint(WindowUseRole.Chat, focus: navigation, behind: source with { ProcessId = main.ProcessId })?.TextFocus == false &&
            Hint(WindowUseRole.Chat, target: main with { Owner = source.Handle })?.TextFocus == false &&
            Hint(WindowUseRole.Chat, target: main with { Owner = source.Handle }, caret: 1)?.TextFocus == true,
            "a derived article cannot inherit the chat position; its own focused text field can establish a new one");

        var small = main with { VisualRect = new(20, 80, 560, 460) };
        var identity = new LayoutFocus(1, 0, 0);
        ForegroundHistory NewVisit()
        {
            var history = new ForegroundHistory();
            history.Created(1, main.ProcessId, 1000);
            history.Visit(1, main.ProcessId, 1020);
            return history;
        }
        var first = NewVisit();
        var visit = first.Resolve([small], 1500, out var active);
        TaskWindowHint FirstHint(WindowSnapshot w, nint caret = 0) => WindowUseRoles.Bind([w], [], visit, active, [],
            _ => WindowUseRole.Document, caret, keyboardRoot: 1, initialPlacement: 1, profile: new()).Single();
        var firstHint = FirstHint(small);
        var firstPlan = LayoutDispatch.Create(LayoutRoute.Smart, new VisibilityAnalyzer().SelectForManualArrange([small], true),
            new(), null, [small], new(Focus: visit, Attention: active, WindowHints: [firstHint]));
        check(first.TakeInitialPlacement(small, visit, 1500) && !first.TakeInitialPlacement(small, visit, 1550) &&
            firstHint is { InitialTextPlacement: true, TextFocus: false } &&
            firstPlan.Moves.Any(m => m.TargetVisualRect.Width > small.VisualRect.Width) &&
            FirstHint(small, 1).TextFocus && FirstHint(main).TextFocus,
            "a genuinely new undersized document gets one placement at scheduler time while a caret or readable size protects the view");
        var input = NewVisit();
        input.Interact(1, small.ProcessId, 1200);
        var returning = NewVisit();
        returning.Visit(2, source.ProcessId, 1120);
        returning.Visit(1, small.ProcessId, 1200);
        check(!input.TakeInitialPlacement(small, identity, 1500) &&
            !returning.TakeInitialPlacement(small, identity, 1500) && !NewVisit().TakeInitialPlacement(small, navigation, 1500) &&
            !NewVisit().TakeInitialPlacement(small, identity, 3500) && !NewVisit().TakeInitialPlacement(small, identity, 1500, 1150),
            "input, a return visit, a linked opening or an expired birth preserves reading position");

        var data = RecordedTaskSignals.From(new(WindowHints: [Hint(WindowUseRole.Unknown, caret: 1)!]),
            new Dictionary<nint, int> { [1] = 54 });
        string json = JsonSerializer.Serialize(data);
        var replay = JsonSerializer.Deserialize<RecordedTaskSignals>(json)!.ApplyToReplay(new());
        var legacy = JsonSerializer.Deserialize<RecordedTaskSignals>(json.Replace(",\"TextFocus\":true", ""))!.ApplyToReplay(new());
        check(replay.WindowHints is [{ Handle: 54, TextFocus: true }] && legacy.WindowHints is [{ TextFocus: false }] &&
            !json.Contains("Caret", StringComparison.OrdinalIgnoreCase), "reading continuity replays by anonymous window id and defaults off when absent");
    }
}
