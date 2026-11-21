namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    // A known companion may only expose a sliver. Keep the regular automatic working set
    // for everything else, including fully covered windows and anchored observation regions.
    private static IReadOnlyList<VisibleWindow> IncludeWorkingCompanion(IReadOnlyList<VisibleWindow> visible,
        IReadOnlyList<WindowSnapshot> desktop, TaskLayoutContext context)
    {
        if (!context.ExplicitArrange && context.Focus is { NavigationAncestors.Length: > 0 } chain &&
            visible.Any(v => v.Window.Handle == chain.Main))
        {
            var visibleHandles = visible.Select(v => v.Window.Handle).ToHashSet();
            if (chain.NavigationAncestors.Any(h => !visibleHandles.Contains(h)))
            {
                var monitorHandle = visible.First(v => v.Window.Handle == chain.Main).Window.MonitorHandle;
                var related = new VisibilityAnalyzer().SelectForManualArrange(desktop, false)
                    .Where(v => v.Window.MonitorHandle == monitorHandle && chain.IsNavigationAncestor(v.Window.Handle) &&
                        !visibleHandles.Contains(v.Window.Handle));
                visible = visible.Concat(related).OrderBy(v => v.Window.ZOrder).ToArray();
            }
        }
        if (!context.UseWorkingPairs || context.ExplicitArrange || context.Focus is not { Main: not 0 } focus ||
            !visible.Any(v => v.Window.Handle == focus.Main) ||
            (focus.Companion != 0 && visible.Any(v => v.Window.Handle == focus.Companion))) return visible;
        if (focus.Companion == 0 && context.RulePairs is not { Length: > 0 } &&
            context.Placement?.Any(p => p.Partner != 0 && p.Confidence >= .4) != true &&
            context.WindowHints?.Any(h => h.MediaCompanion && !h.Anchor) != true) return visible;

        var monitor = visible.First(v => v.Window.Handle == focus.Main).Window.MonitorHandle;
        var eligible = new VisibilityAnalyzer().SelectVisibleWorkingSet(desktop, new(0, 1))
            .Where(v => v.Window.MonitorHandle == monitor).ToArray();
        var companion = ResolveWorkingPair(eligible, context).Focus?.Companion ?? 0;
        var peer = eligible.FirstOrDefault(v => v.Window.Handle == companion);
        if (peer == null || visible.Any(v => v.Window.Handle == companion) ||
            context.WindowHints?.Any(h => h.Handle == companion && h.Anchor) == true) return visible;
        return visible.Append(peer).OrderBy(v => v.Window.ZOrder).ToArray();
    }

    private static TaskLayoutContext ResolveWorkingPair(VisibleWindow[] windows, TaskLayoutContext context)
    {
        if (!context.PairPlanningEnabled || context.Focus is { Companion: not 0 } ||
            context.PairHints?.Any(h => h.Relation != TaskRelation.Automatic) == true) return context;
        var mainHandle = context.Focus?.Main ?? 0;
        var main = windows.FirstOrDefault(v => v.Window.Handle == mainHandle);
        if (main != null)
            windows = windows.Where(v => v.Window.MonitorHandle == main.Window.MonitorHandle &&
                v.Window.WorkArea == main.Window.WorkArea).ToArray();
        // A persistent placement prior needs fresh evidence to participate in this task.
        bool CanJoin(nint handle) => context.Attention == null ||
            handle == mainHandle || context.HasRecentInput(handle);
        var learned = context.Placement?.Where(p => p.Confidence >= .4 && p.Partner != 0 &&
            (mainHandle == 0 || p.Window == mainHandle || p.Partner == mainHandle) &&
            CanJoin(p.Window) && CanJoin(p.Partner) &&
            windows.Any(w => w.Window.Handle == p.Window) && windows.Any(w => w.Window.Handle == p.Partner))
            .OrderByDescending(p => p.Confidence).FirstOrDefault();
        if (learned != null)
        {
            var primary = mainHandle != 0 ? mainHandle : learned.Window;
            var companion = primary == learned.Window ? learned.Partner : learned.Window;
            return context with { Focus = new(primary, companion, 0, "learned-working-pair") };
        }
        var members = windows.Select(w => w.Window.Handle).ToHashSet();
        var rules = context.RulePairs?.Where(r => members.Contains(r.First) && members.Contains(r.Second) &&
            CanJoin(r.First) && CanJoin(r.Second) &&
            (mainHandle == 0 || r.First == mainHandle || r.Second == mainHandle)).Take(2).ToArray();
        if (rules is { Length: 1 })
        {
            var rule = rules[0];
            var primary = mainHandle == 0 ? rule.First : mainHandle;
            return context with { Focus = new(primary, primary == rule.First ? rule.Second : rule.First, 0, "configured-pair") };
        }
        // Evidence of joint work combines recent interaction/transition history with
        // simultaneous visibility and proximity. This also applies to stacked monitors or
        // vertically arranged surfaces without assigning roles from application identity.
        if (main != null && context.Attention != null && context.Focus != null)
        {
            var references = windows.Where(v => v.Window.Handle != mainHandle && v.Window.IsManageable &&
                context.Attention.GetValueOrDefault(v.Window.Handle) is { } recent &&
                IsReference(main, v, recent) &&
                context.WindowHints?.Any(h => h.Handle == v.Window.Handle && h.Anchor) != true)
                .Take(2).ToArray();
            if (references.Length == 1)
                return context with { Focus = new(mainHandle, references[0].Window.Handle, context.Focus.Switches, "visible-reference") };
        }
        bool IsReference(VisibleWindow primary, VisibleWindow other, LayoutAttention recent)
        {
            if (recent.Weight < .35) return false;
            var a = primary.Window.VisualRect; var b = other.Window.VisualRect;
            double independentArea = 1 - a.Intersect(b).Area / (double)Math.Max(1, Math.Min(a.Area, b.Area));
            double coVisible = Math.Min(primary.VisibleRatio, other.VisibleRatio) * independentArea;
            double comparisonEvidence = Math.Max(recent.Association, Math.Max(recent.Input, recent.Dwell * recent.Recency));
            return coVisible * TaskAffinity(primary.Window, other.Window, context) * comparisonEvidence >= .35;
        }
        var passive = context.WindowHints?.Where(h => h.MediaCompanion && !h.Anchor && h.Handle != mainHandle &&
            members.Contains(h.Handle) && CanJoin(h.Handle)).Take(2).ToArray();
        if (mainHandle != 0 && members.Contains(mainHandle) && passive is { Length: 1 })
            return context with { Focus = new(mainHandle, passive[0].Handle, 0, "visible-media-pair") };
        return context;
    }

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
            if (activePair)
            {
                bool coVisible = windows[i].VisibleRatio >= .25 && windows[j].VisibleRatio >= .25;
                bool configured = focus!.Evidence == "configured-pair";
                joint = Math.Max(joint, coVisible || configured ? .88 : .78);
                parked = Math.Min(parked, coVisible || configured ? .18 : .45);
                if (focus.Evidence == "handoff") { joint = .22; parked = .86; }
                source = focus.Evidence;
                uncertainty = configured ? .10 : focus.Switches > 0 ? coVisible ? .24 : .38 : .40;
            }
            var passive = context.WindowHints?.Any(h => h.PassiveVisual &&
                (h.Handle == windows[i].Window.Handle || h.Handle == windows[j].Window.Handle)) == true;
            if (passive && dx > .3) { joint = Math.Max(joint, .88); source += "+passive-visual"; uncertainty = .25; }
            var integrated = 0.0;
            if (hint is { Relation: not TaskRelation.Automatic })
            {
                joint = hint.Relation is TaskRelation.JointView or TaskRelation.Integrated ? 1 : 0;
                parked = hint.Relation == TaskRelation.Alternating ? 1 : 0;
                integrated = hint.Relation == TaskRelation.Integrated ? 1 : 0;
                if ((hint.Relation is TaskRelation.JointView or TaskRelation.Integrated) && windows.Length == 2) column = false;
                uncertainty = 0; source = "explicit-task-hint";
            }
            evidence.Add(new(i, j, joint * affinity, parked * affinity, integrated, uncertainty, column, source, affinity));
        }
        return evidence.ToArray();
    }

    private static bool CoversHalfOfRear(RectI front, RectI rear) =>
        front.Intersect(rear).Area >= rear.Area * .50;

    private static RectI InnerEdgeOverlap(RectI first, RectI second)
    {
        if (first.Left > second.Left) (first, second) = (second, first);
        return first.Left < second.Left && first.Right < second.Right && first.VerticalOverlapRatio(second) >= .50
            ? first.Intersect(second) : default;
    }

    // Off-screen pixels must never earn visible-information utility.
    internal static OcclusionMetrics.Exposure ScreenExposure(RectI rect, IEnumerable<RectI> occluders,
        RectI area, uint dpi)
    {
        var blockers = occluders.ToList();
        if (rect.Left < area.Left) blockers.Add(RectI.FromEdges(rect.Left, rect.Top, area.Left, rect.Bottom));
        if (rect.Right > area.Right) blockers.Add(RectI.FromEdges(area.Right, rect.Top, rect.Right, rect.Bottom));
        if (rect.Top < area.Top) blockers.Add(RectI.FromEdges(rect.Left, rect.Top, rect.Right, area.Top));
        if (rect.Bottom > area.Bottom) blockers.Add(RectI.FromEdges(rect.Left, area.Bottom, rect.Right, rect.Bottom));
        return OcclusionMetrics.Measure(rect, blockers, dpi);
    }

    private static (int Left, int Right) BleedBudget(WindowSnapshot window, TaskLayoutContext context)
    {
        var profile = context.Preferences;
        // Keep a fully on-screen current input surface inside its display. The facing
        // edges can still overlap, and an explicit arrangement can use peripheral bleed.
        if (!context.ExplicitArrange && context.Focus?.Main == window.Handle && context.Attention != null &&
            window.WorkArea.Intersect(window.VisualRect) == window.VisualRect) return (0, 0);
        if (!profile.AllowPeripheralBleed || profile.ProtectWindowEdges || context.WindowHints?.Any(h => h.Handle == window.Handle && h.ProtectPeriphery) == true)
            return (0, 0);
        var display = context.Displays?.FirstOrDefault(d => d.Monitor == window.MonitorHandle && d.WorkArea == window.WorkArea);
        if (display is null) return (0, 0);
        var amount = (int)Math.Round(Math.Min(profile.MaximumBleedDip * window.Dpi / 96.0, window.VisualRect.Width * .035));
        var left = display.WorkArea.Left == display.Bounds.Left ? amount : 0;
        var right = display.WorkArea.Right == display.Bounds.Right ? amount : 0;
        foreach (var other in context.Displays!.Where(d => d.Monitor != display.Monitor))
        {
            if (other.Bounds.Intersect(new(display.Bounds.Left - amount, display.Bounds.Top, amount, display.Bounds.Height)).Area > 0) left = 0;
            if (other.Bounds.Intersect(new(display.Bounds.Right, display.Bounds.Top, amount, display.Bounds.Height)).Area > 0) right = 0;
        }
        return (left, right);
    }

    private static OcclusionMetrics.Exposure[] MeasureBeforeExposure(VisibleWindow[] windows,
        RectI[] original, RectI area, WindowSnapshot[]? obstacles)
    {
        var front = new List<RectI>();
        return original.Select((r, i) =>
        {
            var result = ScreenExposure(r, front.Concat((obstacles ?? []).Where(w => w.ZOrder < windows[i].Window.ZOrder)
                .Select(w => w.VisualRect)), area, windows[i].Window.Dpi);
            front.Add(r); return result;
        }).ToArray();
    }

    private static bool TaskSafe(VisibleWindow[] windows, RectI[] original, Candidate candidate,
        RectI area, TidyOptions options, TaskLayoutContext context, WindowSnapshot[]? obstacles = null,
        OcclusionMetrics.Exposure[]? beforeExposure = null, AttentionTarget[]? goals = null)
    {
        var previous = beforeExposure ?? MeasureBeforeExposure(windows, original, area, obstacles);
        var front = new List<RectI>();
        foreach (var i in candidate.Order)
        {
            var window = windows[i].Window; var a = original[i]; var b = candidate.Rects[i];
            bool gestureEndpoint = window.Handle == context.GestureAnchor && b == context.GestureTarget;
            bool textView = context.HoldsTextView(window);
            if (textView)
            {
                int rank = Array.IndexOf(candidate.Order, i);
                var prior = windows.Where(v => v.Window.ZOrder < window.ZOrder).Select(v => v.Window.VisualRect)
                    .Concat((obstacles ?? []).Where(w => w.ZOrder < window.ZOrder).Select(w => w.VisualRect));
                var next = front.Concat((obstacles ?? []).Where(w => w.ZOrder < windows[rank].Window.ZOrder).Select(w => w.VisualRect));
                if (!TextViewAcceptsFrame(window, b, context, prior, next)) return false;
            }
            if (goals != null && (!goals[i].Movable && (a != b && !gestureEndpoint || Array.IndexOf(candidate.Order, i) != i) ||
                !goals[i].Resizable && !gestureEndpoint && (a.Width != b.Width || a.Height != b.Height) &&
                !(candidate.Kind == "refined" && context.VideoHint?.WindowHandle == window.Handle))) return false;
            if (window.Handle == context.GestureAnchor && a != b && !gestureEndpoint) return false;
            var bleed = BleedBudget(window, context);
            // A fixed/resize-disabled window larger than the work area cannot fit entirely.
            // Only the explicit rescue candidate may retain that unavoidable overflow; it must
            // equal the bounded rescue target and cannot resize the window or invent more area.
            var oversizedRescue = candidate.Kind == "rescue" && options.RescueOffscreenWindows &&
                (!window.IsResizable || !context.Preferences.AllowUsefulResize) &&
                (a.Width > area.Width || a.Height > area.Height) &&
                b == Fit(a, window, options, allowResize: false);
            if (!IntentEvidence.IsValidRect(b) || ((b != a || options.RescueOffscreenWindows && goals?[i].Movable != false && !textView) && !oversizedRescue &&
                (b.Left < area.Left - bleed.Left || b.Right > area.Right + bleed.Right ||
                 b.Top < area.Top || b.Bottom > area.Bottom))) return false;
            if (window.IsTopmost && b != a) return false;
            if ((!window.IsResizable || !context.Preferences.AllowUsefulResize) && (b.Width != a.Width || b.Height != a.Height)) return false;
            var sizeFloor = context.MinimumSize(window.Handle);
            if (window.IsResizable && ((sizeFloor.Width > 0 && b.Width < Math.Min(a.Width, sizeFloor.Width)) ||
                (sizeFloor.Height > 0 && b.Height < Math.Min(a.Height, sizeFloor.Height)))) return false;
            if (window.IsResizable && (b.Width < Math.Min(a.Width, Math.Min(options.MinimumWidth * window.Dpi / 96.0, area.Width)) ||
                b.Height < Math.Min(a.Height, Math.Min(options.MinimumHeight * window.Dpi / 96.0, area.Height)))) return false;
            // A near-maximized draft is not a measured readability requirement. Use the
            // finite content-scale prior for shrinking large drafts; fixed/native floors remain.
            var scale = window.Dpi / 96.0;
            var widthFloor = Math.Min(a.Width, context.Preferences.ComfortableWidthDip * scale) * .60;
            var heightFloor = Math.Min(a.Height, context.Preferences.ComfortableHeightDip * scale) * .60;
            if (goals?[i] is { LearnedWidth: true } learnedWidth) widthFloor = Math.Min(widthFloor, learnedWidth.Width);
            if (goals?[i] is { LearnedHeight: true } learnedHeight) heightFloor = Math.Min(heightFloor, learnedHeight.Height);
            if (goals?[i] is { MinimumFrameWidth: > 0 } budget && !gestureEndpoint)
                widthFloor = Math.Max(widthFloor, Math.Min(a.Width, budget.MinimumFrameWidth));
            if (a.Width <= area.Width && (b.Width < widthFloor || !gestureEndpoint && b.Width > Math.Max(a.Width * 1.50, Math.Max(goals?[i].FrameWidth ?? 0, goals?[i].Width ?? 0)))) return false;
            if (a.Height <= area.Height && (b.Height < heightFloor || !gestureEndpoint && b.Height > Math.Max(a.Height * 1.50, goals?[i].Height ?? 0))) return false;
            var after = ScreenExposure(b, front.Concat((obstacles ?? []).Where(w => w.ZOrder < windows[Array.IndexOf(candidate.Order, i)].Window.ZOrder).Select(w => w.VisualRect)), area, window.Dpi);
            if (goals?[i].Role is not (AttentionRole.Return or AttentionRole.Waiting) && context.Attention != null && (context.Focus?.Main == window.Handle ||
                context.Focus?.Companion == window.Handle || context.HasRecentInput(window.Handle)))
            {
                var before = previous[i];
                var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == window.Handle);
                double usefulWidth = Math.Min(a.Width, (hint?.UsefulWidthDip is > 0 and <= 4000
                    ? hint.UsefulWidthDip.Value : context.Preferences.ComfortableWidthDip) * scale);
                double usefulHeight = Math.Min(a.Height, (hint?.UsefulHeightDip is > 0 and <= 3000
                    ? hint.UsefulHeightDip.Value : context.Preferences.ComfortableHeightDip) * scale);
                if (goals?[i] is { LearnedWidth: true } preferredWidth) usefulWidth = Math.Min(usefulWidth, preferredWidth.Width);
                if (goals?[i] is { LearnedHeight: true } preferredHeight) usefulHeight = Math.Min(usefulHeight, preferredHeight.Height);
                if (goals?[i] is { Role: AttentionRole.Main, FrameWidth: > 0 } primary)
                    usefulWidth = Math.Min(usefulWidth, primary.Width);
                if (goals?[i] is { Role: AttentionRole.Reference } reference)
                {
                    usefulWidth = Math.Min(usefulWidth, reference.Width * .65);
                    usefulHeight = Math.Min(usefulHeight, reference.Height * .70);
                }
                if (context.VideoHint is { Confidence: >= .82, ContentAspectRatio: >= .55 and <= 4.5 } video &&
                    video.WindowHandle == window.Handle && !video.ViewportVisualRect.IsEmpty)
                {
                    // The known letterbox area can be removed while the video itself keeps its size.
                    var viewport = video.ViewportVisualRect;
                    double barWidth = Math.Max(0, viewport.Width - viewport.Height * video.ContentAspectRatio);
                    double barHeight = Math.Max(0, viewport.Height - viewport.Width / video.ContentAspectRatio);
                    usefulWidth = Math.Min(usefulWidth, a.Width - barWidth);
                    usefulHeight = Math.Min(usefulHeight, a.Height - barHeight);
                }
                bool shrinksReadableArea = b.Width + 3 < usefulWidth || b.Height + 3 < usefulHeight;
                bool revealsSubstantialContent = before.CenterVisible < .85 && after.CenterVisible >= before.CenterVisible + .15 &&
                    after.Visible * b.Area > before.Visible * a.Area;
                if ((shrinksReadableArea && !revealsSubstantialContent) ||
                    after.CenterVisible + .05 < Math.Min(goals?[i].Role == AttentionRole.Reference ? .6 : .95, before.CenterVisible)) return false;
            }
            if (after.Visible < Math.Min(.035, previous[i].Visible) - .001) return false;
            var access = Math.Min(96 * window.Dpi / 96.0, previous[i].AccessWidth);
            if (after.AccessWidth + 1 < access && after.UsefulWidth < Math.Min(200 * window.Dpi / 96.0, previous[i].UsefulWidth)) return false;
            front.Add(b);
        }
        if (HasRegionProtection(context))
        {
            var before = windows.Select((w, i) => w.Window with { VisualRect = original[i] }).Concat(obstacles ?? []).ToArray();
            var after = candidate.Order.Select((i, rank) => windows[i].Window with
                { VisualRect = candidate.Rects[i], ZOrder = windows[rank].Window.ZOrder }).Concat(obstacles ?? []).ToArray();
            if (!DesktopRegionsPreserved(before, after, context)) return false;
        }
        return true;
    }

    private readonly record struct WindowDemand(double Joint, double Parked, double Ambiguity);

    private static WindowDemand[] MeasureDemand(int count, TaskEvidence[] relations)
    {
        var demand = new WindowDemand[count];
        foreach (var relation in relations)
        {
            Accumulate(relation.First, relation);
            Accumulate(relation.Second, relation);
        }
        return demand;

        void Accumulate(int index, TaskEvidence evidence)
        {
            var old = demand[index];
            demand[index] = new(Math.Max(old.Joint, evidence.Joint), Math.Max(old.Parked, evidence.Parked),
                Math.Max(old.Ambiguity, evidence.Uncertainty * Math.Min(evidence.Joint, evidence.Parked)));
        }
    }

    private static TaskCostBreakdown TaskCost(VisibleWindow[] windows, RectI[] original, Candidate candidate,
        TaskEvidence[] relations, WindowDemand[] demands, RectI area, TaskLayoutContext context, IntentHint hint,
        WindowSnapshot[]? obstacles = null, AttentionTarget[]? goals = null)
    {
        var profile = context.Preferences;
        int mainDeficit = 0, accessDeficit = 0, comfortDeficit = 0, gestureDeficit = 0;
        var information = 0.0; var switching = 0.0; var continuity = 0.0; var reflow = 0.0;
        var peripheral = 0.0; var alignment = 0.0; var uncertainty = 0.0;
        double overlay = goals?.FirstOrDefault(t => t.Role == AttentionRole.Main)?.PlacementStability ?? 0;
        var handoffSource = context.Focus is { Evidence: "handoff" } handoff
            ? Array.FindIndex(windows, v => v.Window.Handle == handoff.Companion) : -1;
        double pairCostForWidth = 0;
        var front = new List<RectI>();
        for (var rank = 0; rank < candidate.Order.Length; rank++)
        {
            var i = candidate.Order[rank];
            var w = windows[i].Window; var a = original[i]; var b = candidate.Rects[i];
            var scale = w.Dpi / 96.0;
            var intent = demands[i];
            if (w.Handle == context.GestureAnchor && context.GestureTarget is { } endpoint && b != endpoint)
                gestureDeficit = 1;
            var blockers = (obstacles ?? []).Where(o => o.ZOrder < windows[rank].Window.ZOrder).Select(o => o.VisualRect);
            var exposure = ScreenExposure(b, front.Concat(blockers), area, w.Dpi);
            var windowHint = context.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle);
            var geometricSmall = a.Area < area.Area * .15;
            // Otherwise use the initial readable size. An established working pair can earn
            // readability from additional space, up to the same targets used to propose growth.
            var needWidth = geometricSmall ? a.Width : Math.Min(a.Width, profile.ComfortableWidthDip * scale);
            var needHeight = geometricSmall ? a.Height : Math.Min(a.Height, profile.ComfortableHeightDip * scale);
            if (windowHint?.PassiveVisual == true)
            {
                needWidth = Math.Min(profile.ComfortableWidthDip * scale, Math.Max(a.Width, profile.ComfortableWidthDip * scale * .75));
                needHeight = Math.Min(profile.ComfortableHeightDip * scale, Math.Max(a.Height, profile.ComfortableHeightDip * scale * .75));
            }
            if (windowHint?.UsefulWidthDip is double width && double.IsFinite(width) && width > 0) needWidth = Math.Clamp(width, 160, 4000) * scale;
            if (windowHint?.UsefulHeightDip is double height && double.IsFinite(height) && height > 0) needHeight = Math.Clamp(height, 120, 3000) * scale;
            if (goals?[i] is { } target)
            {
                // The working minimum and the comfortable upper bound serve different roles.
                // Visible content and reachable title area are measured after all projected moves.
                static int Missing(double fraction) => (int)Math.Round(20 * (1 - Math.Clamp(fraction, 0, 1)));
                if (target.RestingCenter is double center)
                {
                    var offset = Math.Abs(CenterX(b) - center) / area.Width;
                    alignment += 3 * Math.Max(0, offset - .04);
                }
                // Commit to a floating composition in proportion to the evidence. Reaching a
                // covered foreground, a return title, or a direct gesture has higher priority.
                double stability = Math.Max(overlay, target.PlacementStability);
                if (stability > 0)
                {
                    // During direct input, movement is salient relative to the surface
                    // containing the controls, even when it spans a small part of the monitor.
                    double input = target.Role == AttentionRole.Main ? context.Attention?.GetValueOrDefault(w.Handle)?.Input ?? 0 : 0;
                    double dx = Math.Abs(CenterX(a) - CenterX(b)), dy = Math.Abs(CenterY(a) - CenterY(b));
                    double displacement = (1 - input) * (dx / area.Width + dy / area.Height) +
                        input * (dx / Math.Min(area.Width, a.Width) + dy / Math.Min(area.Height, a.Height));
                    double resize = Math.Abs(Math.Log(b.Width / (double)a.Width)) + Math.Abs(Math.Log(b.Height / (double)a.Height));
                    comfortDeficit += (int)Math.Round(20 * stability * (displacement + resize));
                    continuity += 8 * stability * (displacement + resize);
                }
                double exposedWidth = Math.Min(exposure.UsefulWidth, b.Width);
                double exposedHeight = Math.Min(b.Height, b.Area * exposure.Visible / Math.Max(1, exposedWidth));
                if (target.Role is AttentionRole.Main or AttentionRole.Reference &&
                    context.VideoHint?.WindowHandle != w.Handle)
                {
                    needWidth = target.Width;
                    needHeight = target.Height;
                    double readable = Math.Min(exposedWidth / target.Width, exposedHeight / target.Height);
                    if (target.Role == AttentionRole.Main) mainDeficit += Missing(readable / .72);
                    else accessDeficit += Missing(readable / .60);
                    double comfort = (target.Role == AttentionRole.Main ? 2 : 1) *
                        (Missing(exposedWidth / target.Width) + Missing(exposedHeight / target.Height));
                    comfortDeficit += (int)Math.Round((1 - stability) * comfort);
                }
                if (target.FrameWidth > 0)
                {
                    // Quantized visibility guards readability. Within a band, attend to the
                    // actual binding width budget, including both over- and under-allocation.
                    pairCostForWidth += Math.Abs(Math.Min(exposedWidth, target.FrameWidth) - target.Width) /
                        target.Width + Math.Abs(b.Width - target.FrameWidth) / (double)target.FrameWidth;
                }
                if (target.FrameWidth > target.Width)
                {
                    // Shared margins belong inside the workspace. Offscreen width and deep
                    // overlap cannot substitute for visible content on the two-window desktop.
                    var outside = Math.Max(0, area.Left - b.Left) + Math.Max(0, b.Right - area.Right);
                    int allowed = target.FrameWidth - target.Width +
                        candidate.Order.Take(rank).Sum(j => Math.Max(0, (goals?[j].FrameWidth ?? 0) - (goals?[j].Width ?? 0)));
                    var shared = front.Select(r => InnerEdgeOverlap(b, r).Width).DefaultIfEmpty().Max();
                    double waste = outside + Math.Max(0, shared - allowed);
                    accessDeficit += (int)Math.Ceiling(Math.Max(0, waste - 3 * scale) / Math.Max(1, target.AccessWidth));

                    // A few hidden edge pixels can preserve an application's useful content
                    // width. Credit frame width only while the central area is fully readable.
                    double edge = Math.Min(1, b.Width / (double)target.FrameWidth);
                    comfortDeficit += Missing(Math.Min(edge, exposure.CenterVisible));
                }
                if (target.LearnedHeight) comfortDeficit += Missing(target.Height / (double)b.Height);
                if (target.LearnedWidth) comfortDeficit += Missing(target.Width / (double)b.Width);
                if (target.AccessWidth > 0 && target.Role is AttentionRole.Return or AttentionRole.Reference or AttentionRole.Waiting)
                    accessDeficit += Missing(exposure.AccessWidth / (double)target.AccessWidth);
            }
            var capacity = Math.Pow(Math.Min(1, b.Width / Math.Max(1, needWidth)), .65) *
                Math.Pow(Math.Min(1, b.Height / Math.Max(1, needHeight)), .35);
            var contentVisibility = (context.Preferences.ProtectWindowEdges || windowHint?.ProtectPeriphery == true) ? exposure.Visible :
                .65 * exposure.CenterVisible + .35 * exposure.Visible;
            double occlusionLoss = 1 - contentVisibility;
            double thirdPartyLoss = occlusionLoss;
            if (context.PairPlanningEnabled && !context.Preferences.ProtectWindowEdges && windowHint?.ProtectPeriphery != true)
            {
                // Allow only the configured strip at a facing inner edge. Keep the remainder of
                // that window and all other covers in the geometry used for readability scoring.
                List<RectI>? remainingFront = null;
                double allowedFraction = 0;
                for (int rIdx = 0; rIdx < rank; rIdx++)
                {
                    var partnerRect = front[rIdx];
                    var overlap = InnerEdgeOverlap(b, partnerRect);
                    if (overlap.IsEmpty) continue;
                    var pref = PairPreferenceFor(windows[candidate.Order[rIdx]].Window.Handle, w.Handle, context);
                    if (pref?.Overlap is not double authOverlap || authOverlap <= 0) continue;
                    var narrow = Math.Min(b.Width, partnerRect.Width);
                    var allowance = (int)Math.Round(Math.Min(overlap.Width, narrow * authOverlap));
                    if (allowance <= 0) continue;
                    remainingFront ??= front.ToList();
                    remainingFront[rIdx] = overlap with
                    {
                        X = b.Left < partnerRect.Left ? overlap.X : overlap.X + allowance,
                        Width = overlap.Width - allowance,
                    };
                    allowedFraction += (double)allowance / narrow;
                }
                if (remainingFront is not null)
                {
                    var remaining = ScreenExposure(b, remainingFront.Concat(blockers), area, w.Dpi);
                    thirdPartyLoss = 1 - (.65 * remaining.CenterVisible + .35 * remaining.Visible);
                    occlusionLoss = Math.Min(occlusionLoss, thirdPartyLoss + .12 * allowedFraction);
                }
            }
            var demand = windows.Length == 1 ? 0 : Math.Max(intent.Joint * profile.JointVisibilityWeight,
                w.IsForeground ? .85 : .18 * intent.Parked);
            if (context.Attention != null && w.Handle != context.Focus?.Main && w.Handle != context.Focus?.Companion)
                demand *= .25 + .75 * (context.Attention.GetValueOrDefault(w.Handle)?.Weight ?? 0);
            var alternativeDemand = intent.Ambiguity;
            if (context.PairPlanningEnabled && context.Focus is { Companion: not 0 } pair &&
                w.Handle != pair.Main && w.Handle != pair.Companion && windows[i].VisibleRatio < .08 &&
                context.PairHints?.Any(h => h.First == w.Handle || h.Second == w.Handle) != true)
            {
                demand *= .25;
                alternativeDemand *= .5;
                continuity += .6 * (Math.Abs(CenterX(a) - CenterX(b)) / area.Width +
                    Math.Abs(CenterY(a) - CenterY(b)) / area.Height);
            }
            var readingLoss = 3.4 * occlusionLoss + 1.8 * (1 - capacity);
            var accessLoss = Math.Max(0, 1 - exposure.AccessWidth / Math.Max(1, Math.Min(140 * scale, b.Width * .30)));
            information += demand * readingLoss;
            // Read-together and quick-switch interpretations share the title/access outcome.
            // Ambiguous evidence also charges the worse residual of the two interpretations.
            uncertainty += alternativeDemand * Math.Max(.35 * readingLoss, accessLoss);
            if (context.PairPlanningEnabled && context.Focus is { Companion: not 0, Evidence: not "handoff" } working &&
                w.Handle == working.Companion && context.PairHints?.Any(h => h.Relation == TaskRelation.Alternating &&
                    (h.First == w.Handle || h.Second == w.Handle)) != true &&
                context.Placement?.Any(p => p.Partner == w.Handle && p.RevealedCell >= 0) != true)
            {
                // Background windows should not dilute the visibility of the two working surfaces.
                information += windows.Length * 1.5 * thirdPartyLoss;
            }
            if (context.VideoHint is { Confidence: >= .82, ContentAspectRatio: >= .55 and <= 4.5 } video && video.WindowHandle == w.Handle)
            {
                var chromeX = Math.Max(0, a.Width - video.ViewportVisualRect.Width);
                var chromeY = Math.Max(0, a.Height - video.ViewportVisualRect.Height);
                var aspect = Math.Max(1, b.Width - chromeX) / (double)Math.Max(1, b.Height - chromeY);
                information += 1.5 * Math.Abs(Math.Log(aspect / video.ContentAspectRatio));
            }
            switching += .8 * accessLoss;
            peripheral += 1.4 * (1 - b.Intersect(area).Area / (double)b.Area);
            var distance = Math.Sqrt(Math.Pow(CenterX(a) - CenterX(b), 2) + Math.Pow(CenterY(a) - CenterY(b), 2));
            continuity += .35 * profile.SpatialContinuityWeight * distance / Math.Max(300 * scale, Math.Min(area.Width, area.Height));
            reflow += .18 * (Math.Abs(Math.Log(b.Width / (double)a.Width)) + Math.Abs(Math.Log(b.Height / (double)a.Height)));
            if (context.RecentGesture is { } gesture && gesture.WindowHandle == w.Handle &&
                gesture.WorkArea == area && gesture.EndRect == a && gesture.Kind != ManualGestureKind.Move)
                reflow += .45 * (Math.Abs(Math.Log(b.Width / (double)a.Width)) + Math.Abs(Math.Log(b.Height / (double)a.Height)));
            if (context.Focus?.Main == w.Handle)
            {
                if (handoffSource >= 0)
                {
                    var source = original[handoffSource];
                    double x = CenterX(source), y = CenterY(source);
                    // Reading can begin near the point where the preceding task was used.
                    switching += Math.Abs(x - Math.Clamp(x, b.Left, b.Right)) / area.Width +
                        Math.Abs(y - Math.Clamp(y, b.Top, b.Bottom)) / area.Height;
                }
                information += 1.6 * (1 - capacity) + .8 * (1 - contentVisibility);
                reflow += .9 * Math.Max(0, Math.Log(a.Height / (double)b.Height));
            }
            alignment += VerticalFillPreferenceCost(w, a, b, context) / Math.Max(1, windows.Length);
            front.Add(b);
        }
        var n = Math.Max(1, windows.Length);
        information /= n; switching /= n; continuity /= n; reflow /= n; peripheral /= n; uncertainty /= n;
        double pairCost = pairCostForWidth;
        foreach (var r in relations)
        {
            var a = candidate.Rects[r.First]; var b = candidate.Rects[r.Second];
            var oa = original[r.First]; var ob = original[r.Second];
            var choice = PairPreferenceFor(windows[r.First].Window.Handle, windows[r.Second].Window.Handle, context);
            if (choice != null) pairCost += PairPlacementCost(a, b, oa, ob, area, choice);
            // Deep coverage with no usable rear title is an observable access problem
            // under either reading or alternating. Deliberate overlap still earns credit.
            if (r.Joint > r.Parked && oa.Intersect(ob).Area >= .50 * Math.Max(oa.Area, ob.Area))
            {
                double rawOverlap = a.Intersect(b).Area / Math.Max(1d, Math.Min(a.Area, b.Area));
                double unpermitted = choice?.Overlap is double permitted && permitted > 0 && !InnerEdgeOverlap(a, b).IsEmpty
                    ? Math.Max(0, a.HorizontalOverlapRatio(b) - permitted)
                    : rawOverlap;
                information += (2.6 * (r.Joint - .5 * r.Parked) * unpermitted) / n;
            }
            var dx = CenterX(oa) - CenterX(ob); var dy = CenterY(oa) - CenterY(ob);
            // Inside a larger background window, a smaller window's center can be to either
            // side without establishing a reading order. Penalize reversals only for distinct positions.
            var preserveHorizontalOrder = oa.Intersect(ob).Area < .50 * Math.Min(oa.Area, ob.Area);
            if ((choice?.Order ?? 0) == 0 && preserveHorizontalOrder && Math.Abs(dx) > .20 * Math.Min(oa.Width, ob.Width) &&
                dx * (CenterX(a) - CenterX(b)) < 0) continuity += 1.8 * r.Affinity;
            if (r.SameColumn)
            {
                continuity += Math.Max(0, Math.Abs(CenterX(a) - CenterX(b)) / Math.Max(1, Math.Min(a.Width, b.Width)) - .30) * 2 * r.Affinity;
                if (Math.Abs(dy) > 20 * windows[r.First].Window.Dpi / 96.0 && dy * (CenterY(a) - CenterY(b)) < 0) continuity += 1.2 * r.Affinity;
            }
            var oldDistance = Math.Sqrt(dx * dx + dy * dy);
            var newDistance = Math.Sqrt(Math.Pow(CenterX(a) - CenterX(b), 2) + Math.Pow(CenterY(a) - CenterY(b), 2));
            // Proximity compatibility is task-dependent, not 'closer is always better'.
            var separation = newDistance / Math.Max(1, Math.Min(area.Width, area.Height));
            if (context.Calibration is { } calibration && calibration.Matches(area, windows[r.First].Window.Dpi))
                separation = calibration.AngularSeparation(CenterX(a), CenterY(a), CenterX(b), CenterY(b)) / 45.0;
            switching += (.04 * r.Joint + .28 * r.Integrated) * separation;
            continuity += r.Parked * .85 * Math.Max(0, (newDistance - oldDistance) / Math.Max(1, Math.Min(oa.Width, ob.Width)));
            var horizontal = !r.SameColumn && a.VerticalOverlapRatio(b) >= .4;
            if (horizontal)
            {
                alignment += .13 * r.Affinity * Math.Min(1, Math.Min(Math.Abs(a.Top - b.Top), Math.Abs(a.Bottom - b.Bottom)) / Math.Max(1, 80 * windows[r.First].Window.Dpi / 96.0));
                var gap = CenterX(a) < CenterX(b) ? b.Left - a.Right : a.Left - b.Right;
                if (gap >= 0) alignment += .12 * r.Affinity * Math.Min(1, Math.Abs(gap - hint.GapPixels) / (160 * windows[r.First].Window.Dpi / 96.0));
            }
            else if (a.HorizontalOverlapRatio(b) >= .4)
            {
                var gap = RectI.IntervalGap(a.Top, a.Bottom, b.Top, b.Bottom);
                var edge = Math.Min(Math.Abs(a.Left - b.Left), Math.Abs(a.Right - b.Right));
                var scale = windows[r.First].Window.Dpi / 96.0;
                alignment += r.Affinity * (.10 * Math.Min(1, edge / (80 * scale)) +
                    .08 * Math.Min(1, Math.Abs(gap - hint.GapPixels) / (160 * scale)));
            }
            uncertainty += r.Uncertainty * .04 * Math.Abs(newDistance - oldDistance) / Math.Max(1, Math.Min(area.Width, area.Height));
        }
        if (!candidate.Order.SequenceEqual(Enumerable.Range(0, n))) continuity += .08;
        var ranks = windows.Select(w => w.Window.ZOrder).Order().ToArray();
        var projected = candidate.Order.Select((i, rank) => windows[i].Window with
            { VisualRect = candidate.Rects[i], ZOrder = ranks[rank] }).ToArray();
        var feedback = context.RejectedLayouts?.Contains(TaskLayoutSession.LayoutKey(projected)) == true ? 5 : 0;
        double personal = 0;
        foreach (var preference in context.Placement ?? [])
        {
            int index = Array.FindIndex(windows, w => w.Window.Handle == preference.Window);
            if (index < 0) continue;
            var r = candidate.Rects[index];
            double targetCenter = (CenterX(r) - area.Left) / area.Width;
            double error = preference.PreferCenter ? Math.Abs(targetCenter - preference.Center) : 0;
            if (preference.PreferWidth) error += .5 * Math.Abs(r.Width / (double)area.Width - preference.Width);
            int peer = Array.FindIndex(windows, w => w.Window.Handle == preference.Partner);
            if (peer >= 0 && Math.Abs(preference.Order) >= .5 &&
                Math.Sign(CenterX(r) - CenterX(candidate.Rects[peer])) != Math.Sign(preference.Order))
                error += 2 * Math.Abs(preference.Order);
            if (peer >= 0 && preference.RevealedCell is >= 0 and < 16)
            {
                var cell = WindowExposure.Cell(candidate.Rects[peer], preference.RevealedCell);
                var slot = Array.IndexOf(candidate.Order, peer);
                var covers = candidate.Order.Take(slot).Select(i => candidate.Rects[i])
                    .Concat((obstacles ?? []).Where(w => w.ZOrder < ranks[slot]).Select(w => w.VisualRect));
                error += 2 * (1 - ScreenExposure(cell, covers, area, windows[peer].Window.Dpi).Visible);
            }
            personal += preference.Confidence * error;
        }
        return new(information, switching, continuity, reflow, peripheral, alignment, uncertainty, feedback, personal, pairCost,
            new(mainDeficit, accessDeficit, comfortDeficit, gestureDeficit));
    }

    internal static bool TaskRefinementAcceptable(IReadOnlyList<VisibleWindow> visible,
        IntentLayoutPlan planned, IReadOnlyList<TidyMove> refined, TidyOptions options,
        TaskLayoutContext context, IReadOnlyList<WindowSnapshot> desktop)
    {
        if (context.VerifiedRepeat && !context.ExplicitArrange) return refined.Count == 0 && planned.Layers.Count == 0;
        context = ResolveExplicitFocus(visible, context);
        visible = IncludeWorkingCompanion(visible, desktop, context);
        var known = planned.Groups.SelectMany(g => g.Handles).ToHashSet();
        if (refined.Any(m => !known.Contains(m.Window.Handle))) return false;
        if (!TextViewAcceptsPlan(desktop, planned with { Moves = refined }, context)) return false;
        var ranks = desktop.ToDictionary(w => w.Handle, w => w.ZOrder);
        foreach (var layer in planned.Layers)
        {
            var slots = layer.FrontToBack.Select(w => ranks[w.Handle]).Order().ToArray();
            for (var i = 0; i < slots.Length; i++) ranks[layer.FrontToBack[i].Handle] = slots[i];
        }
        WindowSnapshot[] Project(IReadOnlyList<TidyMove> moves)
        {
            var targets = moves.ToDictionary(m => m.Window.Handle, m => m.TargetVisualRect);
            return desktop.Select(w => w with { VisualRect = targets.GetValueOrDefault(w.Handle, w.VisualRect),
                ZOrder = ranks[w.Handle] }).ToArray();
        }
        var plannedDesktop = Project(planned.Moves);
        var refinedDesktop = Project(refined);
        if (!DesktopRegionsPreserved(plannedDesktop, refinedDesktop, context)) return false;
        foreach (var trace in planned.Groups)
        {
            var group = visible.Where(v => trace.Handles.Contains(v.Window.Handle)).OrderBy(v => v.Window.ZOrder).ToArray();
            var original = group.Select(v => v.Window.VisualRect).ToArray();
            var before = group.Select(v => planned.Moves.FirstOrDefault(m => m.Window.Handle == v.Window.Handle)?.TargetVisualRect ?? v.Window.VisualRect).ToArray();
            var after = group.Select(v => refined.FirstOrDefault(m => m.Window.Handle == v.Window.Handle)?.TargetVisualRect ?? v.Window.VisualRect).ToArray();
            if (before.SequenceEqual(after)) continue;
            var layer = planned.Layers.FirstOrDefault(l => l.FrontToBack.Any(w => trace.Handles.Contains(w.Handle)));
            var order = layer is null ? Enumerable.Range(0, group.Length).ToArray() :
                layer.FrontToBack.Select(w => Array.FindIndex(group, v => v.Window.Handle == w.Handle)).ToArray();
            if (order.Length != group.Length || order.Any(i => i < 0)) return false;
            var area = group[0].Window.WorkArea;
            var beforeObstacles = plannedDesktop.Where(w => !trace.Handles.Contains(w.Handle) && !w.VisualRect.Intersect(area).IsEmpty).ToArray();
            var afterObstacles = refinedDesktop.Where(w => !trace.Handles.Contains(w.Handle) && !w.VisualRect.Intersect(area).IsEmpty).ToArray();
            var previousObstacleRects = beforeObstacles.ToDictionary(w => w.Handle, w => w.VisualRect);
            var c = new Candidate("refined", after, order);
            var groupContext = ResolveWorkingPair(group, context);
            var goals = AttentionLayout.Targets(group, groupContext, options.SmartStrength);
            AttentionLayout.SetRestingCenter(group, goals, beforeObstacles, groupContext);
            if (!TaskSafe(group, original, c, area, options, groupContext, afterObstacles, goals: goals)) return false;
            for (var i = 0; i < group.Length; i++)
                foreach (var obstacle in afterObstacles)
                    if (after[i].Intersect(obstacle.VisualRect).Area >
                        before[i].Intersect(previousObstacleRects.GetValueOrDefault(obstacle.Handle)).Area) return false;
            var task = trace.Task?.Relations ?? InferTask(group, original, groupContext);
            var demands = MeasureDemand(group.Length, task);
            var hint = new IntentHint((int)Math.Round(8 * group[0].Window.Dpi / 96.0), 0, 0);
            var changed = EffectiveBreakdown(TaskCost(group, original, c, task, demands, area, groupContext, hint, afterObstacles, goals), options.SmartStrength, task);
            var plannedCost = EffectiveBreakdown(TaskCost(group, original, new("planned", before, order), task, demands, area, groupContext, hint, beforeObstacles, goals), options.SmartStrength, task);
            var priority = changed.Goal.CompareTo(plannedCost.Goal);
            if (priority > 0 || priority == 0 && changed.Total > plannedCost.Total + .01) return false;
        }
        return true;
    }
}
