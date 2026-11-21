using System.Text.Json.Serialization;

namespace NeatWin.Core;

// A sample describes a stable manual edit. Application keys are anonymous and installation-local.
public sealed record LayoutHabit(DateTimeOffset Time, string Display, string App, string? Partner,
    double Center, double Width, int Order, int RevealedCell, bool Corrected, double? InnerOverlap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PartnerCenter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PartnerWidth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Height = null);

public sealed record PlacementPreference(nint Window, nint Partner, double Center, double Width,
    double Order, int RevealedCell, double Confidence, double? InnerOverlap = null, double? Height = null);

public sealed record LayoutFocus(nint Main, nint Companion, int Switches, string Evidence = "recent-switch-pair");

public sealed record LayoutAttention(double Recency, double Dwell, double Input, double Association, bool Foreground)
{
    public double Weight => Foreground ? 1 : Math.Clamp(Recency * (.70 + .15 * Dwell) + .35 * Input, 0, 1);
}

public sealed class ForegroundHistory
{
    internal const double WorkingAttention = .50;
    internal const int EstablishedTaskMilliseconds = 6_000;
    internal const int RecentInputMilliseconds = 15_000;
    private const int AttentionHalfLifeMilliseconds = 12_000;
    private readonly List<(nint Handle, uint Pid, long Time)> _visits = [];
    private readonly Dictionary<(nint Handle, uint Pid), long> _input = new();

    public void Visit(nint handle, uint pid, long now)
    {
        if (handle == 0 || pid == 0) return;
        // Opening WinWindow ends the foreground dwell without creating a task window.
        if (pid == Environment.ProcessId) { handle = 0; pid = 0; }
        lock (_visits)
        {
            if (_visits.Count > 0 && _visits[^1].Handle == handle && _visits[^1].Pid == pid)
                return;
            // A brief pass-through or bounce should not establish a reading pair. Keep a
            // short, intentional visit when a real input landed on that window.
            if (handle != 0 && _visits.Count >= 2 && _visits[^1].Handle != 0)
            {
                var last = _visits[^1];
                if (now - last.Time is >= 0 and < AutomaticLayoutScheduler.SettleMilliseconds &&
                    !ReceivedInput(last, now))
                {
                    _visits.RemoveAt(_visits.Count - 1);
                    if (_visits[^1].Handle == handle && _visits[^1].Pid == pid) return;
                }
            }
            _visits.Add((handle, pid, now));
            if (_visits.Count > 64) _visits.RemoveAt(0);
        }
    }

    public void Retire(nint handle)
    {
        lock (_visits)
        {
            // Keep the interruption in the sequence without trusting a reused handle.
            for (var i = 0; i < _visits.Count; i++)
                if (_visits[i].Handle == handle) _visits[i] = (0, 0, _visits[i].Time);
            foreach (var key in _input.Keys.Where(key => key.Handle == handle).ToArray()) _input.Remove(key);
        }
    }

    internal void Interact(nint handle, uint pid, long now)
    {
        if (handle == 0 || pid == 0 || pid == Environment.ProcessId) return;
        lock (_visits)
        {
            if (_input.Count >= 128 && !_input.ContainsKey((handle, pid)))
                _input.Remove(_input.MinBy(x => x.Value).Key);
            _input[(handle, pid)] = now;
        }
    }

    private bool ReceivedInput((nint Handle, uint Pid, long Time) visit, long until) =>
        _input.TryGetValue((visit.Handle, visit.Pid), out var tick) && tick >= visit.Time && tick <= until;

    internal long LastUse(WindowSnapshot window, long now)
    {
        lock (_visits)
        {
            long last = _input.GetValueOrDefault((window.Handle, window.ProcessId));
            int i = _visits.FindLastIndex(v => v.Handle == window.Handle && v.Pid == window.ProcessId);
            if (i >= 0) last = Math.Max(last, i + 1 < _visits.Count ? _visits[i + 1].Time : now);
            return last;
        }
    }

    public LayoutFocus Resolve(IReadOnlyList<WindowSnapshot> windows, long now) => Resolve(windows, now, out _);

    internal LayoutFocus Resolve(IReadOnlyList<WindowSnapshot> windows, long now, out Dictionary<nint, LayoutAttention> attention)
    {
        lock (_visits)
        {
            var valid = windows.Where(w => w.IsManageable).ToDictionary(w => w.Handle);
            bool IsLive((nint Handle, uint Pid, long Time) v) =>
                valid.TryGetValue(v.Handle, out var w) && w.ProcessId == v.Pid;
            bool IsCurrentVisit((nint Handle, uint Pid, long Time) v) => now - v.Time is >= 0 and <= 600_000 && IsLive(v);
            static double Decay(long elapsed, int halfLife = AttentionHalfLifeMilliseconds) => Math.Pow(.5, elapsed / (double)halfLife);
            attention = new();
            var foreground = windows.FirstOrDefault(w => w.IsForeground);
            bool hasForeground = foreground?.IsManageable == true;
            int foregroundVisit = hasForeground ? _visits.FindLastIndex(v => v.Handle == foreground!.Handle &&
                v.Pid == foreground.ProcessId && v.Time <= now) : -1;
            long foregroundSince = foregroundVisit >= 0 ? _visits[foregroundVisit].Time : now;
            bool establishedTask = hasForeground && now - foregroundSince >= EstablishedTaskMilliseconds &&
                _input.TryGetValue((foreground!.Handle, foreground.ProcessId), out long currentInput) &&
                currentInput >= foregroundSince && now - currentInput is >= 0 and <= 4_000;
            double InputFor(WindowSnapshot w) => _input.TryGetValue((w.Handle, w.ProcessId), out var tick) &&
                now - tick is >= 0 and <= RecentInputMilliseconds ? Decay(now - tick, 5_000) : 0;
            for (var i = _visits.Count - 1; i >= 0; i--)
            {
                var visit = _visits[i];
                if (!IsLive(visit) || visit.Time > now || attention.ContainsKey(visit.Handle)) continue;
                var end = i + 1 < _visits.Count ? Math.Clamp(_visits[i + 1].Time, visit.Time, now) : now;
                if (now - end > 600_000) continue;
                var dwell = Math.Min(1, (end - visit.Time) / 4000d);
                double input = InputFor(valid[visit.Handle]);
                int halfLife = establishedTask && end <= foregroundSince && input < .5 ? 5_000 : AttentionHalfLifeMilliseconds;
                attention.Add(visit.Handle, new(Decay(now - end, halfLife), dwell, input, 0, visit.Handle == foreground?.Handle));
            }
            foreach (var w in valid.Values)
                if (!attention.ContainsKey(w.Handle) && (w.IsForeground || InputFor(w) > 0))
                    attention.Add(w.Handle, new(0, 0, InputFor(w), 0, w.IsForeground));
            // A maximized/full-screen task is still the task even though we cannot move it.
            if (foreground is { IsManageable: false } && (foreground.SurfaceState != null || foreground.Owner != 0))
                return new(0, 0, 0);
            var latest = _visits.LastOrDefault(IsCurrentVisit).Handle;
            var main = hasForeground ? foreground!.Handle :
                attention.GetValueOrDefault(latest)?.Weight >= WorkingAttention ? latest : 0;
            if (main == 0) return new(0, 0, 0);

            var monitor = valid[main].MonitorHandle;
            var traffic = new Dictionary<nint, double>();
            var partners = new Dictionary<nint, (int Count, double Weight)>();
            for (var i = 1; i < _visits.Count; i++)
            {
                var a = _visits[i - 1];
                var b = _visits[i];
                if (a.Handle == b.Handle || b.Time - a.Time is < 0 or > 120_000 ||
                    now - b.Time > 3 * AttentionHalfLifeMilliseconds ||
                    !IsCurrentVisit(a) || !IsCurrentVisit(b) ||
                    valid[a.Handle].MonitorHandle != monitor || valid[b.Handle].MonitorHandle != monitor)
                    continue;

                double weight = Decay(now - b.Time);
                traffic[a.Handle] = traffic.GetValueOrDefault(a.Handle) + weight;
                traffic[b.Handle] = traffic.GetValueOrDefault(b.Handle) + weight;
                var peer = a.Handle == main ? b.Handle : b.Handle == main ? a.Handle : 0;
                if (peer == 0) continue;
                var pair = partners.GetValueOrDefault(peer);
                partners[peer] = (pair.Count + 1, pair.Weight + weight);
            }

            // Normalize direct, recent transitions against both windows' traffic. A window
            // shared across several tasks can have many switches without a dominant peer.
            var weights = attention;
            var associations = partners.Select(p => (Peer: p.Key, p.Value.Count, p.Value.Weight,
                Association: 2 * p.Value.Weight / (traffic[main] + traffic[p.Key]))).ToArray();
            foreach (var p in associations)
                weights[p.Peer] = weights[p.Peer] with { Association = p.Association };
            var ranked = associations
                .Where(p => weights.GetValueOrDefault(p.Peer)?.Weight >= WorkingAttention)
                .Select(p => (p.Peer, p.Count, p.Weight, p.Association,
                    Score: p.Association * (.65 + .25 * weights[p.Peer].Weight + .10 * weights[p.Peer].Input)))
                .OrderByDescending(p => p.Score).ThenByDescending(p => p.Weight)
                .Take(2).ToArray();
            var best = ranked.FirstOrDefault();
            bool clear = best.Count >= 2 && best.Weight >= 1 && best.Association >= .55 &&
                (ranked.Length == 1 || ranked[1].Score < best.Score * .8);
            if (clear) return new(main, best.Peer, best.Count);

            // A first transition supports access to the source, even before repeated switches
            // demonstrate joint use. The weak role expires as attention settles on the new task.
            if (foregroundVisit >= 0 && foregroundVisit == _visits.Count - 1 &&
                now - foregroundSince <= AttentionHalfLifeMilliseconds &&
                (now - foregroundSince >= AutomaticLayoutScheduler.SettleMilliseconds ||
                    ReceivedInput(_visits[foregroundVisit], now)))
            {
                if (foreground!.Owner != 0 && valid.TryGetValue(foreground.Owner, out var owner) &&
                    owner.MonitorHandle == monitor)
                    return new(main, owner.Handle, partners.GetValueOrDefault(owner.Handle).Count, "handoff");
                if (foregroundVisit > 0)
                {
                    var source = _visits[foregroundVisit - 1];
                    if (IsLive(source) && source.Handle != main && valid[source.Handle].MonitorHandle == monitor &&
                        partners.GetValueOrDefault(source.Handle).Count <= 1 &&
                        weights.GetValueOrDefault(source.Handle)?.Weight >= WorkingAttention &&
                        (foregroundSince - source.Time >= AutomaticLayoutScheduler.SettleMilliseconds ||
                            ReceivedInput(source, foregroundSince)))
                        return new(main, source.Handle, 1, "handoff");
                }
            }
            return new(main, 0, best.Count);
        }
    }
}

public static class LayoutHabits
{
    public static LayoutHabit? Observe(WindowAdjustmentObservation o, bool byInstance = false)
    {
        bool assisted = o.Outcome == "assisted-release";
        var endpoint = byInstance || assisted ? o.EndContext : o.SettledContext;
        bool eligible = byInstance ? o.Outcome == "manual-release" : o.Outcome == "stable" || assisted;
        if (!eligible || o.Start == o.End || o.WorkArea != o.StartWorkArea ||
            endpoint is not { Truncated: false } frame || o.StartContext is not { Truncated: false } start ||
            o.WorkArea.IsEmpty) return null;
        var moved = frame.Windows.FirstOrDefault(w => w.Id == o.WindowId);
        if (moved == null || !moved.Manageable || moved.Rect != o.End || moved.VisibleRatio < .2) return null;
        string? Key(ObservedWindow w) => byInstance ? w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : w.AppKey;
        if (Key(moved) is not { } movedKey) return null;
        // Two live documents can share a persistent application identity. Only a session
        // observation can tell which document the user actually placed.
        if (!byInstance && frame.Windows.Count(w => w.Manageable && w.WorkArea == o.WorkArea && w.AppKey == movedKey) > 1)
            return null;
        var peers = from w in frame.Windows
                    where w.Id != moved.Id && w.Manageable && Key(w) != null && w.WorkArea == o.WorkArea && w.VisibleRatio >= .04
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
        if (!byInstance && partner != null && frame.Windows.Count(w => w.Manageable && w.WorkArea == o.WorkArea && w.AppKey == partner.AppKey) > 1)
            return null;
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
        return new(o.Time, IntentEvidence.ContextFor(o.WorkArea), movedKey, partner == null ? null : Key(partner),
            Math.Clamp(center, 0, 1), Math.Clamp(moved.Rect.Width / (double)o.WorkArea.Width, .05, 1), order, cell,
            o.AfterRequest != null, partner != null && order != 0 && moved.Rect.VerticalOverlapRatio(partner.Rect) >= .5
                ? moved.Rect.HorizontalOverlapRatio(partner.Rect) : null,
            partnerCenter, partnerWidth, o.Start.Height != o.End.Height
                ? Math.Clamp(moved.Rect.Height / (double)o.WorkArea.Height, .05, 1) : null);
    }

    public static LayoutHabit[] Add(IEnumerable<LayoutHabit> history, LayoutHabit sample)
    {
        long bucket = sample.Time.ToUnixTimeSeconds() / 1800;
        var recent = history.Where(s => s.Time >= sample.Time.AddDays(-120)).ToArray();
        bool SameVote(LayoutHabit s) => s.Display == sample.Display && s.App == sample.App &&
            s.Partner == sample.Partner && s.Time.ToUnixTimeSeconds() / 1800 == bucket;
        // Dragging the resized window again in the same burst still contributes one size vote.
        if (sample.Height == null && recent.LastOrDefault(SameVote) is { Height: not null } previous)
            sample = sample with { Height = previous.Height };
        return recent.Where(s => !SameVote(s)).Append(sample).TakeLast(1024).ToArray();
    }

    public static PlacementPreference? Resolve(IEnumerable<LayoutHabit> history, WindowSnapshot window,
        string app, string? partner, nint partnerHandle, DateTimeOffset now, bool sessionOnly = false)
    {
        string display = IntentEvidence.ContextFor(window.WorkArea);
        var earliest = sessionOnly ? now.AddHours(-4) : now.AddDays(-120);
        int minimumVotes = sessionOnly ? 2 : 3;
        var recent = history.Where(s => s.Display == display && s.Time <= now && s.Time >= earliest).ToArray();
        var direct = recent.Where(s => s.App == app).ToArray();
        var directPair = direct.Where(s => partner != null && s.Partner == partner).ToArray();
        // Include mirrored evidence when the user consistently dragged the companion relative to this stationary window.
        var mirroredPair = partner == null ? [] : recent
            .Where(s => s.App == partner && s.Partner == app && s.PartnerCenter is double && s.PartnerWidth is double)
            .Select(s => new LayoutHabit(s.Time, s.Display, app, partner, s.PartnerCenter!.Value, s.PartnerWidth!.Value,
                -s.Order, -1, s.Corrected, s.InnerOverlap)).ToArray();
        // Keep both original gestures for diagnostics and old files. A pair contributes at
        // most one vote per half hour, even if both windows were moved in that period.
        LayoutHabit[] Independent(IEnumerable<LayoutHabit> observations) =>
            observations.GroupBy(s => s.Time.ToUnixTimeSeconds() / (sessionOnly ? 120 : 1800))
                .Select(g => g.MaxBy(s => s.Time)!).ToArray();
        var pair = Independent(directPair.Concat(mirroredPair));
        bool specific = pair.Length >= minimumVotes;
        var solo = direct.Where(s => s.Partner == null).ToArray();
        var used = specific ? pair : Independent(solo);
        if (used.Length < minimumVotes || sessionOnly && (used.Max(s => s.Time) - used.Min(s => s.Time)).TotalMinutes < 2)
            return null;
        HashSet<LayoutHabit> mirrorSet = [.. mirroredPair];
        double Weight(LayoutHabit s) => Math.Pow(.5, sessionOnly ? (now - s.Time).TotalMinutes / 45 : (now - s.Time).TotalDays / 30) *
            (s.Corrected ? 1.5 : 1) * (mirrorSet.Contains(s) ? .85 : 1);
        double sum = used.Sum(Weight);
        if (sum < 1) return null;
        double Mean(Func<LayoutHabit, double> get) => used.Sum(s => Weight(s) * get(s)) / sum;
        var center = Mean(s => s.Center);
        var dispersion = Mean(s => Math.Abs(s.Center - center));
        double confidence = sum / (sum + (sessionOnly ? 2 : 4)) * Math.Clamp(1 - dispersion * 3, 0, 1);
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
        // Only vertical resizes of this window teach its height. A stationary partner or
        // a position-only edit can include geometry previously chosen by the layout engine.
        var heights = direct.Where(s => s.Height is >= .05 and <= 1).ToArray();
        var heightVotes = specific ? Independent(heights.Where(s => s.Partner == partner)) : [];
        if (heightVotes.Length < minimumVotes) heightVotes = Independent(heights.Where(s => s.Partner == null));
        double? height = null;
        double totalHeightWeight = heightVotes.Sum(Weight);
        if (heightVotes.Length >= minimumVotes && totalHeightWeight >= 1)
        {
            double meanHeight = heightVotes.Sum(s => Weight(s) * s.Height!.Value) / totalHeightWeight;
            double deviation = heightVotes.Sum(s => Weight(s) * Math.Abs(s.Height!.Value - meanHeight)) / totalHeightWeight;
            if (deviation <= .10) height = meanHeight;
        }
        return new(window.Handle, specific ? partnerHandle : 0, center, Mean(s => s.Width),
            specific ? Mean(s => s.Order) : 0, cell, confidence, overlap, height);
    }
}
