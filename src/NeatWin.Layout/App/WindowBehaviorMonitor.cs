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
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 550 };
    private readonly Dictionary<uint, ProcessIdentity> _processes = [];
    private Dictionary<int, Observed> _previous = [];
    private readonly WindowBehaviorDiscovery _discovery = new();
    private readonly Dictionary<string, WindowBehaviorFit> _fits;
    private readonly Dictionary<string, WindowBehaviorRule> _candidates;
    private int _traceSegment;
    private long _lastTraceTick;
    private BehaviorConfiguration _configuration;
    private Entry[] _entries;

    internal WindowBehaviorMonitor(WindowManager windows, IntentReferenceStore store, SettingsStore settings,
        RecorderSession recorder, Func<bool> busy)
    {
        _windows = windows;
        _store = store;
        _settings = settings;
        _recorder = recorder;
        _busy = busy;
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

            var automaticDepartures = departures.Keys.Where(id => _entries.Any(e => e.Sequence.IsAutomatic(id))).ToHashSet();

            var trace = _recorder.CanWriteDiagnostics ? new List<WindowBehaviorEvent>() : null;
            if (trace is not null)
            {
                foreach (var (id, previous) in _previous)
                {
                    if (current.ContainsKey(id)) continue;
                    var cause = _entries.Any(e => e.Sequence.IsAutomatic(id)) ? "winwindow" : "observed";
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
            foreach (var entry in _entries)
            {
                bool enabled = IsEnabled(entry.Rule);
                if (!enabled && !_configuration.Learn) continue;
                var update = entry.Sequence.Observe(tick, now.UtcTicks, observations, departures, Fit(entry.Rule),
                    _configuration.Learn, enabled && canAct, automaticDepartures);
                if (entry.Rule.IsLocal && update.Reopened is null) update = update with { Fit = null };
                if (update.Fit is { } fit)
                {
                    _fits[entry.Rule.FitKey] = fit;
                    changed = true;
                }
                if (update.Reopened is not null && foreground is not null)
                    trace?.Add(Event(foreground, "reopened", "observed", entry.Rule.Id));
                if (update.Dismiss is not int id || !handled.Add(id) || foreground is null ||
                    !current.TryGetValue(id, out var target)) continue;
                bool applied = TryDismiss(target, foreground, entry.Rule.Action);
                trace?.Add(Event(target, applied ? "dismiss-requested" : "dismiss-failed", "winwindow", entry.Rule.Id));
            }
            _previous = current;
            if (changed) _store.SaveBehaviorProfile(new(_fits, _candidates.Values.ToArray()));
            if (trace is not null && (trace.Count > 0 || tick - _lastTraceTick >= 5_000))
            {
                _store.AppendBehavior(new(_recorder.SessionId, tick, now, trace.ToArray(), _traceSegment));
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

    private static bool TryDismiss(Observed source, Observed foreground, WindowBehaviorAction action)
    {
        nint hwnd = source.Window.Handle;
        if (source.Window.IsTopmost || hwnd == foreground.Window.Handle ||
            NativeMethods.GetForegroundWindow() != foreground.Window.Handle ||
            !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != source.Window.ProcessId || ReadProcess(pid) is not { } process ||
            process.Started != source.Behavior.ProcessStartedUtc ||
            !StringComparer.OrdinalIgnoreCase.Equals(process.App, source.Behavior.App) ||
            (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTopmost) != 0 ||
            ClassName(hwnd) != source.Class || NativeMethods.GetForegroundWindow() != foreground.Window.Handle)
            return false;
        return action == WindowBehaviorAction.Close
            ? NativeMethods.PostMessage(hwnd, 0x0010, 0, 0)
            : NativeMethods.ShowWindowAsync(hwnd, 7); // SW_SHOWMINNOACTIVE
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

    public void Dispose() => _timer.Dispose();
}
