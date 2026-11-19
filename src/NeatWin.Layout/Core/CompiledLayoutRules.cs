namespace NeatWin.Core;

// Only anonymous keys from the recorder export may participate in cross-session rules.
public sealed record AppPairRule(string FirstApp, string SecondApp, string FirstSide = "either",
    double? Overlap = null);

public sealed record CompiledLayoutRules(int Version = 1, double MediaOverlap = .31, double AudioPeakThreshold = .02,
    double FragmentChangeThreshold = .08, AppPairRule[]? Pairs = null)
{
    public static CompiledLayoutRules Default { get; } = new(Pairs: []);

    internal bool IsValid()
    {
        static bool Key(string? key) => key is { Length: 24 } && key.All(char.IsAsciiHexDigit);
        if (Version != 1 || MediaOverlap is not (>= 0 and <= .40) ||
            AudioPeakThreshold is not (> 0 and <= 1) || FragmentChangeThreshold is not (> 0 and <= 1) ||
            Pairs?.Length > 64) return false;
        var seen = new HashSet<(string, string)>();
        foreach (var pair in Pairs ?? [])
        {
            if (pair is null || !Key(pair.FirstApp) || !Key(pair.SecondApp) ||
                pair.FirstSide is not ("left" or "right" or "either") ||
                pair.Overlap is not (null or (>= 0 and <= .40))) return false;
            string a = pair.FirstApp.ToUpperInvariant(), b = pair.SecondApp.ToUpperInvariant();
            if (a == b || !seen.Add(string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a))) return false;
        }
        return true;
    }

    internal WorkingPairRule[] Bind(IEnumerable<(WindowSnapshot Window, string? Key)> entries)
    {
        var matches = new List<WorkingPairRule>();
        foreach (var screen in entries.Where(e => e.Key != null && e.Window.IsManageable).GroupBy(e => e.Window.MonitorHandle))
        {
            var unique = screen.GroupBy(e => e.Key!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single().Window.Handle, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Pairs ?? [])
                if (unique.TryGetValue(pair.FirstApp, out var first) && unique.TryGetValue(pair.SecondApp, out var second))
                    matches.Add(new(first, second, pair.FirstSide, pair.Overlap));
        }
        return matches.ToArray();
    }
}

public sealed record WorkingPairRule(nint First, nint Second, string FirstSide, double? Overlap)
{
    internal bool Matches(nint a, nint b) => First == a && Second == b || First == b && Second == a;
}
