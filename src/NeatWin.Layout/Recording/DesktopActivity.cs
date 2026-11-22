using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using NeatWin.Core;
using NeatWin.Reference;
using NeatWin.Windows;
using Windows.Media.Control;
using static NeatWin.Windows.NativeMethods;

namespace NeatWin.Recording;

internal sealed record ProcessActivity(string Instance, long? WorkingSetBytes, double? CpuCores, string? MediaSource, float? AudioPeak = null, float? SharedAudioPeak = null,
    long? OwnWorkingSetBytes = null, long? OwnPrivateBytes = null, int? DescendantsRead = null, int? DescendantsConsidered = null, uint? ProcessId = null, string? ExecutablePath = null,
    DateTime? StartedUtc = null, string? AppUserModelId = null);
internal sealed record MediaActivity(string Source, string Playback, string? AppUserModelId = null);
internal sealed record WindowActivity(int Id, string? Process, int? ComparedCells, int? ChangedCells,
    long? ForegroundMs, long? BackgroundVisibleMs, string ActivityHint, RectI? Fragment = null, double? FragmentChange = null,
    string? ApplicationRole = null, double? VisibleRatio = null, double? LargestFragmentRatio = null, long? FocusEventMs = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Playing = false);
internal sealed record ActivitySample(int Version, string Session, long Tick, long ObservedMilliseconds,
    WorkspaceFrame Frame, ProcessActivity[] Processes, WindowActivity[] Windows, MediaActivity[]? Media,
    uint? InputIdleMilliseconds, long? AudioTick = null, bool? DisplayRequired = null, UsageInterval? Usage = null, CoUseSample[]? CoUse = null, FocusCoverage? FocusCoverage = null);

// The color probes are transient. Persist only the masks of comparable cells and changed cells.
internal sealed class DesktopActivity(PlacementHistoryStore store)
{
    private sealed record ProcessInfo(string? Path, string? Source, string Instance, DateTime Start);
    private readonly byte[] _ephemeralKey = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<uint, ProcessInfo> _process = [];
    private readonly Dictionary<(uint Pid, nint Hwnd), RawWindowIdentity> _rawWindows = [];
    private readonly Dictionary<(uint Pid, nint Hwnd), string?> _keys = [];
    private readonly Dictionary<(uint Pid, nint Hwnd), (string? App, string Class, WindowUseRole Role)> _roles = [];
    private readonly OfficeSurfaceProbe _officeSurface = new();
    private readonly Dictionary<(uint Pid, DateTime Start), (long Tick, TimeSpan Cpu)> _cpu = [];
    private readonly Dictionary<int, (RectI Rect, string Cells, int[] Colors)> _colors = [];
    private readonly Dictionary<int, (RectI Window, RectI Patch, int[] Colors, int Streak)> _fragments = [];
    private AudioSessionMeter _audio = new();
    private Task<GlobalSystemMediaTransportControlsSessionManager?>? _mediaManager;
    private long _mediaAttempt;
    private long _lastTick;
    private readonly Dictionary<int, (bool Foreground, bool Visible, int Motion)> _previous = [];

    internal static RecordedDisplay[] Displays() => Screen.AllScreens.Select(s => new RecordedDisplay(
        new(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height),
        new(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))).ToArray();

    internal void Retire(nint hwnd)
    {
        foreach (var key in _rawWindows.Keys.Where(k => k.Hwnd == hwnd).ToArray()) _rawWindows.Remove(key);
        foreach (var key in _keys.Keys.Where(k => k.Hwnd == hwnd).ToArray()) _keys.Remove(key);
        foreach (var key in _roles.Keys.Where(k => k.Hwnd == hwnd).ToArray()) _roles.Remove(key);
        _officeSurface.Retire(hwnd);
    }

    internal void ResetVisual() { _colors.Clear(); _fragments.Clear(); _previous.Clear(); }

    internal void Reset()
    {
        _process.Clear(); _rawWindows.Clear(); _keys.Clear(); _roles.Clear(); _cpu.Clear(); _colors.Clear(); _previous.Clear(); _lastTick = 0;
        _fragments.Clear(); _audio = new();
        _mediaManager = null; _mediaAttempt = 0;
    }

    private string EphemeralKey(string identity) =>
        Convert.ToHexString(HMACSHA256.HashData(_ephemeralKey, Encoding.UTF8.GetBytes(identity)))[..24];

    private string ResolveKey(string identity, bool persist) =>
        store.Key(identity, persist) ?? EphemeralKey(identity);

    private (string? App, string Class, WindowUseRole Role) Identity(WindowSnapshot window)
    {
        var key = (window.ProcessId, window.Handle);
        if (!_roles.TryGetValue(key, out var identity))
        {
            var name = new StringBuilder(256);
            NativeMethods.GetClassName(window.Handle, name, name.Capacity);
            string? executable = null;
            try
            {
                using var process = Process.GetProcessById((int)window.ProcessId);
                executable = process.ProcessName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { }
            if (_roles.Count >= 512) _roles.Clear();
            string windowClass = name.ToString();
            _roles[key] = identity = (executable, windowClass, WindowUseRoles.Identify(executable, windowClass));
        }
        return identity;
    }

    internal bool CaptureOverlay(WindowSnapshot window)
    {
        if (!TaskSurfaces.CanBeCaptureTool(window)) return false;
        var app = Identity(window).App?.ToLowerInvariant();
        if (app is not ("qq" or "tim" or "wechat" or "weixin" or "qqscreenshot")) return false;
        return TaskSurfaces.IsCaptureTool(window, NativeWindowActivity.ChatSurface(window));
    }

    internal WindowUseRole SemanticRole(WindowSnapshot window, ForegroundReading reading = default)
    {
        var identity = Identity(window);
        return reading.Root == window.Handle && reading.Pid == window.ProcessId &&
            WindowUseRoles.IsSharedOfficeFrame(identity.App, identity.Class)
            ? WindowUseRoles.Identify(identity.App, identity.Class, _officeSurface.Sample(reading, Environment.TickCount64))
            : identity.Role;
    }

    internal bool AwaitingOfficeSurface(nint handle) => _officeSurface.JustStarted(handle, Environment.TickCount64);

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
            var value = store.Key(info.Path.ToUpperInvariant() + "|" + name, create);
            if (value == null) return null;
            if (_keys.Count >= 512) _keys.Clear();
            return _keys[key] = value;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    internal RawWindowIdentity? RawIdentity(nint hwnd, uint pid, bool refreshTitle)
    {
        if (pid == 0 || hwnd == 0 || pid == Environment.ProcessId) return null;
        var key = (pid, hwnd);
        bool known = _rawWindows.TryGetValue(key, out var info);
        if (known && !refreshTitle) return info;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var livePid);
        if (livePid != pid) return null;
        if (!known)
        {
            var cls = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
            string? app = null, path = null;
            DateTime? started = null;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                app = process.ProcessName;
                var original = Identify(pid, process);
                path = original.Path; started = original.Start;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { }
            info = new($"0x{hwnd:X}", pid, app, path, started, cls.Length > 0 ? cls.ToString() : null, null);
            if (_rawWindows.Count >= 1024) _rawWindows.Clear();
        }
        // 标题可以在同一 HWND 内变化。展示/改名/快照时读取，滚轮与插入点事件复用最近一次读数。
        var title = new StringBuilder(Math.Max(256, NativeMethods.GetWindowTextLength(hwnd) + 1));
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        return _rawWindows[key] = info! with
        {
            Title = title.ToString(),
            Style = unchecked((uint)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle)),
            ExtendedStyle = unchecked((uint)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle))
        };
    }

    private ProcessInfo Identify(uint id, Process process)
    {
        var start = process.StartTime.ToUniversalTime();
        if (_process.TryGetValue(id, out var info) && info.Start == start) return info;
        string? path = null;
        try { path = process.MainModule?.FileName; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
        var source = new StringBuilder(512); uint length = (uint)source.Capacity;
        string? app = null;
        try
        {
            if (GetApplicationUserModelId(process.SafeHandle.DangerousGetHandle(), ref length, source) == 0)
                app = source.ToString();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
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

    private static bool? QueryDisplayRequired() =>
        CallNtPowerInformation(16, 0, 0, out uint state, sizeof(uint)) == 0 ? (state & 2) != 0 : null;

    private static Dictionary<uint, List<uint>> SnapshotProcessChildren()
    {
        var children = new Dictionary<uint, List<uint>>();
        var snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == 0 || snap == -1) return children;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snap, ref entry)) return children;
            do
            {
                if (entry.ProcessId == 0 || entry.ParentProcessId == 0 || entry.ProcessId == entry.ParentProcessId) continue;
                if (!children.TryGetValue(entry.ParentProcessId, out var list))
                    children[entry.ParentProcessId] = list = [];
                if (list.Count < 64) list.Add(entry.ProcessId);
            } while (Process32Next(snap, ref entry));
        }
        finally { _ = CloseHandle(snap); }
        return children;
    }

    private static List<uint> CollectDescendants(uint rootPid, Dictionary<uint, List<uint>> childrenByParent, HashSet<uint> rootWindowPids)
    {
        var result = new List<uint>();
        if (!childrenByParent.TryGetValue(rootPid, out var direct)) return result;
        var visited = new HashSet<uint> { rootPid };
        var queue = new Queue<(uint Pid, int Depth)>();
        foreach (var child in direct) queue.Enqueue((child, 1));
        while (queue.Count > 0 && result.Count < 32)
        {
            var (pid, depth) = queue.Dequeue();
            if (!visited.Add(pid) || rootWindowPids.Contains(pid)) continue;
            result.Add(pid);
            if (depth < 3 && childrenByParent.TryGetValue(pid, out var next))
                foreach (var grand in next) queue.Enqueue((grand, depth + 1));
        }
        return result;
    }

    internal ActivitySample Sample(string session, long now, WorkspaceFrame frame,
        IReadOnlyList<WindowSnapshot> snapshot, ObservationIdentityTracker ids, bool visual,
        CompiledLayoutRules? rules = null, bool persistKeys = true)
    {
        rules ??= CompiledLayoutRules.Default;
        var elapsed = now - _lastTick;
        var interval = _lastTick != 0 && elapsed is >= 0 and <= 8_000 ? elapsed : 0;
        if (interval == 0) { _colors.Clear(); _cpu.Clear(); _previous.Clear(); _fragments.Clear(); }
        _lastTick = now;
        var media = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? Media(now, persistKeys) : null;
        var audio = _audio.Poll(now);
        var displayRequired = QueryDisplayRequired();
        var childrenByParent = SnapshotProcessChildren();
        var usage = new Dictionary<uint, ProcessActivity?>();
        var windows = new List<WindowActivity>();
        var byId = snapshot.ToDictionary(ids.Id);
        var activeSnapshots = snapshot.Where(w => w.IsManageable || w.SurfaceState != null).ToArray();
        var rootWindowPids = activeSnapshots.Select(w => w.ProcessId).Where(p => p != 0).ToHashSet();
        var processWindows = activeSnapshots.GroupBy(w => w.ProcessId).ToDictionary(g => g.Key, g => g.Count());
        var alive = new HashSet<int>();
        var seenColors = new HashSet<int>();
        var sampledFragments = new HashSet<int>();
        int visualBudget = 8;
        foreach (var w in frame.Windows.Where(w => w.Manageable || w.SurfaceState != null))
        {
            var native = byId[w.Id];
            if (!usage.TryGetValue(native.ProcessId, out var resource))
                usage[native.ProcessId] = resource = ReadUsage(native.ProcessId, now, audio, childrenByParent, rootWindowPids, persistKeys);
            int? compared = null, changed = null;
            RectI? fragment = null;
            double? fraction = null;
            int fragmentStreak = 0;
            if (visual && visualBudget > 0 && !w.Foreground && w.Exposure is { Visible: > 0 and < .25 } exposure &&
                ActivityHints.FragmentPatch(exposure.LargestFragment) is { } patch)
            {
                visualBudget--;
                var cursor = Cursor.Position;
                var pixels = new Rectangle(patch.X, patch.Y, patch.Width, patch.Height).Contains(cursor)
                    ? null : TryProbe(patch, 16);
                if (pixels != null)
                {
                    sampledFragments.Add(w.Id);
                    fragment = patch;
                    if (_fragments.TryGetValue(w.Id, out var old) && old.Window == w.Rect && old.Patch == patch)
                    {
                        int changes = 0;
                        for (int i = 0; i < pixels.Length; i++)
                            if (ColorChanged(old.Colors[i], pixels[i])) changes++;
                        fraction = changes / (double)pixels.Length;
                        fragmentStreak = fraction >= rules.FragmentChangeThreshold ? Math.Min(3, old.Streak + 1) : 0;
                    }
                    _fragments[w.Id] = (w.Rect, patch, pixels, fragmentStreak);
                }
            }
            else if (visual && visualBudget > 0 && w.Exposure is { Visible: > 0 } e && e.Cells.Contains('F') &&
                TryProbe(w.Rect, 4) is { } colors)
            {
                visualBudget--;
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
            // Shared playback and power requests cannot identify a particular browser window.
            string hint = ActivityHints.Classify(w.Foreground, w.VisibleRatio, w.Rect.Area / (double)Math.Max(1, w.WorkArea.Area),
                motion, playing, resource?.CpuCores, resource?.AudioPeak >= rules.AudioPeakThreshold, fragmentStreak, processWindows.GetValueOrDefault(native.ProcessId));
            windows.Add(new(w.Id, resource?.Instance, compared, changed,
                known && prior.Foreground == w.Foreground ? (w.Foreground ? interval : 0) : null,
                known && prior.Foreground == w.Foreground && prior.Visible == visible ? (!w.Foreground && visible ? interval : 0) : null,
                hint, fragment, fraction, persistKeys ? SemanticRole(native).ToString() : null,
                w.VisibleRatio, w.Exposure?.LargestFragment.Area / (double)Math.Max(1, w.Rect.Area),
                Playing: playing && processWindows.GetValueOrDefault(native.ProcessId) == 1));
            _previous[w.Id] = (w.Foreground, visible, motion);
        }
        foreach (var id in _fragments.Keys.Where(id => !sampledFragments.Contains(id)).ToArray()) _fragments.Remove(id);
        foreach (int id in _previous.Keys.Where(id => !alive.Contains(id)).ToArray()) _previous.Remove(id);
        foreach (int id in _colors.Keys.Where(id => !seenColors.Contains(id)).ToArray()) _colors.Remove(id);
        var present = usage.Values.OfType<ProcessActivity>().ToArray();
        var sharedSources = present.Where(p => p.MediaSource != null).GroupBy(p => p.MediaSource!)
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(p => p.Instance)).ToHashSet();
        foreach (var id in _cpu.Where(p => p.Value.Tick != now).Select(p => p.Key).ToArray()) _cpu.Remove(id);
        var last = new LastInput { Size = 8 };
        uint? idle = GetLastInputInfo(ref last) ? unchecked((uint)now - last.Time) : null;
        return new(8, session, now, interval, frame, present, windows.Select(w => w.Playing && w.Process != null && sharedSources.Contains(w.Process)
            ? w with { Playing = false } : w).ToArray(), media, idle, audio?.Tick, displayRequired);
    }

    private ProcessActivity? ReadUsage(uint pid, long now, AudioSessionMeter.Reading? audio,
        Dictionary<uint, List<uint>> childrenByParent, HashSet<uint> rootWindowPids, bool persistKeys)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var info = Identify(pid, p);
            long ownWorkingSet = p.WorkingSet64;
            long workingSet = ownWorkingSet;
            long? ownPrivate = null;
            try { ownPrivate = p.PrivateMemorySize64; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            double? cores = CpuDelta(p, pid, info.Start, now);
            float? maxAudio = audio != null && audio.Peaks.TryGetValue(pid, out var rootPeak) ? rootPeak : null;
            float? sharedAudio = audio != null && audio.SharedPeaks.TryGetValue(pid, out var sharedPeak) ? sharedPeak : null;
            var descendants = CollectDescendants(pid, childrenByParent, rootWindowPids);
            int sampledChildren = 0;
            foreach (var childPid in descendants)
            {
                if (audio != null && audio.Peaks.TryGetValue(childPid, out var childPeak))
                    maxAudio = maxAudio is float prev ? Math.Max(prev, childPeak) : childPeak;
                if (audio != null && audio.SharedPeaks.TryGetValue(childPid, out var childShared))
                    sharedAudio = Math.Max(sharedAudio ?? 0, childShared);
                if (sampledChildren >= 12) continue;
                try
                {
                    using var child = Process.GetProcessById((int)childPid);
                    if (child.StartTime.ToUniversalTime() < info.Start) continue;
                    workingSet += child.WorkingSet64;
                    sampledChildren++;
                    var childDelta = CpuDelta(child, childPid, child.StartTime.ToUniversalTime(), now);
                    if (childDelta.HasValue && cores.HasValue) cores += childDelta;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { }
            }
            return new(info.Instance, workingSet, cores, info.Source == null ? null : ResolveKey(info.Source.ToUpperInvariant(), persistKeys), maxAudio, sharedAudio,
                ownWorkingSet, ownPrivate, sampledChildren, descendants.Count, pid, info.Path, info.Start, info.Source);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private double? CpuDelta(Process process, uint pid, DateTime start, long now)
    {
        var key = (pid, start);
        TimeSpan cpu;
        try { cpu = process.TotalProcessorTime; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { return null; }
        bool comparable = _cpu.TryGetValue(key, out var old) && now > old.Tick;
        _cpu[key] = (now, cpu);
        return comparable ? Math.Max(0, (cpu - old.Cpu).TotalMilliseconds / (now - old.Tick)) : null;
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private MediaActivity[]? Media(long now, bool persistKeys)
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
            {
                var aumid = item.SourceAppUserModelId;
                if (!string.IsNullOrWhiteSpace(aumid))
                    result.Add(new(ResolveKey(aumid.ToUpperInvariant(), persistKeys), item.GetPlaybackInfo().PlaybackStatus.ToString(), aumid));
            }
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

    private static int[]? TryProbe(RectI r, int grid)
    {
        // Downsample straight into a small ARGB bitmap using COLORONCOLOR (no BLACKONWHITE scanline AND distortion).
        using var bitmap = new Bitmap(grid, grid, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        var dest = g.GetHdc();
        var screen = GetDC(0);
        bool ok = false;
        try
        {
            if (screen != 0)
            {
                _ = SetStretchBltMode(dest, StretchColorOnColor);
                ok = StretchBlt(dest, 0, 0, grid, grid, screen, r.X, r.Y, r.Width, r.Height, 0x00CC0020);
            }
        }
        finally { if (screen != 0) _ = ReleaseDC(0, screen); g.ReleaseHdc(dest); }
        if (!ok) return null;
        var data = bitmap.LockBits(new Rectangle(0, 0, grid, grid), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[grid * grid];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
