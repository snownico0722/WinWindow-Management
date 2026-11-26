using System.Text.Json;
using NeatWin.App;
using NeatWin.Core;
using NeatWin.Reference;

namespace NeatWin.Tests;

public sealed class StoreReadFailureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    // 允许替换但暂时禁止读取：旧实现仍可用默认值成功覆盖文件。
    private static FileStream BlockReads(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Delete);

    [Fact]
    public void SettingsWriteSkipsUnreadableFileAndRetriesAfterUnlock()
    {
        var path = Path.Combine(_dir, "settings.json");
        const string original = """{"NeighborSnapDistance":33,"RecordingEnabled":true}""";
        File.WriteAllText(path, original);
        var store = new SettingsStore(path);
        using (BlockReads(path)) store.VisualSampling = true;
        Assert.Equal(original, File.ReadAllText(path));
        store.VisualSampling = true;
        Assert.True(store.VisualSampling);
        Assert.True(store.RecordingEnabled);
        Assert.Equal(33, store.LoadTidyOptions().NeighborSnapDistance);
    }

    [Fact]
    public void FailedHistoryReadIsNotCachedOrUsedAsWriteBaseline()
    {
        var store = new PlacementHistoryStore(_dir);
        var first = new LayoutHabit(DateTimeOffset.UtcNow, "display", "old", null, .25, .4, 0, -1, false);
        var next = first with { App = "new" };
        string original = JsonSerializer.Serialize(new[] { first });
        var path = Path.Combine(_dir, "placement-v1.json");
        File.WriteAllText(path, original);
        using (BlockReads(path))
        {
            Assert.Empty(store.Read());
            store.Learn(next);
        }
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(first, Assert.Single(store.Read()));
        store.Learn(next);
        Assert.Equal(new[] { "old", "new" }, new PlacementHistoryStore(_dir).Read().Select(s => s.App));
    }

    [Fact]
    public void FailedFeedbackReadIsNotCachedAsEmptyRatingsOrFlushedAsEmptyHistory()
    {
        var store = new PlacementHistoryStore(_dir);
        var first = new SolverFeedback("old", DateTimeOffset.UtcNow, "desktop", true,
            [new(0, "width", 100, 120, 2)], new() { ["a"] = 1, ["b"] = -1 });
        var next = first with { RequestId = "new" };
        string original = JsonSerializer.Serialize(new[] { first });
        var path = Path.Combine(_dir, PlacementHistoryStore.FeedbackFile);
        File.WriteAllText(path, original);
        using (BlockReads(path))
        {
            Assert.Empty(store.DurableFeedback());
            Assert.Empty(store.Ratings());
            store.LearnFeedback(next, durable: true);
            store.FlushFeedback();
        }
        Assert.Equal(original, File.ReadAllText(path));
        Assert.NotEmpty(store.Ratings());
        store.LearnFeedback(next, durable: true);
        store.FlushFeedback();
        Assert.Equal(new[] { "old", "new" }, new PlacementHistoryStore(_dir).DurableFeedback().Select(s => s.RequestId));
    }
}
