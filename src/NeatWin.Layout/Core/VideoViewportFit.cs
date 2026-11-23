namespace NeatWin.Core;

// 浏览器外框包括标签栏、工具栏和页面留白。记住相对视口的四条边，换位置或分栏时才能比较同一块画面。
public sealed record VideoViewportFit(double Aspect, FrameInsets Insets)
{
    internal static VideoViewportFit? From(RectI frame, VideoBlackBarHint hint)
    {
        var view = hint.ViewportVisualRect;
        if (hint.Confidence is not (>= .82 and <= 1) || hint.ContentAspectRatio is not (>= .55 and <= 4.5) ||
            view.IsEmpty || frame.Intersect(view) != view) return null;
        return new(hint.ContentAspectRatio, new(view.Left - frame.Left, view.Top - frame.Top,
            frame.Right - view.Right, frame.Bottom - view.Bottom));
    }

    internal bool IsValid => Aspect is >= .55 and <= 4.5 && Insets.Left >= 0 && Insets.Right >= 0 && Insets.Top >= 0 && Insets.Bottom >= 0 &&
        (long)Insets.Left + Insets.Right < int.MaxValue && (long)Insets.Top + Insets.Bottom < int.MaxValue;

    private int MarginX => Insets.Left + Insets.Right;
    private int MarginY => Insets.Top + Insets.Bottom;

    internal bool Fits(RectI frame)
    {
        int width = frame.Width - MarginX, height = frame.Height - MarginY;
        // 容许原生边框的像素取整，以及尺寸下限导致的少量剩余黑边。沿用视频尺寸求解的相同比例容差。
        return width > 0 && height > 0 && Math.Abs(width / (double)height / Aspect - 1) <= .035;
    }

    internal bool Preserves(RectI original, RectI target)
    {
        int width = target.Width - MarginX, height = target.Height - MarginY;
        if (width <= 0 || height <= 0) return false;
        // 已经无边时，允许约一个像素的离散误差。3.5% 是初次受尺寸下限影响时的接纳范围，不能让每次分栏再长出同样厚的新黑边。
        double prior = Math.Abs((original.Width - MarginX) / (double)Math.Max(1, original.Height - MarginY) - Aspect);
        return Math.Abs(width / (double)height - Aspect) <= Math.Max(prior, Math.Max(1, Aspect) / height);
    }

    internal RectI Inside(RectI slot, RectI original)
    {
        int width = slot.Width - MarginX, height = slot.Height - MarginY;
        if (width <= 0 || height <= 0) return original;
        int w = (int)Math.Round(Math.Min(width, height * Aspect)) + MarginX;
        int h = (int)Math.Round(Math.Min(height, width / Aspect)) + MarginY;
        // 分栏可以给出新的上限。在这个槽内尽量保住原来的屏幕坐标，避免收掉黑边又额外移动观看中心。
        return new(Math.Clamp(original.X, slot.X, slot.Right - w), Math.Clamp(original.Y, slot.Y, slot.Bottom - h), w, h);
    }

    internal bool SameMeasurement(VideoViewportFit other) =>
        Math.Abs(Aspect / other.Aspect - 1) <= .035 &&
        Math.Abs(Insets.Left - other.Insets.Left) <= 12 && Math.Abs(Insets.Right - other.Insets.Right) <= 12 &&
        Math.Abs(Insets.Top - other.Insets.Top) <= 12 && Math.Abs(Insets.Bottom - other.Insets.Bottom) <= 12;
}

internal sealed record VideoPageIdentity(uint Pid, long Started, string Class, string Caption, string? PageKey = null);

// 只保留本次运行中确实提交过的尺寸。下一次读到相符的真实外框后才能作为求解依据，窗口标题不进入记录。
internal sealed class VideoFitSession
{
    private sealed record Entry(VideoPageIdentity Page, WindowSnapshot Window, RectI Expected,
        VideoViewportFit Fit, long SubmittedAt, bool ReachedExpected = false, RectI? SampledAt = null,
        VideoViewportFit? Baseline = null, VideoViewportFit? Transition = null, bool Unbarred = false);
    private readonly Dictionary<nint, Entry> _entries = [];

    internal void Forget(nint handle) => _entries.Remove(handle);
    internal void Clear() => _entries.Clear();

    internal void Submitted(WindowSnapshot window, RectI target, VideoPageIdentity page, VideoViewportFit fit, long tick)
    {
        if (fit.Fits(target)) _entries[window.Handle] = new(page, window, target, fit, tick);
    }

    internal void Follow(IReadOnlyList<TidyMove> moves, long tick)
    {
        foreach (var move in moves)
        {
            if (!_entries.TryGetValue(move.Window.Handle, out var entry) ||
                SameSize(move.Window.VisualRect, move.TargetVisualRect)) continue;
            if (entry.ReachedExpected && entry.Fit.Fits(move.TargetVisualRect))
                _entries[move.Window.Handle] = new(entry.Page, move.Window, move.TargetVisualRect, entry.Fit, tick);
            else Forget(move.Window.Handle);
        }
    }

    internal bool AwaitingGeometry(IReadOnlyList<WindowSnapshot> desktop, Func<WindowSnapshot, VideoPageIdentity?> pageOf, long tick)
    {
        // 异步提交后短等一个普通调度周期。外框已到位、窗口实例已变，或等待过期，就不延迟开窗。
        foreach (var (handle, entry) in _entries)
        {
            if (entry.ReachedExpected || tick - entry.SubmittedAt >= AutomaticLayoutScheduler.SettleMilliseconds) continue;
            var w = desktop.FirstOrDefault(w => w.Handle == handle);
            if (w != null && !SameSize(w.VisualRect, entry.Expected) &&
                CouldBeNativeClamp(entry, w.VisualRect) && SameSurface(entry, w, pageOf(w))) return true;
        }
        return false;
    }

    internal RectI? SettledViewport(WindowSnapshot window, VideoPageIdentity page, long tick)
    {
        if (!_entries.TryGetValue(window.Handle, out var entry) ||
            !SameSurface(entry, window, page) || !SameSize(window.VisualRect, entry.Expected) ||
            tick - entry.SubmittedAt < AutomaticLayoutScheduler.SettleMilliseconds) return null;
        var rect = window.VisualRect;
        var insets = entry.Fit.Insets;
        return RectI.FromEdges(rect.Left + insets.Left, rect.Top + insets.Top,
            rect.Right - insets.Right, rect.Bottom - insets.Bottom);
    }

    internal void ObserveUnbarred(WindowSnapshot window, VideoPageIdentity page, long tick)
    {
        if (SettledViewport(window, page, tick) != null)
            _entries[window.Handle] = _entries[window.Handle] with
            {
                ReachedExpected = true, SampledAt = window.VisualRect, Unbarred = true,
                Baseline = null, Transition = null,
            };
    }

    internal bool Repeat(WindowSnapshot window, VideoPageIdentity page, VideoViewportFit fresh, long tick)
    {
        if (!_entries.TryGetValue(window.Handle, out var entry)) return false;
        bool landed = SameSize(window.VisualRect, entry.Expected);
        if (!SameSurface(entry, window, page) || (!landed && !CouldBeNativeClamp(entry, window.VisualRect)))
        {
            Forget(window.Handle);
            return false;
        }
        if (!landed && tick - entry.SubmittedAt < AutomaticLayoutScheduler.SettleMilliseconds) return true;
        entry = entry with { ReachedExpected = landed };
        if (entry.SampledAt is not { } old || !SameSize(old, window.VisualRect))
            entry = entry with { SampledAt = window.VisualRect, Baseline = null, Transition = null, Unbarred = false };

        // 已看到原视口在同尺寸下填满。暗带重新出现且已通过三帧画面验证，足以确认内容变化。
        if (entry.Unbarred)
        {
            Forget(window.Handle);
            return false;
        }

        // 首次落地后的测量只建立同尺寸基线。浏览器重新排版也会改变暗带比例和四边留白，
        // 不能把与提交前的差异直接解释为换片。只比较在落地尺寸上独立通过画面验证的测量。
        if (entry.Baseline is not { } baseline || baseline.SameMeasurement(fresh))
        {
            _entries[window.Handle] = entry with { Baseline = entry.Baseline ?? fresh, Transition = null };
            return true;
        }
        // 同标题播放器也能换片或切模式。要求在同一个外框下再次看到新的稳定视口，
        // 排除一次转场、浏览器工具栏或测量波动，保留暂停的视频已验证过的比例。
        if (entry.Transition is not { } pending || !pending.SameMeasurement(fresh))
        {
            _entries[window.Handle] = entry with { Transition = fresh };
            return true;
        }
        Forget(window.Handle);
        return false;
    }

    internal Dictionary<nint, VideoViewportFit>? Read(IReadOnlyList<WindowSnapshot> desktop, Func<WindowSnapshot, VideoPageIdentity?> pageOf)
    {
        if (_entries.Count == 0) return null;
        var fits = new Dictionary<nint, VideoViewportFit>();
        foreach (var (handle, entry) in _entries.ToArray())
        {
            var window = desktop.FirstOrDefault(w => w.Handle == handle);
            if (window == null || !SameSurface(entry, window, pageOf(window)))
            {
                Forget(handle);
            }
            else if (SameSize(window.VisualRect, entry.Expected))
            {
                if (!entry.ReachedExpected) _entries[handle] = entry with { ReachedExpected = true };
                fits.Add(handle, entry.Fit);
            }
            // 原生最小尺寸有时只实现一部分缩放，甚至整次拒绝。记住这次尝试避免反复提交，
            // 但实际外框没有到达目标就不能给后续普通布局提供已拟合的比例。
            else if (!CouldBeNativeClamp(entry, window.VisualRect))
            {
                Forget(handle);
            }
        }
        return fits.Count == 0 ? null : fits;
    }

    private static bool CouldBeNativeClamp(Entry entry, RectI current) =>
        !entry.ReachedExpected &&
        Between(current.Width, entry.Window.VisualRect.Width, entry.Expected.Width) &&
        Between(current.Height, entry.Window.VisualRect.Height, entry.Expected.Height);

    private static bool Between(int actual, int before, int target) =>
        actual >= (long)Math.Min(before, target) - 3 && actual <= (long)Math.Max(before, target) + 3;

    private static bool SameSize(RectI first, RectI second) =>
        Math.Abs((long)first.Width - second.Width) <= 3 && Math.Abs((long)first.Height - second.Height) <= 3;

    private static bool SameSurface(Entry entry, WindowSnapshot window, VideoPageIdentity? page) =>
        page == entry.Page && window.ProcessId == entry.Window.ProcessId && window.MonitorHandle == entry.Window.MonitorHandle &&
        window.WorkArea == entry.Window.WorkArea && window.Dpi == entry.Window.Dpi && window.SurfaceState == entry.Window.SurfaceState;
}
