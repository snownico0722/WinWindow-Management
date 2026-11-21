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

internal sealed class DesktopSnapshotHistory : IDisposable
{
    internal const int LookbackMilliseconds = 60_000;
    internal const int SampleIntervalMilliseconds = 1000;
    private readonly object _gate = new();
    private readonly Queue<DesktopSnapshot.Sample> _windows = new();
    private readonly Queue<Decision> _decisions = new();
    private readonly Queue<Gesture> _gestures = new();
    private PeriodicTimer? _timer;
    private Capture? _capture;
    private long _clearedAt;

    internal sealed record Gesture(string Session, GestureEvent Event);
    internal sealed record Decision(long Tick, LayoutRunObservation Observation);
    internal sealed record Capture(DateTimeOffset Time, long Tick, bool LookbackEnabled,
        DesktopSnapshot.Sample[] PastWindows, Decision[] PastDecisions, Gesture[] PastGestures, long GestureEarliestTick)
    {
        internal ConcurrentQueue<Gesture> LiveGestures { get; } = new();
        internal ConcurrentQueue<Decision> LiveDecisions { get; } = new();
    }

    internal void SetEnabled(bool enabled, nint workbench, Func<IReadOnlySet<nint>> getManaged)
    {
        lock (_gate)
        {
            if (enabled && _timer is null)
            {
                _clearedAt = Environment.TickCount64;
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
                    Trim(Environment.TickCount64);
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
            var decision = new Decision(Environment.TickCount64, observation);
            if (_timer is not null)
            {
                Trim(decision.Tick);
                _decisions.Enqueue(decision);
            }
            _capture?.LiveDecisions.Enqueue(decision);
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
                Trim(Environment.TickCount64);
                _gestures.Enqueue(gesture);
            }
            _capture?.LiveGestures.Enqueue(gesture);
        }
    }

    internal Capture BeginCapture()
    {
        lock (_gate)
        {
            var tick = Environment.TickCount64;
            Trim(tick);
            return _capture = new(DateTimeOffset.Now, tick, _timer is not null, _windows.ToArray(), _decisions.ToArray(),
                _gestures.ToArray(), _timer == null ? tick : Math.Max(_clearedAt, tick - LookbackMilliseconds));
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
        while (_windows.TryPeek(out var sample) && sample.Tick < earliest) _windows.Dequeue();
        while (_gestures.TryPeek(out var gesture) && gesture.Event.Tick < earliest) _gestures.Dequeue();
        while (_decisions.TryPeek(out var decision) && decision.Tick < earliest) _decisions.Dequeue();
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _clearedAt = Environment.TickCount64;
            _windows.Clear();
            _decisions.Clear();
            _gestures.Clear();
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
