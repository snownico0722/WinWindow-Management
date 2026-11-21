namespace NeatWin.Core;

// Roles describe content use. The window's dimensions and task decide whether a particular
// editor is a floating note. Shared toolkit classes (e.g. Chromium) do not identify content.
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<WindowUseRole>))]
public enum WindowUseRole { Unknown, Browser, FileManager, Editor, FloatingEditor }

internal static class WindowUseRoles
{
    internal static WindowUseRole Identify(string? executable, string? windowClass)
    {
        if (windowClass is "#32770" or "Progman" or "WorkerW") return WindowUseRole.Unknown;
        if (windowClass is "CabinetWClass" or "ExploreWClass") return WindowUseRole.FileManager;
        if (windowClass is "Notepad" or "Notepad++" or "WordPadClass") return WindowUseRole.Editor;
        string app = Path.GetFileNameWithoutExtension(executable)?.ToLowerInvariant() ?? "";
        return app switch
        {
            "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera" => WindowUseRole.Browser,
            "notepad" or "notepad++" or "wordpad" or "typora" or "sublime_text" => WindowUseRole.Editor,
            "totalcmd64" or "totalcmd" or "doublecmd" => WindowUseRole.FileManager,
            _ => WindowUseRole.Unknown
        };
    }

    internal static WindowUseRole Contextualize(WindowUseRole identified, WindowSnapshot window,
        IReadOnlyList<WindowSnapshot> desktop, LayoutFocus? focus, IReadOnlyDictionary<nint, LayoutAttention>? attention,
        IReadOnlyList<PlacementPreference>? placements)
    {
        if (identified != WindowUseRole.Editor || !window.IsManageable || window.WorkArea.IsEmpty) return identified;
        var area = window.WorkArea;
        var rect = window.VisualRect;
        if (rect.Width > area.Width * .70 || rect.Height > area.Height * .78 || rect.Area > area.Area * .36)
            return identified;

        // Repeated deliberate edits that call for more space take precedence over the compact frame.
        var habit = placements?.FirstOrDefault(p => p.Window == window.Handle && p.Confidence >= .5);
        if (habit != null && (habit.Width * area.Width > rect.Width * 1.15 ||
            habit.Height * area.Height > rect.Height * 1.15)) return identified;

        var visit = attention?.GetValueOrDefault(window.Handle);
        // Switching back to source material often puts it above the note. A recent
        // sustained editing visit keeps its footprint through that handoff.
        bool editingVisit = visit is { Dwell: >= .5, Recency: >= .25 };
        bool inUse = window.IsForeground || focus?.Main == window.Handle ||
            visit?.Weight >= ForegroundHistory.WorkingAttention || editingVisit;
        if (!inUse) return identified;
        bool overlaysTask = desktop.Any(other => other.Handle != window.Handle &&
            other.MonitorHandle == window.MonitorHandle && (other.ZOrder > window.ZOrder || editingVisit) &&
            (other.IsManageable || other.SurfaceState is "maximized" or "fullscreen") &&
            other.VisualRect.Intersect(rect).Area >= rect.Area * .20);
        return overlaysTask ? WindowUseRole.FloatingEditor : identified;
    }

    internal static TaskWindowHint[] Bind(IReadOnlyList<WindowSnapshot> desktop, TaskWindowHint[] hints,
        LayoutFocus? focus, IReadOnlyDictionary<nint, LayoutAttention>? attention, IReadOnlyList<PlacementPreference>? placements,
        Func<WindowSnapshot, WindowUseRole> identify)
    {
        var byWindow = hints.ToDictionary(h => h.Handle);
        foreach (var window in desktop.Where(w => w.IsManageable))
        {
            var role = Contextualize(identify(window), window, desktop, focus, attention, placements);
            if (role == WindowUseRole.Unknown) continue;
            var hint = byWindow.GetValueOrDefault(window.Handle) ?? new TaskWindowHint(window.Handle);
            byWindow[window.Handle] = hint with { Role = role };
        }
        return byWindow.Values.ToArray();
    }
}
