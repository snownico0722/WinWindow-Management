namespace NeatWin.Core;

// A single endpoint inferred from a completed gesture. Compute it from the raw release frame;
// a later observation of the assisted frame cannot trigger another increment of growth.
internal static class GesturePlacement
{
    internal static TaskLayoutContext Resolve(TaskLayoutContext context, IReadOnlyList<VisibleWindow> visible,
        TidyOptions options, IntentReferenceDocument? reference, GesturePreview? preview = null)
    {
        if (!context.AllowGestureAssist) return context.GestureTarget.HasValue ? context with { GestureTarget = null } : context;
        if (context.ExplicitArrange || context.GestureAnchor == 0 || context.GestureTarget.HasValue ||
            context.RecentGesture is not { } gesture || gesture.WindowHandle != context.GestureAnchor ||
            visible.FirstOrDefault(v => v.Window.Handle == gesture.WindowHandle) is not { } moved ||
            moved.Window.VisualRect != gesture.EndRect || moved.Window.WorkArea != gesture.WorkArea ||
            context.WindowHints?.Any(h => h.Handle == gesture.WindowHandle && h.Anchor) == true) return context;
        var window = moved.Window;
        var a = gesture.EndRect;
        var area = gesture.WorkArea;
        if (gesture.StartRect.IsEmpty || a.IsEmpty || area.IsEmpty || a.Width > area.Width || a.Height > area.Height) return context;
        var scale = window.Dpi == 0 ? 1 : Math.Max(.5, window.Dpi / 96d);
        if (gesture.EndSpeedPixelsPerSecond > 2400 * scale) return context;

        double dx = a.Left - gesture.StartRect.Left;
        double dy = a.Top - gesture.StartRect.Top;
        var travel = Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Max(
            Math.Abs(a.Right - gesture.StartRect.Right), Math.Abs(a.Bottom - gesture.StartRect.Bottom)));
        if (travel < 24 * scale) return context;
        var (left, right, center) = preview?.Exact(gesture) == true ? preview.Directions() : Directions(gesture, scale);
        int centerX = area.Left + area.Width / 2;
        RectI target;
        if (left || right || center)
        {
            int width = a.Width, height = a.Height;
            if ((left || right) && Math.Abs(dx) >= Math.Max(96 * scale, area.Width * .10) &&
                context.Focus is { Companion: 0 } focus && focus.Main == window.Handle && context.UseWorkingPairs &&
                window.Owner == 0 && !window.IsTopmost && context.WindowHints?.Any(h => h.Handle == window.Handle &&
                    (h.PassiveVisual || h.FloatingAffinity >= .5 || WindowUseRoles.IsFloating(h.Role))) != true &&
                focus.Evidence is not ("handoff" or "navigation-return") && focus.NavigationAncestors is not { Length: > 0 } &&
                context.PairHints?.Any(h => h.Relation != TaskRelation.Automatic) != true && context.RulePairs is not { Length: > 0 })
            {
                // The release supplies task evidence for this solve only. Ignore utility strips
                // and require an unambiguous second reading surface on the opposite side.
                var peers = visible.Where(v => v.Window.Handle != window.Handle && v.Window.MonitorHandle == window.MonitorHandle &&
                    v.Window.WorkArea == area && v.Window.IsManageable && !v.Window.IsTopmost && v.Window.Owner == 0 &&
                    v.VisibleRatio >= .45 && v.LargestVisibleFragmentArea >= area.Area * .08 &&
                    context.WindowHints?.Any(h => h.Handle == v.Window.Handle && (h.Anchor || h.PassiveVisual || h.MediaCompanion ||
                        h.FloatingAffinity >= .5 || WindowUseRoles.IsFloating(h.Role))) != true).Take(2).ToArray();
                if (peers is [var peer] && a.VerticalOverlapRatio(peer.Window.VisualRect) >= .5 &&
                    (left ? peer.Window.VisualRect.Left + peer.Window.VisualRect.Width / 2 > centerX :
                        peer.Window.VisualRect.Left + peer.Window.VisualRect.Width / 2 < centerX))
                    context = context with { Focus = focus with { Companion = peer.Window.Handle, Evidence = "gesture-side-pair" } };
            }
            var adjacent = visible.Where(v => v.Window.Handle != window.Handle && v.Window.MonitorHandle == window.MonitorHandle &&
                (context.Focus?.Companion == v.Window.Handle || context.HasRecentInput(v.Window.Handle)))
                .Any(v => a.VerticalOverlapRatio(v.Window.VisualRect) >= .5);
            if ((center || adjacent) && window.IsResizable && context.Preferences.AllowUsefulResize &&
                context.VideoHint?.WindowHandle != window.Handle &&
                context.WindowHints?.Any(h => h.Handle == window.Handle && WindowUseRoles.IsFloating(h.Role)) != true)
            {
                // Longer deliberate approaches carry more evidence of a working position.
                // A side companion still needs a column of its own. A central reading surface
                // can use more of the display without becoming effectively maximized.
                double approach = Math.Clamp((Math.Abs(dx) / area.Width - .06) / .18, 0, 1);
                int Grow(int size, double comfort, double share, int span)
                {
                    double readingSize = Math.Min(comfort * scale, span * share);
                    double increment = Math.Min(size * .12, Math.Max(0, readingSize - size));
                    return size + (int)Math.Round(increment + approach * Math.Max(0, readingSize - size - increment));
                }
                var hint = context.WindowHints?.FirstOrDefault(h => h.Handle == window.Handle);
                var habit = context.Placement?.FirstOrDefault(p => p.Window == window.Handle && p.Confidence >= .40);
                double desiredWidth = hint?.UsefulWidthDip is > 0 and <= 4000 ? hint.UsefulWidthDip.Value :
                    habit is { PreferWidth: true, Width: > 0 and <= 1 } ? habit.Width * area.Width / scale : context.Preferences.ComfortableWidthDip;
                double desiredHeight = hint?.UsefulHeightDip is > 0 and <= 3000 ? hint.UsefulHeightDip.Value :
                    habit?.Height is > 0 and <= 1 ? habit.Height.Value * area.Height / scale : context.Preferences.ComfortableHeightDip;
                width = Grow(width, desiredWidth, center ? .72 : .50, area.Width);
                height = Grow(height, desiredHeight, .74, area.Height);
            }
            int x = left ? area.Left : right ? area.Right - width : centerX - width / 2;
            target = new(x, Math.Clamp(a.Top + (a.Height - height) / 2, area.Top, area.Bottom - height), width, height);
        }
        else
        {
            // Follow-hand can slide the released frame into alignment while keeping its new size.
            var neighborhood = visible.Where(v => v.Window.MonitorHandle == window.MonitorHandle &&
                (v.Window.Handle == window.Handle || v.Window.Handle == context.Focus?.Companion || context.HasRecentInput(v.Window.Handle))).ToArray();
            var hint = IntentEvidence.Resolve(reference, area, window.Dpi == 0 ? 96u : window.Dpi, DateTimeOffset.UtcNow);
            var snap = FollowHandAssistant.Decide(gesture, neighborhood, options, SmartInteractionContext.Empty,
                SmartPersonalizationState.Default with { PreferredGapPixels = hint.GapPixels }, preview);
            if (!snap.ShouldApply || gesture.Kind == ManualGestureKind.Move &&
                snap.TargetKind == FollowHandTargetKind.ScreenEdge) return context;
            target = snap.TargetRect;
        }
        if (target == a || target.Intersect(area) != target) return context;
        return context with { GestureTarget = target };
    }

    internal static (bool Left, bool Right, bool Center) Directions(ManualWindowGesture gesture, double scale)
    {
        var a = gesture.EndRect;
        var area = gesture.WorkArea;
        double dx = a.Left - gesture.StartRect.Left, dy = a.Top - gesture.StartRect.Top;
        double close = Math.Min(area.Width * .045, 64 * scale);
        double approach = Math.Clamp((Math.Abs(dx) - Math.Abs(dy) - close) / (area.Width * .18), 0, 1);
        double sideZone = close + approach * Math.Max(0, Math.Min(area.Width * .095, 180 * scale) - close);
        int centerX = area.Left + area.Width / 2, endCenter = a.Left + a.Width / 2;
        bool horizontal = gesture.Kind == ManualGestureKind.Move && Math.Abs(dx) >= Math.Max(24 * scale, Math.Abs(dy) * .45);
        bool left = horizontal && dx < 0 && Math.Abs(a.Left - area.Left) <= sideZone;
        bool right = horizontal && dx > 0 && Math.Abs(area.Right - a.Right) <= sideZone;
        bool center = horizontal && Math.Abs(endCenter - centerX) <= close &&
            Math.Abs(gesture.StartRect.Left + a.Width / 2 - centerX) - Math.Abs(endCenter - centerX) >= 20 * scale;
        return (left, right, center);
    }
}
