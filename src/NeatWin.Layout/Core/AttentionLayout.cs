namespace NeatWin.Core;

internal enum AttentionRole { Main, Reference, Return, Waiting, Background }

internal sealed record AttentionTarget(AttentionRole Role, int Width, int Height, int AccessWidth, bool Movable, bool Resizable, bool LearnedHeight = false, bool LearnedWidth = false, int FrameWidth = 0, double? RestingCenter = null, double PlacementStability = 0, int MinimumFrameWidth = 0, bool DefaultCenter = false);

// Frozen at the beginning of a solve. Dimensions are pixels in the window's current work area.
internal static class AttentionLayout
{
    internal static RectI WorkingArea(WindowSnapshot window, AttentionTarget goal)
    {
        var rect = window.VisualRect;
        var area = window.WorkArea;
        int width = Math.Min(area.Width, Math.Max(rect.Width, goal.Width));
        int height = Math.Min(area.Height, Math.Max(rect.Height, goal.Height));
        return new(Math.Clamp(rect.Left + (rect.Width - width) / 2, area.Left, area.Right - width),
            Math.Clamp(rect.Top + (rect.Height - height) / 2, area.Top, area.Bottom - height), width, height);
    }

    internal static void SetRestingCenter(VisibleWindow[] group, AttentionTarget[]? targets, WindowSnapshot[] obstacles,
        TaskLayoutContext context)
    {
        if (targets is not [{ Role: AttentionRole.Main, Movable: true } target] || context.GestureAnchor != 0) return;
        var window = group[0].Window;
        if (context.Focus?.OpeningResidentLeft != null || context.Focus?.Navigation is { Length: > 0 } ||
            context.VideoHint?.WindowHandle == window.Handle || context.RecentGesture?.WindowHandle == window.Handle ||
            target.RestingCenter.HasValue) return;
        // 同伴退出、普通切换或只剩一扇可管理窗口，都不能说明用户要换观看位置。
        // 明确整理与真实首次落位可以选中央作为落点。人工学到的位置已在 Targets 中独立绑定。
        if (!context.ExplicitArrange && context.FirstPlacementWindow != window.Handle &&
            context.WindowHints?.Any(h => h.Handle == window.Handle && h.InitialTextPlacement) != true) return;
        var area = window.WorkArea;
        var rect = WorkingArea(window, target);
        var centered = rect with { X = area.Left + (area.Width - rect.Width) / 2 };
        // 将用户刚推开的摆法当作本次任务的反证，邻窗改变后重新评估。一个横向回拖不会否定纵向尺寸收益。
        if (!context.ExplicitArrange && context.Attention?.GetValueOrDefault(window.Handle)?.Correction?.X
            ?.Cost(window.VisualRect.X, centered.X) >= .5) return;
        // 最大化或全屏内容同样可能供人瞄一眼。前景从侧面移到中央会切断连续视野，即使总露出面积完全相同。
        // 这里只评估默认居中的收益。真正的开窗碰撞、手势与有效画面尺寸仍走全局比较。
        var desktop = obstacles.Append(window).OrderBy(w => w.ZOrder).ToArray();
        foreach (var other in obstacles.Where(w => w.MonitorHandle == window.MonitorHandle && w.ZOrder > window.ZOrder &&
            (w.IsManageable || w.SurfaceState is "maximized" or "fullscreen")))
        {
            if (other.SurfaceState is "maximized" or "fullscreen")
            {
                var covers = desktop.Where(w => w.Handle != window.Handle && w.ZOrder < other.ZOrder).Select(w => w.VisualRect).ToArray();
                var before = IntentLayoutPlanner.ScreenExposure(other.VisualRect, covers.Append(window.VisualRect), area, other.Dpi);
                var after = IntentLayoutPlanner.ScreenExposure(other.VisualRect, covers.Append(centered), area, other.Dpi);
                // 连续横向可读区允许跨过细小遮挡；以原有视野为基准，显著切碎或缩减它才放弃默认居中。
                if (after.UsefulWidth < before.UsefulWidth * .88 || after.Visible < before.Visible * .88) return;
                continue;
            }
            var overlap = centered.Intersect(other.VisualRect);
            if (overlap.IsEmpty || overlap.Area <= other.VisualRect.Area * .12) continue;
            var front = desktop.Where(w => w.ZOrder < other.ZOrder).Select(w => w.VisualRect);
            if (ScreenArea(overlap, front, area, other.Dpi) > other.VisualRect.Area * .12) return;
        }
        double currentCenter = window.VisualRect.Left + window.VisualRect.Width / 2d;
        targets[0] = target with { RestingCenter = currentCenter * target.PlacementStability +
            (area.Left + area.Width / 2d) * (1 - target.PlacementStability), DefaultCenter = true };
    }

    private static double ScreenArea(RectI rect, IEnumerable<RectI> covers, RectI area, uint dpi) =>
        IntentLayoutPlanner.ScreenExposure(rect, covers, area, dpi).Visible * rect.Area;

    internal static AttentionTarget[]? Targets(VisibleWindow[] group, TaskLayoutContext context, SmartTidyStrength strength)
    {
        if (context.Attention is null || context.Focus is not { Main: not 0 } focus ||
            !group.Any(v => v.Window.Handle == focus.Main) ||
            group.Any(v => v.Window.VisualRect.IsEmpty || v.Window.WorkArea.IsEmpty)) return null;
        var profile = context.Preferences;
        double comfort = strength switch { SmartTidyStrength.Gentle => .65, SmartTidyStrength.Balanced => .84, _ => 1 };
        double transition = TransientInteractionStability(group, context, focus);
        var targets = group.Select(v =>
        {
            var w = v.Window;
            var a = w.VisualRect;
            bool waiting = !context.ExplicitArrange && focus.IsNavigationAncestor(w.Handle) && w.Handle != focus.Companion;
            var role = w.Handle == focus.Main ? AttentionRole.Main :
                // 同侧接力时，上一步需要一个可点回的入口；只有确认并看，才给它分配持续阅读预算。
                w.Handle == focus.Companion ? (focus.Evidence == "handoff" || context.Scene?.Lane?.Source == w.Handle
                    ? AttentionRole.Return : AttentionRole.Reference) :
                waiting ? AttentionRole.Waiting : context.HasRecentInput(w.Handle) ? AttentionRole.Reference : AttentionRole.Background;
            if (role == AttentionRole.Reference && context.PairHints?.Any(h => h.Relation == TaskRelation.Alternating &&
                (h.First == w.Handle && h.Second == focus.Main || h.Second == w.Handle && h.First == focus.Main)) == true)
                role = AttentionRole.Return;
            var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle);
            double floating = Math.Max(Floating(hint), role == AttentionRole.Main ? transition : 0);
            bool movable = !waiting && context.GestureAnchor != w.Handle && hint?.Anchor != true && !context.HoldsTextView(w);
            bool resize = movable && w.IsResizable && profile.AllowUsefulResize && context.VideoHint?.WindowHandle != w.Handle &&
                !(context.RecentGesture is { } g && !context.ExplicitArrange && g.WindowHandle == w.Handle &&
                  (g.StartRect.Width != g.EndRect.Width || g.StartRect.Height != g.EndRect.Height));
            double scale = w.Dpi > 0 ? w.Dpi / 96d : 1;
            var preference = context.Placement?.FirstOrDefault(p => p.Window == w.Handle && p.Confidence >= .40);
            double width = hint?.UsefulWidthDip is > 0 and <= 4000 ? hint.UsefulWidthDip.Value * scale :
                preference is { PreferWidth: true, Width: > 0 and <= 1 } ? preference.Width * w.WorkArea.Width : profile.ComfortableWidthDip * scale * comfort;
            bool learnedHeight = preference?.Height is > 0 and <= 1 && hint?.UsefulHeightDip is not (> 0 and <= 3000);
            double height = hint?.UsefulHeightDip is > 0 and <= 3000 ? hint.UsefulHeightDip.Value * scale :
                learnedHeight ? preference!.Height!.Value * w.WorkArea.Height : profile.ComfortableHeightDip * scale * comfort;
            if (preference?.PreferWidth != true && hint?.UsefulWidthDip is not (> 0 and <= 4000))
                width = width * (1 - floating) + a.Width * floating;
            if (!learnedHeight && hint?.UsefulHeightDip is not (> 0 and <= 3000))
                height = height * (1 - floating) + a.Height * floating;
            var floor = context.MinimumSize(w.Handle);
            bool content = resize && role is AttentionRole.Main or AttentionRole.Reference;
            int Snap(double requested, int current, int minimum, int available)
            {
                int value = Math.Clamp((int)Math.Round(requested), Math.Clamp(minimum, 1, available), available);
                return Math.Abs(current - value) <= Math.Max(8 * scale, value * .02) ? Math.Min(current, available) : value;
            }
            int accessWidth = Math.Min(a.Width, Math.Max(1, (int)Math.Round(140 * scale)));
            if (waiting)
            {
                var covers = group.Where(v => v.Window.ZOrder < w.ZOrder).Select(v => v.Window.VisualRect);
                accessWidth = Math.Min(accessWidth, IntentLayoutPlanner.ScreenExposure(a, covers, w.WorkArea, w.Dpi).AccessWidth);
            }
            return new AttentionTarget(role,
                content ? Snap(width, a.Width, floor.Width, w.WorkArea.Width) : a.Width,
                content ? Snap(height, a.Height, floor.Height, w.WorkArea.Height) : a.Height,
                accessWidth, movable, resize, content && learnedHeight,
                content && preference is { PreferWidth: true, Width: > 0 and <= 1 } && hint?.UsefulWidthDip is not (> 0 and <= 4000),
                RestingCenter: preference is { PreferCenter: true } ? w.WorkArea.Left + preference.Center * w.WorkArea.Width : null,
                PlacementStability: floating);
        }).ToArray();
        // A temporary overlay can leave its source partly visible on purpose. Preserve the
        // source's actual reading surface, with a minimum useful portion even when buried.
        double overlay = targets.First(t => t.Role == AttentionRole.Main).PlacementStability;
        if (overlay > 0)
        for (int i = 0; i < targets.Length; i++)
        {
            if (targets[i].Role != AttentionRole.Reference) continue;
            var source = group[i].Window;
            var rect = source.VisualRect;
            var covers = group.Where(v => v.Window.ZOrder < source.ZOrder).Select(v => v.Window.VisualRect);
            var exposed = IntentLayoutPlanner.ScreenExposure(rect, covers, source.WorkArea, source.Dpi);
            double floor = Math.Min(targets[i].Width, rect.Width) * .45;
            double width = Math.Max(floor, exposed.UsefulWidth);
            targets[i] = targets[i] with { Width = (int)Math.Round(targets[i].Width * (1 - overlay) + width * overlay),
                Height = (int)Math.Round(targets[i].Height * (1 - overlay) + rect.Height * overlay) };
        }
        AllocateReadingWidth(group, targets, context, strength);

        return targets;
    }
    // A click in an already usable foreground surface is a poor moment for a long move.
    // Measure the current visit, exposed source and title instead of assuming an app role.
    // A blocked source, sustained work, or a deliberate arrangement removes this preference.
    private static double TransientInteractionStability(VisibleWindow[] group, TaskLayoutContext context, LayoutFocus focus)
    {
        if (context.ExplicitArrange || context.GestureAnchor != 0 ||
            focus.Evidence is not ("handoff" or "recent-switch-pair") ||
            context.PairHints?.Any(h => h.Relation != TaskRelation.Automatic &&
                (h.First == focus.Main && h.Second == focus.Companion || h.Second == focus.Main && h.First == focus.Companion)) == true ||
            context.RulePairs?.Any(p => p.Matches(focus.Main, focus.Companion)) == true ||
            context.Attention?.GetValueOrDefault(focus.Main) is not { Foreground: true, Input: > 0, Dwell: < 1 } active ||
            context.Attention.GetValueOrDefault(focus.Companion) is not { Recency: > 0 } recent) return 0;
        var primary = group.FirstOrDefault(v => v.Window.Handle == focus.Main);
        var source = group.FirstOrDefault(v => v.Window.Handle == focus.Companion);
        if (primary == null || source == null || source.Window.ZOrder <= primary.Window.ZOrder) return 0;
        var a = primary.Window.VisualRect; var b = source.Window.VisualRect;
        double overlap = a.Intersect(b).Area / (double)Math.Max(1, Math.Min(a.Area, b.Area));
        if (overlap == 0) return 0;
        var covers = group.Where(v => v.Window.ZOrder < source.Window.ZOrder).Select(v => v.Window.VisualRect);
        var sourceVisibility = IntentLayoutPlanner.ScreenExposure(b, covers, source.Window.WorkArea, source.Window.Dpi);
        double scale = primary.Window.Dpi / 96d;
        double mainReady = Math.Min(primary.VisibleRatio, Math.Min(a.Width / (context.Preferences.ComfortableWidthDip * scale * .72),
            a.Height / (context.Preferences.ComfortableHeightDip * scale * .72)));
        double sourceReady = Math.Min(sourceVisibility.AccessWidth / Math.Max(1, 140 * scale),
            sourceVisibility.UsefulWidth / Math.Max(1, .45 * Math.Min(b.Width, context.Preferences.ComfortableWidthDip * scale)));
        // One short round trip is compatible with a quick lookup. A sequence of sustained
        // visits supplies progressively stronger evidence of a shared reading task.
        double shared = recent.Dwell * (1 - 1d / Math.Max(1, focus.Switches));
        return Math.Clamp(mainReady, 0, 1) * Math.Clamp(sourceReady, 0, 1) * Math.Min(1, overlap / .45) *
            active.Input * (1 - active.Dwell) * recent.Recency * (1 - shared);
    }

    internal static double Floating(TaskWindowHint? hint) => WindowUseRoles.IsFloating(hint?.Role) ? 1 :
        Math.Clamp(hint?.FloatingAffinity ?? 0, 0, 1);

    internal static int HorizontalSide(RectI frame, RectI area)
    {
        var clipped = frame.Intersect(area);
        if (clipped.IsEmpty) return 0;
        double offset = clipped.Left + clipped.Width / 2d - (area.Left + area.Width / 2d);
        // 中央附近的小偏差不能认作选了左栏或右栏，也不能借屏外像素制造侧别。
        return Math.Abs(offset) > area.Width * .10 ? Math.Sign(offset) : 0;
    }

    internal static bool IsCentralStack(RectI first, RectI second, RectI area)
    {
        if (first.IsEmpty || second.IsEmpty || area.IsEmpty) return false;
        double middle = area.Left + area.Width / 2d;
        // 用较大窗口的面积衡量重叠，能区分两份叠放的正文和正文上方的小工具窗。
        return Math.Abs(first.Left + first.Width / 2d - middle) < area.Width * .10 &&
            Math.Abs(second.Left + second.Width / 2d - middle) < area.Width * .10 &&
            first.Intersect(second).Area >= .72 * Math.Max(first.Area, second.Area);
    }

    // Once a working set fills the screen with readable surfaces, small changes in
    // attention alone need not move the boundary between them. Re-evaluate actual
    // visibility on every observation so a covered title, a new task or lost space
    // immediately opens the normal search again.
    internal static string? SettledWorkspaceReason(VisibleWindow[] group, AttentionTarget[]? targets,
        OcclusionMetrics.Exposure[] exposed, WindowSnapshot[] obstacles, TaskLayoutContext context)
    {
        if (targets == null || context.ExplicitArrange || !context.IsWorkspaceObservation ||
            context.Focus?.Navigation is { Length: > 0 } ||
            context.PairHints?.Any(h => h.Relation != TaskRelation.Automatic) == true ||
            context.RulePairs?.Any(p => !p.OnlyWhenPaired) == true) return null;
        var area = group[0].Window.WorkArea;
        if (context.Scene?.Lane is { Restore: { } remembered } restoring &&
            HorizontalSide(restoring.Front.VisualRect, area) != HorizontalSide(remembered, area)) return null;
        // 同侧打开的正文可以沿用原位叠放。开窗碰撞、最近来回切换，都不能单独证明另一栏空闲。
        int side = context.Scene?.WorkingSide is int occupiedSide && occupiedSide != 0 ? occupiedSide : context.Scene?.Lane?.Side ?? 0;
        if (group.Length == 2 && side != 0 && group.All(v => HorizontalSide(v.Window.VisualRect, area) == side))
        {
            int main = Array.FindIndex(group, v => v.Window.IsForeground && v.Window.Handle == context.Focus?.Main);
            if (main >= 0)
            {
                var frame = group[main].Window.VisualRect;
                var peer = group[1 - main].Window.VisualRect;
                var seen = exposed[main];
                if ((!targets[main].LearnedWidth || targets[main].Width == frame.Width) &&
                    frame.Intersect(area) == frame && frame.Intersect(peer).Area >= Math.Min(frame.Area, peer.Area) * .50 &&
                    seen.CenterVisible >= .90 && seen.AccessWidth >= targets[main].AccessWidth &&
                    seen.UsefulWidth >= targets[main].Width * .80 && seen.UsefulHeight(frame) >= targets[main].Height * .80)
                    return context.Scene?.WorkingSide != 0 ? "existing-occupied-side-stack" : "inherited-side-stack";
            }
        }
        // 工作区观察有时仍带着上一手的锚点。上面的同侧叠放依旧有效，真正的松手辅助由独立手势请求处理。
        if (context.GestureAnchor != 0 || context.Focus?.OpeningResidentLeft != null) return null;
        if (HasReadableColumns(group, targets, exposed, obstacles, context)) return "existing-readable-columns";
        // 黑边属于特定视频窗口。已有可读分栏时由后续视频步骤校正比例，其他窗口无需仅因这次测量重新摆位。
        if (group.Length < 2 || context.VideoHint != null) return null;
        // 在两个居中窗之间切回一次，可能只是翻回刚才看的内容。两边都有输入，或多次切换且各自有停留，才有更强的并排使用证据。
        if (group.Length == 2 && context.Focus is { Evidence: "recent-switch-pair" } focus &&
            !(focus.Companion != 0 && (group.All(v => context.Attention?.GetValueOrDefault(v.Window.Handle)?.Input >= .35) ||
                focus.Switches >= 3 && group.All(v => context.Attention?.GetValueOrDefault(v.Window.Handle)?.Dwell >= .6))) &&
            context.Placement?.Any(p => p.Confidence >= .40 && group.Any(v => v.Window.Handle == p.Window)) != true &&
            context.WindowHints?.Any(h => h.InitialTextPlacement) != true)
        {
            int active = Array.FindIndex(group, v => v.Window.Handle == focus.Main && v.Window.IsForeground);
            if (active >= 0 && exposed[active].CenterVisible >= .94 && exposed[active].AccessWidth >= targets[active].AccessWidth &&
                exposed[active].UsefulWidth >= targets[active].Width * .80 && exposed[active].UsefulHeight(group[active].Window.VisualRect) >= targets[active].Height * .80 &&
                group.All(v => area.Intersect(v.Window.VisualRect) == v.Window.VisualRect) &&
                IsCentralStack(group[0].Window.VisualRect, group[1].Window.VisualRect, area))
                return "existing-central-stack";
        }
        double occupied = 0;
        for (int i = 0; i < group.Length; i++)
        {
            var window = group[i].Window;
            var frame = window.VisualRect;
            var seen = exposed[i];
            var target = targets[i];
            if (target.Role is not (AttentionRole.Main or AttentionRole.Reference) ||
                frame.Intersect(area) != frame || seen.Visible < .90 || seen.CenterVisible < .94 ||
                seen.AccessWidth < target.AccessWidth || seen.UsefulWidth < target.Width * .84 ||
                seen.UsefulHeight(frame) < target.Height * .84 ||
                target.MinimumFrameWidth > frame.Width ||
                context.Placement?.Any(p => p.Window == window.Handle && p.Confidence >= .40) == true) return null;
            occupied += seen.Visible * frame.Area;
        }
        // 两扇小窗也可能完全可见。再检查实际使用了多少工作区，给空白过多的桌面留下扩容机会。
        return occupied >= area.Area * .65 ? "workspace-already-readable" : null;
    }

    private static bool HasReadableColumns(VisibleWindow[] group, AttentionTarget[] targets,
        OcclusionMetrics.Exposure[] exposed, WindowSnapshot[] obstacles, TaskLayoutContext context)
    {
        if (group.Length > 2 || context.Focus is not { } focus ||
            context.WindowHints?.Any(h => h.InitialTextPlacement) == true) return false;
        int main = Array.FindIndex(group, v => v.Window.Handle == focus.Main && v.Window.IsForeground);
        if (main < 0) return false;
        var active = group[main].Window;
        var area = active.WorkArea;
        if (!Readable(active, exposed[main])) return false;

        if (group.Length == 2)
        {
            int other = 1 - main;
            var peer = group[other].Window;
            return targets[other].Role is AttentionRole.Reference or AttentionRole.Return &&
                ColumnsFit(peer) && BothReadable(peer, exposed[other]);
        }
        // 副窗的输入分数会随时间衰减。即使这一轮只给主窗排位，移向中央仍可能盖住用户正在看的另一栏。
        if (focus.Companion != 0) return false;
        foreach (var peer in obstacles)
        {
            if (peer.MonitorHandle != active.MonitorHandle || !peer.IsManageable || peer.IsTopmost ||
                peer.Owner != 0 || peer.ZOrder <= active.ZOrder || !ColumnsFit(peer)) continue;
            var covers = obstacles.Where(w => w.ZOrder < peer.ZOrder).Select(w => w.VisualRect).Append(active.VisualRect);
            if (BothReadable(peer, IntentLayoutPlanner.ScreenExposure(peer.VisualRect, covers, area, peer.Dpi))) return true;
        }
        return false;

        bool ColumnsFit(WindowSnapshot peer)
        {
            var a = active.VisualRect;
            var b = peer.VisualRect;
            // 允许左右边缘少量叠放。中心分居工作区两侧且纵向相交，才是用户可以来回看的两栏。
            double middle = area.Left + area.Width / 2d;
            return (a.X + a.Width / 2d - middle) * (b.X + b.Width / 2d - middle) < 0 &&
                a.VerticalOverlapRatio(b) >= .5 && a.HorizontalOverlapRatio(b) <= OcclusionMetrics.HorizontalInsetFraction;
        }

        bool BothReadable(WindowSnapshot peer, OcclusionMetrics.Exposure seen)
        {
            if (exposed[main].Visible * active.VisualRect.Area + seen.Visible * peer.VisualRect.Area < area.Area * .65 ||
                !Readable(peer, seen)) return false;
            foreach (var p in context.Placement ?? [])
            {
                if (p.Confidence < .40 || p.Window != active.Handle && p.Window != peer.Handle) continue;
                var other = p.Window == active.Handle ? peer : active;
                if (p.Partner != 0 && p.Partner != other.Handle) continue;
                // 习惯来自一段坐标范围。桌面已经落在用户教过的范围内时，追逐平均坐标只会增加一次移动。
                var rect = p.Window == active.Handle ? active.VisualRect : peer.VisualRect;
                if (p.Partner != 0 && (p.RevealedCell >= 0 || p.InnerOverlap.HasValue ||
                    Math.Abs(p.Order) >= .5 && Math.Sign(rect.Left + rect.Width / 2d - other.VisualRect.Left - other.VisualRect.Width / 2d) != Math.Sign(p.Order))) return false;
                if (p.PreferCenter && Math.Abs((rect.Left + rect.Width / 2d - area.Left) / area.Width - p.Center) > LayoutHabits.AxisTolerance ||
                    p.PreferWidth && Math.Abs((double)rect.Width / area.Width - p.Width) > LayoutHabits.AxisTolerance ||
                    p.Height is double height && Math.Abs((double)rect.Height / area.Height - height) > LayoutHabits.AxisTolerance) return false;
            }
            return true;
        }

        bool Readable(WindowSnapshot window, OcclusionMetrics.Exposure seen)
        {
            var rect = window.VisualRect;
            double scale = (window.Dpi == 0 ? 96 : window.Dpi) / 96d;
            var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == window.Handle);
            double width = Math.Min(area.Width / 2d, (hint?.UsefulWidthDip ?? context.Preferences.ComfortableWidthDip) * scale);
            double height = Math.Min(area.Height, (hint?.UsefulHeightDip ?? context.Preferences.ComfortableHeightDip) * scale);
            var floor = context.MinimumSize(window.Handle);
            return rect.Intersect(area) == rect && seen.CenterVisible >= .90 && seen.AccessWidth >= 140 * scale &&
                seen.UsefulWidth >= .84 * width && seen.UsefulHeight(rect) >= .80 * height &&
                rect.Width >= floor.Width && rect.Height >= floor.Height;
        }
    }

    internal static bool CanStackReferences(AttentionTarget[] targets, TaskLayoutContext context,
        SmartTidyStrength strength, int height, int gap) =>
        targets.Length == 3 && strength != SmartTidyStrength.Gentle && context.GestureAnchor == 0 &&
        targets.Count(t => t.Role == AttentionRole.Reference) == 2 &&
        targets.All(t => t.Movable && t.PlacementStability < .5) &&
        targets.Where(t => t.Role == AttentionRole.Reference).Sum(t => t.Resizable ? t.Height * .70 : t.Height) + gap <= height;

    // Weighted water filling: first reserve legible content for each reading surface, then
    // spend screen space where there is remaining useful demand. The calculation uses no
    // executable names and never requires two windows to start at a particular position.
    private static void AllocateReadingWidth(VisibleWindow[] group, AttentionTarget[] targets,
        TaskLayoutContext context, SmartTidyStrength strength)
    {
        var readers = Enumerable.Range(0, group.Length).Where(i =>
            targets[i].Role is AttentionRole.Main or AttentionRole.Reference).ToArray();
        if (readers.Length < 2) return;
        var area = group[readers[0]].Window.WorkArea;
        int gap = (int)Math.Round(8 * group[readers[0]].Window.Dpi / 96d);
        double available = Math.Max(1, area.Width - (readers.Length - 1) * gap);
        double horizontalDemand = readers.Sum(i => (double)targets[i].Width);
        if (readers.Length == 2)
        {
            var a = group[readers[0]].Window.VisualRect;
            var b = group[readers[1]].Window.VisualRect;
            if (a.VerticalOverlapRatio(b) == 0 && a.HorizontalOverlapRatio(b) >= .6 &&
                readers.Sum(i => (double)Math.Max(group[i].Window.VisualRect.Height, targets[i].Height)) + gap <= area.Height)
                return; // Already reading in separate rows with room to preserve both widths.
        }
        if (horizontalDemand <= available)
        {
            // The targets may already fit even while an oversized current frame starves
            // its peer. Authorize the target width only when the actual pair needs to share.
            if (readers.Length == 2 && readers.Sum(i => (double)Math.Max(group[i].Window.VisualRect.Width, targets[i].Width)) > available)
                foreach (int i in readers) targets[i] = targets[i] with { FrameWidth = targets[i].Width };
            return;
        }
        // Three readers can share two columns. Score alternative compositions against
        // their full reading demand instead of pre-shrinking all three to narrow strips.
        if (CanStackReferences(targets, context, strength, area.Height, gap))
        {
            int main = readers.First(i => targets[i].Role == AttentionRole.Main);
            int side = readers.Where(i => i != main).Max(i => targets[i].Width);
            if (group[main].Window.VisualRect.Width + side + gap > area.Width)
                targets[main] = targets[main] with { FrameWidth = targets[main].Width };
            return;
        }

        var allocated = new double[group.Length];
        var weights = new double[group.Length];
        foreach (int i in readers)
        {
            var w = group[i].Window;
            var goal = targets[i];
            var explicitWidth = context.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle)?.UsefulWidthDip;
            int floor = context.MinimumSize(w.Handle).Width;
            bool knownWidth = !goal.Resizable || goal.LearnedWidth || explicitWidth is > 0 and <= 4000;
            allocated[i] = knownWidth ? goal.Width : Math.Min(goal.Width, Math.Max(floor, goal.Width * .60));
            var attention = context.Attention?.GetValueOrDefault(w.Handle);
            // 前后台、共同使用和停留描述本轮空间需求。单次滚轮／点击只更新观察，不按输入强度再奖励一份宽度。
            double engaged = goal.Role == AttentionRole.Main ? 1 : (attention?.Recency ?? 0) * (attention?.Dwell ?? 0);
            weights[i] = 1 + engaged * (1 + (attention?.Association ?? 0));
        }
        var frameFloors = (double[])allocated.Clone();
        double reserved = allocated.Sum();
        if (reserved > available)
        {
            // With competing fixed or explicit sizes this is a visibility target. The
            // physical frames still obey the native minimum size and resizability checks.
            // Allocate visible access first. The current task keeps its readable frame;
            // background references may overlap instead of forcing all applications below
            // their native, learned, or content-size floor.
            int main = readers.First(i => targets[i].Role == AttentionRole.Main);
            double returns = readers.Where(i => i != main).Sum(i => Math.Min(allocated[i], targets[i].AccessWidth));
            allocated[main] = Math.Min(allocated[main], Math.Max(targets[main].AccessWidth, available - returns));
            double remainder = Math.Max(0, available - allocated[main]);
            double others = reserved - frameFloors[main];
            foreach (int i in readers.Where(i => i != main)) allocated[i] = remainder * frameFloors[i] / Math.Max(1, others);
        }
        else
        {
            double remaining = available - reserved;
            var unsatisfied = readers.Where(i => targets[i].Width > allocated[i]).ToList();
            while (remaining >= 1 && unsatisfied.Count > 0)
            {
                double weight = unsatisfied.Sum(i => weights[i]);
                double unit = remaining / weight;
                foreach (int i in unsatisfied)
                {
                    double increment = Math.Min(targets[i].Width - allocated[i], unit * weights[i]);
                    allocated[i] += increment;
                    remaining -= increment;
                }
                unsatisfied.RemoveAll(i => targets[i].Width - allocated[i] < 1);
            }
        }

        // Sharing the facing margins is an option when it preserves useful frame width.
        // Cap it by the narrower surface's peripheral band and by the width still missing.
        bool shareEdges = readers.Length == 2 && strength != SmartTidyStrength.Gentle && !context.Preferences.ProtectWindowEdges &&
            readers.All(i => context.WindowHints?.Any(h => h.Handle == group[i].Window.Handle &&
                (h.ProtectPeriphery || h.ProtectedRegions is { Length: > 0 })) != true);
        if (shareEdges)
        {
            var a = group[readers[0]].Window.Handle; var b = group[readers[1]].Window.Handle;
            shareEdges = context.Placement?.Any(p => p.InnerOverlap.HasValue &&
                (p.Window == a && p.Partner == b || p.Window == b && p.Partner == a)) != true &&
                context.RulePairs?.Any(p => p.Matches(a, b) && p.Overlap.HasValue) != true;
        }
        double missing = horizontalDemand - available;
        double shared = shareEdges ? Math.Min(missing, readers.Min(i => allocated[i]) * OcclusionMetrics.HorizontalInsetFraction / 2) : 0;
        foreach (int i in readers)
        {
            var target = targets[i];
            int width = Math.Max(1, (int)Math.Floor(allocated[i]));
            // The front surface is fully exposed. Only the rear surface can gain layout
            // width behind the facing edge without taking away its partner's reading budget.
            bool rear = readers.Any(j => group[j].Window.ZOrder < group[i].Window.ZOrder);
            int frame = Math.Max((int)Math.Ceiling(frameFloors[i]), width + (rear ? (int)Math.Round(shared) : 0));
            targets[i] = target with { Width = width, FrameWidth = Math.Min(target.Width, frame),
                MinimumFrameWidth = (int)Math.Ceiling(frameFloors[i]) };
        }
    }

}

internal static partial class IntentLayoutPlanner
{
    // Retain a small frontier of provisional desktop geometries. A temporarily covered peer may
    // move on the next step. Only complete candidates that pass TaskSafe reach the native layer.
    private static void AddAttentionLayouts(List<Candidate> candidates, VisibleWindow[] group, RectI[] original,
        AttentionTarget[] targets, RectI area, WindowSnapshot[] obstacles, int gap, int[] order, SmartTidyStrength strength,
        TaskLayoutContext context, Func<Candidate, TaskCostBreakdown> score)
    {
        int main = Array.FindIndex(targets, t => t.Role == AttentionRole.Main);
        var budget = strength switch { SmartTidyStrength.Gentle => 2, SmartTidyStrength.Balanced => 4, _ => 6 };
        var steps = order.Where(i => targets[i].Movable || i == main).OrderBy(i => targets[i].Role)
            .ThenBy(i => Math.Abs(CenterX(original[i]) - CenterX(original[main])))
            .Take(budget).ToArray();
        // Compare complete left/right arrangements directly. An intermediate move toward the
        // outside edge can temporarily worsen overlap and otherwise get pruned from the beam.
        int reference = context.Focus is { OpeningResidentLeft: not null } opening
            ? Array.FindIndex(group, v => v.Window.Handle == opening.Companion)
            : Array.FindIndex(targets, t => t.Role == AttentionRole.Reference);
        if (reference >= 0 && targets[main].Movable && targets[reference].Movable &&
            strength != SmartTidyStrength.Gentle)
        {
            var pair = context.Focus?.OpeningResidentLeft switch
            {
                true => new[] { reference, main },
                false => new[] { main, reference },
                null => new[] { main, reference }.OrderBy(i => CenterX(original[i])).ToArray()
            };
            if (context.Focus?.OpeningResidentLeft is bool residentLeft)
            {
                // Compare with a plan that leaves the established surface exactly as found.
                var resident = original[reference];
                var incoming = original[main];
                int free = residentLeft ? area.Right - resident.Right - gap : resident.Left - area.Left - gap;
                foreach (int width in new[] { incoming.Width, Math.Min(incoming.Width, free) }.Distinct())
                {
                    if (width <= 0 || width > area.Width || width != incoming.Width && !targets[main].Resizable) continue;
                    var stay = original.ToArray();
                    stay[main] = incoming with { X = residentLeft ? area.Right - width : area.Left, Width = width };
                    candidates.Add(new("opening-placement", stay, order));
                }
            }
            foreach (bool shareEdge in new[] { false, true })
            {
                var rects = original.ToArray();
                for (int side = 0; side < pair.Length; side++)
                {
                    int i = pair[side];
                    var t = targets[i];
                    int width = t.Resizable ? Math.Max(t.Width, shareEdge ? t.FrameWidth : 0) : original[i].Width;
                    int height = t.Resizable ? (t.LearnedHeight ? t.Height : Math.Max(original[i].Height, t.Height)) : original[i].Height;
                    if (width > area.Width || height > area.Height) continue;
                    rects[i] = new(side == 0 ? area.Left : area.Right - width,
                        Math.Clamp(original[i].Top, area.Top, area.Bottom - height), width, height);
                }
                candidates.Add(new("attention-layout", rects, order));
                if (targets[main].PlacementStability > 0 || targets[reference].PlacementStability > 0)
                {
                    // Also try a partial move. A transient overlay can preserve its working
                    // location while still giving its source additional usable space.
                    var nearby = rects.ToArray();
                    foreach (int i in pair)
                    {
                        double keep = Math.Max(targets[main].PlacementStability, targets[i].PlacementStability);
                        var r = rects[i];
                        double x = CenterX(original[i]) * keep + CenterX(r) * (1 - keep);
                        double y = CenterY(original[i]) * keep + CenterY(r) * (1 - keep);
                        nearby[i] = r with { X = Math.Clamp((int)Math.Round(x - r.Width / 2d), area.Left, Math.Max(area.Left, area.Right - r.Width)),
                            Y = Math.Clamp((int)Math.Round(y - r.Height / 2d), area.Top, Math.Max(area.Top, area.Bottom - r.Height)) };
                    }
                    candidates.Add(new("attention-layout", nearby, order));
                }
            }
        }
        if (AttentionLayout.CanStackReferences(targets, context, strength, area.Height, gap))
        {
            var peers = order.Where(i => i != main).OrderBy(i => CenterY(original[i])).ToArray();
            int top = peers[0], bottom = peers[1];
            int space = area.Height - gap;
            int topHeight = !targets[top].Resizable ? original[top].Height :
                !targets[bottom].Resizable ? space - original[bottom].Height :
                (int)Math.Round(space * targets[top].Height / (double)(targets[top].Height + targets[bottom].Height));
            int bottomHeight = targets[bottom].Resizable ? space - topHeight : original[bottom].Height;
            int peerWidth = peers.Max(i => targets[i].Width);
            int mainWidth = targets[main].Resizable ? Math.Min(targets[main].Width,
                (int)Math.Round((area.Width - gap) * targets[main].Width / (double)(targets[main].Width + peerWidth))) : original[main].Width;
            int sideWidth = area.Width - gap - mainWidth;
            int mainHeight = Math.Max(original[main].Height, targets[main].Height);
            if (topHeight > 0 && bottomHeight > 0 && topHeight + bottomHeight <= space &&
                mainWidth > 0 && mainHeight <= area.Height && sideWidth > 0 &&
                peers.All(i => targets[i].Resizable || original[i].Width <= sideWidth))
            foreach (bool mainLeft in new[] { true, false })
            {
                var rects = original.ToArray();
                rects[main] = new(mainLeft ? area.Left : area.Right - mainWidth,
                    Math.Clamp(original[main].Top, area.Top, area.Bottom - mainHeight), mainWidth, mainHeight);
                foreach (int i in peers)
                {
                    int width = targets[i].Resizable ? Math.Min(sideWidth, targets[i].Width) : original[i].Width;
                    rects[i] = new(mainLeft ? rects[main].Right + gap : rects[main].Left - gap - width,
                        i == top ? area.Top : area.Top + topHeight + gap, width, i == top ? topHeight : bottomHeight);
                }
                candidates.Add(new("attention-side-stack", rects, order));
            }
        }
        var frontier = new List<Candidate> { new("attention-layout", original, order) };
        int anchored = Array.FindIndex(group, v => v.Window.Handle == context.GestureAnchor);
        if (anchored >= 0 && context.GestureTarget is { } endpoint)
        {
            var rects = original.ToArray();
            rects[anchored] = endpoint;
            var placement = new Candidate("attention-layout", rects, order);
            candidates.Add(placement);
            frontier.Add(placement);
        }
        foreach (int i in steps)
        {
            var t = targets[i];
            if (!t.Movable) continue;
            var next = new Dictionary<string, Candidate>();
            foreach (var seed in frontier)
            {
                next.TryAdd(CandidateKey(seed), seed);
                foreach (var r in Positions(seed, i).Distinct())
                {
                    if (r == seed.Rects[i]) continue;
                    var rects = seed.Rects.ToArray();
                    rects[i] = r;
                    var candidate = new Candidate("attention-layout", rects, order);
                    if (next.TryAdd(CandidateKey(candidate), candidate)) candidates.Add(candidate);
                }
            }
            if (i != steps[^1])
                frontier = next.Values.Select(c => (Candidate: c, Score: score(c)))
                    .OrderBy(x => x.Score.Total).Take(4).Select(x => x.Candidate).ToList();
        }

        IEnumerable<RectI> Positions(Candidate seed, int i)
        {
            var a = original[i];
            var target = targets[i];
            // Consider original dimensions too. A side step alone can free enough room.
            var sizes = new List<RectI> { a };
            if (target.Resizable && target.Role is AttentionRole.Main or AttentionRole.Reference)
                sizes.Add(a with { Width = target.LearnedWidth ? target.Width : Math.Max(a.Width, target.Width),
                    Height = target.LearnedHeight ? target.Height : Math.Max(a.Height, target.Height) });
            if (i == main && target.Resizable)
            {
                // Intermediate widths reserve enough room for a useful reference. Searching only
                // original and full comfort widths would miss layouts where both windows grow.
                foreach (int j in steps.Where(j => targets[j].Role == AttentionRole.Reference))
                {
                    int usable = area.Width - gap;
                    int both = target.Width + targets[j].Width;
                    int fair = (int)Math.Round(usable * target.Width / (double)both);
                    foreach (int w in new[] { usable - targets[j].Width, fair, usable - original[j].Width })
                        if (w > a.Width && w < target.Width)
                            sizes.Add(a with { Width = w, Height = Math.Max(a.Height, target.Height) });
                }
            }
            var mainRect = seed.Rects[main];
            if (target.Role == AttentionRole.Background)
            {
                // Yield to the surface actually obstructed, including a rear reference window.
                mainRect = order.Where(j => j != i && targets[j].Role != AttentionRole.Background &&
                    group[j].Window.ZOrder > group[i].Window.ZOrder)
                    .Select(j => seed.Rects[j]).OrderByDescending(r => r.Intersect(a).Area).FirstOrDefault(mainRect);
            }
            if (i != main && target.Resizable && strength != SmartTidyStrength.Gentle)
            {
                int available = Math.Max(mainRect.Left - area.Left, area.Right - mainRect.Right) - gap;
                if (available > 0 && available < Math.Max(a.Width, target.Width))
                    sizes.Add(a with { Width = available, Height = Math.Max(a.Height, target.Height) });
            }
            foreach (var size in sizes.Distinct())
            {
                if (size.Width > area.Width || size.Height > area.Height) continue;
                RectI Place(int x, int y) => size with { X = Math.Clamp(x, area.Left, area.Right - size.Width),
                    Y = Math.Clamp(y, area.Top, area.Bottom - size.Height) };
                if (target.RestingCenter is double center)
                    yield return Place((int)Math.Round(center - size.Width / 2d), a.Top);
                yield return Place(a.Left, a.Top);
                yield return Place(a.Right - size.Width, a.Top);
                yield return Place(a.Left + (a.Width - size.Width) / 2, a.Top + (a.Height - size.Height) / 2);
                if (i == main)
                {
                    var covers = obstacles.Where(w => w.ZOrder < group[i].Window.ZOrder)
                        .Select(w => w.VisualRect).ToArray();
                    if (covers.Length > 0)
                        foreach (var slot in RectRegion.FreeRectangles(area, covers, size.Width, size.Height).Take(4))
                            yield return Place(Math.Clamp(a.Left, slot.Left, slot.Right - size.Width),
                                Math.Clamp(a.Top, slot.Top, slot.Bottom - size.Height));
                    if (reference >= 0 && target.PlacementStability > 0)
                    {
                        // Stop as soon as the source has a useful region instead of pushing
                        // the active input surface all the way to a monitor edge.
                        var source = seed.Rects[reference];
                        var goal = targets[reference];
                        foreach (int width in new[] { (int)Math.Ceiling(.60 * goal.Width), goal.Width })
                        {
                            yield return Place(source.Right - width - size.Width, a.Top);
                            yield return Place(source.Left + width, a.Top);
                        }
                    }
                    int returning = Array.FindIndex(targets, x => x.Role == AttentionRole.Return);
                    if (returning >= 0)
                    {
                        var source = seed.Rects[returning];
                        yield return Place(source.Left + target.AccessWidth, source.Top);
                        yield return Place(source.Right - size.Width - target.AccessWidth, source.Top);
                    }
                    // Reserve physical space for a return/reference window on either side.
                    if (targets.Length > 1)
                    {
                        yield return Place(area.Left, a.Top);
                        yield return Place(area.Right - size.Width, a.Top);
                    }
                    continue;
                }
                yield return Place(mainRect.Left - size.Width - gap, a.Top);
                yield return Place(mainRect.Right + gap, a.Top);
                yield return Place(a.Left, mainRect.Top - size.Height - gap);
                yield return Place(a.Left, mainRect.Bottom + gap);
                if (target.Role == AttentionRole.Return)
                {
                    yield return Place(mainRect.Left - target.AccessWidth, a.Top);
                    yield return Place(mainRect.Right - size.Width + target.AccessWidth, a.Top);
                    yield return Place(a.Left, mainRect.Top - (int)Math.Ceiling(32 * group[i].Window.Dpi / 96d));
                }
            }
        }
    }
}
