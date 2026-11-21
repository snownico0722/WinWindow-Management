namespace NeatWin.Core;

internal static class PlacementPreferences
{
    internal const string ModelVersion = "v1.0";

    private const double MinimumConfidence = .40;
    private const double ConfidenceMargin = .08;
    private sealed record Choice(PlacementPreference? Preference, bool Ambiguous, bool FromActivePair);

    internal static PlacementPreference[] Bind((WindowSnapshot Window, string? App, string Instance)[] entries,
        LayoutFocus focus, IReadOnlyList<LayoutHabit> stored, IReadOnlyList<LayoutHabit> session, DateTimeOffset now)
    {
        var choices = new Dictionary<nint, Choice>();
        var solo = new Dictionary<nint, PlacementPreference>();
        // Instance votes only describe this particular live window. Apply them after app-wide priors.
        Add(stored, instance: false);
        Add(session, instance: true);
        var preferred = choices.Values.Select(c => c.Preference).OfType<PlacementPreference>().ToArray();
        // Older records can lack the stationary window's geometry. Two incoming proposals
        // can then contend for it even though there is no preference for that window itself.
        var incoming = preferred.Where(p => p.Partner != 0).ToLookup(p => p.Partner);
        var accepted = new Dictionary<nint, nint>();
        foreach (var group in incoming)
        {
            if (choices.TryGetValue(group.Key, out var target) && target.Ambiguous) continue;
            nint known = ActivePeer(group.Key);
            if (known == 0) known = target?.Preference?.Partner ?? 0;
            if (known != 0) { accepted[group.Key] = known; continue; }
            var ranked = group.OrderByDescending(p => p.Confidence).Take(2).ToArray();
            if (IsClear(ranked)) accepted[group.Key] = ranked[0].Window;
        }
        return preferred.Select(p => p.Partner == 0 || accepted.GetValueOrDefault(p.Partner) == p.Window
                ? p : solo.GetValueOrDefault(p.Window)).OfType<PlacementPreference>().ToArray();

        nint ActivePeer(nint window) => window == focus.Main ? focus.Companion : window == focus.Companion ? focus.Main : 0;

        void Add(IReadOnlyList<LayoutHabit> samples, bool instance)
        {
            if (samples.Count == 0) return;
            var indexed = Index(samples).ToLookup(item => (item.Sample.Display, item.Key));
            foreach (var monitor in entries.GroupBy(e => e.Window.MonitorHandle))
            {
                // 同屏两份文档可能共用应用标识。只有标识唯一时才能把票投给具体窗口，否则一次左侧习惯会同时套到两份文档上。
                var live = monitor.Select(e => (e.Window, Key: instance ? e.Instance : e.App))
                    .Where(e => !string.IsNullOrEmpty(e.Key)).GroupBy(e => e.Key!)
                    .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Window);
                var keys = live.ToDictionary(p => p.Value.Handle, p => p.Key);
                foreach (var (key, window) in live)
                {
                    var matches = indexed[(IntentEvidence.ContextFor(window.WorkArea), key)];
                    if (!matches.Any()) continue;
                    var votes = matches.ToLookup(item => item.Peer, item => item.Sample);
                    var own = Resolve(votes[null], null, 0);
                    if (own != null) solo[window.Handle] = own;

                    nint active = ActivePeer(window.Handle);
                    PlacementPreference? best = null;
                    bool ambiguous = false;
                    if (active != 0)
                    {
                        if (keys.TryGetValue(active, out var peer)) best = Resolve(votes[peer], peer, active);
                        if (best == null && choices.GetValueOrDefault(window.Handle)?.FromActivePair == true) continue;
                    }
                    else
                    {
                        // A size learned with a particular partner applies once that partner
                        // is known. Width and height alone cannot identify the current partner.
                        var ranked = votes.Where(g => g.Key != null && g.Key != key && live.ContainsKey(g.Key))
                            .Select(g => Resolve(g, g.Key, live[g.Key!].Handle))
                            .OfType<PlacementPreference>().Where(p => p.Partner != 0 && ActivePeer(p.Partner) == 0)
                            .OrderByDescending(p => p.Confidence).Take(2).ToArray();
                        ambiguous = ranked.Length > 0 && !IsClear(ranked);
                        if (ranked.Length > 0 && !ambiguous) best = ranked[0];
                    }
                    if (best != null || own != null || ambiguous)
                        choices[window.Handle] = new(best ?? solo.GetValueOrDefault(window.Handle), ambiguous, active != 0 && best != null);

                    PlacementPreference? Resolve(IEnumerable<LayoutHabit> history, string? peer, nint handle) =>
                        LayoutHabits.Resolve(history, window, key, peer, handle, now, instance) is { Confidence: >= MinimumConfidence } p ? p : null;
                }
            }
        }
    }

    private static bool IsClear(PlacementPreference[] ranked) =>
        ranked.Length == 1 || ranked[0].Confidence - ranked[1].Confidence >= ConfidenceMargin;

    private static IEnumerable<(string Key, string? Peer, LayoutHabit Sample)> Index(IReadOnlyList<LayoutHabit> samples)
    {
        foreach (var sample in samples)
        {
            yield return (sample.App, sample.Partner, sample);
            if (sample.Partner != null && sample.Partner != sample.App && sample.PartnerCenter != null && sample.PartnerWidth != null &&
                (sample.Edited ?? HorizontalEdit.Position).HasFlag(HorizontalEdit.Position))
                yield return (sample.Partner, sample.App, sample);
        }
    }
}
