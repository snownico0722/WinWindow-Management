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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HorizontalEdit? Edited = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlacementUsage? Usage = null,
    RawWindowIdentity? Identity = null, RawWindowIdentity? PartnerIdentity = null);

[JsonConverter(typeof(JsonStringEnumConverter<PlacementUsage>))]
public enum PlacementUsage { Joint, BackgroundPlayback, ForegroundPlayback, TextFocus, ScrollReading, BackgroundReading }

// 匿名 ID 只在当前录制会话内有意义。用途取自手势开始前或布局求解前的观察。
public sealed record PlacementUseObservation(int Foreground, int JointPeer, bool TextFocus, int[] Playing, int[]? Scrolling = null)
{
    internal bool SupportsPair(int window, int peer)
    {
        if (Foreground == 0 || window != Foreground && peer != Foreground) return false;
        int background = window == Foreground ? peer : window;
        return background != 0 && (background == JointPeer || HasPlaybackPeer(background) || Scrolling?.Contains(background) == true);
    }

    internal PlacementUsage? For(int window, int peer = 0)
    {
        if (Foreground == 0 || window != Foreground && peer != Foreground) return null;
        int background = window == Foreground ? peer : window;
        if (Playing.Contains(Foreground)) return PlacementUsage.ForegroundPlayback;
        if (background != 0 && HasPlaybackPeer(background)) return PlacementUsage.BackgroundPlayback;
        if (background != 0 && Scrolling?.Contains(background) == true) return PlacementUsage.BackgroundReading;
        if (Scrolling?.Contains(Foreground) == true) return PlacementUsage.ScrollReading;
        if (background != 0 && background == JointPeer) return PlacementUsage.Joint;
        return TextFocus ? PlacementUsage.TextFocus : null;
    }

    private bool HasPlaybackPeer(int background) =>
        Playing.Contains(background) && (background == JointPeer || Playing.Count(id => id != Foreground) == 1);
}

public sealed record PlacementConfidence(double Position, double Width, double Height,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Region = null);

public sealed record PlacementPreference(nint Window, nint Partner, double Center, double Width,
    double Order, int RevealedCell, double Confidence, double? InnerOverlap = null, double? Height = null,
    bool PreferWidth = true, bool PreferCenter = true, PlacementConfidence? AxisConfidence = null,
    PlacementUsage? Usage = null)
{
    // 老快照只有组级置信度，沿用其证据。新样本按实际调整的轴投票，熟悉宽度不会让左右偏好更可信。
    internal double PositionConfidence => PreferCenter ? AxisConfidence?.Position ?? Confidence : 0;
    internal double WidthConfidence => PreferWidth ? AxisConfidence?.Width ?? Confidence : 0;
    internal double HeightConfidence => Height.HasValue ? AxisConfidence?.Height ?? Confidence : 0;
    internal double RegionConfidence => RevealedCell >= 0 ? AxisConfidence?.Region ?? PositionConfidence : 0;
}

public sealed record NavigationHop(nint Source, nint Target, string Evidence, long AgeMilliseconds);

public sealed record LayoutFocus(nint Main, nint Companion, int Switches, string Evidence = "recent-switch-pair",
    nint[]? NavigationAncestors = null, NavigationHop[]? Navigation = null)
{
    internal bool? OpeningResidentLeft => Evidence switch { "opening-resident-left" => true, "opening-resident-right" => false, _ => null };
    public bool IsNavigationAncestor(nint handle) => NavigationAncestors?.Contains(handle) == true;
}

public sealed record LayoutAttention(double Recency, double Dwell, double Input, double Association, bool Foreground,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double SustainedForeground = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double ManualFloatPreference = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlacementCorrection? Correction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ManualSide = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlacementCorrection? RepeatedReversal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlacementCorrection? RecentReturn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RectI? RestoreFrame = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double RecentSystemPlacement = 0)
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
    private sealed record ManualPlace(uint Pid, RectI WorkArea, RectI End, double Strength, long Tick, int Side, long SideTick);
    private readonly Dictionary<nint, RecentReversals> _reversals = [];
    private readonly Dictionary<nint, ManualPlace> _manualPlaces = [];
    private readonly Dictionary<nint, (TidyMove Move, WindowSnapshot[] Neighbors, long Tick)> _submitted = [];
    private readonly Dictionary<nint, (WindowSnapshot Window, WindowSnapshot[] Neighbors, PlacementCorrection Evidence)> _corrections = [];
    private sealed record RememberedNavigation((nint Handle, uint Pid, long Time) Visit, NavigationState Path, long Sampled);
    private RememberedNavigation? _rememberedNavigation;
    private ((nint Handle, uint Pid, long Time) Visit, PointI Position)? _openingPointer;
    private sealed record WindowBirth(long Time, nint Source = 0, uint SourcePid = 0, string? Evidence = null, bool PlacementSeen = false);
    private readonly Dictionary<(nint Handle, uint Pid), WindowBirth> _created = [];
    private readonly Dictionary<(nint Handle, uint Pid), long> _shown = [];
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

    // Show/restore is evidence of a newly visible surface, separate from a new HWND lifetime.
    public void Shown(nint handle, uint pid, long now)
    {
        if (handle == 0 || pid == 0 || pid == Environment.ProcessId) return;
        lock (_visits)
        {
            var key = (handle, pid);
            if (_shown.Count >= 128 && !_shown.ContainsKey(key)) _shown.Remove(_shown.MinBy(entry => entry.Value).Key);
            _shown[key] = now;
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
            if (_visits.Count > 0)
            {
                var previous = _visits[^1];
                if (_returning.TryGetValue(previous.Handle, out var priorReturn) && priorReturn.Restored <= now)
                    _returning.Remove(previous.Handle);
                if (_created.TryGetValue((previous.Handle, previous.Pid), out var left) && !left.PlacementSeen)
                    _created[(previous.Handle, previous.Pid)] = left with { PlacementSeen = true };
            }
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

    internal bool TakeInitialPlacement(WindowSnapshot window, LayoutFocus focus, long now, uint? lastInput = null)
    {
        lock (_visits)
        {
            var key = (window.Handle, window.ProcessId);
            if (!_created.TryGetValue(key, out var birth) || birth.PlacementSeen) return false;
            _created[key] = birth with { PlacementSeen = true };
            if (!window.IsForeground || !window.IsManageable || window.Owner != 0 || focus.Main != window.Handle ||
                focus.Evidence is "handoff" or "navigation-return" || now - birth.Time is < 0 or > 1_500 ||
                _visits.Count == 0) return false;
            var visit = _visits[^1];
            // LastInputInfo also catches keyboard activity in controls without a native caret.
            // Compare elapsed durations so the 32-bit Windows input clock may wrap.
            return visit.Handle == key.Handle && visit.Pid == key.ProcessId && visit.Time >= birth.Time &&
                !ReceivedInput(visit, now) && (lastInput == null || unchecked((uint)now - lastInput.Value) >= now - visit.Time);
        }
    }

    // Use the last working surface even when a taskbar or launcher owned focus in between.
    // A visit token makes the accelerated placement single-use across repeated window events.
    // 记录当时确实在前台的工作面。打开器退出后仍可短暂继承原侧；后台窗口的旧坐标不能给下一次开窗投票。
    private (WindowSnapshot Window, long Visit)? _lastWorkingFrame;
    private ((nint Handle, uint Pid, long Time) Visit, RectI Area, nint Monitor, int Side)? _inheritedLane;

    internal int ObserveWorkingSide(IReadOnlyList<WindowSnapshot> desktop, long now)
    {
        lock (_visits)
        {
            var front = desktop.FirstOrDefault(w => w.IsForeground);
            if (front == null || !front.IsManageable || front.IsTopmost || front.WorkArea.IsEmpty || _visits.Count == 0) return 0;
            var current = _visits[^1];
            if (current.Handle != front.Handle || current.Pid != front.ProcessId || current.Time > now) return 0;
            if (_lastWorkingFrame is { } last && (last.Visit != current.Time ||
                last.Window.Handle != front.Handle || last.Window.ProcessId != front.ProcessId))
            {
                _inheritedLane = null;
                // 允许任务栏或打开器短暂接管焦点。若中间还用过其他工作窗，旧侧别已经不是这次动作的来源。
                int index = _visits.FindLastIndex(v => v.Time == last.Visit);
                bool connected = index >= 0 && index + 1 < _visits.Count &&
                    (_visits[index + 1].Time - last.Visit >= AutomaticLayoutScheduler.SettleMilliseconds ||
                     ReceivedInput(_visits[index], _visits[index + 1].Time)) &&
                    current.Time - _visits[index + 1].Time <= 1500 &&
                    _visits.Skip(index + 1).SkipLast(1).All(v => v.Handle == 0 || !desktop.Any(w => w.IsManageable && w.Handle == v.Handle && w.ProcessId == v.Pid));
                var key = (front.Handle, front.ProcessId);
                bool NearVisit(long tick) => tick >= current.Time - 1500 && tick <= now && tick <= current.Time + 750;
                bool appeared = _created.TryGetValue(key, out var birth) && NearVisit(birth.Time) ||
                    _shown.TryGetValue(key, out long shown) && NearVisit(shown);
                var source = last.Window;
                if (connected && appeared && (source.Handle != front.Handle || source.ProcessId != front.ProcessId) &&
                    source.MonitorHandle == front.MonitorHandle && source.WorkArea == front.WorkArea)
                    _inheritedLane = (current, front.WorkArea, front.MonitorHandle, AttentionLayout.HorizontalSide(source.VisualRect, source.WorkArea));
            }
            RememberWorkingFrame(front, now);
            _lastWorkingFrame = (front, current.Time);
            return _inheritedLane is { } lane && lane.Visit == current && lane.Area == front.WorkArea &&
                lane.Monitor == front.MonitorHandle && now - current.Time <= AttentionHalfLifeMilliseconds ? lane.Side : 0;
        }
    }

    internal sealed record Opening(long Visit, WindowSnapshot Incoming, WindowSnapshot Resident, bool ResidentLeft);
    private Opening? _dispatchedOpening;

    // 同一进程内两份文档也可能分居左右。仅保留这个窗口实例上次在前台可用的矩形。
    private readonly Dictionary<nint, (WindowSnapshot Frame, long Seen)> _workingFrames = [];
    private readonly Dictionary<nint, (WindowSnapshot Frame, long Minimized, long? Restored)> _returning = [];

    internal void ResetRestoreMemory()
    {
        lock (_visits) { _workingFrames.Clear(); _returning.Clear(); }
    }

    public void MinimizeChanged(nint handle, uint pid, long now, bool restoring)
    {
        lock (_visits)
        {
            if (restoring)
            {
                if (_returning.TryGetValue(handle, out var old) && old.Frame.ProcessId == pid && now >= old.Minimized && old.Restored == null)
                    _returning[handle] = (old.Frame, old.Minimized, now);
            }
            else if (_workingFrames.TryGetValue(handle, out var place) && place.Frame.ProcessId == pid && place.Seen <= now)
                _returning[handle] = (place.Frame, now, null);
        }
    }

    private void RememberWorkingFrame(WindowSnapshot window, long tick)
    {
        if (window.Owner != 0 || window.SurfaceState is not (null or "windowed") ||
            window.VisualRect.IsEmpty || window.VisualRect.Intersect(window.WorkArea).Area < window.VisualRect.Area * .85) return;
        if (_workingFrames.Count >= 128 && !_workingFrames.ContainsKey(window.Handle))
        {
            var oldest = _workingFrames.MinBy(p => p.Value.Seen).Key;
            _workingFrames.Remove(oldest);
            _returning.Remove(oldest);
        }
        _workingFrames[window.Handle] = (window, tick);
    }

    private RectI? ReturnFrame(WindowSnapshot front, long tick)
    {
        if (!_returning.TryGetValue(front.Handle, out var place) || place.Restored is not long returned ||
            tick - returned is < 0 or > AttentionHalfLifeMilliseconds || _visits.Count == 0) return null;
        var visit = _visits[^1];
        // MINIMIZEEND 和切前台的先后都可能发生。只有同一次恢复、同一块工作区可引用旧位置。
        if (visit.Handle != front.Handle || visit.Pid != front.ProcessId || Math.Abs(visit.Time - returned) > 1500 ||
            !WindowStateRules.Matches(place.Frame, front, front.VisualRect)) return null;
        return place.Frame.VisualRect;
    }


    internal sealed record NewTaskOpening(long Visit, WindowSnapshot Incoming);

    internal NewTaskOpening? NewTaskOpeningFor(IReadOnlyList<WindowSnapshot> desktop, long now)
    {
        var front = desktop.FirstOrDefault(w => w.IsForeground);
        if (front == null || !front.IsManageable || front.IsTopmost || front.NativeUtility || front.Owner != 0 ||
            front.SurfaceState is not (null or "windowed") || front.WorkArea.IsEmpty ||
            front.VisualRect.IsEmpty) return null;
        lock (_visits)
        {
            if (_visits.Count == 0) return null;
            var visit = _visits[^1];
            var key = (front.Handle, front.ProcessId);
            // 仅窗口本次生命期的首次前台可授权轻度开窗整理，恢复、切回和应用内短暂表面另有用途。
            if ((visit.Handle, visit.Pid) != key || now - visit.Time is < 0 or > 750 ||
                !_created.TryGetValue(key, out var birth) || birth.PlacementSeen ||
                visit.Time - birth.Time is < 0 or > 1500 || ReceivedInput(visit, now)) return null;
            return new(visit.Time, front);
        }
    }

    internal bool ConsumeLocalOpening(NewTaskOpening opening, IReadOnlyList<WindowSnapshot> snapshot, long now, uint? lastInput)
    {
        lock (_visits)
        {
            if (NewTaskOpeningFor(snapshot, now) != opening || !lastInput.HasValue ||
                unchecked((uint)now - lastInput.Value) < now - opening.Visit) return false;
            var key = (opening.Incoming.Handle, opening.Incoming.ProcessId);
            _created[key] = _created[key] with { PlacementSeen = true };
            return true;
        }
    }

    internal Opening? OpeningFor(IReadOnlyList<WindowSnapshot> desktop, long now)
    {
        lock (_visits)
        {
            if (_visits.Count < 2) return null;
            var visit = _visits[^1];
            var front = desktop.FirstOrDefault(w => w.IsForeground);
            if (front != null && ReturnFrame(front, now) != null) return null;
            if (front == null || visit.Handle != front.Handle || visit.Pid != front.ProcessId ||
                now - visit.Time is < 0 or > 750 || !WorkingSurface(front) ||
                _dispatchedOpening is { } sent && sent.Visit == visit.Time && sent.Incoming.Handle == front.Handle) return null;

            // Repeated Alt-Tab within an existing pair should preserve the reading locations.
            for (int i = _visits.Count - 2; i >= 0 && visit.Time - _visits[i + 1].Time <= 8000; i--)
                if (_visits[i].Handle == front.Handle && _visits[i].Pid == front.ProcessId) return null;

            for (int i = _visits.Count - 2; i >= 0; i--)
            {
                var previous = _visits[i];
                var other = desktop.FirstOrDefault(w => w.Handle == previous.Handle && w.ProcessId == previous.Pid);
                if (other == null) continue;
                var end = _visits[i + 1].Time;
                bool used = _input.TryGetValue((other.Handle, other.ProcessId), out long input) &&
                    visit.Time - input is >= 0 and <= 8000;
                if (!used && (visit.Time - end > 8000 || end - previous.Time < 650) ||
                    !WorkingSurface(other) || other.Handle == front.Handle || other.MonitorHandle != front.MonitorHandle ||
                    other.WorkArea != front.WorkArea || other.ZOrder <= front.ZOrder) return null;
                var rect = other.VisualRect.Intersect(other.WorkArea);
                var incoming = front.VisualRect.Intersect(front.WorkArea);
                // Require a collision that takes away a substantial part of the prior task.
                var overlap = rect.Intersect(incoming);
                if (overlap.Area < .40 * rect.Area) return null;
                var fragments = new List<RectI> { overlap };
                foreach (var cover in desktop.Where(w => w.Handle != front.Handle && w.ZOrder < other.ZOrder))
                {
                    if (!overlap.Intersect(cover.VisualRect).IsEmpty)
                        fragments = RectRegion.Subtract(fragments, cover.VisualRect);
                }
                if (fragments.Sum(r => r.Area) < .40 * rect.Area) return null;
                double center = rect.Left + rect.Width / 2d;
                double middle = other.WorkArea.Left + other.WorkArea.Width / 2d;
                bool centralStack = AttentionLayout.IsCentralStack(rect, incoming, other.WorkArea);
                // 两扇旧窗可能一直叠在中央。近期的创建或显示事件能佐证一次开窗，单凭切到前台就拆开它们会打断阅读。
                if (centralStack)
                {
                    var key = (front.Handle, front.ProcessId);
                    bool recent(long tick) => now >= tick && tick >= visit.Time - 1500;
                    if (!(_created.TryGetValue(key, out var birth) && recent(birth.Time)) &&
                        !(_shown.TryGetValue(key, out long shown) && recent(shown))) return null;
                }
                // At nearly identical centers, a few pixels of asymmetry do not give the
                // newly shown surface a claim on the left-hand slot.
                bool left = centralStack || (Math.Abs(center - middle) > other.WorkArea.Width * .08
                    ? center < middle : center <= incoming.Left + incoming.Width / 2d);
                return new(visit.Time, front, other, left);
            }
            return null;
        }

        static bool WorkingSurface(WindowSnapshot w) => w.IsManageable && w.Owner == 0 && !w.IsTopmost &&
            w.SurfaceState is null or "windowed" && !w.WorkArea.IsEmpty && w.VisualRect.Intersect(w.WorkArea).Area >= w.WorkArea.Area * .18;
    }

    internal void Dispatched(Opening opening)
    {
        lock (_visits) _dispatchedOpening = opening;
    }

    internal bool OpeningStillIdle(Opening opening, long now, uint? lastInput)
    {
        lock (_visits)
            return _visits.Count != 0 && _visits[^1] == (opening.Incoming.Handle, opening.Incoming.ProcessId, opening.Visit) &&
                now - opening.Visit is >= 0 and <= 750 && lastInput.HasValue &&
                unchecked((uint)now - lastInput.Value) >= now - opening.Visit && !ReceivedInput(_visits[^1], now);
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
            foreach (var key in _shown.Keys.Where(key => key.Handle == handle).ToArray()) _shown.Remove(key);
            _clicks.RemoveAll(click => click.Handle == handle);
            if (_rememberedNavigation?.Path.Hops.Any(hop => hop.Source == handle) == true) _rememberedNavigation = null;
            _workingFrames.Remove(handle);
            _returning.Remove(handle);
            _manualPlaces.Remove(handle);
            _reversals.Remove(handle);
            _submitted.Remove(handle);
            _corrections.Remove(handle);
        }
    }

    internal void Submitted(IntentLayoutPlan plan, IReadOnlyList<WindowSnapshot> before, long now)
    {
        if (plan.Moves.Count == 0) return;
        var projected = TaskLayoutSession.Project(before, plan);
        lock (_visits)
            foreach (var move in plan.Moves)
                _submitted[move.Window.Handle] = (move, ContentNeighbors(move.Window, projected), now);
    }

    // 邻窗关闭、换屏或可用视野变化时，旧任务里的回拖不能锁死新任务。层级比较只关心内容窗口之间的顺序。
    private static WindowSnapshot[] ContentNeighbors(WindowSnapshot target, IReadOnlyList<WindowSnapshot> desktop) =>
        desktop.Where(w => w.Handle != target.Handle && w.MonitorHandle == target.MonitorHandle &&
            (w.IsManageable || w.SurfaceState is "maximized" or "fullscreen")).OrderBy(w => w.ZOrder).ToArray();

    private static bool SameNeighbors(WindowSnapshot[] previous, WindowSnapshot[] current) =>
        previous.Length == current.Length && previous.Zip(current).All(pair =>
            pair.First.SurfaceState == pair.Second.SurfaceState && WindowStateRules.Matches(pair.First, pair.Second, pair.First.VisualRect));

    internal void Placed(WindowSnapshot window, ManualWindowGesture gesture, long now,
        IReadOnlyList<WindowSnapshot>? desktop = null)
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
            _returning.Remove(window.Handle);
            RememberWorkingFrame(window, now);
            if (desktop != null)
            {
                var neighbors = ContentNeighbors(window, desktop);
                bool beganAtSubmission = _submitted.Remove(window.Handle, out var last) &&
                    WindowStateRules.Matches(last.Move.Window, window, end) && WindowStateRules.Near(start, last.Move.TargetVisualRect);
                // 用户很久以后独立改布局不应累计成连续推翻；短时重复证据额外要求起点与近期提交同时吻合。
                var reversal = beganAtSubmission && now - last.Tick is >= 0 and <= 30_000 ? PlacementCorrection.Observe(start, end, last.Move!.Window.VisualRect,
                    last.Move.TargetVisualRect, window.Dpi) : null;
                if (!_reversals.TryGetValue(window.Handle, out var votes) || !votes.Matches(window))
                {
                    votes = new(window);
                    if (reversal != null) _reversals[window.Handle] = votes;
                    else _reversals.Remove(window.Handle);
                }
                votes.Observe(start, end, gesture.Kind, reversal, now);
                bool linked = beganAtSubmission && SameNeighbors(last.Neighbors, neighbors);
                _corrections.Remove(window.Handle, out var old);
                var previous = old.Evidence != null && WindowStateRules.Matches(old.Window, window, end) && SameNeighbors(old.Neighbors, neighbors) ? old.Evidence : null;
                var evidence = PlacementCorrection.Observe(start, end, linked ? last.Move!.Window.VisualRect : null,
                    linked ? last.Move!.TargetVisualRect : null, window.Dpi, previous);
                if (evidence != null) _corrections[window.Handle] = (window, neighbors, evidence);
            }
            if (_manualPlaces.Count >= 64 && !_manualPlaces.ContainsKey(window.Handle))
                _manualPlaces.Remove(_manualPlaces.MinBy(entry => entry.Value.Tick).Key);
            // 大幅横拖到一侧是当下的直接意图。另一扇窗开关、换层或随后贴边几个像素，均不能抹掉这次选择。
            // 单独保留侧别的原始时间，随后调高度或微调不会无限续期，也不会改变永久习惯。
            int side = AttentionLayout.HorizontalSide(end, work);
            long sideTick = now;
            int dx = end.X - start.X;
            bool choseSide = gesture.Kind == ManualGestureKind.Move && side != 0 && Math.Sign(dx) == side &&
                Math.Abs((double)dx) >= work.Width * .16 && Math.Abs((double)dx) >= Math.Abs((double)end.Y - start.Y);
            if (!choseSide)
            {
                var previous = _manualPlaces.GetValueOrDefault(window.Handle);
                if (previous != null && previous.Pid == window.ProcessId && previous.WorkArea == work && previous.Side == side)
                    sideTick = previous.SideTick;
                else side = 0;
            }
            _manualPlaces[window.Handle] = new(window.ProcessId, work, end, intent * Math.Min(1, travel / distance), now, side, sideTick);
        }
    }

    internal RectI LimitLiveReturn(WindowSnapshot window, RectI target, long now)
    {
        lock (_visits)
        {
            if (!_reversals.TryGetValue(window.Handle, out var votes) || !votes.Matches(window)) return target;
            return TaskLayoutContext.LimitReturn(window.VisualRect, target, votes.Active(now, false), votes.Active(now));
        }
    }

    internal void IgnoreLocalVisit(WindowSnapshot surface)
    {
        lock (_visits)
        {
            // 截图层或名片可能先收到前台事件，后被认出。把这段临时焦点移出任务序列，收起时继续原任务。
            _visits.RemoveAll(v => v.Handle == surface.Handle && v.Pid == surface.ProcessId);
            _input.Remove((surface.Handle, surface.ProcessId));
            _clicks.RemoveAll(v => v.Handle == surface.Handle && v.Pid == surface.ProcessId);
        }
    }

    internal bool RecentlyClicked(WindowSnapshot w, long tick)
    {
        lock (_visits) return _clicks.Any(c => c.Handle == w.Handle && c.Pid == w.ProcessId && tick - c.Time is >= 0 and <= 1500);
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
            if (foreground is { IsManageable: true } && ReturnFrame(foreground, now) is { } restore &&
                attention.TryGetValue(foreground.Handle, out var restored))
                attention[foreground.Handle] = restored with { RestoreFrame = restore };
            // 已兑现的上一笔移动让紧接着的第二次移动更显眼。只在下一次真实请求采样，衰减不会唤起求解。
            foreach (var (handle, submission) in _submitted)
            {
                long elapsed = now - submission.Tick;
                if (elapsed is < 0 or >= 4000 || !valid.TryGetValue(handle, out var w) ||
                    !WindowStateRules.Matches(submission.Move.Window, w, submission.Move.TargetVisualRect)) continue;
                var signal = attention.GetValueOrDefault(handle) ?? new(0, 0, 0, 0, w.IsForeground);
                attention[handle] = signal with { RecentSystemPlacement = 1 - elapsed / 4000d };
            }
            foreach (var (handle, placed) in _manualPlaces)
            {
                if (now - placed.Tick is < 0 or > 3 * AttentionHalfLifeMilliseconds ||
                    !valid.TryGetValue(handle, out var target) || target.ProcessId != placed.Pid ||
                    target.WorkArea != placed.WorkArea) continue;
                var signal = attention.GetValueOrDefault(target.Handle) ?? new(0, 0, 0, 0, target.IsForeground);
                double strength = 1 - Math.Clamp(((now - placed.Tick) / (double)AttentionHalfLifeMilliseconds - 1) / 2, 0, 1);
                bool sameFrame = WindowStateRules.Near(target.VisualRect, placed.End);
                bool sameSide = now - placed.SideTick is >= 0 and <= 3 * AttentionHalfLifeMilliseconds &&
                    AttentionLayout.HorizontalSide(target.VisualRect, placed.WorkArea) == placed.Side && placed.Side != 0;
                if (sameFrame || sameSide)
                    attention[target.Handle] = signal with { ManualFloatPreference = sameFrame ? placed.Strength * strength : 0,
                        ManualSide = sameSide ? placed.Side : 0 };
            }
            foreach (var (handle, votes) in _reversals)
                if (valid.TryGetValue(handle, out var live) && votes.Matches(live) && votes.Active(now, repeatedOnly: false) is { } recent)
                {
                    var signal = attention.GetValueOrDefault(handle) ?? new(0, 0, 0, 0, live.IsForeground);
                    attention[handle] = signal with { RecentReturn = recent, RepeatedReversal = votes.Active(now) };
                }
            foreach (var (handle, item) in _corrections.ToArray())
            {
                if (!valid.TryGetValue(handle, out var w) || !WindowStateRules.Matches(item.Window, w, w.VisualRect) ||
                    !SameNeighbors(item.Neighbors, ContentNeighbors(w, windows)))
                {
                    _corrections.Remove(handle);
                    continue;
                }
                var current = attention.GetValueOrDefault(handle) ?? new(0, 0, 0, 0, w.IsForeground);
                attention[handle] = current with { Correction = item.Evidence };
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

[Flags]
internal enum HabitAxes { None = 0, Position = 1, Width = 2, Height = 4, All = Position | Width | Height }

public static class LayoutHabits
{
    internal const double AxisTolerance = 1d / 12;
    public static LayoutHabit? Observe(WindowAdjustmentObservation o, bool byInstance = false) =>
        Observe(o, byInstance, HabitAxes.All);

    internal static LayoutHabit? Observe(WindowAdjustmentObservation o, bool byInstance, HabitAxes allowed)
    {
        bool assisted = o.Outcome == "assisted-release";
        var endpoint = byInstance || assisted ? o.EndContext : o.SettledContext;
        bool eligible = byInstance ? o.Outcome == "manual-release" : o.Outcome == "stable" || assisted;
        if (!eligible || o.Start == o.End || o.WorkArea != o.StartWorkArea ||
            endpoint is not { Truncated: false } frame || o.StartContext is not { Truncated: false } start ||
            o.WorkArea.IsEmpty) return null;
        var actualAxes = EditedAxes(o);
        var axes = actualAxes & allowed;
        // 回拖表示用户拒绝上一轮在该轴上的改动。长期偏好只收录能独立归因的轴。
        axes &= ~ReversedAxes(LinkedReversal(o));
        if (axes == HabitAxes.None) return null;
        var moved = frame.Windows.FirstOrDefault(w => w.Id == o.WindowId);
        // 移动资料卡不等于更改主聊天窗的摆放偏好，即使它们使用同一应用和窗口类。
        if (moved == null || moved.LocalSurface || start.Windows.Any(w => w.Id == o.WindowId && w.LocalSurface) || !moved.Manageable || moved.Rect != o.End || moved.VisibleRatio < .2) return null;
        string? Key(ObservedWindow w) => byInstance ? w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : w.AppKey;
        if (Key(moved) is not { } movedKey) return null;
        // Two live documents can share a persistent application identity. Only a session
        // observation can tell which document the user actually placed.
        if (!byInstance && frame.Windows.Count(w => w.Manageable && !w.LocalSurface && w.WorkArea == o.WorkArea && w.AppKey == movedKey) > 1)
            return null;
        var movedBefore = start.Windows.FirstOrDefault(w => w.Id == o.WindowId && w.Manageable && !w.LocalSurface);
        var peers = from w in frame.Windows
                    where w.Id != moved.Id && w.Manageable && !w.LocalSurface && Key(w) != null && w.WorkArea == o.WorkArea && w.VisibleRatio >= .04
                    let before = start.Windows.FirstOrDefault(v => !v.LocalSurface && v.Id == w.Id && v.Rect == w.Rect && v.AppKey == w.AppKey)
                    where before != null
                    let gain = w.VisibleRatio - before.VisibleRatio
                    // 优先考虑动手前已经确认的搭档或正在播放的后台窗，防止移完才因偶然露出而选错搭档。
                    let scopedPeer = movedBefore != null && o.StartUsage?.SupportsPair(moved.Id, w.Id) == true
                    orderby scopedPeer descending, gain descending, w.VisibleRatio descending
                    select (Window: w, Before: before, Gain: gain, Scoped: scopedPeer);
        var peersFound = peers.Take(2).ToArray();
        var peer = peersFound.FirstOrDefault();
        bool paired = peer.Window != null && (peer.Scoped || peer.Gain >= .04 || peersFound.Length == 1 && peer.Window.VisibleRatio >= .45);
        double center = (moved.Rect.X + moved.Rect.Width / 2d - o.WorkArea.X) / o.WorkArea.Width;
        var partner = paired ? peer.Window : null;
        if (!byInstance && partner != null && frame.Windows.Count(w => w.Manageable && !w.LocalSurface && w.WorkArea == o.WorkArea && w.AppKey == partner.AppKey) > 1)
            return null;
        int order = partner == null ? 0 : Math.Sign(moved.Rect.X + moved.Rect.Width / 2d - partner.Rect.X - partner.Rect.Width / 2d);
        int cell = -1;
        // 某轴已被撤回或归入另一笔试摆时，无法把这次露出归到任何一笔保留的编辑。
        if (axes == actualAxes && paired && peer.Gain >= .04 && peer.Window!.VisibleRatio <= .45 &&
            peer.Window.Exposure?.Cells is { Length: 16 } after && peer.Before!.Exposure?.Cells is { Length: 16 } previous)
        {
            cell = Enumerable.Range(0, 16).Where(i => after[i] == 'F' && previous[i] <= '4')
                .OrderBy(i => previous[i]).DefaultIfEmpty(-1).First();
        }
        double? partnerCenter = partner == null ? null :
            Math.Clamp((partner.Rect.X + partner.Rect.Width / 2d - o.WorkArea.X) / o.WorkArea.Width, 0, 1);
        double? partnerWidth = partner == null ? null :
            Math.Clamp(partner.Rect.Width / (double)o.WorkArea.Width, .05, 1);
        var usage = movedBefore == null ? null : o.StartUsage?.For(moved.Id, partner?.Id ?? 0);
        var edited = (HorizontalEdit)(axes & (HabitAxes.Position | HabitAxes.Width));
        return new(o.Time, IntentEvidence.ContextFor(o.WorkArea), movedKey, partner == null ? null : Key(partner),
            Math.Clamp(center, 0, 1), Math.Clamp(moved.Rect.Width / (double)o.WorkArea.Width, .05, 1), order, cell,
            o.AfterRequest != null, partner != null && order != 0 && moved.Rect.VerticalOverlapRatio(partner.Rect) >= .5
                ? moved.Rect.HorizontalOverlapRatio(partner.Rect) : null,
            partnerCenter, partnerWidth, axes.HasFlag(HabitAxes.Height)
                ? Math.Clamp(moved.Rect.Height / (double)o.WorkArea.Height, .05, 1) : null, edited, usage, moved.Identity, partner?.Identity);
    }

    internal static bool HasLinkedReversal(WindowAdjustmentObservation o) => LinkedReversal(o) != null;

    private static PlacementCorrection? LinkedReversal(WindowAdjustmentObservation o) =>
        o.PriorPlacement is { ElapsedMs: >= 0 and <= 30_000 } prior
            ? PlacementCorrection.Observe(o.Start, o.End, prior.Before, prior.Submitted, o.Dpi)
            : null;

    private static HabitAxes ReversedAxes(PlacementCorrection? reversal) =>
        (reversal?.X != null ? HabitAxes.Position : HabitAxes.None) |
        (reversal?.Width != null ? HabitAxes.Width : HabitAxes.None) |
        (reversal?.Height != null ? HabitAxes.Height : HabitAxes.None);

    private static int EditTolerance(uint dpi) => Math.Max(3, (int)Math.Round(4 * dpi / 96d));

    private static HabitAxes EditedAxes(WindowAdjustmentObservation o)
    {
        if (o.StartWorkArea != o.WorkArea) return HabitAxes.None;
        int tolerance = EditTolerance(o.Dpi);
        // 拖一条边也会改变窗口中心。两条边同向移动才给位置偏好投票，宽度和高度各看自己的变化量。
        int leftDelta = o.End.Left - o.Start.Left, rightDelta = o.End.Right - o.Start.Right;
        var edited = Math.Abs(o.End.Width - o.Start.Width) > tolerance ? HabitAxes.Width : HabitAxes.None;
        if (Math.Sign(leftDelta) == Math.Sign(rightDelta) && Math.Min(Math.Abs(leftDelta), Math.Abs(rightDelta)) > tolerance)
            edited |= HabitAxes.Position;
        if (Math.Abs(o.End.Height - o.Start.Height) > tolerance) edited |= HabitAxes.Height;
        return edited;
    }

    internal static HabitAxes[] Coalesce(IReadOnlyList<WindowAdjustmentObservation> sequence)
    {
        var selected = new HabitAxes[sequence.Count];
        if (sequence.Any(o => o.Outcome is "cross-monitor" or "monitor-changed" or "identity-changed"))
            return selected;
        var edits = sequence.Select(EditedAxes).ToArray();
        // 微调回拖后的落点仍属于同一轮拒绝，不能靠一次试摆把回拖变成正向偏好。
        var reversed = sequence.Where(Verified).Aggregate(HabitAxes.None,
            (axes, o) => axes | (EditedAxes(o) & ReversedAxes(LinkedReversal(o))));
        foreach (var axis in new[] { HabitAxes.Position, HabitAxes.Width, HabitAxes.Height })
        {
            int last = Array.FindLastIndex(edits, edited => (edited & axis) != 0);
            if (last < 0 || !Verified(sequence[last]) || (reversed & axis) != 0) continue;
            var final = sequence[last];
            int first = Enumerable.Range(0, last + 1).First(i => (edits[i] & axis) != 0 && Verified(sequence[i]));
            var initial = sequence[first];
            if (initial.StartWorkArea != final.WorkArea) continue;
            // 一连串试摆仅保留每轴最后的选择。该轴回到起点说明用户撤回这次试摆，其他轴仍可单独取样。
            double Value(RectI rect) => axis switch
            {
                HabitAxes.Position => rect.Left + rect.Width / 2d,
                HabitAxes.Width => rect.Width,
                _ => rect.Height,
            };
            if (Math.Abs(Value(final.End) - Value(initial.Start)) > EditTolerance(final.Dpi))
                selected[last] |= axis;
        }
        return selected;

        static bool Verified(WindowAdjustmentObservation o) => o.Outcome is "stable" or "assisted-release";
    }

    public static LayoutHabit[] Add(IEnumerable<LayoutHabit> history, LayoutHabit sample, bool sessionOnly = false)
    {
        int seconds = sessionOnly ? 120 : 1800;
        long bucket = sample.Time.ToUnixTimeSeconds() / seconds;
        var earliest = sessionOnly ? sample.Time.AddHours(-4) : sample.Time.AddDays(-120);
        var recent = history.Where(s => s.Time >= earliest).ToArray();
        bool SameVote(LayoutHabit s) => s.Display == sample.Display && s.App == sample.App &&
            s.Partner == sample.Partner && s.Usage == sample.Usage && s.Time.ToUnixTimeSeconds() / seconds == bucket;
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
                RevealedCell = sample.RevealedCell >= 0 ? sample.RevealedCell : previous.RevealedCell,
                Width = !newEdits.HasFlag(HorizontalEdit.Width) && oldEdits.HasFlag(HorizontalEdit.Width) ? previous.Width : sample.Width,
                Height = sample.Height ?? previous.Height,
            };
        }
        return recent.Where(s => !SameVote(s)).Append(sample).TakeLast(sessionOnly ? 256 : 1024).ToArray();
    }

    public static PlacementPreference? Resolve(IEnumerable<LayoutHabit> history, WindowSnapshot window,
        string app, string? partner, nint partnerHandle, DateTimeOffset now, bool sessionOnly = false,
        PlacementUsage? usage = null)
    {
        var legacy = ResolveScope(history.Where(s => s.Usage == null), window, app, partner, partnerHandle, now, sessionOnly);
        if (usage == null) return legacy;
        var scoped = ResolveScope(history.Where(s => s.Usage == usage), window, app, partner, partnerHandle, now, sessionOnly);
        if (scoped == null) return legacy;
        // 缺少某一轴的用途样本时保留旧偏好。区域能在左右位置仍不确定时独立生效。
        var position = scoped.PreferCenter ? scoped : legacy ?? scoped;
        var width = scoped.PreferWidth ? scoped : legacy ?? scoped;
        var height = scoped.Height != null ? scoped : legacy ?? scoped;
        var region = scoped.RegionConfidence >= .40 ? scoped : legacy ?? scoped;
        var confidence = new PlacementConfidence(position.PositionConfidence, width.WidthConfidence, height.HeightConfidence,
            region.RegionConfidence);
        return position with
        {
            Width = width.Width, PreferWidth = width.PreferWidth, Height = height.Height,
            Partner = position.Partner != 0 ? position.Partner : region.Partner,
            RevealedCell = region.RevealedCell,
            Confidence = Math.Max(region.RegionConfidence, Math.Max(confidence.Position, Math.Max(confidence.Width, confidence.Height))),
            AxisConfidence = confidence, Usage = usage,
        };
    }

    private static PlacementPreference? ResolveScope(IEnumerable<LayoutHabit> history, WindowSnapshot window,
        string app, string? partner, nint partnerHandle, DateTimeOffset now, bool sessionOnly)
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
            var consistent = votes.Where(s => Math.Abs(value(s) - median) <= AxisTolerance).ToArray();
            double coherentWeight = consistent.Sum(Weight);
            if (!HasSupport(consistent) || coherentWeight <= .60 * weight) return default;
            double average = consistent.Sum(s => Weight(s) * value(s)) / coherentWeight;
            return (average, coherentWeight / (weight + (sessionOnly ? 2 : 4)));
        }
        var positions = Independent((specific ? directPair.Concat(mirroredPair) : solo)
            .Where(s => (s.Edited ?? HorizontalEdit.Position).HasFlag(HorizontalEdit.Position)));
        var center = Estimate(positions, s => s.Center);
        bool preferCenter = center.Confidence >= .40;
        int cell = -1;
        double positionWeight = positions.Sum(Weight);
        double regionConfidence = 0;
        if (specific)
        {
            // 拉宽、缩短或移动都能露出一块内容。只统计直接露出过区域的编辑，不让其他轴的票稀释证据。
            var regionVotes = Independent(directPair.Where(s => s.RevealedCell is >= 0 and < 16));
            var region = regionVotes.GroupBy(s => s.RevealedCell).OrderByDescending(g => g.Sum(Weight)).FirstOrDefault();
            if (region != null && region.Count() >= 3 && HasSupport(region.ToArray()))
            {
                var regionWeight = region.Sum(Weight);
                var total = regionVotes.Sum(Weight);
                regionConfidence = regionWeight / (total + (sessionOnly ? 2 : 4));
                if (regionWeight > total * .55 && regionConfidence >= .40) cell = region.Key;
                else regionConfidence = 0;
            }
        }
        // Clamp moderate overlap outliers up to .55 instead of dropping samples > .40 and losing the habit.
        var overlaps = positions.Where(s => s.InnerOverlap is >= 0 and <= .55)
            .Select(s => (Habit: s, Value: Math.Min(.40, s.InnerOverlap!.Value))).ToArray();
        double? overlap = null;
        double overlapWeight = overlaps.Sum(o => Weight(o.Habit));
        if (specific && overlaps.Length >= 3 && overlapWeight >= .6 * positionWeight)
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
        double confidence = Math.Max(regionConfidence, Math.Max(center.Confidence, Math.Max(width.Confidence, height.Confidence)));
        if (confidence < .40) return null;
        var descriptiveCenter = used.Sum(s => s.Center * Weight(s)) / sum;
        var descriptiveWidth = used.Sum(s => s.Width * Weight(s)) / sum;
        return new(window.Handle, specific && (preferCenter || cell >= 0) ? partnerHandle : 0,
            preferCenter ? center.Value : descriptiveCenter, preferWidth ? width.Value : descriptiveWidth,
            specific && preferCenter ? positions.Sum(s => Weight(s) * s.Order) / positions.Sum(Weight) : 0,
            cell, confidence, preferCenter ? overlap : null, height.Confidence >= .40 ? height.Value : null,
            PreferWidth: preferWidth, PreferCenter: preferCenter, AxisConfidence: new(center.Confidence, width.Confidence, height.Confidence, regionConfidence));
    }
}
