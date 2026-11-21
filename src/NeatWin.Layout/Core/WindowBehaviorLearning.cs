using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeatWin.Core;

public enum WindowBehaviorAction { Minimize, Close }

public sealed record WindowBehaviorRule(string Id, string Name, string SourceApp,
    string[]? NextApps = null, WindowBehaviorAction Action = WindowBehaviorAction.Minimize,
    bool RequireNewProcess = false, int DelayMilliseconds = 2500)
{
    internal bool MatchesSource(string executable) =>
        StringComparer.OrdinalIgnoreCase.Equals(executable, SourceApp);

    internal bool MatchesTarget(string executable) =>
        !MatchesSource(executable) && (NextApps is not { Length: > 0 } ||
            NextApps.Contains(executable, StringComparer.OrdinalIgnoreCase));

    internal bool IsLocal => Id.StartsWith(WindowBehaviorRules.LearnedPrefix, StringComparison.Ordinal);

    internal string FitKey => $"{Id}|{SourceApp}|{string.Join(',', NextApps ?? [])}|{Action}|{RequireNewProcess}".ToLowerInvariant();

    internal bool IsValid() =>
        Id is { Length: > 0 and <= 48 } && Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        Name is { Length: > 0 and <= 96 } && !Name.Any(char.IsControl) &&
        IsExecutable(SourceApp) && Enum.IsDefined(Action) && DelayMilliseconds is >= 1500 and <= 30_000 &&
        (NextApps is null || NextApps is { Length: > 0 and <= 12 } &&
            NextApps.All(app => IsExecutable(app) && !MatchesSource(app))) &&
        (Action != WindowBehaviorAction.Close || NextApps is { Length: > 0 }) &&
        (!RequireNewProcess || NextApps is { Length: > 0 });

    private static bool IsExecutable(string? value) =>
        value is { Length: > 4 and <= 96 } && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !value.Any(char.IsControl) && value.IndexOfAny(['/', '\\', '|', ':', '*', '?', '"', '<', '>', ',']) < 0;
}

public sealed record WindowBehaviorRules(int Version = 1, WindowBehaviorRule[]? Rules = null)
{
    internal const string FileName = "behavior-rules.json";
    internal const string LearnedPrefix = "learned-";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<WindowBehaviorAction>(allowIntegerValues: false) },
    };

    internal bool IsValid() => Version == 1 && Rules is { Length: <= 32 } &&
        Rules.All(rule => rule is not null && rule.IsValid() &&
            !rule.Id.StartsWith(LearnedPrefix, StringComparison.OrdinalIgnoreCase)) &&
        Rules.Select(rule => rule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Rules.Length;
}

internal sealed record WindowBehaviorFit(int Observations = 0, int DelayMilliseconds = 2500, int Reopens = 0)
{
    internal WindowBehaviorFit ObserveDismissal(long elapsed)
    {
        int sample = (int)Math.Clamp(elapsed, 1500, 30_000);
        int delay = Observations == 0 ? sample : (DelayMilliseconds * 3 + sample) / 4;
        return new(Math.Min(Observations + 1, 100), delay, Math.Max(0, Reopens - 1));
    }

    internal WindowBehaviorFit ObserveReopen() => this with { Reopens = Math.Min(Reopens + 1, 2) };

    internal bool Suppressed => Reopens >= 2;
    internal int DelayFor(WindowBehaviorRule rule) =>
        Math.Max(1500, (Observations >= 2 ? DelayMilliseconds : rule.DelayMilliseconds) + Reopens * 2500);
}

internal sealed record WindowBehaviorProfile(Dictionary<string, WindowBehaviorFit>? Fits = null,
    WindowBehaviorRule[]? Candidates = null);

internal sealed record DiscoveredBehavior(WindowBehaviorRule Rule, long DelayMilliseconds);

/// <summary>Find repeated task handoffs without recognizing any specific application.</summary>
internal sealed class WindowBehaviorDiscovery
{
    private sealed record Handoff(BehaviorWindow Source, BehaviorWindow Target, long Tick, bool Fresh);
    private sealed record Pending(WindowBehaviorRule Rule, long Delay, long Tick);
    private Handoff? _handoff;
    private readonly List<Pending> _pending = [];
    private BehaviorWindow? _foreground;
    private long _focusedAt, _focusedUtc;

    internal IReadOnlyList<DiscoveredBehavior> Observe(long tick, long utc, IReadOnlyList<BehaviorWindow> windows,
        IReadOnlyDictionary<int, BehaviorDeparture> departures, IReadOnlySet<int> automaticDepartures)
    {
        var foreground = windows.FirstOrDefault(window => window.Foreground);
        if (foreground?.Id != _foreground?.Id)
        {
            _handoff = null; // A different foreground ends the previous direct handoff.
            if (_foreground is { } old && foreground is { } next && tick - _focusedAt >= 1000 &&
                !StringComparer.OrdinalIgnoreCase.Equals(old.App, next.App))
                _handoff = new(old, next, tick, next.ProcessStartedUtc >= _focusedUtc);
            _foreground = foreground;
            _focusedAt = tick;
            _focusedUtc = utc;
        }

        if (_handoff is { } previous && tick - previous.Tick > 30_000) _handoff = null;
        foreach (var (id, departure) in departures)
        {
            if (automaticDepartures.Contains(id) || departure == BehaviorDeparture.Hidden) continue;
            if (_handoff is not { } handoff || handoff.Source.Id != id || handoff.Target.Id != foreground?.Id ||
                windows.Any(w => StringComparer.OrdinalIgnoreCase.Equals(w.App, handoff.Source.App)))
                continue;

            var action = departure == BehaviorDeparture.Closed ? WindowBehaviorAction.Close : WindowBehaviorAction.Minimize;
            var rule = CreateRule(handoff, action);
            if (!rule.IsValid()) continue;
            _handoff = null;
            _pending.RemoveAll(p => p.Rule.FitKey == rule.FitKey);
            _pending.Add(new(rule, tick - handoff.Tick, tick));
        }

        var learned = new List<DiscoveredBehavior>();
        foreach (var candidate in _pending.ToArray())
        {
            if (windows.Any(w => candidate.Rule.MatchesSource(w.App)))
            {
                _pending.Remove(candidate);
            }
            else if (tick - candidate.Tick >= 10_000)
            {
                _pending.Remove(candidate);
                learned.Add(new(candidate.Rule, candidate.Delay));
            }
        }
        return learned;
    }

    private static WindowBehaviorRule CreateRule(Handoff handoff, WindowBehaviorAction action)
    {
        string source = handoff.Source.App, target = handoff.Target.App;
        string identity = $"{source}|{target}|{action}|{handoff.Fresh}".ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        string name = $"{Path.GetFileNameWithoutExtension(source)} → {Path.GetFileNameWithoutExtension(target)}";
        return new(WindowBehaviorRules.LearnedPrefix + hash, name.Length <= 96 ? name : name[..96],
            source, [target], action, handoff.Fresh);
    }

    internal void Reset()
    {
        _handoff = null;
        _pending.Clear();
        _foreground = null;
    }
}

internal sealed record WindowBehaviorEvent(int WindowId, string App, long ProcessStartedUtc, string Event, string Cause, string? Rule);
internal sealed record WindowBehaviorFrame(string Session, long Tick, DateTimeOffset Time, WindowBehaviorEvent[] Events, int Segment = 0);

internal sealed record BehaviorWindow(int Id, string App, long ProcessStartedUtc, bool Foreground);
internal enum BehaviorDeparture { Closed, Hidden, Minimized }

internal sealed record BehaviorUpdate(int? Dismiss = null, WindowBehaviorFit? Fit = null, int? Reopened = null);

/// <summary>One visit to a helper and its following task. Uses only evidence available at the current tick.</summary>
internal sealed class WindowBehaviorSequence(WindowBehaviorRule rule)
{
    private const long VisitTimeout = 120_000;
    private BehaviorWindow? _source, _target;
    private long _visitStarted, _sourceTick, _sourceUtc, _targetTick, _foregroundSince, _actionTick;
    private bool _targetWasForeground, _autoDeparted;
    private int? _sourceForegroundId;
    private int? _autoWindow, _actionTarget, _blockedTarget;
    private long _blockedUntil;

    internal bool IsAutomatic(int id) => _autoWindow == id;

    internal void SubmissionFailed(int id)
    {
        if (_autoWindow == id) _autoWindow = null;
    }

    internal BehaviorUpdate Observe(long tick, long utc, IReadOnlyList<BehaviorWindow> windows,
        IReadOnlyDictionary<int, BehaviorDeparture> departures, WindowBehaviorFit fit, bool learn, bool canAct, IReadOnlySet<int>? automaticDepartures = null)
    {
        var foreground = windows.FirstOrDefault(window => window.Foreground);
        var sourceForeground = foreground is not null && rule.MatchesSource(foreground.App);
        int? sourceId = sourceForeground ? foreground!.Id : null;
        bool enteredSource = sourceId.HasValue && _sourceForegroundId != sourceId;
        _sourceForegroundId = sourceId;

        if (_autoWindow is int actionWindow)
        {
            _autoDeparted |= departures.ContainsKey(actionWindow);
            if (_autoDeparted && sourceForeground && tick - _actionTick <= 30_000)
            {
                _autoWindow = null;
                _blockedTarget = _actionTarget;
                _blockedUntil = tick + VisitTimeout;
                return new(Fit: learn ? fit.ObserveReopen() : null, Reopened: actionWindow);
            }
            if (tick - _actionTick <= 30_000) return new();
            _autoWindow = null;
        }

        if (_source is not null && tick - _sourceTick > VisitTimeout) ClearVisit();
        if (enteredSource)
        {
            ClearVisit(); // Returning to the source starts a new visit and invalidates the old launch time.
            _source = foreground;
            _visitStarted = _sourceTick = tick;
            _sourceUtc = utc;
        }
        if (_source is null) return new();
        if (foreground is null || (_target != null && foreground.Id != _target.Id) ||
            (!sourceForeground && !rule.MatchesTarget(foreground.App)))
        {
            ClearVisit();
            return new();
        }
        if (sourceForeground && foreground!.Id == _source.Id && _target is null)
            _sourceTick = tick;

        if (_target is null && !sourceForeground)
        {
            if (tick - _visitStarted < 1000 || rule.RequireNewProcess && foreground.ProcessStartedUtc < _sourceUtc)
            {
                ClearVisit();
                return new();
            }
            _target = foreground;
            _targetTick = tick;
        }
        if (!windows.Any(window => window.Id == _source.Id))
        {
            var observed = departures.GetValueOrDefault(_source.Id);
            bool fitsAction = departures.ContainsKey(_source.Id) && (rule.Action == WindowBehaviorAction.Close
                ? observed == BehaviorDeparture.Closed
                : observed == BehaviorDeparture.Minimized);
            bool relevant = _target is not null && tick - _targetTick <= 30_000 && automaticDepartures?.Contains(_source.Id) != true;
            var learned = learn && fitsAction && relevant ? fit.ObserveDismissal(tick - _targetTick) : null;
            ClearVisit();
            return new(Fit: learned);
        }
        bool targetInFront = _target is not null && foreground?.Id == _target.Id;
        if (targetInFront && !_targetWasForeground) _foregroundSince = tick;
        _targetWasForeground = targetInFront;
        if (!canAct || fit.Suppressed || !targetInFront ||
            (_target?.Id == _blockedTarget && tick < _blockedUntil) || tick - _foregroundSince < fit.DelayFor(rule) ||
            windows.Count(window => rule.MatchesSource(window.App)) != 1)
            return new();
        int dismiss = _source.Id;
        _actionTick = tick;
        _autoWindow = dismiss;
        _actionTarget = _target?.Id;
        _autoDeparted = false;
        ClearVisit();
        return new(Dismiss: dismiss);
    }

    internal void Reset()
    {
        ClearVisit();
        _autoWindow = null;
        _sourceForegroundId = null;
        _blockedTarget = null;
        _blockedUntil = 0;
    }

    private void ClearVisit()
    {
        _source = _target = null;
        _targetWasForeground = false;
    }
}
