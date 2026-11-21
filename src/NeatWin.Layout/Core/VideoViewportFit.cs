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

internal sealed record VideoPageIdentity(uint Pid, long Started, string Class, string Caption);

// 只保留本次运行中确实提交过的尺寸。下一次读到相符的真实外框后才能作为求解依据，窗口标题不进入记录。
internal sealed class VideoFitSession
{
    private sealed record Entry(VideoPageIdentity Page, WindowSnapshot Window, RectI Expected,
        VideoViewportFit Fit, long MeasuredAt, long SubmittedAt);
    private readonly Dictionary<nint, Entry> _entries = [];

    internal void Forget(nint handle) => _entries.Remove(handle);
    internal void Clear() => _entries.Clear();

    internal void Submitted(WindowSnapshot window, RectI target, VideoPageIdentity page, VideoViewportFit fit, long tick)
    {
        if (fit.Fits(target)) _entries[window.Handle] = new(page, window, target, fit, tick, tick);
    }

    internal void Follow(IReadOnlyList<TidyMove> moves, long tick)
    {
        foreach (var move in moves)
            if (_entries.TryGetValue(move.Window.Handle, out var entry))
            {
                if (entry.Fit.Fits(move.TargetVisualRect))
                    _entries[move.Window.Handle] = entry with { Window = move.Window, Expected = move.TargetVisualRect, SubmittedAt = tick };
                else Forget(move.Window.Handle);
            }
    }

    internal bool AwaitingGeometry(IReadOnlyList<WindowSnapshot> desktop, Func<WindowSnapshot, VideoPageIdentity?> pageOf, long tick)
    {
        // Win32 异步提交后，下一次开窗可能采到旧外框。仅在同一实例仍完整停在提交前尺寸时等一个普通调度周期，
        // 避免覆盖正在落地的请求。看到目标尺寸立即放行，用户手势和显式整理也不等待。
        foreach (var (handle, entry) in _entries)
        {
            if (tick - entry.SubmittedAt >= AutomaticLayoutScheduler.SettleMilliseconds) continue;
            var w = desktop.FirstOrDefault(w => w.Handle == handle);
            if (w != null && !SameSize(w.VisualRect, entry.Expected) && SameSize(w.VisualRect, entry.Window.VisualRect) &&
                SameSurface(entry, w, pageOf(w))) return true;
        }
        return false;
    }

    internal bool Repeat(WindowSnapshot window, VideoPageIdentity page, VideoViewportFit fresh, long tick)
    {
        if (!_entries.TryGetValue(window.Handle, out var entry)) return false;
        if (Matches(entry, window, page) && entry.Fit.SameMeasurement(fresh))
            return tick - entry.MeasuredAt <= 20_000;
        // 相同 HWND 可以换标签、换片或切播放器模式。新的三帧测量与旧视口冲突时立即允许纠正。
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
            if (window != null && Matches(entry, window, pageOf(window))) fits.Add(handle, entry.Fit);
            else Forget(handle);
        }
        return fits.Count == 0 ? null : fits;
    }

    private static bool Matches(Entry entry, WindowSnapshot window, VideoPageIdentity? page) =>
        SameSize(window.VisualRect, entry.Expected) && SameSurface(entry, window, page);

    private static bool SameSize(RectI first, RectI second) =>
        Math.Abs((long)first.Width - second.Width) <= 3 && Math.Abs((long)first.Height - second.Height) <= 3;

    private static bool SameSurface(Entry entry, WindowSnapshot window, VideoPageIdentity? page) =>
        page == entry.Page && window.ProcessId == entry.Window.ProcessId && window.MonitorHandle == entry.Window.MonitorHandle &&
        window.WorkArea == entry.Window.WorkArea && window.Dpi == entry.Window.Dpi && window.SurfaceState == entry.Window.SurfaceState;
}
