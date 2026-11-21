using System.IO.Compression;
using System.Text.Json;
using NeatWin.Core;

// Older troubleshooting snapshots kept native window metadata in their timeline. Restore a
// weak semantic hint only when a contemporary preceding sample matches unambiguously.
internal sealed class SnapshotRoles
{
    private sealed record Metadata(DateTimeOffset Start);
    private sealed record Request(string Session, string RequestId);
    private sealed record Window(string? App, string? Class, RectI? Frame, uint? Dpi,
        bool Visible, bool Minimized, bool Cloaked, bool Foreground);
    private sealed record Frame(long ElapsedMs, Window[] Windows);
    private sealed record Sample(DateTimeOffset Time, Dictionary<(RectI Rect, uint Dpi, bool Foreground), WindowUseRole> Roles);
    private readonly List<Sample> _samples = [];
    private readonly HashSet<Request> _requests = [];

    internal SnapshotRoles(ZipArchive archive)
    {
        if (archive.GetEntry("timeline.jsonl") is not { } timeline ||
            archive.GetEntry("layout-decisions.jsonl") is not { } decisions) return;
        using (var plans = new StreamReader(decisions.Open()))
            while (plans.ReadLine() is { } plan)
                if (JsonSerializer.Deserialize<Request>(plan) is { Session.Length: > 0, RequestId.Length: > 0 } request)
                    _requests.Add(request);
        using var reader = new StreamReader(timeline.Open());
        if (reader.ReadLine() is not { } header || JsonSerializer.Deserialize<Metadata>(header) is not { } meta) return;
        while (reader.ReadLine() is { } line)
        {
            if (JsonSerializer.Deserialize<Frame>(line) is not { Windows: not null } frame) continue;
            var unique = frame.Windows.Where(w => w.Visible && !w.Minimized && !w.Cloaked && w.Frame.HasValue && w.Dpi.HasValue)
                .GroupBy(w => (w.Frame!.Value, w.Dpi!.Value, w.Foreground)).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => WindowUseRoles.Identify(g.First().App, g.First().Class));
            _samples.Add(new(meta.Start.AddMilliseconds(frame.ElapsedMs), unique));
        }
    }

    internal TaskWindowHint[] Enrich(string session, string requestId, DateTimeOffset time, IReadOnlyList<WindowSnapshot> desktop, TaskLayoutContext context)
    {
        var captured = context.WindowHints ?? [];
        if (!_requests.Contains(new(session, requestId))) return captured;
        var sample = _samples.LastOrDefault(s => s.Time <= time);
        if (sample == null || (time - sample.Time).TotalMilliseconds > 1250 ||
            captured.Any(h => h.Role != WindowUseRole.Unknown || h.FloatingAffinity > 0)) return captured;
        var roles = desktop.GroupBy(w => (w.VisualRect, w.Dpi, w.IsForeground)).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Single().Handle, g => sample.Roles.GetValueOrDefault(g.Key));
        return WindowUseRoles.Bind(desktop, captured, context.Focus, context.Attention, context.Placement,
            window => roles.GetValueOrDefault(window.Handle));
    }
}
