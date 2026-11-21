using System.Diagnostics;
using System.Text;
using NeatWin.Core;
using NeatWin.Recording;
using NeatWin.Reference;
using NeatWin.Windows;

namespace NeatWin.App;

internal sealed record BehaviorConfiguration(bool Learn = false, string[]? Enabled = null);
internal sealed record BehaviorRuleStatus(WindowBehaviorRule Rule, bool Enabled, WindowBehaviorFit Fit);
internal sealed record BehaviorSettings(bool Learn, BehaviorRuleStatus[] Rules);

internal sealed class WindowBehaviorMonitor : IDisposable
{
    private sealed record ProcessIdentity(string App, long Started);
    private sealed record Observed(WindowSnapshot Window, BehaviorWindow Behavior, string Class);
    private sealed record Entry(WindowBehaviorRule Rule, WindowBehaviorSequence Sequence);

    private readonly WindowManager _windows;
    private readonly IntentReferenceStore _store;
    private readonly SettingsStore _settings;
    private readonly RecorderSession _recorder;
    private readonly Func<bool> _busy;
    private readonly Func<nint, uint, long, string, bool>? _keepRunning;
    private readonly ForegroundHistory _focus;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 550 };
    private readonly Dictionary<uint, ProcessIdentity> _processes = [];
    private Dictionary<int, Observed> _previous = [];
    private readonly WindowBehaviorDiscovery _discovery = new();
    private readonly Dictionary<string, WindowBehaviorFit> _fits;
    private readonly Dictionary<string, WindowBehaviorRule> _candidates;
    private readonly IdleWindowReclaim _idle = new();
    private readonly Dictionary<int, long> _idleRequests = [];
    private readonly NativeMethods.WinEventProc _restoreCallback;
    private (Observed Source, nint Stamp, long Requested)? _reclaimUndo;
    private nint _restoreHook;
    private int _traceSegment;
    private long _lastTraceTick;
    private HashSet<int> _lastNavigationSources = [];
    private BehaviorConfiguration _configuration;
    private Entry[] _entries;

    internal WindowBehaviorMonitor(WindowManager windows, IntentReferenceStore store, SettingsStore settings,
        RecorderSession recorder, Func<bool> busy, ForegroundHistory focus, Func<nint, uint, long, string, bool>? keepRunning = null)
    {
        _windows = windows;
        _store = store;
        _settings = settings;
        _recorder = recorder;
        _busy = busy;
        _keepRunning = keepRunning;
        _focus = focus;
        _restoreCallback = (hook, _, hwnd, _, _, _, _) =>
        {
            // A user restore ends ownership, even if they minimize the window again before undo.
            if (hook == _restoreHook && _reclaimUndo is { } receipt && receipt.Source.Window.Handle == hwnd)
            {
                _idle.Used(receipt.Source.Behavior.Id, Environment.TickCount64);
                ForgetReclaimUndo();
            }
        };
        _configuration = settings.LoadBehaviorConfiguration();
        var profile = store.ReadBehaviorProfile();
        _fits = profile.Fits ?? [];
        _candidates = (profile.Candidates ?? []).ToDictionary(rule => rule.Id);
        _entries = [];
        SyncRules();
        _timer.Tick += OnTick;
        RefreshTimer();
    }

    internal BehaviorSettings Status => new(_configuration.Learn, _entries
        .Select(e => new BehaviorRuleStatus(e.Rule, IsEnabled(e.Rule), Fit(e.Rule))).ToArray());

    internal void SetLearning(bool value)
    {
        Apply(_configuration with { Learn = value });
        _discovery.Reset();
    }

    internal void SetEnabled(string id, bool enabled)
    {
        var rule = _entries.FirstOrDefault(e => e.Rule.Id == id)?.Rule
            ?? throw new ArgumentException(nameof(id));
        var values = new HashSet<string>(_configuration.Enabled ?? [], StringComparer.Ordinal);
        if (enabled) values.Add(rule.FitKey);
        else values.Remove(rule.FitKey);
        Apply(_configuration with { Enabled = values.ToArray() });
        if (enabled && Fit(rule) is { Suppressed: true } paused)
        {
            _fits[rule.FitKey] = paused with { Reopens = 0 };
            _store.SaveBehaviorProfile(new(_fits, _candidates.Values.ToArray()));
        }
        _entries.First(e => e.Rule.Id == id).Sequence.Reset();
    }

    internal void ImportRules(string source)
    {
        _store.ImportBehaviorRules(source);
        SyncRules();
        // Only a matching rule signature retains an existing opt-in.
        var active = _entries.Where(e => IsEnabled(e.Rule)).Select(e => e.Rule.FitKey).ToArray();
        Apply(_configuration with { Enabled = active });
    }

    internal void ClearLearning()
    {
        _fits.Clear();
        _candidates.Clear();
        SyncRules();
        Apply(_configuration with { Enabled = _entries.Where(e => IsEnabled(e.Rule)).Select(e => e.Rule.FitKey).ToArray() });
        RecordingChanged();
    }

    internal void RefreshTimer()
    {
        _timer.Enabled = _recorder.CanWriteDiagnostics || _configuration.Learn || _entries.Any(e => IsEnabled(e.Rule));
        if (!_timer.Enabled) ResetObservation();
    }

    private void Apply(BehaviorConfiguration configuration)
    {
        _settings.SaveBehaviorConfiguration(configuration);
        _configuration = configuration;
        RefreshTimer();
    }

    internal void RecordingChanged()
    {
        ++_traceSegment;
        _lastTraceTick = 0;
        _lastNavigationSources.Clear();
        ResetObservation();
        RefreshTimer();
    }

    private void SyncRules()
    {
        var entries = _entries.ToDictionary(e => e.Rule.FitKey);
        _entries = (_store.ReadBehaviorRules().Rules ?? [])
            .Concat(_candidates.Values.Where(r => Fit(r).Observations >= 3))
            .Select(rule => new Entry(rule,
                entries.GetValueOrDefault(rule.FitKey) is { } existing && existing.Rule.DelayMilliseconds == rule.DelayMilliseconds
                    ? existing.Sequence : new(rule)))
            .ToArray();
    }

    private bool Learn(IEnumerable<DiscoveredBehavior> observations)
    {
        bool changed = false, promoted = false;
        foreach (var sample in observations)
        {
            var rule = sample.Rule;
            if (!_candidates.ContainsKey(rule.Id) && _candidates.Count >= 48)
            {
                var discarded = _candidates.Values.FirstOrDefault(r => Fit(r).Observations < 3 && !IsEnabled(r));
                if (discarded is null) continue;
                _candidates.Remove(discarded.Id);
                _fits.Remove(discarded.FitKey);
            }
            _candidates[rule.Id] = rule;
            var previous = Fit(rule);
            var next = previous.ObserveDismissal(sample.DelayMilliseconds);
            _fits[rule.FitKey] = next;
            promoted |= previous.Observations < 3 && next.Observations >= 3;
            changed = true;
        }
        if (promoted) SyncRules();
        return changed;
    }

    internal void ResetIdleHistory() => _idle.Reset();

    internal nint TryReclaim(IReadOnlyList<WindowSnapshot> snapshot, IntentLayoutPlan applied,
        TaskLayoutContext context, SmartTidyStrength strength, nint foreground, ForegroundHistory history)
    {
        long now = Environment.TickCount64;
        var candidate = _idle.Choose(snapshot, applied, context, strength, now, _recorder.Identity.Id, history);
        if (candidate is null || foreground == 0 || NativeWindowActivity.PointerBusy ||
            NativeMethods.GetForegroundWindow() != foreground || !DesktopActivity.Interactive()) return 0;
        NativeMethods.GetWindowThreadProcessId(foreground, out var frontPid);
        if (foreground != context.Focus?.Main && !(context.ExplicitArrange && frontPid == Environment.ProcessId)) return 0;
        var current = _windows.Capture().FirstOrDefault(w => w.Handle == candidate.Handle &&
            w.ProcessId == candidate.ProcessId && w.IsManageable && !w.IsForeground && !w.IsTopmost);
        if (current is null || ReadProcess(current.ProcessId) is not { } process ||
            NativeMethods.IsHungAppWindow(current.Handle)) return 0;
        int id = _recorder.Identity.Id(current);
        var source = new Observed(current, new(id, process.App, process.Started, false), ClassName(current.Handle));
        var restoreHook = NativeMethods.SetWinEventHook(0x0017, 0x0017, 0, _restoreCallback,
            current.ProcessId, 0, NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
        if (restoreHook == 0) return 0;

        AutomationGuard.Mark(current.Handle);
        var stamp = AutomationGuard.Stamp(current.Handle);
        if (stamp == 0 || !TryDismiss(source, foreground, WindowBehaviorAction.Minimize))
        {
            NativeMethods.UnhookWinEvent(restoreHook);
            return 0;
        }
        ForgetReclaimUndo();
        _restoreHook = restoreHook;
        _reclaimUndo = (source, stamp, now);
        _idle.Used(id, now);
        foreach (var old in _idleRequests.Where(p => now - p.Value > 5_000).Select(p => p.Key).ToArray())
            _idleRequests.Remove(old);
        _idleRequests[id] = now;
        return current.Handle;
    }

    internal void ForgetReclaimUndo()
    {
        _reclaimUndo = null;
        if (_restoreHook != 0) NativeMethods.UnhookWinEvent(_restoreHook);
        _restoreHook = 0;
    }

    internal bool UndoReclaim()
    {
        var undo = _reclaimUndo;
        ForgetReclaimUndo();
        if (undo is not { } token) return false;
        var source = token.Source;
        var hwnd = source.Window.Handle;
        if (!NativeMethods.IsWindowVisible(hwnd) || AutomationGuard.Stamp(hwnd) != token.Stamp ||
            !NativeIdentityMatches(source)) return false;
        // ShowWindowAsync requests are queued. An immediate Undo can precede the minimize.
        if (!NativeMethods.IsIconic(hwnd) && Environment.TickCount64 - token.Requested > 1000) return false;
        _idle.Used(source.Behavior.Id, Environment.TickCount64);
        return NativeMethods.ShowWindowAsync(hwnd, 4); // SW_SHOWNOACTIVATE
    }

    private bool IsEnabled(WindowBehaviorRule rule) => _configuration.Enabled?.Contains(rule.FitKey) == true;

    private WindowBehaviorFit Fit(WindowBehaviorRule rule) =>
        _fits.GetValueOrDefault(rule.FitKey) ?? new(DelayMilliseconds: rule.DelayMilliseconds);

    private void OnTick(object? sender, EventArgs args)
    {
        try
        {
            if (!DesktopActivity.Interactive())
            {
                if (_previous.Count > 0) RecordingChanged();
                return;
            }

            var tick = Environment.TickCount64;
            var now = DateTimeOffset.UtcNow;
            var current = Capture();
            var foreground = current.Values.FirstOrDefault(w => w.Behavior.Foreground);
            var departures = new Dictionary<int, BehaviorDeparture>();
            foreach (var (id, old) in _previous)
            {
                if (current.ContainsKey(id)) continue;
                var hwnd = old.Window.Handle;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                BehaviorDeparture? departure = pid != old.Window.ProcessId ? BehaviorDeparture.Closed :
                    NativeMethods.IsIconic(hwnd) ? BehaviorDeparture.Minimized :
                    !NativeMethods.IsWindowVisible(hwnd) ? BehaviorDeparture.Hidden : null;
                if (departure is { } kind) departures[id] = kind;
            }

            var automaticDepartures = departures.Keys.Where(id => _entries.Any(e => e.Sequence.IsAutomatic(id)) ||
                _idleRequests.TryGetValue(id, out var time) && tick - time <= 5_000).ToHashSet();

            var trace = _recorder.CanWriteDiagnostics ? new List<WindowBehaviorEvent>() : null;
            if (trace is not null)
            {
                foreach (var (id, previous) in _previous)
                {
                    if (current.ContainsKey(id)) continue;
                    var cause = automaticDepartures.Contains(id) ? "winwindow" : "observed";
                    var type = departures.TryGetValue(id, out var result) ? result.ToString().ToLowerInvariant() : "unavailable";
                    trace.Add(Event(previous, type, cause));
                }
                foreach (var (id, w) in current)
                    if (!_previous.ContainsKey(id)) trace.Add(Event(w, "appeared", "observed"));
                var previousForeground = _previous.Values.FirstOrDefault(w => w.Behavior.Foreground);
                if (previousForeground?.Behavior.Id != foreground?.Behavior.Id)
                {
                    if (foreground is not null) trace.Add(Event(foreground, "foreground", "observed"));
                    else if (previousForeground is not null) trace.Add(Event(previousForeground, "focus-cleared", "observed"));
                }
            }

            var handled = new HashSet<int>();
            BehaviorWindow[] observations = current.Values.Select(w => w.Behavior).ToArray();
            bool changed = _configuration.Learn &&
                Learn(_discovery.Observe(tick, now.UtcTicks, observations, departures, automaticDepartures));
            bool canAct = !_busy();
            var navigation = trace != null || canAct && _entries.Any(e => IsEnabled(e.Rule))
                ? _focus.NavigationFor(current.Values.Select(w => w.Window).ToArray(), tick) : null;
            HashSet<int> preserved = navigation == null ? [] : current.Values
                .Where(w => navigation.ProtectsAction(w.Window.Handle, tick)).Select(w => w.Behavior.Id).ToHashSet();
            foreach (var entry in _entries)
            {
                bool enabled = IsEnabled(entry.Rule);
                if (!enabled && !_configuration.Learn) continue;
                var update = entry.Sequence.Observe(tick, now.UtcTicks, observations, departures, Fit(entry.Rule),
                    _configuration.Learn, enabled && canAct, automaticDepartures, preserved);
                if (entry.Rule.IsLocal && update.Reopened is null) update = update with { Fit = null };
                if (update.Fit is { } fit)
                {
                    _fits[entry.Rule.FitKey] = fit;
                    changed = true;
                }
                if (update.Reopened is not null && foreground is not null)
                    trace?.Add(Event(foreground, "reopened", "observed", entry.Rule.Id));
                if (update.Dismiss is not int id) continue;
                if (handled.Contains(id) || foreground is null || !current.TryGetValue(id, out var target))
                {
                    entry.Sequence.SubmissionFailed(id);
                    continue;
                }
                if (KeepProtects(target, entry.Rule.Action))
                {
                    entry.Sequence.SubmissionFailed(id);
                    trace?.Add(Event(target, "dismiss-skipped", "background-keep", entry.Rule.Id));
                    continue;
                }
                handled.Add(id);
                bool applied = TryDismiss(target, foreground.Window.Handle, entry.Rule.Action);
                if (!applied) entry.Sequence.SubmissionFailed(id);
                trace?.Add(Event(target, applied ? "dismiss-requested" : "dismiss-failed", "winwindow", entry.Rule.Id));
            }
            _previous = current;
            if (changed) _store.SaveBehaviorProfile(new(_fits, _candidates.Values.ToArray()));
            if (trace is not null && (trace.Count > 0 || tick - _lastTraceTick >= 5_000 || !_lastNavigationSources.SetEquals(preserved)))
            {
                _store.AppendBehavior(new(_recorder.SessionId, tick, now, trace.ToArray(), _traceSegment,
                    preserved.Count == 0 ? null : preserved.Order().ToArray()));
                _lastNavigationSources = preserved;
                _lastTraceTick = tick;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning($"Window behavior observation failed: {ex.Message}");
        }

        static WindowBehaviorEvent Event(Observed w, string type, string cause, string? rule = null) =>
            new(w.Behavior.Id, w.Behavior.App, w.Behavior.ProcessStartedUtc, type, cause, rule);
    }

    private Dictionary<int, Observed> Capture()
    {
        var windows = _windows.CaptureCoalesced().Where(w => w.SurfaceState is not null).ToArray();
        var active = windows.Select(window => window.ProcessId).ToHashSet();
        foreach (var pid in _processes.Keys.Where(pid => !active.Contains(pid)).ToArray())
            _processes.Remove(pid);

        var observed = new Dictionary<int, Observed>();
        foreach (var window in windows)
        {
            if (!_processes.TryGetValue(window.ProcessId, out var process))
            {
                process = ReadProcess(window.ProcessId);
                if (process is null) continue;
                _processes[window.ProcessId] = process;
            }
            var id = _recorder.Identity.Id(window);
            var item = new BehaviorWindow(id, process.App, process.Started, window.IsForeground);
            var windowClass = _previous.GetValueOrDefault(id)?.Class ?? ClassName(window.Handle);
            observed[id] = new(window, item, windowClass);
        }
        return observed;
    }

    // 用户给某一扇窗开启后台保持后，过去学到的关闭习惯要让步。核对实例身份，避免句柄复用后把保护带给另一扇窗。
    private bool KeepProtects(Observed source, WindowBehaviorAction action) =>
        action == WindowBehaviorAction.Close && _keepRunning?.Invoke(source.Window.Handle, source.Window.ProcessId,
            source.Behavior.ProcessStartedUtc, source.Class) == true;

    private bool TryDismiss(Observed source, nint foreground, WindowBehaviorAction action)
    {
        nint hwnd = source.Window.Handle;
        if (!NativeWindowActivity.AllowsAutomaticPlacement(foreground)) return false;
        if (source.Window.IsTopmost || hwnd == foreground ||
            NativeMethods.GetForegroundWindow() != foreground ||
            !NativeMethods.IsWindowEnabled(hwnd) || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            return false;
        if (!NativeIdentityMatches(source) ||
            (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTopmost) != 0 ||
            NativeMethods.GetForegroundWindow() != foreground)
            return false;
        if (KeepProtects(source, action)) return false;
        return action == WindowBehaviorAction.Close
            ? NativeMethods.PostMessage(hwnd, 0x0010, 0, 0)
            : NativeMethods.ShowWindowAsync(hwnd, 7); // SW_SHOWMINNOACTIVE
    }

    private static bool NativeIdentityMatches(Observed source)
    {
        var hwnd = source.Window.Handle;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == source.Window.ProcessId && ReadProcess(pid) is { } process &&
            process.Started == source.Behavior.ProcessStartedUtc &&
            StringComparer.OrdinalIgnoreCase.Equals(process.App, source.Behavior.App) && ClassName(hwnd) == source.Class;
    }

    private static ProcessIdentity? ReadProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return new(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe",
                process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static string ClassName(nint hwnd)
    {
        var text = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private void ResetObservation()
    {
        _previous.Clear();
        _processes.Clear();
        _discovery.Reset();
        foreach (var entry in _entries) entry.Sequence.Reset();
    }

    public void Dispose()
    {
        ForgetReclaimUndo();
        _timer.Dispose();
    }
}
