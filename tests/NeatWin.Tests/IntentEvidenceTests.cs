using NeatWin.Core;
using NeatWin.Reference;
using System.Text.Json;

namespace NeatWin.Tests;

public sealed class IntentEvidenceTests
{
    private static readonly RectI Area = new(0, 0, 1920, 1080);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static WindowAdjustmentObservation Observation => new(Now, "test-session", 1,
        new(160, 100, 600, 500), new(186, 100, 600, 500), Area, Area, 96, 500,
        [new(800, 100, 600, 500)], "stable");

    [Fact]
    public void NoActionIsNotPositiveEvidence()
    {
        var unchanged = Observation with { Start = Observation.End };
        Assert.Null(IntentEvidence.Extract(unchanged));
    }
    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("superseded")]
    [InlineData("automation")]
    [InlineData("changed-after-release")]
    [InlineData("cross-monitor")]
    [InlineData("unavailable")]
    public void AmbiguousOrInterferedEndpointsAreNotTrainingLabels(string outcome) =>
        Assert.Null(IntentEvidence.Extract(Observation with { Outcome = outcome }));

    [Fact]
    public void DeliberateCompletedRelationProducesOnlyGeometryHint()
    {
        var sample = IntentEvidence.Extract(Observation);
        Assert.NotNull(sample);
        Assert.Equal(14, sample!.GapDip);
        Assert.True(sample.Horizontal);
    }
    [Fact]
    public void CrossMonitorGestureDoesNotPolluteWithinMonitorGap()
    {
        Assert.Null(IntentEvidence.Extract(Observation with { StartWorkArea = new RectI(-1920, 0, 1920, 1080) }));
    }
    [Fact]
    public void OneCorrectionHasZeroReferenceInfluence()
    {
        var sample = IntentEvidence.Extract(Observation)!;
        var hint = IntentEvidence.Resolve(new(1, [sample]), Area, 96, Now);
        Assert.Equal(0, hint.Confidence);
        Assert.Equal(8, hint.GapPixels);
    }
    [Fact]
    public void RepeatedMovesInOneBurstDoNotCreateConfidence()
    {
        var document = new IntentReferenceDocument(1, Enumerable.Range(0, 20)
            .Select(i => new IntentReferenceSample(Now.AddSeconds(-i), "standard", 32, true)).ToArray());
        Assert.Equal(0, IntentEvidence.Resolve(document, Area, 96, Now).Confidence);
    }
    [Fact]
    public void SameSessionSampleFloodIsDeduplicated()
    {
        var document = IntentReferenceDocument.Empty;
        for (var i = 0; i < 20; i++) document = IntentEvidence.Add(document,
            new(Now.AddSeconds(-i), "standard", 32, true), Now);
        Assert.Single(document.Samples);
    }
    [Fact]
    public void InfluenceIsBoundedEvenForManySamples()
    {
        var document = ManySamples(48);
        var hint = IntentEvidence.Resolve(document, Area, 96, Now);
        Assert.InRange(hint.Confidence, 0, 0.25);
        Assert.InRange(hint.GapPixels, 6, 14);
    }
    [Fact]
    public void ContextAndDpiAreNotMixedBlindly()
    {
        var document = ManySamples(24);
        Assert.Equal(8, IntentEvidence.Resolve(document, new RectI(0, 0, 5120, 1440), 96, Now).GapPixels);
        Assert.Equal(IntentEvidence.Resolve(document, Area, 96, Now).GapPixels * 2,
            IntentEvidence.Resolve(document, Area, 192, Now).GapPixels);
    }
    [Fact]
    public void OldFutureAndNonFiniteEvidenceIsIgnored()
    {
        var document = new IntentReferenceDocument(1,
            [new(Now.AddDays(-31), "standard", 8, true), new(Now.AddDays(1), "standard", 8, true), new(Now, "standard", double.NaN, true)]);
        Assert.Empty(IntentEvidence.Normalize(document, Now).Samples);
    }
    [Fact]
    public void UnsupportedSchemaCannotInfluenceLayout() =>
        Assert.Empty(IntentEvidence.Normalize(ManySamples(48) with { Version = 99 }, Now).Samples);

    [Fact]
    public void ReferenceStorageRoundTripsAndDisableDoesNotDeleteData()
    {
        WithStore((store, path) =>
        {
            store.Append(Observation);
            Assert.Single(store.Read().Samples);
            store.SetEnabled(false);
            Assert.Empty(store.Read().Samples);
            Assert.Single(store.Read(respectEnabled: false).Samples);
            store.SetEnabled(true);
            Assert.Single(store.Read().Samples);
            Assert.Single(Directory.GetFiles(path, "adjustments-*.jsonl"));
        });
    }
    [Fact]
    public void CorruptAndOversizeProfileFallBackWithoutCrashing()
    {
        WithStore((store, path) =>
        {
            File.WriteAllText(Path.Combine(path, "intent-reference.json"), "{ broken");
            Assert.Empty(store.Read().Samples);
            File.WriteAllText(Path.Combine(path, "intent-reference.json"), new string(' ', 600 * 1024));
            Assert.Empty(store.Read().Samples);
        });
    }
    [Fact]
    public void ClearOnlyRemovesRecorderData()
    {
        WithStore((store, path) =>
        {
            var unrelated = Path.Combine(path, "settings.json");
            File.WriteAllText(unrelated, "keep");
            store.Append(Observation);
            store.Clear();
            Assert.Equal("keep", File.ReadAllText(unrelated));
            Assert.Empty(store.Read().Samples);
            Assert.Empty(Directory.GetFiles(path, "adjustments-*.jsonl"));
        });
    }
    [Fact]
    public void PersistedSchemaContainsNoWindowTitleProcessNameOrPointerHistory()
    {
        var json = JsonSerializer.Serialize(Observation);
        Assert.DoesNotContain("Title", json);
        Assert.DoesNotContain("Process", json);
        Assert.DoesNotContain("Pointer", json);
        Assert.DoesNotContain("Handle", json);
    }
    [Fact]
    public void OldLogsArePrunedWithoutNeedingAnotherGesture()
    {
        WithStore((store, path) =>
        {
            var old = Path.Combine(path, "adjustments-old.jsonl");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-31));
            store.Prune();
            Assert.False(File.Exists(old));
        });
    }
    [Fact]
    public void RawExportIncludesObservationsAndReferenceButNotOtherSettings()
    {
        WithStore((store, path) =>
        {
            store.Append(Observation);
            File.WriteAllText(Path.Combine(path, "settings.json"), "not exported");
            var zip = Path.Combine(path, "export.zip");
            store.ExportRecords(zip);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            Assert.Equal(2, archive.Entries.Count);
            Assert.Contains(archive.Entries, e => e.Name.EndsWith(".jsonl", StringComparison.Ordinal));
            Assert.Contains(archive.Entries, e => e.Name == "intent-reference.json");
        });
    }

    private static IntentReferenceDocument ManySamples(double gap) => new(1,
        Enumerable.Range(1, 100).Select(i => new IntentReferenceSample(Now.AddMinutes(-i), "standard", gap, true)).ToArray());
    private static void WithStore(Action<IntentReferenceStore, string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "NeatWin.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { test(new IntentReferenceStore(path), path); }
        finally { Directory.Delete(path, recursive: true); }
    }
}
