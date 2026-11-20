using System.Text.Json.Serialization;

namespace NeatWin.Core;

// A sample describes a stable manual edit. Application keys are anonymous and installation-local.
public sealed record LayoutHabit(DateTimeOffset Time, string Display, string App, string? Partner,
    double Center, double Width, int Order, int RevealedCell, bool Corrected, double? InnerOverlap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PartnerCenter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PartnerWidth = null);

public sealed record PlacementPreference(nint Window, nint Partner, double Center, double Width,
    double Order, int RevealedCell, double Confidence, double? InnerOverlap = null);

public sealed record LayoutFocus(nint Main, nint Companion, int Switches, string Evidence = "recent-switch-pair");

public sealed class ForegroundHistory
{
    private readonly List<(nint Handle, uint Pid, long Time)> _visits = [];

    public void Visit(nint handle, uint pid, long now)
    {
        if (handle == 0 || pid == 0 || pid == Environment.ProcessId) return;
        lock (_visits)
        {
            if (_visits.Count > 0 && _visits[^1].Handle == handle && _visits[^1].Pid == pid)
                return;
            // Collapse transient Alt+Tab pass-throughs (A -> B -> C where B dwelled < 450ms and A != C).
            if (_visits.Count >= 2 && _visits[^1].Handle != 0 && _visits[^2].Handle != handle &&
                now - _visits[^1].Time is >= 0 and < 450)
                _visits.RemoveAt(_visits.Count - 1);
            _visits.Add((handle, pid, now));
            if (_visits.Count > 64) _visits.RemoveAt(0);
        }
    }

    public void Retire(nint handle)
    {
        lock (_visits)
            // Keep the interruption in the sequence without trusting a reused handle.
            for (var i = 0; i < _visits.Count; i++)
                if (_visits[i].Handle == handle) _visits[i] = (0, 0, _visits[i].Time);
    }

    public LayoutFocus Resolve(IReadOnlyList<WindowSnapshot> windows, long now)
    {
        lock (_visits)
        {
            var valid = windows.Where(w => w.IsManageable).ToDictionary(w => w.Handle);
            bool IsCurrentVisit((nint Handle, uint Pid, long Time) v) => now - v.Time is >= 0 and <= 600_000 &&
                valid.TryGetValue(v.Handle, out var w) && w.ProcessId == v.Pid;
            var main = valid.Values.FirstOrDefault(w => w.IsForeground)?.Handle ??
                _visits.LastOrDefault(IsCurrentVisit).Handle;
            if (main == 0) return new(0, 0, 0);

            var monitor = valid[main].MonitorHandle;
            var traffic = new Dictionary<nint, double>();
            var partners = new Dictionary<nint, (int Count, double Weight)>();
            for (var i = 1; i < _visits.Count; i++)
            {
                var a = _visits[i - 1];
                var b = _visits[i];
                if (a.Handle == b.Handle || b.Time - a.Time is < 0 or > 120_000 ||
                    !IsCurrentVisit(a) || !IsCurrentVisit(b) ||
                    valid[a.Handle].MonitorHandle != monitor || valid[b.Handle].MonitorHandle != monitor)
                    continue;

                double weight = Math.Exp(-(now - b.Time) / 180_000.0);
                traffic[a.Handle] = traffic.GetValueOrDefault(a.Handle) + weight;
                traffic[b.Handle] = traffic.GetValueOrDefault(b.Handle) + weight;
                var peer = a.Handle == main ? b.Handle : b.Handle == main ? a.Handle : 0;
                if (peer == 0) continue;
                var pair = partners.GetValueOrDefault(peer);
                partners[peer] = (pair.Count + 1, pair.Weight + weight);
            }

            // Normalize direct, recent transitions against both windows' traffic. A window
            // shared across several tasks can have many switches without a dominant peer.
            var ranked = partners.Select(p => (Peer: p.Key, p.Value.Count, p.Value.Weight,
                    Association: 2 * p.Value.Weight / (traffic[main] + traffic[p.Key])))
                .OrderByDescending(p => p.Association).ThenByDescending(p => p.Weight)
                .Take(2).ToArray();
            if (ranked.Length == 0) return new(main, 0, 0);
            var best = ranked[0];
            // A second plausible companion or a few stale switches leave the relationship unresolved.
            bool clear = best.Count >= 2 && best.Weight >= 1 && best.Association >= .55 &&
                (ranked.Length == 1 || ranked[1].Association < best.Association * .8);
            return new(main, clear ? best.Peer : 0, best.Count);
        }
    }
}

public static class LayoutHabits
{
    public static LayoutHabit? Observe(WindowAdjustmentObservation o)
    {
        if (o.Outcome != "stable" || o.Start == o.End || o.WorkArea != o.StartWorkArea ||
            o.SettledContext is not { Truncated: false } frame || o.StartContext is not { Truncated: false } start ||
            o.WorkArea.IsEmpty) return null;
        var moved = frame.Windows.FirstOrDefault(w => w.Id == o.WindowId);
        if (moved?.AppKey == null || moved.Rect != o.End || moved.VisibleRatio < .2) return null;
        var peers = from w in frame.Windows
                    where w.Id != moved.Id && w.Manageable && w.AppKey != null && w.WorkArea == o.WorkArea && w.VisibleRatio >= .04
                    let before = start.Windows.FirstOrDefault(v => v.Id == w.Id && v.Rect == w.Rect && v.AppKey == w.AppKey)
                    where before != null
                    let gain = w.VisibleRatio - before.VisibleRatio
                    orderby gain descending, w.VisibleRatio descending
                    select (Window: w, Before: before, Gain: gain);
        var peersFound = peers.Take(2).ToArray();
        var peer = peersFound.FirstOrDefault();
        bool paired = peer.Window != null && (peer.Gain >= .04 || peersFound.Length == 1 && peer.Window.VisibleRatio >= .45);
        double center = (moved.Rect.X + moved.Rect.Width / 2d - o.WorkArea.X) / o.WorkArea.Width;
        var partner = paired ? peer.Window : null;
        int order = partner == null ? 0 : Math.Sign(moved.Rect.X + moved.Rect.Width / 2d - partner.Rect.X - partner.Rect.Width / 2d);
        int cell = -1;
        if (paired && peer.Gain >= .04 && peer.Window!.VisibleRatio <= .45 &&
            peer.Window.Exposure?.Cells is { Length: 16 } after && peer.Before!.Exposure?.Cells is { Length: 16 } previous)
        {
            cell = Enumerable.Range(0, 16).Where(i => after[i] == 'F' && previous[i] <= '4')
                .OrderBy(i => previous[i]).DefaultIfEmpty(-1).First();
        }
        double? partnerCenter = partner == null ? null :
            Math.Clamp((partner.Rect.X + partner.Rect.Width / 2d - o.WorkArea.X) / o.WorkArea.Width, 0, 1);
        double? partnerWidth = partner == null ? null :
            Math.Clamp(partner.Rect.Width / (double)o.WorkArea.Width, .05, 1);
        return new(o.Time, IntentEvidence.ContextFor(o.WorkArea), moved.AppKey, partner?.AppKey,
            Math.Clamp(center, 0, 1), Math.Clamp(moved.Rect.Width / (double)o.WorkArea.Width, .05, 1), order, cell,
            o.AfterRequest != null, partner != null && order != 0 && moved.Rect.VerticalOverlapRatio(partner.Rect) >= .5
                ? moved.Rect.HorizontalOverlapRatio(partner.Rect) : null,
            partnerCenter, partnerWidth);
    }

    public static LayoutHabit[] Add(IEnumerable<LayoutHabit> history, LayoutHabit sample)
    {
        long bucket = sample.Time.ToUnixTimeSeconds() / 1800;
        return history.Where(s => s.Time >= sample.Time.AddDays(-120) &&
            !(s.Display == sample.Display && s.App == sample.App && s.Partner == sample.Partner &&
              s.Time.ToUnixTimeSeconds() / 1800 == bucket)).Append(sample).TakeLast(1024).ToArray();
    }

    public static PlacementPreference? Resolve(IEnumerable<LayoutHabit> history, WindowSnapshot window,
        string app, string? partner, nint partnerHandle, DateTimeOffset now)
    {
        string display = IntentEvidence.ContextFor(window.WorkArea);
        var recent = history.Where(s => s.Display == display && s.Time <= now && s.Time >= now.AddDays(-120)).ToArray();
        var direct = recent.Where(s => s.App == app).ToArray();
        var directPair = direct.Where(s => partner != null && s.Partner == partner).ToArray();
        // Include mirrored evidence when the user consistently dragged the companion relative to this stationary window.
        var mirroredPair = partner == null ? [] : recent
            .Where(s => s.App == partner && s.Partner == app && s.PartnerCenter is double && s.PartnerWidth is double)
            .Select(s => new LayoutHabit(s.Time, s.Display, app, partner, s.PartnerCenter!.Value, s.PartnerWidth!.Value,
                -s.Order, -1, s.Corrected, s.InnerOverlap)).ToArray();
        // Keep both original gestures for diagnostics and old files. A pair contributes at
        // most one vote per half hour, even if both windows were moved in that period.
        static LayoutHabit[] Independent(IEnumerable<LayoutHabit> observations) =>
            observations.GroupBy(s => s.Time.ToUnixTimeSeconds() / 1800)
                .Select(g => g.MaxBy(s => s.Time)!).ToArray();
        var pair = Independent(directPair.Concat(mirroredPair));
        bool specific = pair.Length >= 3;
        var used = specific ? pair : Independent(direct);
        if (used.Length < 3) return null;
        HashSet<LayoutHabit> mirrorSet = [.. mirroredPair];
        double Weight(LayoutHabit s) => Math.Pow(.5, (now - s.Time).TotalDays / 30) *
            (s.Corrected ? 1.5 : 1) * (mirrorSet.Contains(s) ? .85 : 1);
        double sum = used.Sum(Weight);
        if (sum < 1) return null;
        double Mean(Func<LayoutHabit, double> get) => used.Sum(s => Weight(s) * get(s)) / sum;
        var center = Mean(s => s.Center);
        var dispersion = Mean(s => Math.Abs(s.Center - center));
        double confidence = sum / (sum + 4) * Math.Clamp(1 - dispersion * 3, 0, 1);
        int cell = -1;
        if (specific)
        {
            var region = pair.Where(s => s.RevealedCell >= 0).GroupBy(s => s.RevealedCell)
                .OrderByDescending(g => g.Sum(Weight)).FirstOrDefault();
            if (region != null && region.Count() >= 3 && region.Sum(Weight) > sum * .55) cell = region.Key;
        }
        // Clamp moderate overlap outliers up to .55 instead of dropping samples > .40 and losing the habit.
        var overlaps = pair.Where(s => s.InnerOverlap is >= 0 and <= .55)
            .Select(s => (Habit: s, Value: Math.Min(.40, s.InnerOverlap!.Value))).ToArray();
        double? overlap = null;
        double overlapWeight = overlaps.Sum(o => Weight(o.Habit));
        if (specific && overlaps.Length >= 3 && overlapWeight >= .6 * sum)
        {
            double accumulated = 0;
            foreach (var sample in overlaps.OrderBy(o => o.Value))
            {
                accumulated += Weight(sample.Habit);
                if (accumulated >= overlapWeight / 2) { overlap = sample.Value; break; }
            }
        }
        return new(window.Handle, specific ? partnerHandle : 0, center, Mean(s => s.Width),
            specific ? Mean(s => s.Order) : 0, cell, confidence, overlap);
    }
}
