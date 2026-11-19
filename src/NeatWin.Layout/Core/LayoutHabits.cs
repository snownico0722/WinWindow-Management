namespace NeatWin.Core;

// A sample describes a stable manual edit. Application keys are anonymous and installation-local.
public sealed record LayoutHabit(DateTimeOffset Time, string Display, string App, string? Partner,
    double Center, double Width, int Order, int RevealedCell, bool Corrected);
public sealed record PlacementPreference(nint Window, nint Partner, double Center, double Width,
    double Order, int RevealedCell, double Confidence);
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
                _visits.RemoveAt(_visits.Count - 1);
            _visits.Add((handle, pid, now));
            if (_visits.Count > 64) _visits.RemoveAt(0);
        }
    }
    public void Retire(nint handle) { lock (_visits) _visits.RemoveAll(v => v.Handle == handle); }
    public LayoutFocus Resolve(IReadOnlyList<WindowSnapshot> windows, long now)
    {
        lock (_visits)
        {
            var valid = windows.Where(w => w.IsManageable).ToDictionary(w => w.Handle);
            var visits = _visits.Where(v => now - v.Time <= 600_000 &&
                valid.TryGetValue(v.Handle, out var w) && w.ProcessId == v.Pid).ToArray();
            var main = valid.Values.FirstOrDefault(w => w.IsForeground)?.Handle ?? visits.LastOrDefault().Handle;
            if (main == 0) return new(0, 0, 0);
            var partners = new Dictionary<nint, int>();
            for (int i = 1; i < visits.Length; i++)
            {
                var a = visits[i - 1]; var b = visits[i];
                var peer = a.Handle == main ? b.Handle : b.Handle == main ? a.Handle : 0;
                if (peer != 0 && peer != main && b.Time - a.Time <= 120_000 &&
                    valid[peer].MonitorHandle == valid[main].MonitorHandle)
                    partners[peer] = partners.GetValueOrDefault(peer) + 1;
            }
            var best = partners.OrderByDescending(p => p.Value).FirstOrDefault();
            return new(main, best.Value >= 2 ? best.Key : 0, best.Value);
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
        return new(o.Time, IntentEvidence.ContextFor(o.WorkArea), moved.AppKey, partner?.AppKey,
            Math.Clamp(center, 0, 1), Math.Clamp(moved.Rect.Width / (double)o.WorkArea.Width, .05, 1), order, cell,
            o.AfterRequest != null);
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
        var samples = history.Where(s => s.App == app && s.Display == IntentEvidence.ContextFor(window.WorkArea) &&
            s.Time <= now && s.Time >= now.AddDays(-120)).ToArray();
        var pair = samples.Where(s => partner != null && s.Partner == partner).ToArray();
        bool specific = pair.Length >= 3;
        var used = specific ? pair : samples;
        if (used.Length < 3) return null;
        double Weight(LayoutHabit s) => Math.Pow(.5, (now - s.Time).TotalDays / 30) * (s.Corrected ? 1.5 : 1);
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
        return new(window.Handle, specific ? partnerHandle : 0, center, Mean(s => s.Width),
            specific ? Mean(s => s.Order) : 0, cell, confidence);
    }
}
