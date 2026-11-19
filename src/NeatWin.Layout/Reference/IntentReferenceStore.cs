using System.Text.Json;
using System.IO.Compression;
using NeatWin.Core;
using NeatWin.Recording;

namespace NeatWin.Reference;

public sealed class IntentReferenceStore
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeatWin", "Recorder");
    private readonly string _directory;
    internal string DirectoryPath => _directory;
    internal PlacementHistoryStore Placement { get; }
    private string ProfilePath => Path.Combine(_directory, "intent-reference.json");
    private string DisabledPath => Path.Combine(_directory, "reference-disabled");
    public IntentReferenceStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory;
        Placement = new(_directory);
    }
    public bool Enabled => !File.Exists(DisabledPath);

    public void SetEnabled(bool enabled)
    {
        Directory.CreateDirectory(_directory);
        if (enabled) File.Delete(DisabledPath);
        else File.WriteAllText(DisabledPath, "Reference disabled by user.");
    }

    public IntentReferenceDocument Read(bool respectEnabled = true)
    {
        if (respectEnabled && !Enabled) return IntentReferenceDocument.Empty;
        try
        {
            using var stream = new FileStream(ProfilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 512 * 1024) return IntentReferenceDocument.Empty;
            return IntentEvidence.Normalize(JsonSerializer.Deserialize<IntentReferenceDocument>(stream), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return IntentReferenceDocument.Empty;
        }
    }

    // Recorder is the only profile writer. The main application is strictly a reader.
    public void Append(WindowAdjustmentObservation observation)
    {
        Directory.CreateDirectory(_directory);
        Prune();
        var prefix = $"adjustments-{DateTime.UtcNow:yyyyMMdd}";
        var index = 0;
        string log;
        do { log = Path.Combine(_directory, $"{prefix}-{index++:D3}.jsonl"); }
        while (File.Exists(log) && new FileInfo(log).Length > 2 * 1024 * 1024 && index < 1000);
        using (var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.Read)))
            writer.WriteLine(JsonSerializer.Serialize(observation));
        Placement.Learn(observation);
        var sample = IntentEvidence.Extract(observation);
        var relation = IntentEvidence.ExtractRelation(observation);
        if (sample is not null || relation is not null)
        {
            var document = Read(respectEnabled: false);
            if (sample is not null) document = IntentEvidence.Add(document, sample, DateTimeOffset.UtcNow);
            if (relation is not null) document = IntentEvidence.AddRelation(document, relation, DateTimeOffset.UtcNow);
            var temporary = ProfilePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, ProfilePath, overwrite: true);
        }
        Prune();
    }

    public void ExportReference(string destination) =>
        File.WriteAllText(destination, JsonSerializer.Serialize(Read(respectEnabled: false), new JsonSerializerOptions { WriteIndented = true }));

    public void Clear()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in LogFiles()) File.Delete(path);
        Placement.Clear();
        File.Delete(ProfilePath);
        File.Delete(ProfilePath + ".tmp");
    }

    public void ExportRecords(string destination)
    {
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        if (Directory.Exists(_directory))
            foreach (var file in LogFiles())
            {
                try
                {
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var output = archive.CreateEntry(Path.GetFileName(file)).Open();
                    input.CopyTo(output);
                }
                catch (FileNotFoundException) { }
            }
        using (var preferences = archive.CreateEntry("placement-v1.json").Open())
            JsonSerializer.Serialize(preferences, Placement.Read());
        using var writer = new StreamWriter(archive.CreateEntry("intent-reference.json").Open());
        writer.Write(JsonSerializer.Serialize(Read(respectEnabled: false)));
    }

    private IEnumerable<string> LogFiles() => new[] { "adjustments-*.jsonl", "workspace-*.jsonl", "plans-*.jsonl", "activity-*.jsonl" }
        .SelectMany(pattern => Directory.EnumerateFiles(_directory, pattern));

    internal void AppendActivity(ActivitySample sample, WorkspaceFrame? geometry, long geometryTick) =>
        AppendJson("activity", new { sample.Version, sample.Session, sample.Tick, sample.Frame.Time, sample.ObservedMilliseconds,
            GeometryTick = geometryTick, Frame = geometry, sample.Processes, sample.Windows, sample.Media, sample.InputIdleMilliseconds });
    public void AppendTransition(WorkspaceTransition observation) => AppendJson("workspace", observation);
    public void AppendPlan(LayoutRunObservation observation) => AppendJson("plans", observation);

    private void AppendJson<T>(string prefix, T value)
    {
        Directory.CreateDirectory(_directory);
        var index = 0; string path;
        do { path = Path.Combine(_directory, $"{prefix}-{DateTime.UtcNow:yyyyMMdd}-{index++:D3}.jsonl"); }
        while (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024 && index < 1000);
        using (var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)))
            writer.WriteLine(JsonSerializer.Serialize(value));
        Prune();
    }

    public void Prune()
    {
        if (!Directory.Exists(_directory)) return;
        var files = LogFiles().Select(p => new FileInfo(p)).Where(f => f.Exists)
            .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long retainedBytes = 0;
        for (var i = 0; i < files.Length; i++)
        {
            try
            {
                retainedBytes += files[i].Length;
                if (i >= 128 || retainedBytes > 128L * 1024 * 1024 || files[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))
                    files[i].Delete();
            }
            catch (FileNotFoundException) { } // Another writer may prune a closed log concurrently.
        }
    }
}
