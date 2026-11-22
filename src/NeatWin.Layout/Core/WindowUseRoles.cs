namespace NeatWin.Core;

// Application and class identify a content category. Floating intent is inferred from this
// particular window's geometry, relationships and recent user behavior.
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<WindowUseRole>))]
public enum WindowUseRole { Unknown, Browser, FileManager, Editor, FloatingEditor, Chat, Document, Spreadsheet, Presentation, Note }

internal enum FocusedOfficeSurface { Unknown, Document, Grid, Canvas }

internal static class WindowUseRoles
{
    private static bool QtFrame(string? name) => name?.StartsWith("Qt", StringComparison.Ordinal) == true &&
        name.EndsWith("QWindowIcon", StringComparison.Ordinal);

    internal static bool IsSharedOfficeFrame(string? executable, string? windowClass) =>
        QtFrame(windowClass) && Path.GetFileNameWithoutExtension(executable)?.ToLowerInvariant() is "wps" or "et" or "wpp";

    internal static WindowUseRole Identify(string? executable, string? windowClass,
        FocusedOfficeSurface surface = FocusedOfficeSurface.Unknown)
    {
        if (windowClass is "#32770" or "Progman" or "WorkerW") return WindowUseRole.Unknown;
        if (windowClass is "CabinetWClass" or "ExploreWClass") return WindowUseRole.FileManager;
        if (windowClass is "Notepad" or "Notepad++" or "WordPadClass") return WindowUseRole.Editor;
        string app = Path.GetFileNameWithoutExtension(executable)?.ToLowerInvariant() ?? "";
        if (IsSharedOfficeFrame(app, windowClass))
            return surface switch
            {
                FocusedOfficeSurface.Grid when app is "wps" or "et" => WindowUseRole.Spreadsheet,
                FocusedOfficeSurface.Canvas when app is "wps" or "wpp" => WindowUseRole.Presentation,
                FocusedOfficeSurface.Document when app == "wpp" => WindowUseRole.Presentation,
                FocusedOfficeSurface.Document when app == "wps" => WindowUseRole.Document,
                _ => WindowUseRole.Unknown
            };
        return app switch
        {
            "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera" => WindowUseRole.Browser,
            "notepad" or "notepad++" or "wordpad" or "typora" or "sublime_text" => WindowUseRole.Editor,
            "winword" or "wps" when windowClass == "OpusApp" => WindowUseRole.Document,
            "excel" or "et" when windowClass == "XLMAIN" => WindowUseRole.Spreadsheet,
            "powerpnt" or "wpp" when windowClass == "PPTFrameClass" => WindowUseRole.Presentation,
            "wechat" or "weixin" when windowClass is "WeChatMainWndForPC" or "ChatWnd" || QtFrame(windowClass) => WindowUseRole.Chat,
            "qq" or "tim" when windowClass is "TXGuiFoundation" or "Chrome_WidgetWin_1" => WindowUseRole.Chat,
            "code" or "code - insiders" or "cursor" or "obsidian" when windowClass == "Chrome_WidgetWin_1" => WindowUseRole.Editor,
            "devenv" or "rider64" or "idea64" when windowClass?.StartsWith("HwndWrapper", StringComparison.Ordinal) == true ||
                windowClass == "SunAwtFrame" => WindowUseRole.Editor,
            "windowsterminal" when windowClass == "CASCADIA_HOSTING_WINDOW_CLASS" => WindowUseRole.Editor,
            "papertodo" when windowClass?.StartsWith("HwndWrapper", StringComparison.Ordinal) == true => WindowUseRole.Note,
            "microsoft.notes" when windowClass == "Windows.UI.Core.CoreWindow" => WindowUseRole.Note,
            "lark" or "feishu" or "dingtalk" or "discord" when windowClass == "Chrome_WidgetWin_1" => WindowUseRole.Chat,
            "telegram" when QtFrame(windowClass) => WindowUseRole.Chat,
            "totalcmd64" or "totalcmd" or "doublecmd" => WindowUseRole.FileManager,
            _ => WindowUseRole.Unknown
        };
    }

    internal static bool IsFloating(WindowUseRole? role) =>
        role == WindowUseRole.FloatingEditor;

    // A footprint supplies continuous supporting evidence. Owners and user placement
    // explain intent. A recognized editor supplies a weaker prior for an active overlay.
    internal static double FloatingAffinity(WindowUseRole identified, WindowSnapshot window,
        IReadOnlyList<WindowSnapshot> desktop, LayoutFocus? focus, IReadOnlyDictionary<nint, LayoutAttention>? attention,
        IReadOnlyList<PlacementPreference>? placements)
    {
        if (!window.IsManageable || window.WorkArea.IsEmpty || window.VisualRect.IsEmpty) return 0;
        static double Ramp(double value, double low, double high) => Math.Clamp((value - low) / (high - low), 0, 1);
        var area = window.WorkArea;
        var rect = window.VisualRect;
        double compact = 1 - Math.Max(Ramp(rect.Width / (double)area.Width, .55, .85),
            Math.Max(Ramp(rect.Height / (double)area.Height, .65, .90), Ramp(rect.Area / (double)area.Area, .32, .55)));
        if (compact == 0) return 0;

        var visit = attention?.GetValueOrDefault(window.Handle);
        double manual = visit?.ManualFloatPreference ?? 0;
        double editingVisit = visit == null ? 0 : Ramp(visit.Recency, .10, .45) * Ramp(visit.Dwell, 0, .50);
        double current = window.IsForeground || focus?.Main == window.Handle ? 1 : editingVisit;
        double interaction = Math.Max(editingVisit, visit?.Input ?? 0);
        if (focus is { Evidence: "handoff" } && focus.Main == window.Handle)
            interaction = Math.Max(interaction, .70);
        if (manual <= 0 && (current == 0 || window.Owner == 0 && interaction == 0)) return 0;
        double taskUse = focus is { Companion: not 0, Switches: >= 2 } && focus.Evidence != "handoff" &&
            (focus.Main == window.Handle || focus.Companion == window.Handle)
            ? Ramp(focus.Switches, 1, 4) : 0;
        // A new deliberate placement can change a task that was already established.
        // As that gesture ages, sustained reading and repeated switches regain influence.
        double inferred = 1 - Math.Max(0, manual);
        double reading = Math.Max(taskUse, visit?.SustainedForeground ?? 0) * inferred;
        double commitment = (1 + Math.Min(0, manual)) * (1 - reading);
        var habit = placements?.FirstOrDefault(p => p.Window == window.Handle);
        if (habit != null)
        {
            double relativeWidth = habit.PreferWidth ? habit.Width * area.Width / rect.Width : 1;
            double relativeHeight = (habit.Height ?? 0) * area.Height / rect.Height;
            double demand = Ramp(Math.Max(relativeWidth, relativeHeight), 1.03, 1.3);
            commitment *= 1 - inferred * Ramp(habit.Confidence, .40, .80) * demand;
        }
        if (commitment == 0) return 0;

        double best = 0;
        foreach (var source in desktop)
        {
            if (source.Handle == window.Handle || source.MonitorHandle != window.MonitorHandle ||
                !source.IsManageable && source.SurfaceState is not ("maximized" or "fullscreen") ||
                source.ZOrder <= window.ZOrder && editingVisit == 0 && manual <= 0 || source.VisualRect.IsEmpty) continue;
            double overlap = Ramp(source.VisualRect.Intersect(rect).Area / (double)rect.Area, .10, .45);
            if (overlap == 0) continue;
            double ownership = window.Owner == source.Handle ? (window.IsResizable ?
                1 - Ramp(rect.Area / (double)source.VisualRect.Area, .20, .65) : 1) : 0;
            double sourceEvidence = source.Handle == focus?.Companion || focus?.Main == source.Handle ? 1 :
                attention?.GetValueOrDefault(source.Handle)?.Weight ?? 0;
            // The same small foreground overlay on visible material has the same baseline
            // in any application. Identity can only reinforce an observed source relationship.
            double prior = .15 + sourceEvidence * (identified is WindowUseRole.Editor or WindowUseRole.Note ? .30 : .25);
            double intent = Math.Max(manual, Math.Max(ownership, prior * current * interaction));
            if (intent * overlap <= best) continue;
            var covering = desktop.Where(w => w.ZOrder < source.ZOrder).Select(w => w.VisualRect);
            double readable = IntentLayoutPlanner.ScreenExposure(source.VisualRect, covering, area, source.Dpi).Visible;
            best = Math.Max(best, intent * overlap * Ramp(readable, .10, .45));
        }
        return best * compact * commitment;
    }

    // A caret belongs to one native input target. While reading, a foreground document,
    // cell grid, slide canvas or conversation may have no caret. Require a settled visit
    // or recent input for this weaker signal, and never carry it to a newly opened chat surface.
    private static bool KeepsTextFocus(WindowSnapshot window, WindowUseRole role, LayoutFocus? focus,
        LayoutAttention? visit, IReadOnlyList<WindowSnapshot> desktop, bool caret, string? activity, TaskWindowHint? hint)
    {
        if (!window.IsForeground || !window.IsManageable || window.IsTopmost || window.SurfaceState == "fullscreen" ||
            window.WorkArea.Intersect(window.VisualRect).IsEmpty) return false;
        if (caret) return true;
        if (window.Owner != 0 || !window.IsResizable || focus?.Main != window.Handle || visit is not { Foreground: true } ||
            role is not (WindowUseRole.Editor or WindowUseRole.Note or WindowUseRole.Chat or WindowUseRole.Document or
                WindowUseRole.Spreadsheet or WindowUseRole.Presentation) ||
            activity is "game-like-interaction" or "media-with-motion" or "audible-with-motion" or "audible-media-session" ||
            hint is { MediaCompanion: true } or { PassiveVisual: true }) return false;
        if (role == WindowUseRole.Chat && focus.Navigation?.Any(hop => hop.Target == window.Handle &&
            desktop.Any(source => source.Handle == hop.Source && source.ProcessId == window.ProcessId)) == true) return false;
        return visit.Input >= .35 || visit.Dwell >= AutomaticLayoutScheduler.SettleMilliseconds / 4000d;
    }

    internal static TaskWindowHint[] Bind(IReadOnlyList<WindowSnapshot> desktop, TaskWindowHint[] hints,
        LayoutFocus? focus, IReadOnlyDictionary<nint, LayoutAttention>? attention, IReadOnlyList<PlacementPreference>? placements,
        Func<WindowSnapshot, WindowUseRole> identify, nint foregroundCaret = 0,
        IReadOnlyDictionary<nint, string>? activities = null, nint? keyboardRoot = null, nint initialPlacement = 0, TaskLayoutProfile? profile = null)
    {
        var byWindow = hints.ToDictionary(h => h.Handle);
        foreach (var window in desktop.Where(w => w.IsManageable))
        {
            var role = identify(window);
            if (role == WindowUseRole.Note && Math.Min(window.VisualRect.Width, window.VisualRect.Height) <
                100 * (window.Dpi == 0 ? 1 : window.Dpi / 96d)) role = WindowUseRole.FloatingEditor;
            double affinity = FloatingAffinity(role, window, desktop, focus, attention, placements);
            var hint = byWindow.GetValueOrDefault(window.Handle);
            bool text = (!keyboardRoot.HasValue || keyboardRoot == window.Handle) &&
                KeepsTextFocus(window, role, focus, attention?.GetValueOrDefault(window.Handle), desktop,
                window.Handle == foregroundCaret, activities?.GetValueOrDefault(window.Handle), hint);
            bool firstPlacement = false;
            if (text && window.IsResizable && !IsFloating(role) && hint?.Anchor != true && window.Handle == initialPlacement &&
                profile != null && attention?.GetValueOrDefault(window.Handle)?.Input == 0)
            {
                double scale = (window.Dpi == 0 ? 96 : window.Dpi) / 96d;
                // Only spend this first-visit opportunity on a real reading deficit.
                double width = Math.Min(window.WorkArea.Width, (hint?.UsefulWidthDip ?? profile.ComfortableWidthDip) * scale);
                double height = Math.Min(window.WorkArea.Height, (hint?.UsefulHeightDip ?? profile.ComfortableHeightDip) * scale);
                firstPlacement = window.VisualRect.Width < width * .72 || window.VisualRect.Height < height * .72;
            }
            if (focus?.OpeningResidentLeft.HasValue == true && window.Handle == focus.Main)
            {
                firstPlacement = window.Handle == initialPlacement;
                text = !firstPlacement;
            }
            if (hint == null && role == WindowUseRole.Unknown && affinity == 0 && !text && !firstPlacement) continue;
            byWindow[window.Handle] = (hint ?? new(window.Handle)) with { Role = role, FloatingAffinity = affinity, TextFocus = text && !firstPlacement, InitialTextPlacement = firstPlacement };
        }
        return byWindow.Values.ToArray();
    }
}
