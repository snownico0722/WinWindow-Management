namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    // 从手势起点识别已存在的接缝。仅看终点会把刚撞到其他窗口的普通缩放误认成联动。
    internal static IntentLayoutPlan? CoupledDivider(IReadOnlyList<VisibleWindow> visible,
        IReadOnlyList<WindowSnapshot> desktop, TaskLayoutContext ctx, TidyOptions options)
    {
        var release = ctx.RecentGesture;
        if (release == null && ctx.ReplayDivider is { } replay)
            release = new(ctx.GestureAnchor, replay.Start, replay.End, replay.WorkArea, ManualGestureKind.Resize, [], 0, 0);
        if (!ctx.AllowGestureAssist || ctx.ExplicitArrange || ctx.GeometryOnlyWindow != 0 ||
            release is not { Kind: ManualGestureKind.Resize } g || ctx.GestureAnchor != g.WindowHandle ||
            ctx.Focus?.NavigationAncestors is { Length: > 0 } || ctx.Focus?.Navigation is { Length: > 0 }) return null;
        var source = visible.FirstOrDefault(v => v.Window.Handle == g.WindowHandle)?.Window;
        if (source is not { IsForeground: true, IsManageable: true, IsTopmost: false, IsResizable: true, Owner: 0 } ||
            source.VisualRect != g.EndRect || source.WorkArea != g.WorkArea || source.ProcessId != g.ProcessId ||
            ctx.Focus?.Main != source.Handle || g.StartRect.IsEmpty || g.EndRect.IsEmpty ||
            ctx.WindowHints?.Any(h => h.Handle == source.Handle && (h.Anchor || WindowUseRoles.IsFloating(h.Role))) == true) return null;
        var start = g.StartRect;
        var end = g.EndRect;
        var scale = Math.Max(96u, source.Dpi) / 96d;
        double jitter = 3 * scale;
        int dl = end.Left - start.Left, dr = end.Right - start.Right;
        bool fromLeft = Math.Abs(dl) > jitter;
        int delta = fromLeft ? dl : dr;
        if (Math.Abs(delta) < 16 * scale || Math.Abs(fromLeft ? dr : dl) > jitter ||
            Math.Abs(end.Top - start.Top) > jitter || Math.Abs(end.Bottom - start.Bottom) > jitter) return null;

        bool SharesEdge(VisibleWindow v)
        {
            var w = v.Window;
            var b = w.VisualRect;
            int gap = fromLeft ? start.Left - b.Right : b.Left - start.Right;
            // 两列要有一段真正共同的内边缘。允许原有小幅内叠和间隙，两个争用同一边的邻窗不能任猜一个。
            return w.Handle != source.Handle && w.WorkArea == source.WorkArea && w.MonitorHandle == source.MonitorHandle &&
                w.IsManageable && !w.IsTopmost && w.Owner == 0 && v.VisibleRatio >= .35 &&
                Math.Min(start.Bottom, b.Bottom) - Math.Max(start.Top, b.Top) >= .65 * Math.Max(start.Height, b.Height) &&
                b.Width >= g.WorkArea.Width * .19 && start.Width >= g.WorkArea.Width * .19 &&
                gap >= -.10 * Math.Min(b.Width, start.Width) && gap <= 24 * scale;
        }
        var peers = visible.Where(SharesEdge).Take(2).ToArray();
        if (peers.Length == 0) return null;
        if (peers.Length > 1)
            return new([], [], [new([source.Handle], "keep", 0, [new("coupled-divider", null, "ambiguous-shared-edge")])]) { IsDividerGesture = true };
        var neighbor = peers[0];
        var peer = neighbor.Window;
        if (ctx.Focus is { Companion: not 0 } focus && focus.Companion != peer.Handle) return null;
        var old = peer.VisualRect;
        RectI Follow(int travel) => fromLeft ? old with { Width = old.Width + travel } :
            old with { X = old.X + travel, Width = old.Width - travel };
        var target = Follow(delta);
        var members = new[] { source.Handle, peer.Handle };
        // 松手已完成主窗的调整。若搭档无法承接，保留松手结果，此次手势不会升级为第三扇窗的布局请求。
        string? blocked = null;
        var hint = ctx.WindowHints?.FirstOrDefault(h => h.Handle == peer.Handle);
        var pairContext = ctx with { Focus = ctx.Focus! with { Companion = peer.Handle }, GestureTarget = end };
        var group = new[] { visible.First(v => v.Window.Handle == source.Handle), neighbor }.OrderBy(v => v.Window.ZOrder).ToArray();
        var baseline = group.Select(v => v.Window.VisualRect).ToArray();
        var frames = baseline.ToArray();
        int peerIndex = Array.FindIndex(group, v => v.Window.Handle == peer.Handle);
        var others = desktop.Where(w => !members.Contains(w.Handle)).ToArray();
        var proposal = new Candidate("coupled-divider", frames, [0, 1]);
        var goals = AttentionLayout.Targets(group, pairContext, options.SmartStrength);
        bool Fits(RectI frame)
        {
            frames[peerIndex] = frame;
            return !frame.IsEmpty && frame.Intersect(g.WorkArea) == frame && ctx.LimitReturn(peer, frame) == frame &&
                TaskSafe(group, baseline, proposal, g.WorkArea, options, pairContext, others, goals: goals);
        }
        int residual = 0;
        if (!ctx.Preferences.AllowUsefulResize || ctx.Scene?.Settings?.AllowResize == false || !peer.IsResizable ||
            hint?.Anchor == true || hint?.PassiveVisual == true || hint?.MediaCompanion == true ||
            hint != null && WindowUseRoles.IsFloating(hint.Role) || ctx.VideoHint?.WindowHandle == peer.Handle ||
            ctx.VideoFits?.ContainsKey(peer.Handle) == true) blocked = "protected-peer";
        else if (!Fits(target))
        {
            // 用户已经把前景拉到松手位置。邻窗缩到可读边界就停，剩余位移允许形成内叠。
            // 只在内边单轴收缩时搜索，不会为了让接缝对齐而移动外边、改变其他窗口或推翻拒绝记录。
            int lo = 0, hi = target.Width < old.Width ? Math.Min(Math.Abs(delta), old.Width - 1) : 0;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (Fits(Follow(Math.Sign(delta) * mid))) lo = mid;
                else hi = mid - 1;
            }
            target = Follow(Math.Sign(delta) * lo);
            residual = Math.Abs(delta) - lo;
            if (lo == 0 || !Fits(target)) blocked = "geometry-budget";
        }
        frames[peerIndex] = target;
        var planned = new IntentLayoutPlan([new(peer, target)], [], []);
        if (blocked == null && ctx.RejectedLayouts?.Contains(TaskLayoutSession.LayoutKey(TaskLayoutSession.Project(desktop, planned)
            .Where(w => members.Contains(w.Handle)))) == true) blocked = "rejected-layout";
        else if (blocked == null && (others.Any(w => w.ZOrder > peer.ZOrder &&
            target.Intersect(w.VisualRect).Area > old.Intersect(w.VisualRect).Area) ||
            ctx.Scene?.References.Any(r => r.Measure(desktop, w => w.Handle == peer.Handle ? target : w.VisualRect,
                w => w.ZOrder).Lost > r.Limit + 1e-9) == true)) blocked = "third-window-exposure";
        ctx.ShadowProbe?.Observe(group, baseline, 0);
        ctx.ShadowProbe?.Observe(group, frames, blocked == null ? residual : double.PositiveInfinity);
        var keep = new LayoutCandidateTrace("keep", Math.Abs(delta), null);
        // 这里的两种结果用接缝误差像素比较，和通用候选分数无关。真实窗口的位置仍由提交回执核对。
        var axes = ctx.TraceCandidateAxes ? new LayoutAxisEvidence() : null;
        axes?.Observe([end, old], true);
        if (!target.IsEmpty) axes?.Observe([end, target], blocked == null);
        var trace = new LayoutGroupTrace(members, blocked == null ? proposal.Kind : "keep", 0,
            [keep, new(proposal.Kind, blocked == null ? residual : null, blocked)], new([], "divider-error-pixels", ["release-preserves-outer-edges"]), axes?.Export());
        return new(blocked == null ? planned.Moves : [], [], [trace]) { IsDividerGesture = true };
    }
}
