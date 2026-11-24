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
    internal Func<SurfaceRule[]?>? Rules { get; init; }
    internal Func<WindowSnapshot, bool>? JustClicked { get; init; }
    internal Func<ContextPress?>? ReadContextPress { get; init; }
    internal Action<SurfaceMatch, string>? Changed { get; init; }
    internal IEnumerable<WindowSnapshot> Hosts => _local.Values.Select(m => m.Host);
    internal bool IsLocal(WindowSnapshot w) => _local.TryGetValue(w.Handle, out var match) && match.Child.ProcessId == w.ProcessId ||
        IsCaptureOverlay(w);
    internal bool IsCaptureOverlay(WindowSnapshot w) => _capture.TryGetValue(w.Handle, out var tool) && tool.Child.ProcessId == w.ProcessId;
    private HashSet<(nint Handle, uint Pid)> _previous = [];
    internal Action? ResetObserved { get; init; }
    internal void Reset() { _local.Clear(); _capture.Clear(); _previous.Clear(); ResetObserved?.Invoke(); }

    internal void Update(IReadOnlyList<WindowSnapshot> desktop, WindowSnapshot? previous)
    {
        if (desktop.Count == 0) { Reset(); return; }
        var byHandle = desktop.ToDictionary(w => w.Handle);
        var press = ReadContextPress?.Invoke();
        // 全屏选区可以比原窗口大，翻译结果也可能没有 Owner。用工具窗口自身的证据判断，
        // 来源只用于记录。这样在浏览器或游戏上调用 QQ 热键也不会凭空新增一个桌面任务。
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
        // 升级成独立窗口或宿主关闭时解除归属。不能因为这个句柄曾显示过卡片就永久屏蔽它。
        foreach (var m in _local.Values.ToArray())
        {
            var w = byHandle.GetValueOrDefault(m.Child.Handle);
            var h = byHandle.GetValueOrDefault(m.Host.Handle);
            bool childAlive = w?.ProcessId == m.Child.ProcessId;
            bool hostAlive = h?.ProcessId == m.Host.ProcessId;
            bool promoted = childAlive && hostAlive && w!.ProcessId == h!.ProcessId && w.IsForeground &&
                Role?.Invoke(h) == WindowUseRole.Chat && ChatPurpose?.Invoke(w) == SurfacePurpose.IndependentTask;
            if (childAlive && hostAlive && !promoted && w!.Owner == m.Child.Owner &&
                (m.ContextPress is { } input ? IsContextPopup(w, h!, input) : Fits(w, h!, m.MaxArea))) continue;
            _local.Remove(m.Child.Handle);
            Changed?.Invoke(m with { ReleaseReason = !childAlive ? "child-left" : !hostAlive ? "host-left" : "detached-or-enlarged",
                ReleasedChildRect = childAlive ? w!.VisualRect : null, ReleasedHostRect = hostAlive ? h!.VisualRect : null }, "release");
        }
        SurfaceRule[]? rules = null;
        bool rulesRead = false;
        foreach (var w in desktop)
        {
            if (IsLocal(w) || w.Handle == previous?.Handle || w.VisualRect.IsEmpty) continue;
            var host = byHandle.GetValueOrDefault(w.Owner);
            // 便签可能只是后台一张小纸片。上下文菜单可以比宿主大，也不一定夺得前台。
            bool appeared = !_previous.Contains((w.Handle, w.ProcessId));
            if (host == null && appeared && press != null && byHandle.TryGetValue(press.Root, out var clicked) &&
                IsContextPopup(w, clicked, press)) host = clicked;
            if (host == null && previous != null && previous.ProcessId != 0 && previous.ProcessId == w.ProcessId &&
                (w.IsForeground || JustClicked?.Invoke(previous) == true) && !_previous.Contains((w.Handle, w.ProcessId)) && byHandle.TryGetValue(previous.Handle, out var live) && live.ProcessId == previous.ProcessId)
                host = live;
            if (host == null || host.Handle == w.Handle || host.VisualRect.IsEmpty) continue;
            // 原生 Owner 可以跨进程；无 Owner 时必须是刚从同进程宿主打开的窗口。
            bool owned = w.Owner == host.Handle;
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
            var purpose = chat && w.ProcessId == host.ProcessId ? ChatPurpose?.Invoke(w) : null;
            if (purpose != SurfacePurpose.IndependentTask && appeared && press != null && IsContextPopup(w, host, press))
            {
                var menu = new SurfaceMatch(w, host, "context-click-popup", limit, ContextPress: press);
                _local[w.Handle] = menu; Changed?.Invoke(menu, "suppress"); continue;
            }
            if (!Fits(w, host, limit)) { ReportTask("separate-size-or-location"); continue; }
            if (purpose == SurfacePurpose.IndependentTask) { ReportTask("independent-chat-task"); continue; }
            // 独立聊天、文章、弹出视频都可能比宿主小。只有明确的局部表面证据才取消一次任务变化。
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
        // 已确认是聊天应用的工具框。无标题时再要求置顶、工具样式与全屏选区同时成立，
        // 否则一个普通的置顶聊天或独立视频也可能被误认成截图。
        return purpose == SurfacePurpose.CaptureTool || w.IsTopmost && w.NativeUtility && !w.WorkArea.IsEmpty &&
            w.VisualRect.Intersect(w.WorkArea).Area >= w.WorkArea.Area * .95;
    }

    internal static SurfacePurpose ChatCaption(string caption) => WindowClassification.ChatCaption(caption);

    private static bool IsContextPopup(WindowSnapshot child, WindowSnapshot host, ContextPress press)
    {
        if (child.Handle == host.Handle || press.Root != host.Handle || press.Pid != host.ProcessId ||
            child.ProcessId != press.Pid || child.IndependentFrame || child.IsResizable || child.HasCaption ||
            child.Owner != 0 && child.Owner != host.Handle || child.WorkArea.IsEmpty) return false;
        var rect = child.VisualRect;
        // 菜单可翻到点击点的左上方，故比较点到矩形的距离。整张无边框画布或普通独立编辑窗仍参与整理。
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
        // 弹层也会贴在宿主外侧。已确认的 owner，或相接的两个窗口，加上其余类型/时间证据，都能证明这个空间关系。
        var a = child.VisualRect; var b = host.VisualRect;
        if (child.Owner == host.Handle || a.Intersect(b).Area >= a.Area * .50) return true;
        double gap = 32 * child.Dpi / 96d;
        return a.VerticalOverlapRatio(b) >= .5 && RectI.IntervalGap(a.Left,a.Right,b.Left,b.Right) <= gap ||
            a.HorizontalOverlapRatio(b) >= .5 && RectI.IntervalGap(a.Top,a.Bottom,b.Top,b.Bottom) <= gap;
    }
}
