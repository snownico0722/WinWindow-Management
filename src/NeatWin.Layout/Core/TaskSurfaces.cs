namespace NeatWin.Core;

// HostApp 和 ChildApp 使用安装内的应用和窗口类匹配键，沿用已导入的规则。局部卡片仍在桌面里遮挡像素，只退出任务变化、工作组和摆放习惯的输入。
public sealed record SurfaceRule(string Id, string HostApp, double MaxAreaRatio, string Action = "local", string? ChildApp = null)
{
    internal bool IsValid() => Id is { Length: > 0 and <= 64 } && Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
        HostApp is { Length: 24 } && HostApp.All(char.IsAsciiHexDigit) &&
        (ChildApp == null || ChildApp.Length == 24 && ChildApp.All(char.IsAsciiHexDigit)) &&
        MaxAreaRatio is > 0 and <= 1 && Action is "local" or "task";
}

internal sealed record SurfaceMatch(WindowSnapshot Child, WindowSnapshot Host, string Rule, double MaxArea,
    string? ReleaseReason = null, RectI? ReleasedChildRect = null, RectI? ReleasedHostRect = null, ContextPress? ContextPress = null);
internal enum SurfacePurpose { Unknown, LocalDetail, IndependentTask, CaptureTool }

internal sealed class TaskSurfaces
{
    private readonly Dictionary<nint, SurfaceMatch> _local = [];
    private readonly Dictionary<nint, (WindowSnapshot Child, WindowSnapshot? Source)> _capture = [];
    internal Func<WindowSnapshot, bool>? CaptureOverlay { get; init; }
    internal Action<WindowSnapshot>? IgnoreVisit { get; init; }
    internal Func<WindowSnapshot, WindowUseRole>? Role { get; init; }
    internal Func<WindowSnapshot, SurfacePurpose>? ChatPurpose { get; init; }
    internal Func<WindowSnapshot, string?>? AppKey { get; init; }
    internal Func<uint, uint>? ParentProcessId { get; init; }
    internal Func<SurfaceRule[]?>? Rules { get; init; }
    internal Func<WindowSnapshot, bool>? JustClicked { get; init; }
    internal Func<ContextPress?>? ReadContextPress { get; init; }
    internal Action<SurfaceMatch, string>? Changed { get; init; }
    internal IEnumerable<WindowSnapshot> Hosts => _local.Values.Select(m => m.Host);
    private SurfaceMatch? LocalMatch(WindowSnapshot w) =>
        _local.TryGetValue(w.Handle, out var match) && match.Child.ProcessId == w.ProcessId ? match : null;
    internal bool IsLocal(WindowSnapshot w) => w.IsTransientMenu || LocalMatch(w) != null || IsCaptureOverlay(w);
    internal bool IsCaptureOverlay(WindowSnapshot w) => _capture.TryGetValue(w.Handle, out var tool) && tool.Child.ProcessId == w.ProcessId;
    private bool IsMenu(WindowSnapshot w) => !IsCaptureOverlay(w) && (w.IsTransientMenu || LocalMatch(w)?.ContextPress != null);

    internal bool HasOpenMenu(IReadOnlyList<WindowSnapshot> desktop, nint foreground) =>
        desktop.FirstOrDefault(w => w.Handle == foreground) is { } work &&
        desktop.Any(w => w.MonitorHandle == work.MonitorHandle && IsMenu(w));

    internal IReadOnlyList<WindowSnapshot> PrepareForLayout(IReadOnlyList<WindowSnapshot> desktop, nint? workingForeground = null) =>
        ResolveLocalRoots(desktop.Where(w => workingForeground == null || !IsCaptureOverlay(w)).Select(w => IsLocal(w)
            ? w with
            {
                IsManageable = false,
                SurfaceState = null,
                IsForeground = workingForeground == null && w.IsForeground,
                Owner = w.Owner != 0 ? w.Owner : LocalMatch(w)?.Host.Handle ?? 0,
                IsTransientMenu = IsMenu(w),
            }
            : workingForeground == null ? w : w with { IsForeground = w.Handle == workingForeground }).ToArray());

    internal static IReadOnlyList<WindowSnapshot> ResolveLocalRoots(IReadOnlyList<WindowSnapshot> desktop)
    {
        if (!desktop.Any(w => !w.IsManageable && w.Owner != 0)) return desktop;
        var byHandle = desktop.ToDictionary(w => w.Handle);
        return desktop.Select(w =>
        {
            if (w.IsManageable || w.Owner == 0) return w;
            // 保留原生直接 owner 供层序检查。遇到独立任务框即停止，不越过它认领自己的菜单。
            nint root = w.Owner;
            int remaining = byHandle.Count;
            while (remaining-- > 0 && byHandle.TryGetValue(root, out var parent) && !parent.IsManageable &&
                !parent.IndependentFrame && parent.Owner != 0 && byHandle.ContainsKey(parent.Owner))
                root = parent.Owner;
            nint localRoot = remaining >= 0 && root != w.Owner ? root : 0;
            return w.LocalRoot == localRoot ? w : w with { LocalRoot = localRoot };
        }).ToArray();
    }
    private HashSet<(nint Handle, uint Pid)> _previous = [];
    internal Action? ResetObserved { get; init; }
    internal void Reset() { _local.Clear(); _capture.Clear(); _previous.Clear(); ResetObserved?.Invoke(); }

    private bool SameOrChildLineage(uint childPid, uint hostPid)
    {
        if (childPid == 0 || hostPid == 0) return false;
        if (childPid == hostPid) return true;
        if (ParentProcessId == null) return false;
        // NTQQ 的『图片和视频』（QQKit.exe）与微信『小程序/文章』（WeChatAppEx.exe）以独立子进程启动且 Owner==0；
        // 沿进程父子链向上最多追溯 3 跳即可在不读标题文件名的前提下还原真实宿主归属。
        uint current = childPid;
        for (int hop = 0; hop < 3; hop++)
        {
            uint parent = ParentProcessId(current);
            if (parent == 0 || parent == current) break;
            if (parent == hostPid) return true;
            current = parent;
        }
        return false;
    }

    internal void Update(IReadOnlyList<WindowSnapshot> desktop, WindowSnapshot? previous)
    {
        if (desktop.Count == 0) { Reset(); return; }
        var byHandle = desktop.ToDictionary(w => w.Handle);
        var press = ReadContextPress?.Invoke();
        foreach (var entry in _capture.Values.ToArray())
        {
            if (byHandle.TryGetValue(entry.Child.Handle, out var live) && live.ProcessId == entry.Child.ProcessId &&
                CaptureOverlay?.Invoke(live) == true) continue;
            _capture.Remove(entry.Child.Handle);
            if (entry.Source != null) Changed?.Invoke(new(entry.Child, entry.Source, "capture-overlay", 1, "tool-left"), "release");
        }
        foreach (var w in desktop)
        {
            if (_capture.ContainsKey(w.Handle) || CaptureOverlay?.Invoke(w) != true) continue;
            var source = byHandle.GetValueOrDefault(w.Owner) ?? previous;
            if (source?.Handle == w.Handle) source = null;
            _capture[w.Handle] = (w, source);
            if (source != null) Changed?.Invoke(new(w, source, "capture-overlay", 1), "suppress");
        }
        foreach (var m in _local.Values.ToArray())
        {
            var w = byHandle.GetValueOrDefault(m.Child.Handle);
            var h = byHandle.GetValueOrDefault(m.Host.Handle);
            bool childAlive = w?.ProcessId == m.Child.ProcessId;
            bool hostAlive = h?.ProcessId == m.Host.ProcessId;
            bool promoted = childAlive && hostAlive && SameOrChildLineage(w!.ProcessId, h!.ProcessId) && w.IsForeground &&
                Role?.Invoke(h) == WindowUseRole.Chat && ChatPurpose?.Invoke(w) == SurfacePurpose.IndependentTask;
            bool keepUnboundedDetail = m.Rule is "chat-viewer-detail" or "cross-process-viewer" ||
                m.Rule == "non-resizable-dialog" && w is { IsResizable: false };
            if (childAlive && hostAlive && !promoted && w!.Owner == m.Child.Owner &&
                (keepUnboundedDetail || (m.ContextPress is { } input ? IsContextPopup(w, h!, input) : Fits(w, h!, m.MaxArea)))) continue;
            _local.Remove(m.Child.Handle);
            Changed?.Invoke(m with { ReleaseReason = !childAlive ? "child-left" : !hostAlive ? "host-left" : "detached-or-enlarged",
                ReleasedChildRect = childAlive ? w!.VisualRect : null, ReleasedHostRect = hostAlive ? h!.VisualRect : null }, "release");
        }
        SurfaceRule[]? rules = null;
        bool rulesRead = false;
        foreach (var w in desktop)
        {
            if (LocalMatch(w) != null || IsCaptureOverlay(w) || w.Handle == previous?.Handle || w.VisualRect.IsEmpty) continue;
            var host = byHandle.GetValueOrDefault(w.Owner);
            bool appeared = !_previous.Contains((w.Handle, w.ProcessId));
            if (host == null && appeared && press != null && byHandle.TryGetValue(press.Root, out var clicked) &&
                IsContextPopup(w, clicked, press)) host = clicked;
            if (host == null && previous != null && previous.ProcessId != 0 && SameOrChildLineage(w.ProcessId, previous.ProcessId) &&
                (w.IsForeground || JustClicked?.Invoke(previous) == true) && appeared &&
                byHandle.TryGetValue(previous.Handle, out var live) && live.ProcessId == previous.ProcessId)
                host = live;
            // 若前序焦点已被输入法或过渡窗切走，仍允许刚出现的跨进程子查看器关联回当前屏幕上唯一的父进程聊天主窗。
            if (host == null && appeared && ParentProcessId != null)
            {
                uint parentPid = ParentProcessId(w.ProcessId);
                if (parentPid != 0 && parentPid != w.ProcessId)
                {
                    var parents = desktop.Where(cand => cand.Handle != w.Handle && cand.ProcessId == parentPid &&
                        cand.MonitorHandle == w.MonitorHandle && cand.IsManageable && Role?.Invoke(cand) == WindowUseRole.Chat).Take(2).ToArray();
                    if (parents.Length == 1) host = parents[0];
                }
            }
            if (host == null || host.Handle == w.Handle || host.VisualRect.IsEmpty) continue;
            bool owned = w.Owner == host.Handle;
            bool crossProcessChild = w.ProcessId != host.ProcessId && SameOrChildLineage(w.ProcessId, host.ProcessId);
            if (!rulesRead) { rules = Rules?.Invoke(); rulesRead = true; }
            SurfaceRule? custom = null;
            if (rules is { Length: > 0 })
            {
                string? hostKey = AppKey?.Invoke(host);
                custom = rules.FirstOrDefault(r => r.HostApp.Equals(hostKey, StringComparison.OrdinalIgnoreCase) &&
                    (r.ChildApp == null || r.ChildApp.Equals(AppKey?.Invoke(w), StringComparison.OrdinalIgnoreCase)) &&
                    Fits(w, host, r.MaxAreaRatio));
            }
            void ReportTask(string reason)
            {
                if (!_previous.Contains((w.Handle, w.ProcessId)))
                    Changed?.Invoke(new(w, host, custom?.Id ?? reason, custom?.MaxAreaRatio ?? 1, reason), "task");
            }
            if (custom?.Action == "task") { ReportTask("configured-task"); continue; }
            bool chat = Role?.Invoke(host) == WindowUseRole.Chat;
            double limit = custom?.MaxAreaRatio ?? (chat ? .43 : .30);
            var purpose = chat && SameOrChildLineage(w.ProcessId, host.ProcessId) ? ChatPurpose?.Invoke(w) : null;

            if (purpose != SurfacePurpose.IndependentTask && appeared && press != null && IsContextPopup(w, host, press))
            {
                var menu = new SurfaceMatch(w, host, "context-click-popup", limit, ContextPress: press);
                _local[w.Handle] = menu; Changed?.Invoke(menu, "suppress"); continue;
            }
            // 关键修复：微信/QQ『图片和视频』（1033×1200 或 1474×913）、『群聊的聊天记录』、跨进程 QQKit 查看器、
            // 固定尺寸对话框，面积经常大于宿主窗口的 43%，必须在 Fits() 面积截断之前直接拦截为局部表面！
            if (purpose == SurfacePurpose.LocalDetail)
            {
                var viewer = new SurfaceMatch(w, host, "chat-viewer-detail", 1);
                _local[w.Handle] = viewer; Changed?.Invoke(viewer, "suppress"); continue;
            }
            if (crossProcessChild && chat && purpose != SurfacePurpose.IndependentTask && w.NativeUtility)
            {
                var helper = new SurfaceMatch(w, host, "cross-process-viewer", 1);
                _local[w.Handle] = helper; Changed?.Invoke(helper, "suppress"); continue;
            }
            if (purpose != SurfacePurpose.IndependentTask && !w.IsResizable &&
                (owned || !w.IndependentFrame && (w.NativeUtility || SameOrChildLineage(w.ProcessId, host.ProcessId))))
            {
                var modal = new SurfaceMatch(w, host, "non-resizable-dialog", limit);
                _local[w.Handle] = modal; Changed?.Invoke(modal, "suppress"); continue;
            }
            if (!Fits(w, host, limit)) { ReportTask("separate-size-or-location"); continue; }
            if (purpose == SurfacePurpose.IndependentTask) { ReportTask("independent-chat-task"); continue; }
            bool utility = w.NativeUtility || owned && !w.IndependentFrame && !w.IsResizable;
            bool compact = w.VisualRect.Width <= 480 * w.Dpi / 96d && w.VisualRect.Height <= 640 * w.Dpi / 96d;
            bool distinctChild = custom?.ChildApp != null && !custom.ChildApp.Equals(custom.HostApp, StringComparison.OrdinalIgnoreCase);
            bool confirmedDetail = purpose == SurfacePurpose.LocalDetail && (owned || JustClicked?.Invoke(host) == true);
            bool peek = !w.IndependentFrame && compact && (chat || custom != null) &&
                (owned || JustClicked?.Invoke(host) == true && (!w.IsResizable || chat));
            if (!utility && !peek && !confirmedDetail && !distinctChild) { ReportTask("independent-or-ambiguous"); continue; }
            var match = new SurfaceMatch(w, host, custom?.Id ?? (chat ? "chat-detail" : "owned-tool"), limit);
            _local[w.Handle] = match; Changed?.Invoke(match, "suppress");
        }
        if (desktop.FirstOrDefault(w => w.IsForeground) is { } foreground && IsLocal(foreground))
            IgnoreVisit?.Invoke(foreground);
        _previous = desktop.Select(w => (w.Handle, w.ProcessId)).ToHashSet();
    }

    internal static bool CanBeCaptureTool(WindowSnapshot w) => !w.IndependentFrame &&
        (w.NativeUtility || w.IsTopmost || w.Owner != 0);

    internal static bool IsCaptureTool(WindowSnapshot w, SurfacePurpose purpose)
    {
        if (!CanBeCaptureTool(w) || purpose is SurfacePurpose.IndependentTask or SurfacePurpose.LocalDetail) return false;
        return purpose == SurfacePurpose.CaptureTool || w.IsTopmost && w.NativeUtility && !w.WorkArea.IsEmpty &&
            w.VisualRect.Intersect(w.WorkArea).Area >= w.WorkArea.Area * .90;
    }

    internal static SurfacePurpose ChatCaption(string caption) => WindowClassification.ChatCaption(caption);

    private static bool IsContextPopup(WindowSnapshot child, WindowSnapshot host, ContextPress press)
    {
        if (child.Handle == host.Handle || press.Root != host.Handle || press.Pid != host.ProcessId ||
            child.ProcessId != press.Pid || child.IndependentFrame || child.IsResizable || child.HasCaption ||
            child.Owner != 0 && child.Owner != host.Handle || child.WorkArea.IsEmpty) return false;
        var rect = child.VisualRect;
        double gap = 24 * Math.Max(96u, child.Dpi) / 96d;
        return !rect.IsEmpty && rect.Area <= child.WorkArea.Area * .18 &&
            press.Point.X >= rect.Left - gap && press.Point.X <= rect.Right + gap &&
            press.Point.Y >= rect.Top - gap && press.Point.Y <= rect.Bottom + gap;
    }

    private static bool Fits(WindowSnapshot child, WindowSnapshot host, double limit) =>
        child.MonitorHandle == host.MonitorHandle && !host.VisualRect.IsEmpty && !child.VisualRect.IsEmpty &&
        child.VisualRect.Area <= host.VisualRect.Area * limit && NearHost(child, host);

    private static bool NearHost(WindowSnapshot child, WindowSnapshot host)
    {
        var a = child.VisualRect; var b = host.VisualRect;
        if (child.Owner == host.Handle || a.Intersect(b).Area >= a.Area * .50) return true;
        double gap = 32 * child.Dpi / 96d;
        return a.VerticalOverlapRatio(b) >= .5 && RectI.IntervalGap(a.Left,a.Right,b.Left,b.Right) <= gap ||
            a.HorizontalOverlapRatio(b) >= .5 && RectI.IntervalGap(a.Top,a.Bottom,b.Top,b.Bottom) <= gap;
    }
}
