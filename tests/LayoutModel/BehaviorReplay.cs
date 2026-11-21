using System.IO.Compression;
using System.Text.Json;
using NeatWin.Core;

internal static class BehaviorReplay
{
    private sealed record Prediction(string Rule, int Window, string App, WindowBehaviorAction Action, long Tick);

    internal static void Run(string zip, string candidate, string? sessionFilter)
    {
        var rules = JsonSerializer.Deserialize<WindowBehaviorRules>(File.ReadAllText(candidate), WindowBehaviorRules.Json);
        if (rules?.IsValid() != true) throw new InvalidDataException("Invalid candidate behavior rules.");
        using var archive = ZipFile.OpenRead(zip);
        var frames = new List<WindowBehaviorFrame>();
        foreach (var entry in archive.Entries.Where(e => e.Name.StartsWith("behavior-", StringComparison.Ordinal) &&
                     e.Name.EndsWith(".jsonl", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            while (reader.ReadLine() is { } line)
            {
                var frame = JsonSerializer.Deserialize<WindowBehaviorFrame>(line);
                if (frame is not null && (sessionFilter is null || frame.Session == sessionFilter))
                    frames.Add(frame);
            }
        }
        if (frames.Count == 0)
            throw new InvalidDataException("No behavior timeline frames match this export and session.");

        foreach (var segment in frames.GroupBy(f => (f.Session, f.Segment)))
        {
            var ordered = segment.OrderBy(f => f.Tick).ToArray();
            var predictions = Predict(ordered, rules.Rules!);
            var events = ordered.SelectMany(frame => frame.Events.Select(e => (frame.Tick, Item: e))).ToArray();
            var results = predictions.Select(p =>
            {
                var followUp = events.Where(m => m.Tick >= p.Tick && m.Tick - p.Tick <= 30_000).ToArray();
                var match = followUp.FirstOrDefault(m => m.Item.WindowId == p.Window &&
                    m.Item.Cause == "observed" && DismissalMatches(p.Action, m.Item.Event));
                var outcome = match.Item is not null
                    ? followUp.Any(m => m.Tick > match.Tick && m.Tick - match.Tick <= 10_000 &&
                        m.Item.Event == "appeared" && m.Item.App.Equals(p.App, StringComparison.OrdinalIgnoreCase))
                        ? "returned-soon" : "observed-matching-dismissal"
                    : followUp.Any(m => m.Item.WindowId == p.Window && m.Item.Cause == "winwindow" &&
                        DismissalMatches(p.Action, m.Item.Event)) || ordered[^1].Tick - p.Tick < 30_000
                        ? "follow-up-unknown" : "no-matching-dismissal";
                return new { p.Rule, p.App, p.Tick, Outcome = outcome, ObservedDelay = match.Item is null ? (long?)null : match.Tick - p.Tick };
            }).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                segment.Key.Session, segment.Key.Segment, Frames = ordered.Length,
                Counts = results.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()),
                Proposals = results,
            }));
        }
    }

    private static List<Prediction> Predict(WindowBehaviorFrame[] frames, WindowBehaviorRule[] rules)
    {
        var current = new Dictionary<int, BehaviorWindow>();
        var predictions = new List<Prediction>();
        var sequences = rules.Select(rule => (Rule: rule, Sequence: new WindowBehaviorSequence(rule))).ToArray();
        long previous = frames[0].Tick;
        foreach (var frame in frames)
        {
            // The recorder writes only deltas and a heartbeat. Replay stable state between them.
            for (long tick = previous + 550; tick < frame.Tick && tick - previous <= 5_500; tick += 550)
                Observe(tick, frame.Time.UtcTicks - (frame.Tick - tick) * TimeSpan.TicksPerMillisecond, []);
            var departures = new Dictionary<int, BehaviorDeparture>();
            foreach (var item in frame.Events)
            {
                switch (item.Event)
                {
                    case "appeared":
                        current[item.WindowId] = new(item.WindowId, item.App, item.ProcessStartedUtc, false);
                        break;
                    case "foreground":
                    case "focus-cleared":
                        foreach (var w in current.Values.Where(w => w.Foreground).ToArray())
                            current[w.Id] = w with { Foreground = false };
                        if (item.Event == "foreground")
                            current[item.WindowId] = new(item.WindowId, item.App, item.ProcessStartedUtc, true);
                        break;
                    case "closed":
                    case "hidden":
                    case "minimized":
                    case "unavailable":
                        current.Remove(item.WindowId);
                        if (Enum.TryParse<BehaviorDeparture>(item.Event, ignoreCase: true, out var departure))
                            departures[item.WindowId] = departure;
                        break;
                }
            }
            Observe(frame.Tick, frame.Time.UtcTicks, departures);
            previous = frame.Tick;
        }
        return predictions;

        void Observe(long tick, long utc, Dictionary<int, BehaviorDeparture> departures)
        {
            var windows = current.Values.ToArray();
            var alreadyProposed = new HashSet<int>();
            foreach (var (rule, sequence) in sequences)
            {
                var update = sequence.Observe(tick, utc, windows, departures,
                    new(DelayMilliseconds: rule.DelayMilliseconds), learn: false, canAct: true);
                if (update.Dismiss is int id && alreadyProposed.Add(id) && current.TryGetValue(id, out var source))
                    predictions.Add(new(rule.Id, id, source.App, rule.Action, tick));
            }
        }
    }

    private static bool DismissalMatches(WindowBehaviorAction action, string observed) =>
        action == WindowBehaviorAction.Close ? observed is "closed" or "hidden" : observed == "minimized";
}
