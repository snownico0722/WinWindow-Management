using System.ComponentModel;
using System.Diagnostics;
using static NeatWin.Windows.NativeMethods;
using System.Runtime.Versioning;
using System.Text;
using NeatWin.Core;
using NeatWin.Reference;
using NeatWin.Windows;
using Windows.Media.Control;

namespace NeatWin.Recording;

internal sealed record ProcessActivity(string Instance, long? WorkingSetBytes, double? CpuCores, string? MediaSource);
internal sealed record MediaActivity(string Source, string Playback);
internal sealed record WindowActivity(int Id, string? Process, int? ComparedCells, int? ChangedCells,
    long? ForegroundMs, long? BackgroundVisibleMs, string ActivityHint);
internal sealed record ActivitySample(int Version, string Session, long Tick, long ObservedMilliseconds,
    WorkspaceFrame Frame, ProcessActivity[] Processes, WindowActivity[] Windows, MediaActivity[]? Media,
    uint? InputIdleMilliseconds);

// The color probes are transient. Persist only the masks of comparable cells and changed cells.
internal sealed class DesktopActivity(PlacementHistoryStore store)
{
    private sealed record ProcessInfo(string? Path, string? Source, string Instance, DateTime Start);
    private readonly Dictionary<uint, ProcessInfo> _process = [];
    private readonly Dictionary<(uint Pid, nint Hwnd), string?> _keys = [];
    private readonly Dictionary<string, (long Tick, TimeSpan Cpu)> _cpu = [];
    private readonly Dictionary<int, (RectI Rect, string Cells, int[] Colors)> _colors = [];
    private Task<GlobalSystemMediaTransportControlsSessionManager?>? _mediaManager;
    private long _mediaAttempt;
    private long _lastTick;
    private readonly Dictionary<int, (bool Foreground, bool Visible, int Motion)> _previous = [];

    internal static RecordedDisplay[] Displays() => Screen.AllScreens.Select(s => new RecordedDisplay(
        new(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height),
        new(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))).ToArray();

    internal void Retire(nint hwnd)
    {
        foreach (var key in _keys.Keys.Where(k => k.Hwnd == hwnd).ToArray()) _keys.Remove(key);
    }

    internal void Reset()
    {
        _process.Clear(); _keys.Clear(); _cpu.Clear(); _colors.Clear(); _previous.Clear(); _lastTick = 0;
        _mediaManager = null; _mediaAttempt = 0;
    }

    internal string? AppKey(WindowSnapshot w, bool create)
    {
        var key = (w.ProcessId, w.Handle);
        if (_keys.TryGetValue(key, out var found) && found != null) return found;
        try
        {
            using var process = Process.GetProcessById((int)w.ProcessId);
            var info = Identify(w.ProcessId, process);
            if (info.Path == null) return null;
            var name = new StringBuilder(256);
            if (NativeMethods.GetClassName(w.Handle, name, name.Capacity) == 0) return null;
            var value = store.Key(info.Path + "|" + name, create);
            if (_keys.Count >= 512) _keys.Clear();
            return _keys[key] = value;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private ProcessInfo Identify(uint id, Process process)
    {
        var start = process.StartTime.ToUniversalTime();
        if (_process.TryGetValue(id, out var info) && info.Start == start) return info;
        var path = process.MainModule?.FileName?.ToUpperInvariant();
        var source = new StringBuilder(512); uint length = (uint)source.Capacity;
        string? app = GetApplicationUserModelId(process.SafeHandle.DangerousGetHandle(), ref length, source) == 0
            ? source.ToString().ToUpperInvariant() : null;
        if (_process.Count >= 256) _process.Clear();
        return _process[id] = new(path, app, Guid.NewGuid().ToString("N"), start);
    }

    internal static bool Interactive()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(64);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    internal ActivitySample Sample(string session, long now, WorkspaceFrame frame,
        IReadOnlyList<WindowSnapshot> snapshot, ObservationIdentityTracker ids, bool visual)
    {
        var elapsed = now - _lastTick;
        var interval = _lastTick != 0 && elapsed is >= 0 and <= 8_000 ? elapsed : 0;
        if (interval == 0) { _colors.Clear(); _cpu.Clear(); _previous.Clear(); }
        _lastTick = now;
        var media = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? Media(now) : null;
        var usage = new Dictionary<uint, ProcessActivity?>();
        var windows = new List<WindowActivity>();
        var byId = snapshot.ToDictionary(ids.Id);
        var alive = new HashSet<int>();
        var seenColors = new HashSet<int>();
        foreach (var w in frame.Windows.Where(w => w.Manageable || w.SurfaceState != null))
        {
            var native = byId[w.Id];
            if (!usage.TryGetValue(native.ProcessId, out var resource))
                usage[native.ProcessId] = resource = ReadUsage(native.ProcessId, now);
            int? compared = null, changed = null;
            if (visual && seenColors.Count < 8 && w.Exposure is { Visible: > 0 } e && e.Cells.Contains('F') &&
                TryProbe(w.Rect) is { } colors)
            {
                seenColors.Add(w.Id);
                if (_colors.TryGetValue(w.Id, out var prev) && prev.Rect == w.Rect)
                {
                    int allowed = 0, delta = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        if (prev.Cells[i] != 'F' || e.Cells[i] != 'F') continue;
                        allowed |= 1 << i;
                        if (ColorChanged(colors[i], prev.Colors[i])) delta |= 1 << i;
                    }
                    compared = allowed; changed = delta;
                }
                _colors[w.Id] = (w.Rect, e.Cells, colors);
            }
            alive.Add(w.Id);
            bool visible = w.VisibleRatio > 0;
            bool known = _previous.TryGetValue(w.Id, out var prior) && interval > 0;
            int motion = changed is int bits && System.Numerics.BitOperations.PopCount((uint)bits) >= 2
                ? Math.Min(3, prior.Motion + 1) : 0;
            bool playing = resource?.MediaSource is string source && media?.Any(m => m.Source == source && m.Playback == "Playing") == true;
            string hint = ActivityHints.Classify(w.Foreground, w.VisibleRatio, w.Rect.Area / (double)Math.Max(1, w.WorkArea.Area),
                motion, playing, resource?.CpuCores);
            windows.Add(new(w.Id, resource?.Instance, compared, changed,
                known && prior.Foreground == w.Foreground ? (w.Foreground ? interval : 0) : null,
                known && prior.Foreground == w.Foreground && prior.Visible == visible ? (!w.Foreground && visible ? interval : 0) : null,
                hint));
            _previous[w.Id] = (w.Foreground, visible, motion);
        }
        foreach (int id in _previous.Keys.Where(id => !alive.Contains(id)).ToArray()) _previous.Remove(id);
        foreach (int id in _colors.Keys.Where(id => !seenColors.Contains(id)).ToArray()) _colors.Remove(id);
        var present = usage.Values.OfType<ProcessActivity>().ToArray();
        var instances = present.Select(p => p.Instance).ToHashSet();
        foreach (var id in _cpu.Keys.Where(id => !instances.Contains(id)).ToArray()) _cpu.Remove(id);
        var last = new LastInput { Size = 8 };
        uint? idle = GetLastInputInfo(ref last) ? unchecked((uint)now - last.Time) : null;
        return new(3, session, now, interval, frame, present, windows.ToArray(), media, idle);
    }

    private ProcessActivity? ReadUsage(uint pid, long now)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var info = Identify(pid, p);
            var cpu = p.TotalProcessorTime;
            double? cores = _cpu.TryGetValue(info.Instance, out var old) && now > old.Tick
                ? Math.Max(0, (cpu - old.Cpu).TotalMilliseconds / (now - old.Tick)) : null;
            _cpu[info.Instance] = (now, cpu);
            return new(info.Instance, p.WorkingSet64, cores, info.Source == null ? null : store.Key(info.Source, true));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private MediaActivity[]? Media(long now)
    {
        try
        {
            if (_mediaManager == null || _mediaManager.IsCompletedSuccessfully && _mediaManager.Result == null && now - _mediaAttempt > 60_000)
            {
                _mediaAttempt = now; _mediaManager = RequestManager();
            }
            if (!_mediaManager.IsCompletedSuccessfully || _mediaManager.Result is not { } manager) return null;
            var result = new List<MediaActivity>();
            foreach (var item in manager.GetSessions().Take(16))
                if (store.Key(item.SourceAppUserModelId.ToUpperInvariant(), true) is { } key)
                    result.Add(new(key, item.GetPlaybackInfo().PlaybackStatus.ToString()));
            return result.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private static async Task<GlobalSystemMediaTransportControlsSessionManager?> RequestManager()
    {
        try { return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static bool ColorChanged(int a, int b)
    {
        for (int shift = 0; shift <= 16; shift += 8)
            if (Math.Abs(((a >> shift) & 255) - ((b >> shift) & 255)) >= 20) return true;
        return false;
    }

    private static int[]? TryProbe(RectI r)
    {
        // Let GDI downsample straight into a 4x4 bitmap; no full-screen buffer or image on disk.
        using var bitmap = new Bitmap(4, 4);
        using var g = Graphics.FromImage(bitmap);
        var dest = g.GetHdc();
        var screen = GetDC(0);
        bool ok;
        try { ok = screen != 0 && StretchBlt(dest, 0, 0, 4, 4, screen, r.X, r.Y, r.Width, r.Height, 0x00CC0020); }
        finally { if (screen != 0) ReleaseDC(0, screen); g.ReleaseHdc(dest); }
        return ok ? Enumerable.Range(0, 16).Select(i => bitmap.GetPixel(i % 4, i / 4).ToArgb()).ToArray() : null;
    }

}
