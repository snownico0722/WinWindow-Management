using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.App;

internal sealed class BrowserObservations(string directory) : IDisposable
{
    private sealed record Query(BrowserWindowTarget Target, long Started, int Visit, Task<AccessibleBrowserPage?> Work);
    private readonly BrowserBridge? _bridge = BrowserBridge.TryStart(directory);
    private readonly List<Query> _requests = [];
    private readonly Dictionary<nint, (AccessibleBrowserPage Page, long At)> _accessibility = [];
    private readonly Dictionary<nint, BrowserExtensionSample> _samples = [];
    private BrowserWindowTarget? _foreground;
    private nint _queryForeground;
    private int _visit;
    private long _lastRequested;

    internal void ObserveForeground()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        Observe(BrowserAccessibility.Target(foreground), foreground);
    }

    private void Observe(BrowserWindowTarget? target, nint foreground)
    {
        long now = Environment.TickCount64;
        if (target != _foreground || target != null && foreground != _queryForeground)
        {
            _foreground = target;
            _queryForeground = foreground;
            _visit++;
            _lastRequested = now - 850;
        }
        for (int i = _requests.Count - 1; i >= 0; i--)
        {
            var request = _requests[i];
            if (!request.Work.IsCompleted) continue;
            _requests.RemoveAt(i);
            if (request.Visit == _visit && request.Target == target && now - request.Started <= 1200)
            {
                if (request.Work.IsCompletedSuccessfully && request.Work.Result is { } page)
                {
                    if (_accessibility.Count >= 24 && !_accessibility.ContainsKey(page.Target.Handle)) _accessibility.Clear();
                    _accessibility[page.Target.Handle] = (page, request.Started);
                }
                else _accessibility.Remove(request.Target.Handle);
            }
            _ = request.Work.Exception;
        }
        if (target == null || now - _lastRequested < 850 || _requests.Count >= 2 ||
            _requests.Any(request => request.Target == target)) return;
        _lastRequested = now;
        // COM 可以被浏览器阻塞。布局消息循环继续工作，且同一目标最多有一次在途读取。
        _requests.Add(new(target, now, _visit, Task.Run(() => BrowserAccessibility.Read(target, foreground))));
    }

    internal void Prepare(IReadOnlyList<WindowSnapshot> desktop, bool manual)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var target = BrowserAccessibility.Target(foreground);
        if (manual && target == null)
        {
            // 点击工作台上的整理按钮会让 WinWindow 成为前台。手动请求可以读取最前面的浏览器文档区域。
            target = desktop.Where(w => w.IsManageable).OrderBy(w => w.ZOrder)
                .Select(w => BrowserAccessibility.Target(w.Handle))
                .FirstOrDefault(candidate => candidate != null && BrowserAccessibility.IsSupported(candidate.Pid));
        }
        Observe(target, foreground);
        _samples.Clear();
        var reports = _bridge?.Read(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (reports is { Length: > 0 }) BindReports(desktop, reports, foreground);
    }

    private void BindReports(IReadOnlyList<WindowSnapshot> desktop, BrowserExtensionSample[] reports, nint foreground)
    {
        var windows = desktop.Where(w => w.IsManageable && BrowserAccessibility.Target(w.Handle)?.Pid == w.ProcessId &&
            BrowserAccessibility.IsSupported(w.ProcessId)).ToArray();
        var matches = new List<(nint Handle, BrowserExtensionSample Report)>();
        foreach (var report in reports)
        {
            var candidates = windows.Where(w => report.MatchesFrame(w)).ToArray();
            if (candidates.Length != 1) continue;
            var window = candidates[0];
            if (report.Focused != (window.Handle == foreground)) continue;
            if (ReadNative(window) is { UrlKey: { } known } && !string.Equals(known, report.UrlKey, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add((window.Handle, report));
        }
        // 多个在途浏览器配置或窗口重叠时，未知身份比串窗更安全。
        foreach (var match in matches.GroupBy(m => m.Handle))
            if (match.Count() == 1) _samples.Add(match.Key, match.Single().Report);
    }

    private AccessibleBrowserPage? ReadNative(WindowSnapshot window)
    {
        if (!_accessibility.TryGetValue(window.Handle, out var cached) || Environment.TickCount64 - cached.At > 1500 ||
            cached.Page.Target.Pid != window.ProcessId ||
            cached.Page.Target.Caption != BrowserAccessibility.Caption(window.Handle) ||
            BrowserAccessibility.FindViewport(window.Handle).Renderer != cached.Page.Renderer) return null;
        return cached.Page;
    }

    internal string? PageKey(WindowSnapshot window, long started)
    {
        // 同标题后台换页时，过期的原生身份不能否决新标签样本。
        var page = ReadNative(window);
        if (page?.Started != started) page = null;
        if (_samples.TryGetValue(window.Handle, out var report) &&
            report.IsFresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) && report.MatchesFrame(window) &&
            (page == null || string.Equals(page.UrlKey, report.UrlKey, StringComparison.OrdinalIgnoreCase)))
            return $"{report.UrlKey}:{report.Session:N}:{report.TabId}:{report.DocumentId}";
        return page?.UrlKey;
    }

    internal RectI? PlayerViewport(WindowSnapshot window)
    {
        if (!_samples.TryGetValue(window.Handle, out var sample) || sample.Video is not { } video ||
            !sample.IsFresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return null;
        var (_, viewport) = BrowserAccessibility.FindViewport(window.Handle);
        return BrowserPageEvidence.CanUseContent(sample, viewport) ? video.Project(viewport) : null;
    }

    internal TaskWindowHint[] Bind(IReadOnlyList<WindowSnapshot> desktop, TaskWindowHint[] hints, nint initialPlacement)
    {
        var byWindow = hints.ToDictionary(h => h.Handle);
        foreach (var window in desktop.Where(w => w.IsManageable && (_accessibility.ContainsKey(w.Handle) || _samples.ContainsKey(w.Handle))))
        {
            var (_, viewport) = BrowserAccessibility.FindViewport(window.Handle);
            if (viewport.IsEmpty) continue;
            RectI reading = default, video = default;
            bool editing = false, playing = false, audible = false;
            if (ReadNative(window) is { } native && native.Viewport == viewport)
            {
                reading = native.Reading;
                editing = window.IsForeground && native.Editing;
            }
            if (_samples.TryGetValue(window.Handle, out var extension) && extension.IsFresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                audible = extension.Audible && !window.IsForeground;
                if (BrowserPageEvidence.CanUseContent(extension, viewport))
                {
                    reading = extension.Reading?.Project(viewport) ?? reading;
                    video = extension.Video?.Project(viewport) ?? default;
                    editing = extension.Editing;
                    playing = extension.Playing && !video.IsEmpty;
                }
            }
            var hint = byWindow.GetValueOrDefault(window.Handle) ?? new(window.Handle, Role: WindowUseRole.Browser);
            // 标签页声音可以佐证已有的画面活动。纯音频页没有必须露出的播放器。
            bool audiblePicture = audible && hint.PassiveVisual;
            if (reading.IsEmpty && !editing && !playing && !audiblePicture) continue;
            byWindow[window.Handle] = BrowserPageEvidence.Apply(hint with { MediaCompanion = hint.MediaCompanion || audiblePicture },
                window, reading, video, editing && window.Handle != initialPlacement, playing);
        }
        return byWindow.Values.ToArray();
    }

    public void Dispose() => _bridge?.Dispose();
}
