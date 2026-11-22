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
        var sameApp = live.Take(2).Select(e => (e.Window, App: (string?)"browser", e.Instance)).ToArray();
        var scoped = solo.Select(s => s with { App = "browser/reading", Usage = PlacementUsage.ScrollReading }).ToArray();
        var knownRole = PlacementPreferences.Bind(sameApp, new(1, 0, 0), scoped, [], now,
            (_, _) => PlacementUsage.ScrollReading, h => h == 1 ? "reading" : "playback");
        check(knownRole is [{ Window: 1 }] && PlacementPreferences.Bind(sameApp, new(1, 0, 0), scoped, [], now,
            (_, _) => PlacementUsage.ScrollReading, _ => "reading").Length == 0,
            "cross-session same-app placement binds only to a uniquely evidenced use");
        ObservedWindow Observed(int id, RectI r) => new(id, r, area, 96, id-1, id == 1, false, true, 1, AppKey: "browser");
        var origin = rect with { X = 130 };
        var start = new WorkspaceFrame(now, [Observed(1, origin), Observed(2, rect with { X = 1000 })]);
        var end = new WorkspaceFrame(now, [Observed(1, rect), start.Windows[1]]);
        var edit = new WindowAdjustmentObservation(now, "task", 1, origin, rect, area, area, 96, 350, [], "stable",
            StartContext: start, EndContext: end, SettledContext: end, StartUsage: new(1, 2, false, [2], [1]));
        check(LayoutHabits.Observe(edit) is { App: "browser/reading", Partner: "browser/playback" } &&
            LayoutHabits.Observe(edit with { StartUsage = new(1, 2, false, [], [1, 2]) }) == null,
            "two reopened browser surfaces can teach durable distinct uses, while indistinguishable readers cannot");
        var knownWidth = PlacementPreferences.Bind(live, new(1, 3, 1), widthVotes, [], now);
        check(knownWidth.Single(p => p.Window == 1) is { Partner: 0, PreferWidth: true, PreferCenter: false } width &&
            Math.Abs(width.Width - .72) < .001 && PlacementPreferences.Bind(live, new(1, 0, 0), widthVotes, [], now).Length == 0,
            "pair-specific resize evidence sets the width for a confirmed partner and never guesses an unconfirmed partner");

        // 人工样本覆盖一段舒适宽度。验证点取在样本之间，也覆盖越界、分歧和回放三条实际路径。
        double[] widths = [.34, .38, .35, .39, .37, .34, .38, .36, .35, .37, .88];
        var samples = recent.Select((v, i) => v with
            { Edited = HorizontalEdit.Width, Width = widths[i], Height = .22 + widths[i] / 2 }).ToArray();
        PlacementPreference Learn(int width) => LayoutHabits.Resolve(samples, live[0].Window with
            { VisualRect = rect with { Width = width, Height = 422 } }, "app-1", "app-3", 3, now)!;
        var inside = Learn(701);
        var low = Learn(410);
        var high = Learn(980);
        check(inside is { PreferCenter: false, Bands.Center: null, Bands.Width.High: <= .39, Bands.Height: not null } &&
            inside.Width == 701d / area.Width && inside.Height == 422d / area.Height &&
            low.Width == inside.Bands.Width!.Low && high.Width == inside.Bands.Width.High && low.Width < high.Width,
            "unseen sizes inside a coherent band stay put, outside sizes target its nearest boundary and an outlier cannot widen it");
        var positions = recent.Select((v, i) => v with { Center = widths[i], Edited = HorizontalEdit.Position }).ToArray();
        var moved = live[0].Window with { VisualRect = new(381, 80, 640, 700) };
        var left = LayoutHabits.Resolve(positions, moved, "app-1", "app-3", 3, now)!;
        check(left is { Bands.Center: not null, Bands.Width: null, PreferWidth: false } &&
            left.Center == 701d / area.Width, "casual placement preserves an unseen horizontal center within the preferred side");
        var slot = new PlacementPreference(1, 0, .5, .47, 0, -1, 1, PreferCenter: false);
        var merged = CompiledLayoutRules.ApplySlots([inside], [slot]).Single();
        check(merged.Width == .47 && merged.Bands?.Width == null && merged.Bands?.Height == inside.Bands?.Height,
            "an explicit winning width stays exact while independently learned height retains its band");
        var recorded = RecordedTaskSignals.From(new(Placement: [inside]), new Dictionary<nint, int> { [1] = 1 });
        var json = System.Text.Json.JsonSerializer.Serialize(recorded);
        var replay = System.Text.Json.JsonSerializer.Deserialize<RecordedTaskSignals>(json)!.ApplyToReplay(new());
        var legacy = RecordedTaskSignals.From(new(Placement: [slot]), new Dictionary<nint, int> { [1] = 1 });
        check(replay.Placement!.Single().Bands == inside.Bands &&
            !System.Text.Json.JsonSerializer.Serialize(legacy).Contains("\"Bands\""),
            "recorded bands round-trip and old point-only inputs keep their fingerprint");
        var target = new AttentionTarget(AttentionRole.Main, 701, 422, 100, true, true, LearnedHeight: true,
            LearnedWidth: true, Bands: inside.Bands);
        check(target.WidthFor(711, area.Width) == 711 && target.HeightFor(418, area.Height) == 418 &&
            (target with { FrameWidth = 680 }).WidthFor(711, area.Width) == 701 &&
            target.WidthFor(980, area.Width) < 980, "geometry within the observed band has no personal size penalty and a binding pair budget survives");
    }
}
