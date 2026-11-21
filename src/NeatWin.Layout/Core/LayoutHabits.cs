using System.Text.Json.Serialization;

namespace NeatWin.Core;

[Flags]
public enum HorizontalEdit { None = 0, Position = 1, Width = 2 }

// A sample describes a stable manual edit. Application keys are anonymous and installation-local.
public sealed record LayoutHabit(DateTimeOffset Time, string Display, string App, string? Partner,
    double Center, double Width, int Order, int RevealedCell, bool Corrected, double? InnerOverlap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PartnerCenter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PartnerWidth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Height = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HorizontalEdit? Edited = null);

public sealed record PlacementPreference(nint Window, nint Partner, double Center, double Width,
    double Order, int RevealedCell, double Confidence, double? InnerOverlap = null, double? Height = null,
    bool PreferWidth = true, bool PreferCenter = true);

public sealed record NavigationHop(nint Source, nint Target, string Evidence, long AgeMilliseconds);

public sealed record LayoutFocus(nint Main, nint Companion, int Switches, string Evidence = "recent-switch-pair",
    nint[]? NavigationAncestors = null, NavigationHop[]? Navigation = null)
{
    public bool IsNavigationAncestor(nint handle) => NavigationAncestors?.Contains(handle) == true;
}

public sealed record LayoutAttention(double Recency, double Dwell, double Input, double Association, bool Foreground,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double SustainedForeground = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double ManualFloatPreference = 0)
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
    private readonly Dictionary<nint, (uint Pid, RectI WorkArea, RectI End, double Strength, long Tick)> _manualPlaces = [];
    private sealed record RememberedNavigation((nint Handle, uint Pid, long Time) Visit, NavigationState Path, long Sampled);
    private RememberedNavigation? _rememberedNavigation;
    private ((nint Handle, uint Pid, long Time) Visit, PointI Position)? _openingPointer;
    private sealed record WindowBirth(long Time, nint Source = 0, uint SourcePid = 0, string? Evidence = null);
    private readonly Dictionary<(nint Handle, uint Pid), WindowBirth> _created = [];
    private readonly List<(nint Handle, uint Pid, long Time)> _clicks = [];

    // A show/restore/Alt-Tab is not a birth. Bind only the first foreground visit in this HWND lifetime.
    public void Created(nint handle, uint pid, long now)
    {
        if (handle == 0 || pid == 0 || pid == Environment.ProcessId) return;
        lock (_visits)
        {
            if (_created.ContainsKey((handle, pid))) return;
            if (_created.Count >= 256) _created.Remove(_created.MinBy(entry => entry.Value.Time).Key);
            _created[(handle, pid)] = new(now);
        }
    }

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
                    !ReceivedInput(last, now) && (!_created.TryGetValue((last.Handle, last.Pid), out var birth) ||
                    now - birth.Time > 3_000))
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
            foreach (var key in _created.Keys.Where(key => key.Handle == handle).ToArray()) _created.Remove(key);
            _clicks.RemoveAll(click => click.Handle == handle);
            if (_rememberedNavigation?.Path.Hops.Any(hop => hop.Source == handle) == true) _rememberedNavigation = null;
            _manualPlaces.Remove(handle);
        }
    }

    internal void Placed(WindowSnapshot window, ManualWindowGesture gesture, long now)
    {
        if (gesture.WindowHandle != window.Handle || gesture.StartRect == gesture.EndRect ||
            gesture.WorkArea != window.WorkArea || !WindowStateRules.Near(window.VisualRect, gesture.EndRect)) return;
        var end = gesture.EndRect;
        var work = gesture.WorkArea;
        var start = gesture.StartRect;
        double distance = 32 * Math.Max(1u, window.Dpi) / 96d;
        int edge = Math.Min(Math.Min(end.Left - work.Left, work.Right - end.Right),
            Math.Min(end.Top - work.Top, work.Bottom - end.Bottom));
        int expansion = Math.Max(end.Width - start.Width, end.Height - start.Height);
        int travel = Math.Max(Math.Max(Math.Abs(end.Left - start.Left), Math.Abs(end.Top - start.Top)),
            Math.Max(Math.Abs(end.Width - start.Width), Math.Abs(end.Height - start.Height)));
        double interior = Math.Clamp(edge / distance, 0, 1);
        double sameOrSmaller = 1 - Math.Clamp(expansion / distance, 0, 1);
        double intent = 2 * Math.Min(interior, sameOrSmaller) - 1;
        lock (_visits)
        {
            if (_manualPlaces.Count >= 64 && !_manualPlaces.ContainsKey(window.Handle))
                _manualPlaces.Remove(_manualPlaces.MinBy(entry => entry.Value.Tick).Key);
            _manualPlaces[window.Handle] = (window.ProcessId, work, end, intent * Math.Min(1, travel / distance), now);
        }
    }

    internal void Interact(nint handle, uint pid, long now, bool click = false)
    {
        if (handle == 0 || pid == 0 || pid == Environment.ProcessId) return;
        lock (_visits)
        {
            if (_input.Count >= 128 && !_input.ContainsKey((handle, pid)))
                _input.Remove(_input.MinBy(x => x.Value).Key);
            _input[(handle, pid)] = now;
            if (click)
            {
                _clicks.Add((handle, pid, now));
                if (_clicks.Count > 64) _clicks.RemoveAt(0);
            }
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

    internal sealed record NavigationState(nint Main, NavigationHop[] Hops, long SettleUntil)
    {
        internal bool ProtectsAction(nint handle, long now) =>
            (Hops.Length > 1 || now < SettleUntil) && Hops.Any(hop => hop.Source == handle);
    }

    internal long PointerQuietUntil(NavigationState navigation, PointI point, WindowSnapshot foreground, long now)
    {
        lock (_visits)
        {
            long quiet = navigation.SettleUntil;
            var remembered = _rememberedNavigation;
            if (remembered == null || navigation.Main != foreground.Handle || remembered.Path != navigation ||
                quiet <= 0 || now > quiet + 200) return quiet;
            var rect = foreground.VisualRect;
            if (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
            {
                _openingPointer = null;
                return quiet;
            }
            var prior = _openingPointer;
            int tolerance = Math.Max(4, (int)Math.Round(4 * foreground.Dpi / 96d));
            if (prior == null || prior.Value.Visit != remembered.Visit)
                _openingPointer = (remembered.Visit, point);
            else if (Math.Abs((long)point.X - prior.Value.Position.X) >= tolerance ||
                Math.Abs((long)point.Y - prior.Value.Position.Y) >= tolerance)
            {
                // While choosing the next item, allow up to two additional seconds of
                // quieting. Keep only one transient position; the recorder never sees it.
                long limit = remembered.Visit.Time + SettlePeriod(navigation.Hops.Length) + 2_000;
                quiet = Math.Max(quiet, Math.Min(limit, now + 650));
                _openingPointer = (remembered.Visit, point);
                _rememberedNavigation = remembered with { Path = navigation with { SettleUntil = quiet } };
            }
            return quiet;
        }
    }

    private static int SettlePeriod(int depth) => Math.Min(3_600, 1_400 + (depth - 1) * 900);

    internal NavigationState? NavigationFor(IReadOnlyList<WindowSnapshot> windows, long now)
    {
        lock (_visits)
        {
            var foreground = windows.FirstOrDefault(w => w.IsForeground);
            if (foreground is null || _visits.Count < 2) return null;
            var latest = _visits[^1];
            if (latest.Handle != foreground.Handle || latest.Pid != foreground.ProcessId ||
                now - latest.Time is < 0 or > 600_000) return null;

            var live = windows.Where(w => w.IsManageable || w.SurfaceState != null)
                .ToDictionary(w => w.Handle);
            if (!live.ContainsKey(foreground.Handle)) return null;
            bool Matches((nint Handle, uint Pid, long Time) visit) =>
                live.TryGetValue(visit.Handle, out var w) && w.ProcessId == visit.Pid;

            List<NavigationHop> EndingAt(int last)
            {
                List<NavigationHop> hops = [];
                for (int i = last; i > 0 && hops.Count < 8; i--)
                {
                    var source = _visits[i - 1];
                    var target = _visits[i];
                    if (!Matches(source) || !Matches(target) || !_created.TryGetValue((target.Handle, target.Pid), out var birth) ||
                        birth.Time < source.Time || target.Time - birth.Time is < -150 or > 3_000 ||
                        (i < last && _visits[i + 1].Time - target.Time > 12_000)) break;
                    // Freshly created windows may themselves be revisited within the open interval.
                    if (_visits.FindIndex(v => v.Handle == target.Handle && v.Pid == target.Pid) != i) break;
                    var evidence = birth.Source == source.Handle && birth.SourcePid == source.Pid ? birth.Evidence : null;
                    if (evidence == null)
                    {
                        bool click = _clicks.Any(c => c.Handle == source.Handle && c.Pid == source.Pid &&
                            c.Time >= source.Time && birth.Time - c.Time is >= -150 and <= 2_000);
                        bool owned = live[target.Handle].Owner == source.Handle;
                        bool continuous = source.Pid == target.Pid && target.Time - source.Time >= AutomaticLayoutScheduler.SettleMilliseconds;
                        if (!click && !owned && !continuous) break;
                        evidence = click ? "click-open" : owned ? "owner-open" : "foreground-open";
                        _created[(target.Handle, target.Pid)] = birth with { Source = source.Handle, SourcePid = source.Pid, Evidence = evidence };
                    }
                    hops.Add(new(source.Handle, target.Handle, evidence, now - target.Time));
                }
                hops.Reverse();
                return hops;
            }

            NavigationState Remember(NavigationHop[] hops, long settleUntil)
            {
                var path = new NavigationState(latest.Handle, hops, settleUntil);
                _rememberedNavigation = new(latest, path, now);
                return path;
            }
            int last = _visits.Count - 1;
            if (_rememberedNavigation is { } prior && now >= prior.Sampled)
            {
                var previousVisit = _visits[last - 1];
                bool same = prior.Visit == latest;
                bool returned = prior.Path.Hops.LastOrDefault()?.Source == latest.Handle &&
                    previousVisit.Time == prior.Visit.Time && (previousVisit == prior.Visit || previousVisit.Handle == 0);
                if (same || returned)
                {
                    var remaining = returned ? prior.Path.Hops[..^1] : prior.Path.Hops;
                    var refreshed = remaining.Select(hop => hop with { AgeMilliseconds = hop.AgeMilliseconds + now - prior.Sampled }).ToArray();
                    return Remember(refreshed, returned ? 0 : prior.Path.SettleUntil);
                }
            }
            var path = EndingAt(last);
            if (path.Count > 0)
            {
                // Each quick new level replaces the quiet period. The endpoint gets one
                // finite deadline even when the user reads without another input event.
                long settle = latest.Time + SettlePeriod(path.Count);
                return Remember(path.ToArray(), settle);
            }
            // One trip back up an open chain is navigation. Reopening an already visited
            // endpoint in the other direction instead supplies evidence for a working pair.
            var previous = EndingAt(last - 1);
            if (previous.Count == 0 || previous[^1].Source != latest.Handle) return null;
            previous.RemoveAt(previous.Count - 1);
            return Remember(previous.ToArray(), 0);
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
            if (foregroundVisit >= 0 && attention.TryGetValue(foreground!.Handle, out var activeAttention))
                attention[foreground.Handle] = activeAttention with
                {
                    SustainedForeground = Math.Clamp(((now - foregroundSince) / (double)AttentionHalfLifeMilliseconds - 1) / 2, 0, 1)
                };
            foreach (var (handle, placed) in _manualPlaces)
            {
                if (now - placed.Tick is < 0 or > 3 * AttentionHalfLifeMilliseconds ||
                    !valid.TryGetValue(handle, out var target) || target.ProcessId != placed.Pid ||
                    target.WorkArea != placed.WorkArea || !WindowStateRules.Near(target.VisualRect, placed.End)) continue;
                var signal = attention.GetValueOrDefault(target.Handle) ?? new(0, 0, 0, 0, target.IsForeground);
                double strength = 1 - Math.Clamp(((now - placed.Tick) / (double)AttentionHalfLifeMilliseconds - 1) / 2, 0, 1);
                attention[target.Handle] = signal with { ManualFloatPreference = placed.Strength * strength };
            }
            // A maximized/full-screen task is still the task even though we cannot move it.
            if (foreground is { IsManageable: false } && (foreground.SurfaceState != null || foreground.Owner != 0))
                return new(0, 0, 0);
            var latest = _visits.LastOrDefault(IsCurrentVisit).Handle;
            var main = hasForeground ? foreground!.Handle :
                attention.GetValueOrDefault(latest)?.Weight >= WorkingAttention ? latest : 0;
            if (main == 0) return new(0, 0, 0);

            if (NavigationFor(windows, now) is { } navigation)
            {
                var hops = navigation.Hops;
                return new(main, hops.LastOrDefault()?.Source ?? 0, 0, hops.Length > 0 ? "handoff" : "navigation-return",
                    hops.Select(hop => hop.Source).ToArray(), hops);
            }

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
        int tolerance = Math.Max(3, (int)Math.Round(4 * o.Dpi / 96d));
        int leftDelta = o.End.Left - o.Start.Left, rightDelta = o.End.Right - o.Start.Right;
        var edited = Math.Abs(o.End.Width - o.Start.Width) > tolerance ? HorizontalEdit.Width : HorizontalEdit.None;
        if (Math.Sign(leftDelta) == Math.Sign(rightDelta) && Math.Min(Math.Abs(leftDelta), Math.Abs(rightDelta)) > tolerance)
            edited |= HorizontalEdit.Position;
        return new(o.Time, IntentEvidence.ContextFor(o.WorkArea), movedKey, partner == null ? null : Key(partner),
            Math.Clamp(center, 0, 1), Math.Clamp(moved.Rect.Width / (double)o.WorkArea.Width, .05, 1), order, cell,
            o.AfterRequest != null, partner != null && order != 0 && moved.Rect.VerticalOverlapRatio(partner.Rect) >= .5
                ? moved.Rect.HorizontalOverlapRatio(partner.Rect) : null,
            partnerCenter, partnerWidth, Math.Abs(o.Start.Height - o.End.Height) > tolerance
                ? Math.Clamp(moved.Rect.Height / (double)o.WorkArea.Height, .05, 1) : null, edited);
    }

    public static LayoutHabit[] Add(IEnumerable<LayoutHabit> history, LayoutHabit sample, bool sessionOnly = false)
    {
        int seconds = sessionOnly ? 120 : 1800;
        long bucket = sample.Time.ToUnixTimeSeconds() / seconds;
        var earliest = sessionOnly ? sample.Time.AddHours(-4) : sample.Time.AddDays(-120);
        var recent = history.Where(s => s.Time >= earliest).ToArray();
        bool SameVote(LayoutHabit s) => s.Display == sample.Display && s.App == sample.App &&
            s.Partner == sample.Partner && s.Time.ToUnixTimeSeconds() / seconds == bucket;
        if (recent.LastOrDefault(SameVote) is { } previous)
        {
            var oldEdits = previous.Edited ?? HorizontalEdit.Position;
            var newEdits = sample.Edited ?? HorizontalEdit.Position;
            bool keepPosition = !newEdits.HasFlag(HorizontalEdit.Position) && oldEdits.HasFlag(HorizontalEdit.Position);
            sample = sample with
            {
                Edited = oldEdits | newEdits,
                Center = keepPosition ? previous.Center : sample.Center,
                Order = keepPosition ? previous.Order : sample.Order,
                PartnerCenter = keepPosition ? previous.PartnerCenter : sample.PartnerCenter,
                PartnerWidth = keepPosition ? previous.PartnerWidth : sample.PartnerWidth,
                InnerOverlap = keepPosition ? previous.InnerOverlap : sample.InnerOverlap,
                RevealedCell = keepPosition ? previous.RevealedCell : sample.RevealedCell,
                Width = !newEdits.HasFlag(HorizontalEdit.Width) && oldEdits.HasFlag(HorizontalEdit.Width) ? previous.Width : sample.Width,
                Height = sample.Height ?? previous.Height,
            };
        }
        return recent.Where(s => !SameVote(s)).Append(sample).TakeLast(sessionOnly ? 256 : 1024).ToArray();
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
            .Where(s => s.App == partner && s.Partner == app && s.PartnerCenter is double && s.PartnerWidth is double &&
                (s.Edited ?? HorizontalEdit.Position).HasFlag(HorizontalEdit.Position))
            .Select(s => new LayoutHabit(s.Time, s.Display, app, partner, s.PartnerCenter!.Value, s.PartnerWidth!.Value,
                -s.Order, -1, s.Corrected, s.InnerOverlap, Edited: HorizontalEdit.Position)).ToArray();
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
            (mirrorSet.Contains(s) ? .85 : 1);
        double sum = used.Sum(Weight);
        if (sum < 1) return null;
        bool HasSupport(LayoutHabit[] votes) => votes.Length >= minimumVotes &&
            (!sessionOnly || (votes.Max(s => s.Time) - votes.Min(s => s.Time)).TotalMinutes >= 2);
        (double Value, double Confidence) Estimate(LayoutHabit[] votes, Func<LayoutHabit, double> value)
        {
            if (!HasSupport(votes)) return default;
            double weight = votes.Sum(Weight);
            double cumulative = 0, median = 0;
            foreach (var vote in votes.OrderBy(value))
            {
                median = value(vote);
                cumulative += Weight(vote);
                if (cumulative >= weight / 2) break;
            }
            // Do not invent an intermediate placement between two competing arrangements.
            // A coherent majority within one-twelfth of the work area may inform this axis.
            var consistent = votes.Where(s => Math.Abs(value(s) - median) <= 1d / 12).ToArray();
            double coherentWeight = consistent.Sum(Weight);
            if (!HasSupport(consistent) || coherentWeight <= .60 * weight) return default;
            double average = consistent.Sum(s => Weight(s) * value(s)) / coherentWeight;
            return (average, coherentWeight / (weight + (sessionOnly ? 2 : 4)));
        }
        var positions = used.Where(s => (s.Edited ?? HorizontalEdit.Position).HasFlag(HorizontalEdit.Position)).ToArray();
        var center = Estimate(positions, s => s.Center);
        bool preferCenter = center.Confidence >= .40;
        int cell = -1;
        if (specific)
        {
            var region = positions.Where(s => s.RevealedCell >= 0).GroupBy(s => s.RevealedCell)
                .OrderByDescending(g => g.Sum(Weight)).FirstOrDefault();
            if (region != null && region.Count() >= 3 && region.Sum(Weight) > sum * .55) cell = region.Key;
        }
        // Clamp moderate overlap outliers up to .55 instead of dropping samples > .40 and losing the habit.
        var overlaps = positions.Where(s => s.InnerOverlap is >= 0 and <= .55)
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
        // A window's observed size can come from Smart, its application, or a user's prior task.
        // Only direct edits of that axis teach a size. Older samples still inform position.
        var inContext = direct.Where(s => s.Partner == (specific ? partner : null)).ToArray();
        var widths = Independent(inContext.Where(s => s.Edited?.HasFlag(HorizontalEdit.Width) == true));
        var heights = Independent(inContext.Where(s => s.Height is >= .05 and <= 1));
        var width = Estimate(widths, s => s.Width);
        var height = Estimate(heights, s => s.Height!.Value);
        bool preferWidth = width.Confidence >= .40;
        double confidence = Math.Max(center.Confidence, Math.Max(width.Confidence, height.Confidence));
        if (!preferCenter && !preferWidth && height.Confidence < .40) return null;
        var descriptiveCenter = used.Sum(s => s.Center * Weight(s)) / sum;
        var descriptiveWidth = used.Sum(s => s.Width * Weight(s)) / sum;
        return new(window.Handle, specific && preferCenter ? partnerHandle : 0,
            preferCenter ? center.Value : descriptiveCenter, preferWidth ? width.Value : descriptiveWidth,
            specific && preferCenter ? positions.Sum(s => Weight(s) * s.Order) / positions.Sum(Weight) : 0,
            preferCenter ? cell : -1, confidence, preferCenter ? overlap : null, height.Confidence >= .40 ? height.Value : null,
            PreferWidth: preferWidth, PreferCenter: preferCenter);
    }
}
