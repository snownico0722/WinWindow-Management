using System.IO.Compression;
using System.Text.Json;
using NeatWin.Core;

// Follow-ups describe observed effort. They never become inputs to the replayed solve.
internal sealed class ReplayEffort
{
    internal Dictionary<(string Session, string Request), List<WindowAdjustmentObservation>> Linked { get; } = [];
    internal HashSet<(string Session, string Request)> Undone { get; } = [];
    internal Dictionary<(string Session, string Request), List<GestureEvent>> Releases { get; } = [];
    internal int? Starts { get; private set; }
    internal int CompletedReleases { get; private set; }
    internal int MarkedReleases { get; private set; }
    internal int DroppedEvents { get; private set; }
    internal int Unlinked { get; private set; }
    internal int? Total { get; private set; }
    internal int UndoCount { get; private set; }

    internal static ReplayEffort Read(ZipArchive archive, string? session)
    {
        var counts = new ReplayEffort();
        var seen = new HashSet<(string, int, DateTimeOffset, RectI, RectI)>();
        foreach (var file in archive.Entries.Where(e => e.Name.StartsWith("adjustments-") && e.Name.EndsWith(".jsonl")))
        {
            using var reader = new StreamReader(file.Open());
            while (reader.ReadLine() is { } line)
            {
                var edit = JsonSerializer.Deserialize<WindowAdjustmentObservation>(line);
                if (edit == null || session != null && edit.Session != session) continue;
                counts.Total ??= 0;
                if (edit.Outcome is not ("stable" or "assisted-release") ||
                    !seen.Add((edit.Session, edit.WindowId, edit.Time, edit.Start, edit.End))) continue;
                counts.Total++;
                if (string.IsNullOrEmpty(edit.AfterRequest)) { counts.Unlinked++; continue; }
                var key = (edit.Session, edit.AfterRequest);
                if (!counts.Linked.TryGetValue(key, out var group)) counts.Linked[key] = group = [];
                group.Add(edit);
            }
        }
        var observed = new HashSet<(string Session, int Gesture, string Stage)>();
        foreach (var file in archive.Entries.Where(e => e.Name.StartsWith("gestures-") && e.Name.EndsWith(".jsonl")))
        {
            using var reader = new StreamReader(file.Open());
            while (reader.ReadLine() is { } line)
            {
                var batch = JsonSerializer.Deserialize<GestureBatch>(line);
                if (batch == null || session != null && session != batch.Session) continue;
                counts.Starts ??= 0;
                counts.DroppedEvents += batch.DroppedEvents;
                foreach (var gesture in batch.Events)
                {
                    if (!observed.Add((batch.Session, gesture.GestureId, gesture.Stage))) continue;
                    if (gesture.Stage == "start") counts.Starts++;
                    if (gesture.Stage != "release") continue;
                    counts.CompletedReleases++;
                    if (gesture.AutomationMarked) counts.MarkedReleases++;
                    if (gesture.Prior == null) continue;
                    var key = (batch.Session, gesture.Prior.RequestId);
                    if (!counts.Releases.TryGetValue(key, out var group)) counts.Releases[key] = group = [];
                    group.Add(gesture);
                }
            }
        }
        return counts;
    }

    private sealed record UndoEvent(string Session, string RequestId, string Trigger, string? UndoOfRequest);
    internal void ReadUndo(IEnumerable<ZipArchiveEntry> plans, string? session)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var file in plans)
        {
            using var reader = new StreamReader(file.Open());
            while (reader.ReadLine() is { } line)
            {
                var undo = JsonSerializer.Deserialize<UndoEvent>(line);
                if (undo is not { Trigger: "undo" } || session != null && session != undo.Session ||
                    !seen.Add((undo.Session, undo.RequestId))) continue;
                UndoCount++;
                if (undo.UndoOfRequest is { Length: > 0 } prior) Undone.Add((undo.Session, prior));
            }
        }
    }
}
