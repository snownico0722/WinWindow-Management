using System.IO.Compression;
using System.Text.Json;
using NeatWin.Core;

// Follow-ups describe observed effort. They never become inputs to the replayed solve.
internal sealed class ReplayEffort
{
    internal Dictionary<(string Session, string Request), List<WindowAdjustmentObservation>> Linked { get; } = [];
    internal HashSet<(string Session, string Request)> Undone { get; } = [];
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
