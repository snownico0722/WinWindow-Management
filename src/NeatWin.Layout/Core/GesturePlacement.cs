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
            var adjacent = visible.Where(v => v.Window.Handle != window.Handle && v.Window.MonitorHandle == window.MonitorHandle &&
                (context.Focus?.Companion == v.Window.Handle || context.HasRecentInput(v.Window.Handle)))
                .Any(v => a.VerticalOverlapRatio(v.Window.VisualRect) >= .5);
            if ((center || adjacent) && window.IsResizable && context.Preferences.AllowUsefulResize &&
                context.VideoHint?.WindowHandle != window.Handle &&
                context.WindowHints?.Any(h => h.Handle == window.Handle && WindowUseRoles.IsFloating(h.Role)) != true)
            {
                int Grow(int size, double comfort, int limit) => Math.Min(limit, size + Math.Clamp(
                    (int)Math.Round(comfort * scale) - size, 0, (int)Math.Round(size * .12)));
                width = Grow(width, context.Preferences.ComfortableWidthDip, area.Width);
                height = Grow(height, context.Preferences.ComfortableHeightDip, area.Height);
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
        int sideZone = (int)Math.Round(Math.Min(area.Width * .045, 64 * scale));
        int centerX = area.Left + area.Width / 2, endCenter = a.Left + a.Width / 2;
        bool horizontal = gesture.Kind == ManualGestureKind.Move && Math.Abs(dx) >= Math.Max(24 * scale, Math.Abs(dy) * .45);
        bool left = horizontal && dx < 0 && Math.Abs(a.Left - area.Left) <= sideZone;
        bool right = horizontal && dx > 0 && Math.Abs(area.Right - a.Right) <= sideZone;
        bool center = horizontal && Math.Abs(endCenter - centerX) <= sideZone &&
            Math.Abs(gesture.StartRect.Left + a.Width / 2 - centerX) - Math.Abs(endCenter - centerX) >= 20 * scale;
        return (left, right, center);
    }
}
