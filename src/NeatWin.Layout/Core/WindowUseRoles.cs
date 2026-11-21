namespace NeatWin.Core;

// Application and class identify a content category. Floating intent is inferred from this
// particular window's geometry, relationships and recent user behavior.
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
            double prior = .15 + sourceEvidence * (identified == WindowUseRole.Editor ? .30 : .25);
            double intent = Math.Max(manual, Math.Max(ownership, prior * current * interaction));
            if (intent * overlap <= best) continue;
            var covering = desktop.Where(w => w.ZOrder < source.ZOrder).Select(w => w.VisualRect);
            double readable = IntentLayoutPlanner.ScreenExposure(source.VisualRect, covering, area, source.Dpi).Visible;
            best = Math.Max(best, intent * overlap * Ramp(readable, .10, .45));
        }
        return best * compact * commitment;
    }

    internal static TaskWindowHint[] Bind(IReadOnlyList<WindowSnapshot> desktop, TaskWindowHint[] hints,
        LayoutFocus? focus, IReadOnlyDictionary<nint, LayoutAttention>? attention, IReadOnlyList<PlacementPreference>? placements,
        Func<WindowSnapshot, WindowUseRole> identify)
    {
        var byWindow = hints.ToDictionary(h => h.Handle);
        foreach (var window in desktop.Where(w => w.IsManageable))
        {
            var role = identify(window);
            double affinity = FloatingAffinity(role, window, desktop, focus, attention, placements);
            if (role == WindowUseRole.Unknown && affinity == 0) continue;
            var hint = byWindow.GetValueOrDefault(window.Handle) ?? new TaskWindowHint(window.Handle);
            byWindow[window.Handle] = hint with { Role = role, FloatingAffinity = affinity };
        }
        return byWindow.Values.ToArray();
    }
}
