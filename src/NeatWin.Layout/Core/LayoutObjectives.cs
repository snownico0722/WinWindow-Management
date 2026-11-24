namespace NeatWin.Core;

// 任务解释只读整理前的现场。下层为不同候选计算同一组需求的满足程度，避免从自己的排布反推用户想并看。
public sealed record WindowLayoutObjective(double ReadableContent, double PeerExposure, double Ambiguity,
    double BackgroundDrift, WindowMotionObjective Motion,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    WorkingSurfaceObjective? WorkingSurface = null)
{
    internal ContentViewport? Content { get; init; }
}

public readonly record struct WindowMotionObjective(double Resistance, double ReflowSensitivity, double RecentMove,
    double Timing, RectI? AssistedTarget, RectI? RestoredFrame)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PriceLargeChanges { get; init; }

    internal double Cost(RectI frame, RectI target, bool directSnap = false)
    {
        if (Resistance == 0 || frame == target || frame.IsEmpty || target.IsEmpty) return 0;
        double dx = Math.Abs(target.Left - (double)frame.Left) / frame.Width;
        double dy = Math.Abs(target.Top - (double)frame.Top) / frame.Height;
        double change = dx + dy + ReflowSensitivity * Math.Abs(Math.Log(target.Width / (double)frame.Width)) +
            .35 * Math.Abs(Math.Log(target.Height / (double)frame.Height));
        if (PriceLargeChanges)
        {
            // 用前后外框的交并比同时衡量平移、放缩。只对超过三分之一的差异逐渐加价，避免微调被反复计费。
            long overlap = frame.Intersect(target).Area;
            double changed = 1 - overlap / ((double)frame.Area + target.Area - overlap);
            double excess = Math.Max(0, changed - 1d / 3);
            change += 2 * excess * excess;
        }
        static double Mid(RectI r) => r.X + .5 * r.Width;
        bool returning = RestoredFrame is { } old && Math.Abs(Mid(target) - Mid(old)) < Math.Abs(Mid(frame) - Mid(old));
        double timing = directSnap || AssistedTarget == target ? .18 : returning ? .30 : Timing;
        return timing * (Resistance * change + .08 * RecentMove * Math.Min(1, 12 * change));
    }
}

internal static partial class IntentLayoutPlanner
{
    private static ContentViewport? MeasuredContent(WindowSnapshot window, TaskLayoutContext context) =>
        context.VideoHint is { } hint && window.Handle == hint.WindowHandle
            ? VideoViewportFit.From(window.VisualRect, hint)?.Geometry : null;

    private static TaskEvidence[] InferTask(VisibleWindow[] windows, RectI[] rects, TaskLayoutContext context)
    {
        var evidence = new List<TaskEvidence>();
        for (var i = 0; i < windows.Length; i++)
        for (var j = i + 1; j < windows.Length; j++)
        {
            var a = rects[i]; var b = rects[j];
            var affinity = TaskAffinity(windows[i].Window with { VisualRect = a }, windows[j].Window with { VisualRect = b }, context);
            var hint = context.PairHints?.FirstOrDefault(h =>
                (h.First == windows[i].Window.Handle && h.Second == windows[j].Window.Handle) ||
                (h.Second == windows[i].Window.Handle && h.First == windows[j].Window.Handle));
            var dx = Math.Abs(CenterX(a) - CenterX(b)) / Math.Max(1, Math.Min(a.Width, b.Width));
            var dy = Math.Abs(CenterY(a) - CenterY(b)) / Math.Max(1, Math.Min(a.Height, b.Height));
            var topDifference = Math.Abs(a.Top - b.Top) / (windows[i].Window.Dpi / 96.0);
            var column = dx < .3 && topDifference >= 20 && a.HorizontalOverlapRatio(b) >= .65;
            var title = b with { Height = Math.Min(b.Height, (int)Math.Round(32 * windows[j].Window.Dpi / 96.0)) };
            var titleExposed = a.Intersect(title).Area < title.Area * .75;
            var deep = CoversHalfOfRear(a, b);
            var offsetDeck = deep && dx < .28 && dy < .28 && titleExposed;
            var parked = offsetDeck ? .85 : deep && dx < .28 ? .48 : .12;
            var joint = offsetDeck ? .20 : deep ? .76 :
                Math.Clamp(.35 + .50 * Math.Min(1, dx) + .12 * a.VerticalOverlapRatio(b), .35, .92);
            if (column && dy > .55) { joint = .80; parked = .15; }
            var source = "geometry-hypotheses";
            var uncertainty = deep ? .55 : .40;
            var focus = context.Focus;
            bool activePair = focus is { Companion: not 0 } &&
                (windows[i].Window.Handle == focus.Main && windows[j].Window.Handle == focus.Companion ||
                 windows[j].Window.Handle == focus.Main && windows[i].Window.Handle == focus.Companion);
            string? purpose = null;
            if (activePair)
            {
                bool coVisible = windows[i].VisibleRatio >= .25 && windows[j].VisibleRatio >= .25;
                bool configured = focus!.Evidence == "configured-pair";
                joint = Math.Max(joint, coVisible || configured ? .88 : .78);
                parked = Math.Min(parked, coVisible || configured ? .18 : .45);
                if (focus.Evidence == "handoff") { joint = .22; parked = .86; }
                purpose = focus.Evidence == "handoff" || focus.NavigationAncestors is { Length: > 0 } ? "alternate" :
                    configured || focus.Evidence == "learned-working-pair" ? "read-together" : null;
                // 往返说明两窗都重要。已有边缘叠放也支持“轮流使用”；不能用切换次数独自证明同时阅读。
                if (purpose == null && focus.Switches >= 2)
                    parked = Math.Max(parked, Math.Min(.85, .25 + a.Intersect(b).Area / (double)Math.Max(1, Math.Min(a.Area, b.Area))));
                source = focus.Evidence;
                uncertainty = configured ? .10 : focus.Switches > 0 ? coVisible ? .24 : .38 : .40;
            }
            var passive = context.WindowHints?.Any(h => h.PassiveVisual &&
                (h.Handle == windows[i].Window.Handle || h.Handle == windows[j].Window.Handle)) == true;
            if (passive && dx > .3) { joint = Math.Max(joint, .88); source += "+passive-visual"; uncertainty = .25; }
            bool sceneReference = context.Scene is { IntentReference: not 0 } scene &&
                (windows[i].Window.IsForeground && windows[j].Window.Handle == scene.IntentReference ||
                 windows[j].Window.IsForeground && windows[i].Window.Handle == scene.IntentReference);
            if (purpose == null && passive) purpose = "keep-in-view";
            // 兼看要求保留此刻露出的内容。它没有要求给后台窗重新分一栏宽度。
            if (sceneReference && context.Scene!.PrefersGlance(context.Scene.IntentReference, context))
            {
                purpose = "keep-in-view";
                joint = Math.Min(joint, parked);
                source = "scene:" + context.Scene.Settings!.Id;
            }
            var integrated = 0.0;
            if (hint is { Relation: not TaskRelation.Automatic })
            {
                joint = hint.Relation is TaskRelation.JointView or TaskRelation.Integrated ? 1 : 0;
                parked = hint.Relation == TaskRelation.Alternating ? 1 : 0;
                integrated = hint.Relation == TaskRelation.Integrated ? 1 : 0;
                if ((hint.Relation is TaskRelation.JointView or TaskRelation.Integrated) && windows.Length == 2) column = false;
                purpose = hint.Relation == TaskRelation.Alternating ? "alternate" : "read-together";
                uncertainty = 0;
                source = sceneReference && focus?.Evidence is "scene-read-together" or "scene-alternate"
                    ? "scene:" + context.Scene!.Settings!.Id : "explicit-task-hint";
            }
            evidence.Add(new(i, j, joint * affinity, parked * affinity, integrated, uncertainty, column, source, affinity, purpose));
        }
        return evidence.ToArray();
    }

    private static WindowLayoutObjective[] CompileObjectives(VisibleWindow[] windows, TaskEvidence[] relations,
        TaskLayoutContext context, AttentionTarget[]? goals)
    {
        var joint = new double[windows.Length];
        var parked = new double[windows.Length];
        var ambiguity = new double[windows.Length];
        foreach (var r in relations)
        {
            Accumulate(r.First, r);
            Accumulate(r.Second, r);
        }
        void Accumulate(int index, TaskEvidence r)
        {
            joint[index] = Math.Max(joint[index], r.Joint);
            parked[index] = Math.Max(parked[index], r.Parked);
            ambiguity[index] = Math.Max(ambiguity[index], r.Uncertainty * Math.Min(r.Joint, r.Parked));
        }
        double overlay = goals?.FirstOrDefault(t => t.Role == AttentionRole.Main)?.PlacementStability ?? 0;
        var profile = context.Preferences;
        var content = windows.Select(v => MeasuredContent(v.Window, context)).ToArray();
        var surfaces = WorkingSurfaceObjective.Compile(windows, relations, goals, context, content);
        return windows.Select((visible, i) =>
        {
            var w = visible.Window;
            bool main = w.Handle == context.Focus?.Main, peer = w.Handle == context.Focus?.Companion;
            double read = windows.Length == 1 ? 0 : Math.Max(joint[i] * profile.JointVisibilityWeight,
                w.IsForeground ? .85 : .18 * parked[i]);
            if (context.Attention != null && !main && !peer)
                read *= .25 + .75 * (context.Attention.GetValueOrDefault(w.Handle)?.Weight ?? 0);
            bool alternate = context.PairHints?.Any(h => h.Relation == TaskRelation.Alternating &&
                (h.First == w.Handle || h.Second == w.Handle)) == true;
            bool incidental = context.PairPlanningEnabled && context.Focus is { Companion: not 0 } && !main && !peer &&
                visible.VisibleRatio < .08 && context.PairHints?.Any(h => h.First == w.Handle || h.Second == w.Handle) != true;
            double peerExposure = peer && context.PairPlanningEnabled && context.Focus?.Evidence != "handoff" &&
                !alternate && context.Scene?.PrefersGlance(w.Handle, context) != true &&
                context.Placement?.Any(p => p.Partner == w.Handle && p.RevealedCell >= 0) != true
                ? windows.Length * 1.5 : 0;
            var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle);
            return new WindowLayoutObjective(incidental ? .25 * read : read, peerExposure,
                incidental ? .5 * ambiguity[i] : ambiguity[i], incidental ? .6 : 0,
                MotionFor(visible, context, hint, Math.Max(overlay, goals?[i].PlacementStability ?? 0)), surfaces?[i])
                { Content = content[i] };
        }).ToArray();
    }

    // 手势、后处理和全局候选共用内容位移的价格；只有整理权限的入口可以发起一次新操作。
    internal static double AttentionInterruption(VisibleWindow visible, RectI target, TaskLayoutContext context,
        TaskWindowHint? hint, double stability = 0, bool directSnap = false) =>
        MotionFor(visible, context, hint, stability).Cost(visible.Window.VisualRect, target,
            directSnap && context.AllowGestureAssist && context.GestureAnchor == visible.Window.Handle);

    private static WindowMotionObjective MotionFor(VisibleWindow visible, TaskLayoutContext context, TaskWindowHint? hint, double stability)
    {
        var w = visible.Window;
        if (context.Attention == null && !context.IsWorkspaceObservation &&
            context.GestureAnchor == 0 && context.FirstPlacementWindow == 0) return default;
        var signal = context.Attention?.GetValueOrDefault(w.Handle);
        bool reading = hint?.Role is WindowUseRole.Chat or WindowUseRole.Editor or WindowUseRole.FloatingEditor or
            WindowUseRole.Document or WindowUseRole.Note || hint?.TextFocus == true;
        double use = signal == null ? 0 : Math.Max(signal.Input, signal.Dwell * signal.Recency);
        double attention = .25 + Math.Clamp(visible.VisibleRatio, 0, 1) *
            ((w.IsForeground ? .55 : .16) + .70 * use + (hint?.PassiveVisual == true ? .3 : 0));
        // 每扇窗单独计价。刚滚动过的后台文档也可能在阅读；输入只影响候选方案的打断成本。
        if (context.IsWorkspaceObservation && context.ReadingInput?.GetValueOrDefault(w.Handle) is { } input)
            attention += visible.VisibleRatio * (.95 * input.Scrolling + .65 * input.CaretMotion);
        double repeat = Math.Clamp(signal?.RecentSystemPlacement ?? 0, 0, 1);
        bool initial = context.FirstPlacementWindow == w.Handle || hint?.InitialTextPlacement == true;
        // 自动整理时，后台正文的换行、列宽与控件会随着宽度一起变。切回用户已经摆好的窗口，这项代价仍然存在。
        bool background = !initial && context.IsAutomaticBackground(w);
        double reflowSensitivity = (reading ? 1.25 : .55) + (background ? 2 : 0);
        return new(attention + 12 * stability + 1.8 * repeat, reflowSensitivity, repeat,
            initial ? .30 : context.ExplicitArrange ? .45 : 1,
            context.AllowGestureAssist && context.GestureAnchor == w.Handle ? context.GestureTarget : null,
            context.Scene?.Lane is { } lane && lane.Front.Handle == w.Handle ? lane.Restore : null)
            { PriceLargeChanges = !initial && context.IsAutomaticLayout };
    }
}
