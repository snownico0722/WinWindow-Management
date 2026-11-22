using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeatWin.Core;

namespace WinWindow;

internal sealed class DesktopSnapshotHistory(Func<long>? clock = null) : IDisposable
{
    internal const int LookbackMilliseconds = 300_000;
    internal const int SampleIntervalMilliseconds = 1000;
    private const int MaxLiveSurfaceEvents = 512;
    // 追溯期延长五倍，同时给表面分类突发保留原来的每分钟容量。
    internal const int MaxPastSurfaceEvents = 5 * MaxLiveSurfaceEvents;
    private readonly object _gate = new();
    private readonly Queue<DesktopSnapshot.Sample> _windows = new();
    private readonly Queue<Feedback> _feedback = new();
    internal sealed record Feedback(long Tick, SolverFeedback Sample);
    private readonly Queue<Decision> _decisions = new();
    private readonly Queue<Gesture> _gestures = new();
    private readonly Queue<SurfaceEvent> _surfaces = new();
    private readonly Queue<DesktopInteraction> _interactions = new();
    private const int MaxPastInteractions = 32_768, MaxLiveInteractions = 4096;
    private long? _interactionDroppedThrough;
    private PeriodicTimer? _timer;
    private Capture? _capture;
    private long _clearedAt;
    private long? _surfaceDroppedThrough;

    private long Now => clock?.Invoke() ?? Environment.TickCount64;

    internal sealed record Gesture(string Session, GestureEvent Event);
    internal sealed record Decision(long Tick, LayoutRunObservation Observation);
    internal sealed record Capture(DateTimeOffset Time, long Tick, bool LookbackEnabled,
        DesktopSnapshot.Sample[] PastWindows, Decision[] PastDecisions, Gesture[] PastGestures,
        SurfaceEvent[] PastSurfaces, long EarliestTick, long? SurfaceDroppedThrough)
    {
        internal Feedback[] PastFeedback { get; init; } = [];
        internal ConcurrentQueue<Feedback> LiveFeedback { get; } = new();
        internal DesktopInteraction[] PastInteractions { get; init; } = [];
        internal ConcurrentQueue<DesktopInteraction> LiveInteractions { get; } = new();
        internal long? InteractionDroppedThrough { get; init; }
        internal int DroppedInteractions;
        internal ConcurrentQueue<Gesture> LiveGestures { get; } = new();
        internal ConcurrentQueue<Decision> LiveDecisions { get; } = new();
        internal ConcurrentQueue<SurfaceEvent> LiveSurfaces { get; } = new();
        private int _droppedSurfaces;
        internal int DroppedSurfaces => Volatile.Read(ref _droppedSurfaces);
        internal void ObserveSurface(SurfaceEvent observation)
        {
            if (LiveSurfaces.Count < MaxLiveSurfaceEvents) LiveSurfaces.Enqueue(observation);
            else Interlocked.Increment(ref _droppedSurfaces);
        }
    }

    internal void SetEnabled(bool enabled, nint workbench, Func<IReadOnlySet<nint>> getManaged)
    {
        lock (_gate)
        {
            if (enabled && _timer is null)
            {
                _clearedAt = Now;
                var timer = _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(SampleIntervalMilliseconds));
                _ = Task.Run(() => SampleAsync(timer, workbench, getManaged));
            }
            else if (!enabled)
            {
                _timer?.Dispose();
                _timer = null;
                Clear();
            }
        }
    }

    private async Task SampleAsync(PeriodicTimer timer, nint workbench, Func<IReadOnlySet<nint>> getManaged)
    {
        var apps = new Dictionary<uint, string>();
        do
        {
            lock (_gate)
                if (_timer != timer) return;
            if (apps.Count >= 512) apps.Clear();
            try
            {
                var sample = DesktopSnapshot.CaptureWindows(getManaged(), workbench, apps);
                lock (_gate)
                {
                    if (_timer != timer) return;
                    if (sample.Tick >= _clearedAt) _windows.Enqueue(sample);
                    Trim(Now);
                }
            }
            catch (Win32Exception ex) { Trace.TraceWarning(ex.Message); }
        } while (await timer.WaitForNextTickAsync().ConfigureAwait(false));
    }

    internal void Observe(LayoutRunObservation observation)
    {
        lock (_gate)
        {
            if (_timer is null && _capture is null) return;
            var decision = new Decision(Now, observation);
            if (_timer is not null)
            {
                Trim(decision.Tick);
                _decisions.Enqueue(decision);
            }
            _capture?.LiveDecisions.Enqueue(decision);
        }
    }

    internal void ObserveFeedback(SolverFeedback sample)
    {
        lock (_gate)
        {
            if (_timer == null && _capture == null) return;
            var item = new Feedback(Now, sample);
            if (_timer != null) { Trim(item.Tick); _feedback.Enqueue(item); }
            _capture?.LiveFeedback.Enqueue(item);
        }
    }

    internal bool WantsGestures() { lock (_gate) return _timer != null || _capture != null; }

    internal void ObserveGesture(string session, GestureEvent item)
    {
        lock (_gate)
        {
            if (_timer == null && _capture == null || item.Tick < _clearedAt) return;
            var gesture = new Gesture(session, item);
            if (_timer != null)
            {
                Trim(Now);
                _gestures.Enqueue(gesture);
            }
            _capture?.LiveGestures.Enqueue(gesture);
        }
    }

    internal bool WantsSurfaces() { lock (_gate) return _timer != null || _capture != null; }

    internal void ObserveSurface(SurfaceEvent observation)
    {
        lock (_gate)
        {
            if (observation.Tick < _clearedAt) return;
            if (_timer != null)
            {
                Trim(Now);
                if (_surfaces.Count >= MaxPastSurfaceEvents)
                {
                    long dropped = _surfaces.Dequeue().Tick;
                    _surfaceDroppedThrough = Math.Max(_surfaceDroppedThrough ?? dropped, dropped);
                }
                _surfaces.Enqueue(observation);
            }
            // 表面判定的时刻可能早于点击，但回调稍后才拿到锁；按事件时刻归入快照仍可保留这条证据。
            if (_capture is { } capture && observation.Tick >= capture.EarliestTick)
                capture.ObserveSurface(observation);
        }
    }

    internal void ObserveInteraction(DesktopInteraction item)
    {
        lock (_gate)
        {
            if (item.Tick < _clearedAt) return;
            if (_timer != null)
            {
                Trim(Now);
                if (_interactions.Count >= MaxPastInteractions)
                {
                    long lost = _interactions.Dequeue().Tick;
                    _interactionDroppedThrough = Math.Max(_interactionDroppedThrough ?? lost, lost);
                }
                _interactions.Enqueue(item);
            }
            if (_capture is { } capture && item.Tick >= capture.EarliestTick)
            {
                if (capture.LiveInteractions.Count < MaxLiveInteractions) capture.LiveInteractions.Enqueue(item);
                else Interlocked.Increment(ref capture.DroppedInteractions);
            }
        }
    }

    internal Capture BeginCapture()
    {
        lock (_gate)
        {
            var tick = Now;
            Trim(tick);
            // 复制点击前的记录和接收后续事件共用同一把锁，恰逢点击发生的排布或手势就不会落在两段记录之间。
            bool enabled = _timer is not null;
            long earliest = enabled ? Math.Max(_clearedAt, tick - LookbackMilliseconds) : tick;
            return _capture = new(DateTimeOffset.Now, tick, enabled, _windows.ToArray(), _decisions.ToArray(),
                _gestures.Where(gesture => gesture.Event.Tick >= earliest).ToArray(),
                _surfaces.Where(surface => surface.Tick >= earliest).OrderBy(surface => surface.Tick).ToArray(),
                earliest, _surfaceDroppedThrough >= earliest ? _surfaceDroppedThrough : null)
            {
                PastFeedback = _feedback.Where(f => f.Tick >= earliest).ToArray(),
                PastInteractions = _interactions.Where(e => e.Tick >= earliest).ToArray(),
                InteractionDroppedThrough = _interactionDroppedThrough >= earliest ? _interactionDroppedThrough : null
            };
        }
    }

    internal void EndCapture(Capture capture)
    {
        lock (_gate)
            if (ReferenceEquals(_capture, capture)) _capture = null;
    }

    private void Trim(long now)
    {
        var earliest = now - LookbackMilliseconds;
        while (_feedback.TryPeek(out var vote) && vote.Tick < earliest) _feedback.Dequeue();
        while (_windows.TryPeek(out var sample) && sample.Tick < earliest) _windows.Dequeue();
        while (_interactions.TryPeek(out var input) && input.Tick < earliest) _interactions.Dequeue();
        while (_gestures.TryPeek(out var gesture) && gesture.Event.Tick < earliest) _gestures.Dequeue();
        while (_surfaces.TryPeek(out var surface) && surface.Tick < earliest) _surfaces.Dequeue();
        while (_decisions.TryPeek(out var decision) && decision.Tick < earliest) _decisions.Dequeue();
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _clearedAt = Now;
            _windows.Clear();
            _decisions.Clear(); _feedback.Clear();
            _gestures.Clear();
            _interactions.Clear(); _interactionDroppedThrough = null;
            _surfaces.Clear();
            _surfaceDroppedThrough = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _capture = null;
            Clear();
        }
    }
}
