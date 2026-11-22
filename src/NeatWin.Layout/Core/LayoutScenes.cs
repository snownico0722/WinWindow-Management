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

internal sealed record ActiveLayoutScene(LayoutScene? Settings, nint Monitor, ReferenceExposure[] References, int WorkingSide = 0)
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
        return match == null && guards.Count == 0 ? null : new(match, front.MonitorHandle, guards.ToArray(), workingSide);
    }

    internal bool Allows(IReadOnlyList<WindowSnapshot> current, IntentLayoutPlan plan)
    {
        if (References.Length == 0 || plan.Moves.Count == 0 && plan.Layers.Count == 0) return true;
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
