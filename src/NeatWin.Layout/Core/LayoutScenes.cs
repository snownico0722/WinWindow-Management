namespace NeatWin.Core;

// 相同程序在独立编辑、双窗协作和游戏旁监看时可以采用不同的底层取舍。规则顺序即优先级，每次只覆盖一个场景。
public sealed record LayoutScene(string Id, string Trigger, string[]? ForegroundApps = null,
    string? ReferenceApp = null, int? MinWindows = null, int? MaxWindows = null, double MinVisible = .10,
    double? MaxVisibleLoss = null, SceneWeights? Weights = null, bool? AllowResize = null, bool ReconsiderSettled = false)
{
    internal bool IsValid()
    {
        static bool Key(string? key) => key is { Length: 24 } && key.All(char.IsAsciiHexDigit);
        return Id is { Length: > 0 and <= 64 } && Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
            Trigger is "workspace" or "gesture" or "explicit" &&
            (ForegroundApps == null || ForegroundApps is { Length: > 0 and <= 64 } && ForegroundApps.All(Key)) &&
            (ReferenceApp == null || Key(ReferenceApp)) && (MinWindows is null or >= 1 and <= 128) &&
            (MaxWindows is null or >= 1 and <= 128) && !(MinWindows > MaxWindows) &&
            MinVisible is >= 0 and <= 1 && (MaxVisibleLoss == null || ReferenceApp != null && MaxVisibleLoss is >= 0 and <= 1) &&
            (Weights?.Valid ?? true) && (Weights != null || AllowResize != null || ReconsiderSettled || MaxVisibleLoss != null);
    }
}

public sealed record SceneWeights(double Information = 1, double Switching = 1, double Continuity = 1,
    double Reflow = 1, double Personal = 1, double Intervention = 1)
{
    internal bool Valid => new[] { Information, Switching, Continuity, Reflow, Personal }.All(v => v is >= 0 and <= 8)
        && Intervention is >= .125 and <= 8;
    internal TaskCostBreakdown Apply(TaskCostBreakdown cost) => cost with
    {
        InformationLoss = cost.InformationLoss * Information, Switching = cost.Switching * Switching,
        Continuity = cost.Continuity * Continuity, Reflow = cost.Reflow * Reflow,
        PersonalPlacement = cost.PersonalPlacement * Personal, PairPlacement = cost.PairPlacement * Personal,
    };
}

public sealed record SceneEffect(string Rule, int? SubjectId, double? BeforeVisible, double? AfterVisible,
    double? LostVisible, double? MaxLoss);

// 几何记录看不到视线。用整理前确实露出的屏幕区域作基准，下一次用户松手则以新的现场重新计算。
// 按完整 Z 序求遮挡并集；重复遮住同一像素的三个窗口只能计一次。面积始终以原窗口归一，缩小窗口不会制造“可见率提升”。
internal sealed class ReferenceExposure
{
    internal WindowSnapshot Window { get; }
    internal string Rule { get; }
    internal double Limit { get; }
    private readonly List<RectI> _exposed;
    internal double Before { get; }

    internal ReferenceExposure(WindowSnapshot w, IReadOnlyList<WindowSnapshot> desktop, string rule, double limit)
    {
        Window = w; Rule = rule; Limit = limit;
        _exposed = Visible(w.VisualRect, w.ZOrder, desktop, v => v.VisualRect, v => v.ZOrder);
        Before = _exposed.Sum(p => p.Area) / (double)w.VisualRect.Area;
    }

    private List<RectI> Visible(RectI rect, int rank, IReadOnlyList<WindowSnapshot> desktop,
        Func<WindowSnapshot, RectI> box, Func<WindowSnapshot, int> order)
    {
        var clip = rect.Intersect(Window.WorkArea);
        List<RectI> pieces = clip.IsEmpty ? [] : [clip];
        foreach (var front in desktop)
        {
            if (pieces.Count == 0) break;
            var blocker = box(front);
            if (front.Handle != Window.Handle && order(front) < rank && !clip.Intersect(blocker).IsEmpty)
                pieces = RectRegion.Subtract(pieces, blocker, int.MaxValue);
        }
        return pieces;
    }

    internal (double After, double Lost) Measure(IReadOnlyList<WindowSnapshot> desktop,
        Func<WindowSnapshot, RectI> box, Func<WindowSnapshot, int> order)
    {
        var subject = desktop.FirstOrDefault(w => w.Handle == Window.Handle);
        if (subject == null) return (0, Before);
        var rect = box(subject);
        var visible = Visible(rect, order(subject), desktop, box, order);
        long retained = 0;
        foreach (var p in visible)
        foreach (var q in _exposed) retained += p.Intersect(q).Area;
        return (visible.Sum(p => p.Area) / (double)Window.VisualRect.Area,
            Math.Max(0, Before - retained / (double)Window.VisualRect.Area));
    }
}

// 焦点换到新 HWND 不等于视线想换栏。这里描述本次进入工作面的证据，只约束横向，纵向和同侧尺寸仍参与求解。
internal sealed record AttentionLane(WindowSnapshot Front, nint Source, int Side)
{
    internal static AttentionLane? Resolve(WindowSnapshot front, WindowSnapshot[] peers, TaskLayoutContext ctx)
    {
        if (!ctx.IsWorkspaceObservation || ctx.ExplicitArrange || !front.IsManageable || front.IsTopmost ||
            ctx.Focus is not { } focus || focus.Main != front.Handle || front.VisualRect.IsEmpty) return null;
        var source = peers.FirstOrDefault(w => w.Handle == focus.Companion && w.WorkArea == front.WorkArea);
        int side = ctx.OriginSide;
        if (side == 0 && source != null)
            side = AttentionLayout.HorizontalSide(source.VisualRect, front.WorkArea);
        int current = AttentionLayout.HorizontalSide(front.VisualRect, front.WorkArea);
        // 新窗本来就在另一侧时，可能是用户点到了那里。保留它的原生落点，不反向拉回来源一侧。
        if (side is not (-1 or 1) || current == -side || current == 0 && source != null &&
            AttentionLayout.IsCentralStack(front.VisualRect, source.VisualRect, front.WorkArea)) return null;
        if (source == null && ctx.OriginSide == 0) return null;
        if (source != null && focus.Navigation is not { Length: > 0 })
        {
            // 宽窗已横跨中线，且两窗中心本来错开，展开可恢复被它盖掉的大块原工作面。
            // 这种开窗和在原栏叠出一个新窗口有不同的空间证据；知识来自两窗几何，对左右镜像同样成立。
            var area = front.WorkArea;
            var a = front.VisualRect.Intersect(area);
            var b = source.VisualRect.Intersect(area);
            double separation = side * ((b.X + b.Width / 2d) - (a.X + a.Width / 2d));
            if (current == 0 && focus.OpeningResidentLeft.HasValue && a.Width >= area.Width * .60 &&
                separation >= area.Width * .16 && a.Intersect(b).Area >= b.Area * .60) return null;
            bool matches(nint a, nint b) => a == front.Handle && b == source.Handle || b == front.Handle && a == source.Handle;
            // 单次切换和某个程序惯常靠左都不授权分栏。两窗明确联合观看、在这对窗口已建立的方向规则，
            // 或反复切换且双方均有直接操作，才让全局求解重新比较横向转移的收益。
            if (ctx.PairHints?.Any(h => h.Relation == TaskRelation.JointView && matches(h.First, h.Second)) == true ||
                focus.Evidence == "recent-switch-pair" && focus.Switches >= 2 &&
                (ctx.RulePairs?.Any(p => p.FirstSide != "either" && matches(p.First, p.Second)) == true ||
                 focus.Switches >= 3 && ctx.Attention?.GetValueOrDefault(front.Handle)?.Input >= .35 &&
                 ctx.Attention?.GetValueOrDefault(source.Handle)?.Input >= .35)) return null;
        }
        return new(front, source?.Handle ?? 0, side);
    }

    internal bool Allows(WindowSnapshot window, RectI target)
    {
        if (window.Handle != Front.Handle && window.Handle != Source || target == window.VisualRect) return true;
        var area = window.WorkArea;
        var frame = window.VisualRect;
        // 屏外救回必须可达。原来已在中线附近的窗可以留在中线；靠侧的窗则应继续留在同侧阅读。
        if (frame.Intersect(area).Area < frame.Area * .65 || frame.Top < area.Top || frame.Top >= area.Bottom) return true;
        int old = AttentionLayout.HorizontalSide(frame, area), next = AttentionLayout.HorizontalSide(target, area);
        if (window.Handle == Source && old != Side) return true;
        return old == Side ? next == Side : next != -Side;
    }
}

internal sealed record ActiveLayoutScene(LayoutScene? Settings, nint Monitor, ReferenceExposure[] References, int WorkingSide = 0,
    AttentionLane? Lane = null)
{
    internal static ActiveLayoutScene? Resolve(IReadOnlyList<WindowSnapshot> desktop, TaskLayoutContext ctx, bool includeDefaults = true)
    {
        var front = desktop.FirstOrDefault(w => w.IsForeground);
        if (front == null || front.WorkArea.IsEmpty) return null;
        var trigger = ctx.ExplicitArrange ? "explicit" : ctx.IsWorkspaceObservation ? "workspace" :
            ctx.AllowGestureAssist && ctx.GestureAnchor != 0 ? "gesture" : null;
        if (trigger == null) return null;
        var display = desktop.Where(w => w.MonitorHandle == front.MonitorHandle &&
            (w.IsManageable || w.SurfaceState != null) && !w.VisualRect.IsEmpty).ToArray();
        var peers = display.Where(w => w.Handle != front.Handle && !w.IsTopmost && w.ZOrder > front.ZOrder).ToArray();
        var guards = new List<ReferenceExposure>();
        LayoutScene? match = null;
        foreach (var rule in ctx.Rules?.Scenes ?? [])
        {
            if (rule.Trigger != trigger || display.Length < rule.MinWindows || display.Length > rule.MaxWindows ||
                rule.ForegroundApps != null && !rule.ForegroundApps.Contains(ctx.AppKeys?.GetValueOrDefault(front.Handle), StringComparer.OrdinalIgnoreCase))
                continue;
            if (rule.ReferenceApp != null)
            {
                var matches = peers.Where(w => rule.ReferenceApp.Equals(ctx.AppKeys?.GetValueOrDefault(w.Handle), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1) continue;
                var reference = new ReferenceExposure(matches[0], desktop, rule.Id, rule.MaxVisibleLoss ?? 1);
                if (reference.Before <= 0 || reference.Before < rule.MinVisible) continue;
                if (rule.MaxVisibleLoss.HasValue) guards.Add(reference);
            }
            match = rule;
            break;
        }

        // 焦点只说明这一轮在用谁。第三扇窗占着另一栏时，即使很久没点，它露出的正文也是实际使用中的桌面空间。
        // 松手后也按此刻的可见区域起算，否则只保住刚拖过的窗，分栏会把它的搭档推到另一边。
        int workingSide = 0;
        if (includeDefaults && trigger is "workspace" or "gesture" && ctx.Attention != null && front.IsManageable)
        {
            int frontSide = AttentionLayout.HorizontalSide(front.VisualRect, front.WorkArea);
            foreach (var w in peers)
            {
                if (w.Handle == ctx.VideoHint?.WindowHandle || w.VisualRect.Intersect(front.WorkArea).Area < front.WorkArea.Area * .18) continue;
                bool opposite = frontSide != 0 && w.WorkArea == front.WorkArea && w.Owner == 0 && w.IsManageable &&
                    w.Handle != ctx.Focus?.Companion && AttentionLayout.HorizontalSide(w.VisualRect, front.WorkArea) == -frontSide;
                var hint = ctx.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle);
                var history = ctx.Attention.GetValueOrDefault(w.Handle);
                bool fixedSurface = !w.IsManageable && w.SurfaceState is "maximized" or "fullscreen";
                bool recentlyUsed = ctx.Focus?.Companion != w.Handle && history is { Dwell: >= .8, Recency: >= .50, Input: >= .25 };
                bool reference = trigger == "workspace" &&
                    (hint?.PassiveVisual == true || hint?.MediaCompanion == true || fixedSurface || recentlyUsed);
                if (!opposite && !reference) continue;
                var occupied = guards.FirstOrDefault(g => g.Window.Handle == w.Handle) ??
                    new ReferenceExposure(w, desktop, opposite && !reference ? "default-occupied-side" : "default-visible-reference", .10);
                // 过滤只露一条边的旧窗。独立可见的面积也得足以构成一块工作面，侧别在线性工作区内镜像计算。
                bool readableSide = opposite && occupied.Before >= .40 &&
                    occupied.Before * w.VisualRect.Area >= front.WorkArea.Area * .10;
                if (readableSide) workingSide = frontSide;
                if ((readableSide || reference && occupied.Before >= .15) && !guards.Contains(occupied))
                    guards.Add(occupied);
            }
        }
        var lane = includeDefaults ? AttentionLane.Resolve(front, peers, ctx) : null;
        return match == null && guards.Count == 0 && lane == null ? null :
            new(match, front.MonitorHandle, guards.ToArray(), workingSide, lane);
    }

    internal bool Allows(IReadOnlyList<WindowSnapshot> current, IntentLayoutPlan plan)
    {
        if (plan.Moves.Count == 0 && plan.Layers.Count == 0) return true;
        if (Lane is { } lane && (current.FirstOrDefault(w => w.IsForeground) is not { } front ||
            !WindowStateRules.Matches(lane.Front, front, lane.Front.VisualRect) ||
            plan.Moves.Any(m => !lane.Allows(m.Window, m.TargetVisualRect)))) return false;
        if (References.Length == 0) return true;
        var projected = TaskLayoutSession.Project(current, plan);
        foreach (var g in References)
        {
            // 求解到提交之间其他窗口也会动。参照窗口变更身份/现场时放弃这次旧计划，下一次从用户的新状态起算。
            var live = current.FirstOrDefault(w => w.Handle == g.Window.Handle);
            if (live == null || !WindowStateRules.Matches(g.Window, live, g.Window.VisualRect) ||
                g.Measure(projected, w => w.VisualRect, w => w.ZOrder).Lost > g.Limit + 1e-9) return false;
        }
        return true;
    }

    internal SceneEffect[] Effects(IReadOnlyList<WindowSnapshot> after, Func<nint, int> id)
    {
        var result = new List<SceneEffect>();
        if (Lane != null) result.Add(new(Lane.Side < 0 ? "attention-origin-left" : "attention-origin-right", id(Lane.Front.Handle), null, null, null, null));
        if (Settings != null && References.All(g => g.Rule != Settings.Id))
            result.Add(new(Settings.Id, null, null, null, null, null));
        foreach (var g in References)
        {
            var (visible, lost) = g.Measure(after, w => w.VisualRect, w => w.ZOrder);
            result.Add(new(g.Rule, id(g.Window.Handle), g.Before, visible, lost, g.Limit));
        }
        return result.ToArray();
    }
}
