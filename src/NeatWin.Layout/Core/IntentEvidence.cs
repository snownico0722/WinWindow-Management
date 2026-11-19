namespace NeatWin.Core;

// Geometry only. A stable endpoint is an observation, NOT a label saying the layout is good.
public sealed record WindowAdjustmentObservation(
    DateTimeOffset Time, string Session, int WindowId,
    RectI Start, RectI End, RectI StartWorkArea, RectI WorkArea,
    uint Dpi, long DurationMilliseconds, RectI[] Neighbors, string Outcome,
    int Version = 1, WorkspaceFrame? StartContext = null, WorkspaceFrame? EndContext = null,
    WorkspaceFrame? SettledContext = null);

public sealed record IntentReferenceSample(
    DateTimeOffset Time, string Context, double GapDip, bool Horizontal);

public sealed record IntentReferenceDocument(int Version, IntentReferenceSample[] Samples,
    RelationReferenceSample[]? Relations = null)
{
    public static IntentReferenceDocument Empty => new(1, []);
}

public readonly record struct IntentHint(int GapPixels, double HorizontalPreference, double Confidence,
    double MaximumEdgeOverlap = 0.22, double StackPreference = 0);

public static class IntentEvidence
{
    public const int MaximumSamples = 128;

    public static string ContextFor(RectI area) => (area.Width / (double)Math.Max(1, area.Height)) switch
    {
        < 0.9 => "portrait",
        >= 2.4 => "wide",
        _ => "standard",
    };

    public static bool IsValidRect(RectI rect) =>
        rect.Width is > 0 and <= 100_000 && rect.Height is > 0 and <= 100_000 &&
        Math.Abs((long)rect.X) <= 1_000_000 && Math.Abs((long)rect.Y) <= 1_000_000;

    public static IntentReferenceSample? Extract(WindowAdjustmentObservation observation)
    {
        if (observation.Outcome != "stable" || observation.StartWorkArea != observation.WorkArea ||
            !IsValidRect(observation.Start) || !IsValidRect(observation.End) ||
            !IsValidRect(observation.WorkArea) || observation.Dpi is < 48 or > 768 ||
            observation.DurationMilliseconds is < 100 or > 120_000 ||
            observation.End.Intersect(observation.WorkArea).Area != observation.End.Area)
            return null;

        var scale = observation.Dpi / 96.0;
        IntentReferenceSample? best = null;
        foreach (var neighbor in StationaryNeighbors(observation))
        {
            if (!IsValidRect(neighbor)) continue;
            foreach (var horizontal in new[] { true, false })
            {
                var end = Relation(observation.End, neighbor, horizontal);
                var start = Relation(observation.Start, neighbor, horizontal);
                if (end.Overlap < 0.5 || end.Gap < 0 || end.Gap > 48 * scale) continue;
                // Evidence is the deliberate completion of a relation, not simply standing still.
                var improved = Math.Abs(start.Gap - end.Gap) >= 8 * scale ||
                    (start.Alignment - end.Alignment >= 12 * scale && end.Alignment <= 4 * scale);
                if (!improved) continue;
                var candidate = new IntentReferenceSample(observation.Time, ContextFor(observation.WorkArea),
                    Math.Clamp(end.Gap / scale, 0, 48), horizontal);
                if (best is null || candidate.GapDip < best.GapDip) best = candidate;
            }
        }
        return best;
    }

    public static IntentReferenceDocument Normalize(IntentReferenceDocument? document, DateTimeOffset now)
    {
        if (document is null || document.Version is not 1 and not 2) return IntentReferenceDocument.Empty;
        return new(1, (document.Samples ?? [])
            .Where(s => s is not null && s.Time <= now && s.Time >= now.AddDays(-30) &&
                double.IsFinite(s.GapDip) && s.GapDip is >= 0 and <= 48 &&
                s.Context is "wide" or "standard" or "portrait")
            .OrderBy(s => s.Time).TakeLast(MaximumSamples).ToArray(),
            (document.Relations ?? []).Where(s => s is not null && s.Time <= now && s.Time >= now.AddDays(-30) &&
                s.Context is "wide" or "standard" or "portrait" && s.WindowCount is >= 2 and <= 8 &&
                s.Kind is "edge-overlap" or "stack" && double.IsFinite(s.OverlapRatio) && s.OverlapRatio is > 0 and <= 1)
            .OrderBy(s => s.Time).TakeLast(MaximumSamples).ToArray());
    }

    public static IntentReferenceDocument Add(IntentReferenceDocument document, IntentReferenceSample sample,
        DateTimeOffset now)
    {
        var clean = Normalize(document, now);
        // Many corrections during one short adjustment session must not look like repeated preference.
        if (clean.Samples.Any(s => s.Context == sample.Context &&
            Math.Abs((s.Time - sample.Time).TotalSeconds) < 30)) return clean;
        return Normalize(new(1, [.. clean.Samples, sample], clean.Relations), now);
    }

    public static IntentHint Resolve(IntentReferenceDocument? document, RectI area, uint dpi, DateTimeOffset now)
    {
        var scale = Math.Clamp(dpi, 48u, 768u) / 96.0;
        var samples = Normalize(document, now).Samples.Where(s => s.Context == ContextFor(area)).ToArray();
        var related = Normalize(document, now).Relations?.Where(s => s.Context == ContextFor(area)).ToArray() ?? [];
        var dispersed = related.Length >= 6 && related.Select(s => s.Time.ToUnixTimeSeconds() / 300).Distinct().Count() >= 3;
        // Geometry observations are weak evidence; never infer satisfaction or semantic importance.
        var stackPreference = dispersed ? Math.Min(0.08, related.Where(s => s.Kind == "stack").Sum(s => Math.Pow(0.5, (now - s.Time).TotalDays / 14)) / 160.0) : 0;
        var fallback = new IntentHint((int)Math.Round(8 * scale), 0, 0, 0.22, stackPreference);
        if (samples.Length < 6 || samples.Select(s => s.Time.ToUnixTimeSeconds() / 300).Distinct().Count() < 3)
            return fallback;
        var sorted = samples.Select(s => s.GapDip).Order().ToArray();
        var median = sorted[sorted.Length / 2];
        var deviation = sorted.Select(x => Math.Abs(x - median)).Order().ElementAt(sorted.Length / 2);
        var effectiveCount = samples.Sum(s => Math.Pow(0.5, (now - s.Time).TotalDays / 14));
        var confidence = Math.Min(0.25, effectiveCount / 80) / (1 + deviation / 8);
        // At most 25% reference influence; never import learned preserve/movement/resize penalties.
        var gapDip = Math.Clamp(8 + confidence * (median - 8), 6, 14);
        var horizontal = samples.Average(s => s.Horizontal ? 1.0 : -1.0);
        return new((int)Math.Round(gapDip * scale), horizontal, confidence, 0.22, stackPreference);
    }

    internal static IEnumerable<RectI> StationaryNeighbors(WindowAdjustmentObservation observation)
    {
        if (observation.Version < 2) return (observation.Neighbors ?? []).Take(24);
        if (observation.StartContext is not { Truncated: false } before ||
            observation.EndContext is not { Truncated: false } after ||
            observation.SettledContext is not { Truncated: false } settled) return [];
        if (!before.Windows.Any(w => w.Id == observation.WindowId) ||
            !after.Windows.Any(w => w.Id == observation.WindowId) ||
            !settled.Windows.Any(w => w.Id == observation.WindowId)) return [];
        return after.Windows.Where(w => w.Id != observation.WindowId && w.Manageable && w.VisibleRatio > 0 && w.WorkArea == observation.WorkArea &&
            before.Windows.Any(b => b.Id == w.Id && b.Rect == w.Rect && b.WorkArea == w.WorkArea) &&
            settled.Windows.Any(b => b.Id == w.Id && b.Rect == w.Rect && b.WorkArea == w.WorkArea && b.Manageable))
            .Take(24).Select(w => w.Rect);
    }

    public static RelationReferenceSample? ExtractRelation(WindowAdjustmentObservation o)
    {
        if (o.Version < 2 || o.Outcome != "stable" || o.StartWorkArea != o.WorkArea ||
            o.StartContext is not { Truncated: false } || o.EndContext is not { Truncated: false } ||
            o.SettledContext is not { Truncated: false } || !IsValidRect(o.End) || o.Dpi is < 48 or > 768 ||
            o.DurationMilliseconds is < 100 or > 120_000 || o.End.Intersect(o.WorkArea).Area != o.End.Area ||
            o.Start == o.End) return null;
        var scale = o.Dpi / 96.0;
        if (Math.Abs(o.End.X - o.Start.X) + Math.Abs(o.End.Y - o.Start.Y) < 8 * scale) return null;
        foreach (var neighbor in StationaryNeighbors(o))
        {
            if (!IsValidRect(neighbor) || o.End.Intersect(neighbor).Area == 0) continue;
            var overlap = Math.Min(o.End.Right, neighbor.Right) - Math.Max(o.End.Left, neighbor.Left);
            var ratio = overlap / (double)Math.Min(o.End.Width, neighbor.Width);
            if (o.End.VerticalOverlapRatio(neighbor) < 0.5 || ratio <= 0) continue;
            var count = o.EndContext.Windows.Count(w => w.Manageable && w.VisibleRatio > 0 && w.WorkArea == o.WorkArea);
            if (count is < 2 or > 8) continue;
            // Describe the relationship observed, not an assertion that its endpoint was optimal.
            return new(o.Time, ContextFor(o.WorkArea), count, ratio <= 0.25 ? "edge-overlap" : "stack", ratio);
        }
        return null;
    }

    public static IntentReferenceDocument AddRelation(IntentReferenceDocument document,
        RelationReferenceSample sample, DateTimeOffset now)
    {
        var clean = Normalize(document, now);
        var relations = clean.Relations ?? [];
        if (relations.Any(s => s.Context == sample.Context && Math.Abs((s.Time - sample.Time).TotalSeconds) < 30)) return clean;
        return Normalize(clean with { Relations = [.. relations, sample] }, now);
    }

    private static (double Gap, double Overlap, double Alignment) Relation(RectI a, RectI b, bool horizontal)
    {
        if (horizontal)
            return (a.Left + a.Width / 2.0 < b.Left + b.Width / 2.0 ? b.Left - a.Right : a.Left - b.Right,
                a.VerticalOverlapRatio(b), Math.Min(Math.Abs(a.Top - b.Top), Math.Abs(a.Bottom - b.Bottom)));
        return (a.Top + a.Height / 2.0 < b.Top + b.Height / 2.0 ? b.Top - a.Bottom : a.Top - b.Bottom,
            a.HorizontalOverlapRatio(b), Math.Min(Math.Abs(a.Left - b.Left), Math.Abs(a.Right - b.Right)));
    }
}
