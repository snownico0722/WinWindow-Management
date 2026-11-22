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
    internal LayoutRuleFile Rules { get; }
    private string ProfilePath => Path.Combine(_directory, "intent-reference.json");
    private string DisabledPath => Path.Combine(_directory, "reference-disabled");
    public IntentReferenceStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory;
        Placement = new(_directory);
        Rules = new(_directory);
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

    private string BehaviorFitsPath => Path.Combine(_directory, "behavior-fit.json");

    internal WindowBehaviorProfile ReadBehaviorProfile()
    {
        try
        {
            var profile = ReadCandidate<WindowBehaviorProfile>(BehaviorFitsPath, LayoutRuleFile.Json);
            var fits = (profile.Fits ?? []).Where(pair => pair.Value is
                { Observations: >= 0 and <= 100, DelayMilliseconds: >= 1500 and <= 30_000, Reopens: >= 0 and <= 2 })
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var rules = (profile.Candidates ?? []).Where(rule => rule is { IsLocal: true } && rule.IsValid())
                .DistinctBy(rule => rule.Id).Take(48).ToArray();
            return new(fits, rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void SaveBehaviorProfile(WindowBehaviorProfile profile) =>
        WriteCandidate(BehaviorFitsPath, profile, LayoutRuleFile.Json);

    internal WindowBehaviorRules ReadBehaviorRules()
    {
        try
        {
            var rules = ReadCandidate<WindowBehaviorRules>(Path.Combine(_directory, WindowBehaviorRules.FileName), WindowBehaviorRules.Json);
            if (rules.IsValid()) return rules;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(Rules: []);
    }

    internal void ImportBehaviorRules(string source)
    {
        var rules = ReadCandidate<WindowBehaviorRules>(source, WindowBehaviorRules.Json);
        if (!rules.IsValid()) throw new JsonException("Invalid behavior rules.");
        WriteCandidate(Path.Combine(_directory, WindowBehaviorRules.FileName), rules, WindowBehaviorRules.Json);
    }

    internal void ImportPlacementRules(string source)
    {
        var rules = ReadCandidate<CompiledLayoutRules>(source, LayoutRuleFile.Json);
        if (!rules.IsValid()) throw new JsonException("Invalid placement rules.");
        WriteCandidate(Path.Combine(_directory, LayoutRuleFile.Name), rules, LayoutRuleFile.Json);
    }

    private static T ReadCandidate<T>(string path, JsonSerializerOptions json) where T : class
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > 65536) throw new JsonException("Invalid rules file size.");
        return JsonSerializer.Deserialize<T>(stream, json) ?? throw new JsonException("Empty rules file.");
    }

    private static void WriteCandidate<T>(string path, T value, JsonSerializerOptions json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, value, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Clear()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in LogFiles()) File.Delete(path);
        Placement.Clear();
        File.Delete(ProfilePath);
        File.Delete(ProfilePath + ".tmp");
        File.Delete(BehaviorFitsPath);
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
        using (var guide = typeof(IntentReferenceStore).Assembly.GetManifestResourceStream("NeatWin.Layout.RecordingGuide")!)
        using (var output = archive.CreateEntry("layout-guide.md").Open())
            guide.CopyTo(output);
        using (var rules = archive.CreateEntry(LayoutRuleFile.Name).Open())
            JsonSerializer.Serialize(rules, Rules.Read(), LayoutRuleFile.Json);
        using (var rules = archive.CreateEntry(WindowBehaviorRules.FileName).Open())
            JsonSerializer.Serialize(rules, ReadBehaviorRules(), WindowBehaviorRules.Json);
        using (var skill = typeof(IntentReferenceStore).Assembly.GetManifestResourceStream("NeatWin.Layout.LearningSkill")!)
        using (var output = archive.CreateEntry("SKILL.md").Open())
            skill.CopyTo(output);
        using (var script = typeof(IntentReferenceStore).Assembly.GetManifestResourceStream("NeatWin.Layout.RecordingAnalysis")!)
        using (var output = archive.CreateEntry("analyze_recording.py").Open())
            script.CopyTo(output);
        using (var preferences = archive.CreateEntry("placement-v1.json").Open())
            JsonSerializer.Serialize(preferences, Placement.Read());
        using var writer = new StreamWriter(archive.CreateEntry("intent-reference.json").Open());
        writer.Write(JsonSerializer.Serialize(Read(respectEnabled: false)));
    }

    private IEnumerable<string> LogFiles() => new[] { "adjustments-*.jsonl", "workspace-*.jsonl", "plans-*.jsonl", "activity-*.jsonl", "behavior-*.jsonl", "gestures-*.jsonl", "focus-*.jsonl", "surfaces-*.jsonl" }
        .SelectMany(pattern => Directory.EnumerateFiles(_directory, pattern));

    internal void AppendActivity(ActivitySample sample, WorkspaceFrame? geometry, long geometryTick) =>
        AppendJson("activity", new { sample.Version, sample.Session, sample.Tick, sample.Frame.Time, sample.ObservedMilliseconds,
            GeometryTick = geometryTick, Frame = geometry, sample.Processes, sample.Windows, sample.Media, sample.InputIdleMilliseconds, sample.AudioTick, sample.DisplayRequired, sample.Usage, sample.CoUse, sample.FocusCoverage });
    internal void AppendFocusBoundary(string session, string reason, FocusCoverage coverage) =>
        AppendJson("focus", new { Version = 1, Session = session, Time = DateTimeOffset.UtcNow, Reason = reason, FocusCoverage = coverage });
    internal void AppendSurface(object row) => AppendJson("surfaces", row);
    internal void AppendGestures(GestureBatch batch) => AppendJson("gestures", batch);
    internal void AppendBehavior(WindowBehaviorFrame frame) => AppendJson("behavior", frame);
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
