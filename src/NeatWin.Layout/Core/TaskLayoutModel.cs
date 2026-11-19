namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    private static TaskLayoutContext ResolveWorkingPair(VisibleWindow[] windows, TaskLayoutContext context)
    {
        // A crowded desktop can already have two readable working surfaces above a parked stack.
        // Preserve this cue when the utility owns focus and there is no stronger interaction history.
        if (!context.ExplicitArrange || context.Focus is { Companion: not 0 } ||
            context.PairHints?.Any(h => h.Relation != TaskRelation.Automatic) == true) return context;
        var mainHandle = context.Focus?.Main ?? 0;
        var learned = context.Placement?.Where(p => p.Confidence >= .4 && p.Partner != 0 &&
            (mainHandle == 0 || p.Window == mainHandle || p.Partner == mainHandle) &&
            windows.Any(w => w.Window.Handle == p.Window) && windows.Any(w => w.Window.Handle == p.Partner))
            .OrderByDescending(p => p.Confidence).FirstOrDefault();
        if (learned != null)
        {
            var primary = mainHandle != 0 ? mainHandle : learned.Window;
            var companion = primary == learned.Window ? learned.Partner : learned.Window;
            return context with { Focus = new(primary, companion, 0, "learned-working-pair") };
        }
        if (windows.Length < 4) return context;
        var visible = windows.Where(w => w.VisibleRatio >= .4 && w.Window.VisualRect.Area >= .12 * w.Window.WorkArea.Area).ToArray();
        if (visible.Length != 2 || visible[0] != windows[0] || visible[1] != windows[1] ||
            !windows.Skip(2).Any(w => w.VisibleRatio < .08)) return context;
        var main = visible.FirstOrDefault(w => w.Window.Handle == context.Focus?.Main) ?? visible[0];
        if (context.Focus is { Main: not 0 } f && main.Window.Handle != f.Main) return context;
        var peer = visible.First(w => w.Window.Handle != main.Window.Handle);
        var a = main.Window.VisualRect; var b = peer.Window.VisualRect;
        if (a.VerticalOverlapRatio(b) < .5 || Math.Abs(CenterX(a) - CenterX(b)) < .4 * Math.Min(a.Width, b.Width)) return context;
        return context with { Focus = new(main.Window.Handle, peer.Window.Handle, 0, "co-visible-working-pair") };
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
            // Competing hypotheses, not calibrated psychological probabilities. Overlap alone
            // cannot distinguish a parked deck from a space-constrained rough row.
            var parked = dx < .28 && a.Intersect(b).Area > 0 && topDifference is >= 20 and <= 140 ? .85 :
                dx < .20 && dy < .20 && a.Intersect(b).Area > 0 ? .65 : .12;
            var joint = parked > .6 ? .15 : Math.Clamp(.35 + .50 * Math.Min(1, dx) + .12 * a.VerticalOverlapRatio(b), .35, .92);
            if (column && dy > .55) { joint = .80; parked = .15; }
            var source = "geometry-hypotheses";
            var uncertainty = .40;
            // A click is evidence that a heavily covered rear window should be readable.
            // Drag assistance may still infer an intentional stack; explicit pair hints win below.
            var focus = context.Focus;
            bool activePair = focus is { Companion: not 0 } &&
                (windows[i].Window.Handle == focus.Main && windows[j].Window.Handle == focus.Companion ||
                 windows[j].Window.Handle == focus.Main && windows[i].Window.Handle == focus.Companion);
            if (context.ExplicitArrange && CoversHalfOfRear(a, b) && (focus?.Companion is null or 0 || activePair))
            {
                joint = Math.Max(joint, .85);
                parked = .12;
                column = false;
                source = "explicit-arrange-overlap";
            }
            if (activePair)
            {
                // Switching supports association. Joint visibility additionally needs spatial evidence.
                bool coVisible = windows[i].VisibleRatio >= .25 && windows[j].VisibleRatio >= .25;
                joint = Math.Max(joint, coVisible || context.ExplicitArrange ? .85 : .52);
                source = focus!.Evidence; uncertainty = focus.Switches > 0 ? .30 : .45;
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
        OcclusionMetrics.Exposure[]? beforeExposure = null)
    {
        var previous = beforeExposure ?? MeasureBeforeExposure(windows, original, area, obstacles);
        var front = new List<RectI>();
        foreach (var i in candidate.Order)
        {
            var window = windows[i].Window; var a = original[i]; var b = candidate.Rects[i];
            var bleed = BleedBudget(window, context);
            // A fixed/resize-disabled window larger than the work area cannot fit entirely.
            // Only the explicit rescue candidate may retain that unavoidable overflow; it must
            // equal the bounded rescue target and cannot resize the window or invent more area.
            var oversizedRescue = candidate.Kind == "rescue" && options.RescueOffscreenWindows &&
                (!window.IsResizable || !context.Preferences.AllowUsefulResize) &&
                (a.Width > area.Width || a.Height > area.Height) &&
                b == Fit(a, window, options, allowResize: false);
            if (!IntentEvidence.IsValidRect(b) || (!oversizedRescue &&
                (b.Left < area.Left - bleed.Left || b.Right > area.Right + bleed.Right ||
                 b.Top < area.Top || b.Bottom > area.Bottom))) return false;
            if (window.IsTopmost && b != a) return false;
            if ((!window.IsResizable || !context.Preferences.AllowUsefulResize) && (b.Width != a.Width || b.Height != a.Height)) return false;
            if (window.IsResizable && (b.Width < Math.Min(a.Width, Math.Min(options.MinimumWidth * window.Dpi / 96.0, area.Width)) ||
                b.Height < Math.Min(a.Height, Math.Min(options.MinimumHeight * window.Dpi / 96.0, area.Height)))) return false;
            // A near-maximized draft is not a measured readability requirement. Use the
            // finite content-scale prior for shrinking large drafts; fixed/native floors remain.
            var scale = window.Dpi / 96.0;
            var widthFloor = Math.Min(a.Width, context.Preferences.ComfortableWidthDip * scale) * .60;
            var heightFloor = Math.Min(a.Height, context.Preferences.ComfortableHeightDip * scale) * .60;
            if (a.Width <= area.Width && (b.Width < widthFloor || b.Width > a.Width * 1.50)) return false;
            if (a.Height <= area.Height && (b.Height < heightFloor || b.Height > a.Height * 1.50)) return false;
            var after = ScreenExposure(b, front.Concat((obstacles ?? []).Where(w => w.ZOrder < windows[Array.IndexOf(candidate.Order, i)].Window.ZOrder).Select(w => w.VisualRect)), area, window.Dpi);
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

    private static TaskCostBreakdown TaskCost(VisibleWindow[] windows, RectI[] original, Candidate candidate,
        TaskEvidence[] relations, RectI area, TaskLayoutContext context, IntentHint hint, WindowSnapshot[]? obstacles = null)
    {
        var profile = context.Preferences;
        var information = 0.0; var switching = 0.0; var continuity = 0.0; var reflow = 0.0;
        var peripheral = 0.0; var alignment = 0.0; var uncertainty = 0.0;
        var front = new List<RectI>();
        foreach (var i in candidate.Order)
        {
            var w = windows[i].Window; var a = original[i]; var b = candidate.Rects[i];
            var scale = w.Dpi / 96.0;
            var relevant = relations.Where(r => r.First == i || r.Second == i).ToArray();
            var joint = relevant.Length == 0 ? 0 : relevant.Max(r => r.Joint);
            var park = relevant.Length == 0 ? 0 : relevant.Max(r => r.Parked);
            var exposure = ScreenExposure(b, front.Concat((obstacles ?? []).Where(o => o.ZOrder < windows[Array.IndexOf(candidate.Order, i)].Window.ZOrder).Select(o => o.VisualRect)), area, w.Dpi);
            var windowHint = context.WindowHints?.FirstOrDefault(h => h.Handle == w.Handle);
            var geometricSmall = a.Area < area.Area * .15;
            // Without content evidence, a generic minimum must not invent a need to enlarge
            // already unobscured windows. This is saturation, not a prohibition on resizing:
            // shrinkage can buy joint visibility; passive/content hints can justify growth.
            var needWidth = geometricSmall ? a.Width : Math.Min(a.Width, profile.ComfortableWidthDip * scale);
            var needHeight = geometricSmall ? a.Height : Math.Min(a.Height, profile.ComfortableHeightDip * scale);
            if (windowHint?.PassiveVisual == true)
            {
                needWidth = Math.Min(profile.ComfortableWidthDip * scale, Math.Max(a.Width, profile.ComfortableWidthDip * scale * .75));
                needHeight = Math.Min(profile.ComfortableHeightDip * scale, Math.Max(a.Height, profile.ComfortableHeightDip * scale * .75));
            }
            if (windowHint?.UsefulWidthDip is double width && double.IsFinite(width) && width > 0) needWidth = Math.Clamp(width, 160, 4000) * scale;
            if (windowHint?.UsefulHeightDip is double height && double.IsFinite(height) && height > 0) needHeight = Math.Clamp(height, 120, 3000) * scale;
            var capacity = Math.Pow(Math.Min(1, b.Width / Math.Max(1, needWidth)), .65) *
                Math.Pow(Math.Min(1, b.Height / Math.Max(1, needHeight)), .35);
            var contentVisibility = (context.Preferences.ProtectWindowEdges || windowHint?.ProtectPeriphery == true) ? exposure.Visible :
                .65 * exposure.CenterVisible + .35 * exposure.Visible;
            var demand = windows.Length == 1 ? 0 : Math.Max(joint * profile.JointVisibilityWeight,
                i == 0 ? .85 : .18 * park);
            if (context.ExplicitArrange && context.Focus is { Companion: not 0 } pair &&
                w.Handle != pair.Main && w.Handle != pair.Companion && windows[i].VisibleRatio < .08 &&
                context.PairHints?.Any(h => h.First == w.Handle || h.Second == w.Handle) != true)
            {
                demand *= .25;
                continuity += .6 * (Math.Abs(CenterX(a) - CenterX(b)) / area.Width +
                    Math.Abs(CenterY(a) - CenterY(b)) / area.Height);
            }
            information += demand * (3.4 * (1 - contentVisibility) + 1.8 * (1 - capacity));
            if (context.ExplicitArrange && context.Focus is { Companion: not 0 } working &&
                w.Handle == working.Companion && context.PairHints?.Any(h => h.Relation == TaskRelation.Alternating &&
                    (h.First == w.Handle || h.Second == w.Handle)) != true &&
                context.Placement?.Any(p => p.Partner == w.Handle && p.RevealedCell >= 0) != true)
            {
                // Background windows should not dilute the visibility of the two working surfaces.
                information += windows.Length * 1.5 * (1 - contentVisibility);
            }
            if (context.VideoHint is { Confidence: >= .82, ContentAspectRatio: >= .55 and <= 4.5 } video && video.WindowHandle == w.Handle)
            {
                var chromeX = Math.Max(0, a.Width - video.ViewportVisualRect.Width);
                var chromeY = Math.Max(0, a.Height - video.ViewportVisualRect.Height);
                var aspect = Math.Max(1, b.Width - chromeX) / (double)Math.Max(1, b.Height - chromeY);
                information += 1.5 * Math.Abs(Math.Log(aspect / video.ContentAspectRatio));
            }
            switching += .8 * Math.Max(0, 1 - exposure.AccessWidth / Math.Max(1, Math.Min(140 * scale, b.Width * .30)));
            peripheral += 1.4 * (1 - b.Intersect(area).Area / (double)b.Area);
            var distance = Math.Sqrt(Math.Pow(CenterX(a) - CenterX(b), 2) + Math.Pow(CenterY(a) - CenterY(b), 2));
            continuity += .35 * profile.SpatialContinuityWeight * distance / Math.Max(300 * scale, Math.Min(area.Width, area.Height));
            reflow += .18 * (Math.Abs(Math.Log(b.Width / (double)a.Width)) + Math.Abs(Math.Log(b.Height / (double)a.Height)));
            if (context.RecentGesture is { } gesture && gesture.WindowHandle == w.Handle &&
                gesture.WorkArea == area && gesture.EndRect == a && gesture.Kind != ManualGestureKind.Move)
                reflow += .45 * (Math.Abs(Math.Log(b.Width / (double)a.Width)) + Math.Abs(Math.Log(b.Height / (double)a.Height)));
            if (context.Focus?.Main == w.Handle)
            {
                information += 1.6 * (1 - capacity) + .8 * (1 - contentVisibility);
                reflow += .9 * Math.Max(0, Math.Log(a.Height / (double)b.Height));
            }
            alignment += VerticalFillPreferenceCost(w, a, b, context) / Math.Max(1, windows.Length);
            front.Add(b);
        }
        var n = Math.Max(1, windows.Length);
        information /= n; switching /= n; continuity /= n; reflow /= n; peripheral /= n;
        foreach (var r in relations)
        {
            var a = candidate.Rects[r.First]; var b = candidate.Rects[r.Second];
            var oa = original[r.First]; var ob = original[r.Second];
            // Manual arrange treats a heavily stacked pair as a request to see both windows.
            // A larger window behind the pair can otherwise make a partly overlapped row score better.
            if (context.ExplicitArrange && r.Joint > r.Parked &&
                oa.Intersect(ob).Area >= .5 * Math.Max(oa.Area, ob.Area))
                information += 3.4 * r.Joint * a.Intersect(b).Area / Math.Max(1d, Math.Min(a.Area, b.Area));
            var dx = CenterX(oa) - CenterX(ob); var dy = CenterY(oa) - CenterY(ob);
            // Inside a larger background window, a smaller window's center can be to either
            // side without establishing a reading order. Penalize reversals only for distinct positions.
            var preserveHorizontalOrder = !context.ExplicitArrange ||
                oa.Intersect(ob).Area < .50 * Math.Min(oa.Area, ob.Area);
            if (preserveHorizontalOrder && Math.Abs(dx) > .20 * Math.Min(oa.Width, ob.Width) &&
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
            double error = Math.Abs(targetCenter - preference.Center) +
                .5 * Math.Abs(r.Width / (double)area.Width - preference.Width);
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
        return new(information, switching, continuity, reflow, peripheral, alignment, uncertainty, feedback, personal);
    }

    internal static bool TaskRefinementAcceptable(IReadOnlyList<VisibleWindow> visible,
        IntentLayoutPlan planned, IReadOnlyList<TidyMove> refined, TidyOptions options,
        TaskLayoutContext context, IReadOnlyList<WindowSnapshot> desktop)
    {
        if (context.VerifiedRepeat) return refined.Count == 0;
        var known = planned.Groups.SelectMany(g => g.Handles).ToHashSet();
        if (refined.Any(m => !known.Contains(m.Window.Handle))) return false;
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
            var beforeObstacles = plannedDesktop.Where(w => !trace.Handles.Contains(w.Handle) &&
                w.MonitorHandle == group[0].Window.MonitorHandle).ToArray();
            var afterObstacles = refinedDesktop.Where(w => !trace.Handles.Contains(w.Handle) &&
                w.MonitorHandle == group[0].Window.MonitorHandle).ToArray();
            var c = new Candidate("refined", after, order);
            if (!TaskSafe(group, original, c, area, options, context, afterObstacles)) return false;
            for (var i = 0; i < group.Length; i++)
                for (var j = 0; j < afterObstacles.Length; j++)
                    if (after[i].Intersect(afterObstacles[j].VisualRect).Area >
                        before[i].Intersect(beforeObstacles[j].VisualRect).Area) return false;
            var task = InferTask(group, original, context);
            var hint = new IntentHint((int)Math.Round(8 * group[0].Window.Dpi / 96.0), 0, 0);
            if (TaskScore(TaskCost(group, original, c, task, area, context, hint, afterObstacles), options.SmartStrength, task) >
                TaskScore(TaskCost(group, original, new("planned", before, order), task, area, context, hint, beforeObstacles), options.SmartStrength, task) + .01) return false;
        }
        return true;
    }
}
