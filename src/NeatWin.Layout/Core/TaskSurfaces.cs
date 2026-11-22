namespace NeatWin.Core;

// HostApp 和 ChildApp 取匿名应用/窗口类键。局部卡片仍在桌面里遮挡像素，只退出任务变化、工作组和摆放习惯的输入。
public sealed record SurfaceRule(string Id, string HostApp, double MaxAreaRatio, string Action = "local", string? ChildApp = null)
{
    internal bool IsValid() => Id is { Length: > 0 and <= 64 } && Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
        HostApp is { Length: 24 } && HostApp.All(char.IsAsciiHexDigit) &&
        (ChildApp == null || ChildApp.Length == 24 && ChildApp.All(char.IsAsciiHexDigit)) &&
        MaxAreaRatio is > 0 and <= 1 && Action is "local" or "task";
}

internal sealed record SurfaceMatch(WindowSnapshot Child, WindowSnapshot Host, string Rule, double MaxArea,
    string? ReleaseReason = null, RectI? ReleasedChildRect = null, RectI? ReleasedHostRect = null);
internal enum SurfacePurpose { Unknown, LocalDetail, IndependentTask }

internal sealed class TaskSurfaces
{
    private readonly Dictionary<nint, SurfaceMatch> _local = [];
    internal Func<WindowSnapshot, WindowUseRole>? Role { get; init; }
    internal Func<WindowSnapshot, SurfacePurpose>? ChatPurpose { get; init; }
    internal Func<WindowSnapshot, string?>? AppKey { get; init; }
    internal Func<SurfaceRule[]?>? Rules { get; init; }
    internal Func<WindowSnapshot, bool>? JustClicked { get; init; }
    internal Action<SurfaceMatch, string>? Changed { get; init; }
    internal IEnumerable<WindowSnapshot> Hosts => _local.Values.Select(m => m.Host);
    internal bool IsLocal(WindowSnapshot w) => _local.TryGetValue(w.Handle, out var match) && match.Child.ProcessId == w.ProcessId;
    private HashSet<(nint Handle, uint Pid)> _previous = [];
    internal Action? ResetObserved { get; init; }
    internal void Reset() { _local.Clear(); _previous.Clear(); ResetObserved?.Invoke(); }

    internal void Update(IReadOnlyList<WindowSnapshot> desktop, WindowSnapshot? previous)
    {
        if (desktop.Count == 0) { Reset(); return; }
        var byHandle = desktop.ToDictionary(w => w.Handle);
        // 升级成独立窗口或宿主关闭时解除归属。不能因为这个句柄曾显示过卡片就永久屏蔽它。
        foreach (var m in _local.Values.ToArray())
        {
            var w = byHandle.GetValueOrDefault(m.Child.Handle);
            var h = byHandle.GetValueOrDefault(m.Host.Handle);
            bool childAlive = w?.ProcessId == m.Child.ProcessId;
            bool hostAlive = h?.ProcessId == m.Host.ProcessId;
            bool promoted = childAlive && hostAlive && w!.ProcessId == h!.ProcessId && w.IsForeground &&
                Role?.Invoke(h) == WindowUseRole.Chat && ChatPurpose?.Invoke(w) == SurfacePurpose.IndependentTask;
            if (childAlive && hostAlive && !promoted && w!.Owner == m.Child.Owner && Fits(w, h!, m.MaxArea)) continue;
            _local.Remove(m.Child.Handle);
            Changed?.Invoke(m with { ReleaseReason = !childAlive ? "child-left" : !hostAlive ? "host-left" : "detached-or-enlarged",
                ReleasedChildRect = childAlive ? w!.VisualRect : null, ReleasedHostRect = hostAlive ? h!.VisualRect : null }, "release");
        }
        SurfaceRule[]? rules = null;
        bool rulesRead = false;
        foreach (var w in desktop)
        {
            if (_local.ContainsKey(w.Handle) || w.Handle == previous?.Handle || w.VisualRect.IsEmpty) continue;
            var host = byHandle.GetValueOrDefault(w.Owner);
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
            if (!Fits(w, host, limit)) { ReportTask("separate-size-or-location"); continue; }
            var purpose = chat && w.ProcessId == host.ProcessId ? ChatPurpose?.Invoke(w) : null;
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
        _previous = desktop.Select(w => (w.Handle, w.ProcessId)).ToHashSet();
    }

    internal static SurfacePurpose ChatCaption(string caption) => caption.Trim() switch
    {
        "微信音视频通话" or "语音通话" or "视频通话" or "WeChat Call" or "Voice Call" or "Video Call" => SurfacePurpose.IndependentTask,
        "个人资料" or "详细资料" or "联系人资料" or "群成员资料" or "Contact Info" or "Profile Card" => SurfacePurpose.LocalDetail,
        _ => SurfacePurpose.Unknown
    };

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
