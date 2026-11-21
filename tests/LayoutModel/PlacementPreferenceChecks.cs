using NeatWin.Core;

internal static class PlacementPreferenceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var area = new RectI(0, 0, 1920, 1040);
        var rect = new RectI(80, 100, 700, 600);
        var live = Enumerable.Range(1, 70).Select(id =>
            (Window: new WindowSnapshot(id, rect, rect, area, 1, default, true, id == 1, true, id, 96, (uint)id),
             App: (string?)$"app-{id}", Instance: id.ToString())).ToArray();
        LayoutHabit[] Votes(string? peer, int count, int days, string display = "standard") =>
            Enumerable.Range(0, count).Select(i => new LayoutHabit(now.AddDays(-days).AddHours(-i),
                display, "app-1", peer, .32, .46, 1, -1, false, PartnerCenter: .75, PartnerWidth: .32)).ToArray();
        var old = Votes("app-2", 14, 45);
        var recent = Votes("app-3", 11, 0);
        var solo = Votes(null, 5, 0);
        LayoutHabit[] past = [.. old, .. recent, .. Votes("app-4", 40, 0, "wide"), .. solo];
        var inferred = PlacementPreferences.Bind(live, new(1, 0, 0), past, [], now);
        check(inferred.Single(p => p.Window == 1).Partner == 3,
            "current display and decayed placement confidence choose the recent available companion");

        var known = PlacementPreferences.Bind(live, new(1, 2, 4), past, [], now);
        var absent = PlacementPreferences.Bind(live, new(1, 4, 4), past, [], now);
        check(known.Single(p => p.Window == 1).Partner == 2 && absent.Single(p => p.Window == 1).Partner == 0,
            "an explicit companion uses its matching habit or falls back to the window's solo placement");

        LayoutHabit[] tied = [.. recent, .. Votes("app-2", 11, 0), .. solo];
        var ambiguous = PlacementPreferences.Bind(live, new(1, 0, 0), tied, [], now);
        check(ambiguous.Single(p => p.Window == 1).Partner == 0 && ambiguous.All(p => p.Partner != 1),
            "two equally plausible placement partners preserve the solo preference in both directions");

        var inbound = recent.SelectMany(s => new[] { "app-2", "app-3" }.Select(app => s with
            { App = app, Partner = "app-1", PartnerWidth = null, PartnerCenter = null })).ToArray();
        check(PlacementPreferences.Bind(live, new(1, 0, 0), [..inbound, ..solo], [], now).All(p => p.Partner == 0),
            "legacy one-direction records cannot recruit the same main window for two equally likely tasks");

        var widthVotes = recent.Select(s => s with { Edited = HorizontalEdit.Width, Width = .72 }).ToArray();
        var knownWidth = PlacementPreferences.Bind(live, new(1, 3, 1), widthVotes, [], now);
        check(knownWidth.Single(p => p.Window == 1) is { Partner: 0, PreferWidth: true, PreferCenter: false } width &&
            Math.Abs(width.Width - .72) < .001 && PlacementPreferences.Bind(live, new(1, 0, 0), widthVotes, [], now).Length == 0,
            "pair-specific resize evidence sets the width for a confirmed partner and never guesses an unconfirmed partner");
    }
}
