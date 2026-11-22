using System.Text.Json.Serialization;

namespace NeatWin.Core;

// Only anonymous keys from the recorder export or generic behavioral roles may participate in rules.
public sealed record AppPairRule(string FirstApp, string SecondApp, string FirstSide = "either",
    double? Overlap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OnlyWhenPaired = false);

public sealed record RolePairRule(string FirstRole, string SecondRole, string FirstSide = "left",
    double? Overlap = null);

public sealed record AppSlotRule(string App, string Side = "either", double? Width = null);

public sealed record CompiledLayoutRules(int Version = 1, double MediaOverlap = .31, double AudioPeakThreshold = .02,
    double FragmentChangeThreshold = .08, AppPairRule[]? Pairs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RolePairRule[]? RolePairs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AppSlotRule[]? AppSlots = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LayoutScene[]? Scenes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SurfaceRule[]? SurfaceRules = null)
{
    public static CompiledLayoutRules Default { get; } = new(Pairs: []);

    private static readonly HashSet<string> ValidRoles = new(StringComparer.Ordinal)
        { "game", "media", "passive", "foreground", "companion", "any" };

    internal bool IsValid()
    {
        static bool Key(string? key) => key is { Length: 24 } && key.All(char.IsAsciiHexDigit);
        if (SurfaceRules != null && (Version < 4 || SurfaceRules.Length > 64 || SurfaceRules.Any(r => r == null || !r.IsValid()) ||
            SurfaceRules.Select(r => r.Id).Distinct().Count() != SurfaceRules.Length)) return false;
        if (Version is not (1 or 2 or 3 or 4) || Scenes != null && (Version < 4 || Scenes.Length > 64 ||
            Scenes.Any(s => s == null || !s.IsValid()) || Scenes.Select(s => s.Id).Distinct().Count() != Scenes.Length) || MediaOverlap is not (>= 0 and <= .40) ||
            AudioPeakThreshold is not (> 0 and <= 1) || FragmentChangeThreshold is not (> 0 and <= 1) ||
            Pairs?.Length > 64 || RolePairs?.Length > 32 || AppSlots?.Length > 64) return false;
        var seen = new HashSet<(string, string)>();
        foreach (var pair in Pairs ?? [])
        {
            if (pair is null || pair.OnlyWhenPaired && Version < 3 || !Key(pair.FirstApp) || !Key(pair.SecondApp) ||
                pair.FirstSide is not ("left" or "right" or "either") ||
                pair.Overlap is not (null or (>= 0 and <= .40))) return false;
            string a = pair.FirstApp.ToUpperInvariant(), b = pair.SecondApp.ToUpperInvariant();
            if (a == b || !seen.Add(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a))) return false;
        }
        foreach (var rolePair in RolePairs ?? [])
        {
            if (rolePair is null || !ValidRoles.Contains(rolePair.FirstRole) || !ValidRoles.Contains(rolePair.SecondRole) ||
                (rolePair.FirstRole == "any" && rolePair.SecondRole == "any") ||
                rolePair.FirstSide is not ("left" or "right" or "either") ||
                rolePair.Overlap is not (null or (>= 0 and <= .40))) return false;
        }
        var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in AppSlots ?? [])
        {
            if (slot is null || !Key(slot.App) || slot.Side is not ("left" or "right" or "center" or "either") ||
                slot.Width is not (null or (>= .18 and <= .88)) || !seenApps.Add(slot.App)) return false;
        }
        return true;
    }

    internal WorkingPairRule[] Bind(IEnumerable<(WindowSnapshot Window, string? Key)> entries,
        IReadOnlyList<TaskWindowHint>? hints = null, IReadOnlyDictionary<nint, string>? activities = null, LayoutFocus? focus = null)
    {
        var matches = new List<WorkingPairRule>();
        var hintByHandle = (hints ?? []).ToDictionary(h => h.Handle);
        foreach (var screen in entries.Where(e => e.Window.IsManageable).GroupBy(e => e.Window.MonitorHandle))
        {
            var members = screen.ToArray();
            var unique = members.Where(e => e.Key != null).GroupBy(e => e.Key!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single().Window.Handle, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Pairs ?? [])
            {
                if (!unique.TryGetValue(pair.FirstApp, out var first) || !unique.TryGetValue(pair.SecondApp, out var second)) continue;
                var bound = new WorkingPairRule(first, second, pair.FirstSide, pair.Overlap, pair.OnlyWhenPaired);
                // 两个应用同时存在只能识别窗口。条件规则只描述已被观察到的搭配，不从后台拉出搭档，也不拆开导航链。
                if (!pair.OnlyWhenPaired || focus is { Evidence: "recent-switch-pair", Navigation: not { Length: > 0 } } &&
                    bound.Matches(focus.Main, focus.Companion)) matches.Add(bound);
            }

            if (RolePairs is not { Length: > 0 }) continue;
            foreach (var roleRule in RolePairs)
            {
                var firstCandidates = members.Where(m => ActivityHints.MatchesRole(roleRule.FirstRole, m.Window,
                    hintByHandle.GetValueOrDefault(m.Window.Handle), activities?.GetValueOrDefault(m.Window.Handle))).ToArray();
                var secondCandidates = members.Where(m => ActivityHints.MatchesRole(roleRule.SecondRole, m.Window,
                    hintByHandle.GetValueOrDefault(m.Window.Handle), activities?.GetValueOrDefault(m.Window.Handle))).ToArray();
                if (firstCandidates.Length != 1) continue;
                var firstWin = firstCandidates[0].Window;
                var remainingSeconds = secondCandidates.Where(s => s.Window.Handle != firstWin.Handle).ToArray();
                if (remainingSeconds.Length != 1) continue;
                var secondWin = remainingSeconds[0].Window;
                if (!matches.Any(m => m.Matches(firstWin.Handle, secondWin.Handle)))
                    matches.Add(new(firstWin.Handle, secondWin.Handle, roleRule.FirstSide, roleRule.Overlap ?? MediaOverlap));
            }
        }
        return matches.ToArray();
    }

    internal static PlacementPreference[] ApplySlots(PlacementPreference[] learned, PlacementPreference[] slots)
    {
        if (slots.Length == 0) return learned;
        var result = learned.ToDictionary(p => p.Window);
        foreach (var slot in slots)
        {
            if (!result.TryGetValue(slot.Window, out var prior)) { result[slot.Window] = slot; continue; }
            // A rule choosing only one dimension leaves evidence on the other dimensions available.
            var position = prior.PositionConfidence >= slot.PositionConfidence ? prior : slot;
            var size = prior.WidthConfidence >= slot.WidthConfidence ? prior : slot;
            result[slot.Window] = position with
            {
                Width = size.Width, PreferWidth = size.PreferWidth, Height = prior.Height,
                Confidence = Math.Max(position.PositionConfidence, Math.Max(size.WidthConfidence, prior.HeightConfidence)),
                AxisConfidence = new(position.PositionConfidence, size.WidthConfidence, prior.HeightConfidence),
            };
        }
        return result.Values.ToArray();
    }

    internal PlacementPreference[] BindSlots(IEnumerable<(WindowSnapshot Window, string? Key)> entries)
    {
        if (AppSlots is not { Length: > 0 }) return [];
        var list = new List<PlacementPreference>();
        foreach (var screen in entries.Where(e => e.Key != null && e.Window.IsManageable).GroupBy(e => e.Window.MonitorHandle))
        {
            var unique = screen.GroupBy(e => e.Key!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single().Window, StringComparer.OrdinalIgnoreCase);
            foreach (var slot in AppSlots)
            {
                if (!unique.TryGetValue(slot.App, out var w)) continue;
                double width = slot.Width ?? Math.Clamp(w.VisualRect.Width / (double)Math.Max(1, w.WorkArea.Width), .22, .80);
                double center = slot.Side switch
                {
                    "left" => Math.Clamp(width / 2d, .15, .45),
                    "right" => Math.Clamp(1 - width / 2d, .55, .85),
                    "center" => .50,
                    _ => Math.Clamp((w.VisualRect.X + w.VisualRect.Width / 2d - w.WorkArea.X) / Math.Max(1, w.WorkArea.Width), 0, 1),
                };
                int order = slot.Side switch { "left" => -1, "right" => 1, _ => 0 };
                if (slot.Side != "either" || slot.Width.HasValue)
                    list.Add(new(w.Handle, 0, center, width, order, -1, .46,
                        PreferWidth: slot.Width.HasValue, PreferCenter: slot.Side != "either"));
            }
        }
        return list.ToArray();
    }
}

public sealed record WorkingPairRule(nint First, nint Second, string FirstSide, double? Overlap, bool OnlyWhenPaired = false)
{
    internal bool Matches(nint a, nint b) => First == a && Second == b || First == b && Second == a;
}
