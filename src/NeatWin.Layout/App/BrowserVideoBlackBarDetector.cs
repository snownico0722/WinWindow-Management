using System.Diagnostics;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.App;

/// <summary>
/// Conservative screen-space browser-video detector. It never injects into the browser. Subtitles,
/// danmaku and transient controls are treated as sparse bright noise, and three captures must agree
/// before a black-bar hint is emitted.
/// </summary>
internal sealed class BrowserVideoBlackBarDetector
{
    private const int FrameCount = 3;
    private const int FrameDelayMilliseconds = 28;

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "opera_gx", "arc",
    };

    internal VideoFitSession Fits { get; } = new();

    private (VideoBlackBarHint Hint, WindowSnapshot Window, VideoPageIdentity? Page, long Tick)? _lastDetection;

    internal VideoBlackBarHint? TryDetect(IReadOnlyList<VisibleWindow> visibleWindows, bool cachedOnly = false)
    {
        // The release path must never wait for three screen captures. A geometry-stable,
        // recently measured viewport may still protect a companion while the user releases.
        if (cachedOnly)
        {
            if (_lastDetection is { } last && Environment.TickCount64 - last.Tick <= 1500 &&
                visibleWindows.Any(v => v.Window.Handle == last.Window.Handle && v.Window.ProcessId == last.Window.ProcessId &&
                    v.Window.VisualRect == last.Window.VisualRect && v.Window.WorkArea == last.Window.WorkArea &&
                    v.Window.Dpi == last.Window.Dpi && v.VisibleRatio >= .72 &&
                    last.Page != null && ReadPage(v.Window) == last.Page)) return last.Hint;
            return null;
        }
        _lastDetection = null;
        var candidate = visibleWindows
            .Where(item => item.Window.IsManageable && item.Window.IsResizable && item.VisibleRatio >= 0.72)
            .Where(item => IsBrowserWindow(item.Window.Handle))
            .OrderByDescending(item => item.Window.IsForeground)
            .ThenBy(item => item.Window.ZOrder)
            .FirstOrDefault();

        if (candidate is null ||
            !TryGetClientScreenRect(candidate.Window.Handle, out var clientRect) ||
            clientRect.Width < 640 ||
            clientRect.Height < 360)
        {
            return null;
        }

        var workArea = candidate.Window.WorkArea;
        if (!workArea.IsEmpty &&
            (clientRect.Left < workArea.Left - 2 || clientRect.Top < workArea.Top - 2 ||
             clientRect.Right > workArea.Right + 2 || clientRect.Bottom > workArea.Bottom + 2))
        {
            return null;
        }

        var page = ReadPage(candidate.Window);
        var detections = new List<DetectedBars>(FrameCount);
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            if (NativeWindowActivity.IsMoving) return null;
            var detected = CaptureAndAnalyze(clientRect);
            if (detected is null)
            {
                return null;
            }

            detections.Add(detected);
            if (frameIndex + 1 < FrameCount)
            {
                Thread.Sleep(FrameDelayMilliseconds);
            }
        }

        if (!StableAcrossFrames(detections, clientRect))
        {
            return null;
        }

        var viewport = MedianRect(detections.Select(item => item.ViewportClientRect).ToArray());
        var aspect = MedianDouble(detections.Select(item => item.ContentAspectRatio).ToArray());
        var confidence = detections.Min(item => item.Confidence);
        var hint = new VideoBlackBarHint(
            candidate.Window.Handle,
            detections[0].Orientation,
            aspect,
            new RectI(
                clientRect.Left + viewport.Left,
                clientRect.Top + viewport.Top,
                viewport.Width,
                viewport.Height),
            confidence);
        if (page != ReadPage(candidate.Window)) return null;
        if (page != null && VideoViewportFit.From(candidate.Window.VisualRect, hint) is { } fit &&
            Fits.Repeat(candidate.Window, page, fit, Environment.TickCount64)) return null;
        _lastDetection = (hint, candidate.Window, page, Environment.TickCount64);
        return hint;
    }

    internal void RecordApplied(TidyMove move)
    {
        if (_lastDetection is { Page: { } page } last && last.Window.Handle == move.Window.Handle &&
            last.Window.ProcessId == move.Window.ProcessId && last.Window.VisualRect == move.Window.VisualRect &&
            VideoViewportFit.From(move.Window.VisualRect, last.Hint) is { } fit)
            Fits.Submitted(move.Window, move.TargetVisualRect, page, fit, Environment.TickCount64);
    }

    internal void Forget(nint hwnd)
    {
        Fits.Forget(hwnd);
        if (_lastDetection?.Window.Handle == hwnd) _lastDetection = null;
    }

    internal void Reset()
    {
        Fits.Clear();
        _lastDetection = null;
    }

    internal static VideoPageIdentity? ReadPage(WindowSnapshot window)
    {
        NativeMethods.GetWindowThreadProcessId(window.Handle, out uint pid);
        if (pid == 0 || pid != window.ProcessId) return null;
        var caption = new StringBuilder(2048);
        var cls = new StringBuilder(256);
        if (NativeMethods.GetWindowText(window.Handle, caption, caption.Capacity) <= 0 ||
            NativeMethods.GetClassName(window.Handle, cls, cls.Capacity) <= 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new(pid, process.StartTime.ToUniversalTime().Ticks, cls.ToString(), caption.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsBrowserWindow(nint hwnd)
    {
        try
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById((int)processId);
            return BrowserProcesses.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClientScreenRect(nint hwnd, out RectI rect)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var client))
        {
            rect = default;
            return false;
        }

        var topLeft = new NativeMethods.Point { X = client.Left, Y = client.Top };
        var bottomRight = new NativeMethods.Point { X = client.Right, Y = client.Bottom };
        if (!NativeMethods.ClientToScreen(hwnd, ref topLeft) ||
            !NativeMethods.ClientToScreen(hwnd, ref bottomRight))
        {
            rect = default;
            return false;
        }

        rect = RectI.FromEdges(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return !rect.IsEmpty;
    }

    private static DetectedBars? CaptureAndAnalyze(RectI clientRect)
    {
        try
        {
            using var bitmap = new Bitmap(clientRect.Width, clientRect.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    clientRect.Left,
                    clientRect.Top,
                    0,
                    0,
                    bitmap.Size,
                    CopyPixelOperation.SourceCopy);
            }

            var frame = CopyPixels(bitmap);
            var horizontal = AnalyzeHorizontal(frame);
            var vertical = AnalyzeVertical(frame);
            if (horizontal is null)
            {
                return vertical;
            }
            if (vertical is null)
            {
                return horizontal;
            }

            var delta = horizontal.Confidence - vertical.Confidence;
            return Math.Abs(delta) < 0.08 ? null : delta > 0 ? horizontal : vertical;
        }
        catch
        {
            // Protected video, HDR/display-path peculiarities and capture failures simply disable
            // this optional hint for the current tidy.
            return null;
        }
    }

    private static FramePixels CopyPixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new FramePixels(bitmap.Width, bitmap.Height, stride, bytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static DetectedBars? AnalyzeHorizontal(FramePixels frame)
    {
        var step = Math.Max(1, frame.Height / 320);
        var lines = new List<IndexedMetric>();
        for (var y = 0; y < frame.Height; y += step)
        {
            lines.Add(new IndexedMetric(y, MeasureRow(frame, y)));
        }

        var runs = FindDarkRuns(lines, step, frame.Height)
            .Where(run => run.Length >= Math.Max(8, frame.Height * 0.022))
            .Where(run => run.Length <= frame.Height * 0.24)
            .ToArray();

        DetectedBars? best = null;
        foreach (var top in runs.Where(run => run.End < frame.Height * 0.54))
        {
            foreach (var bottom in runs.Where(run => run.Start > frame.Height * 0.46))
            {
                if (bottom.Start <= top.End)
                {
                    continue;
                }

                var symmetry = Math.Abs(top.Length - bottom.Length) /
                               (double)Math.Max(top.Length, bottom.Length);
                if (symmetry > 0.32)
                {
                    continue;
                }

                var topSpan = LongestDarkishSpan(frame.Width, x => Luma(frame, x, (top.Start + top.End) / 2));
                var bottomSpan = LongestDarkishSpan(frame.Width, x => Luma(frame, x, (bottom.Start + bottom.End) / 2));
                var viewportLeft = Math.Max(topSpan.Start, bottomSpan.Start);
                var viewportRight = Math.Min(topSpan.End, bottomSpan.End);
                var viewportWidth = viewportRight - viewportLeft;
                var contentHeight = bottom.Start - top.End;
                var videoHeight = bottom.End - top.Start;

                if (viewportWidth < frame.Width * 0.68 ||
                    contentHeight <= 0 ||
                    videoHeight < frame.Height * 0.50)
                {
                    continue;
                }

                var barRatio = (top.Length + bottom.Length) / (double)videoHeight;
                if (barRatio is < 0.055 or > 0.36 ||
                    InteriorHorizontalSignal(frame, top.End, bottom.Start) < 0.58)
                {
                    continue;
                }

                var aspect = viewportWidth / (double)contentHeight;
                if (aspect is < 0.70 or > 4.20)
                {
                    continue;
                }

                var confidence = Math.Clamp(
                    0.85 + ((1 - symmetry) * 0.07) +
                    (Math.Min(1.0, viewportWidth / (double)frame.Width) * 0.04),
                    0,
                    0.97);
                var detected = new DetectedBars(
                    VideoBlackBarOrientation.Horizontal,
                    aspect,
                    RectI.FromEdges(viewportLeft, top.Start, viewportRight, bottom.End),
                    confidence);
                if (best is null || detected.Confidence > best.Confidence)
                {
                    best = detected;
                }
            }
        }

        return best;
    }

    private static DetectedBars? AnalyzeVertical(FramePixels frame)
    {
        var step = Math.Max(1, frame.Width / 360);
        var lines = new List<IndexedMetric>();
        for (var x = 0; x < frame.Width; x += step)
        {
            lines.Add(new IndexedMetric(x, MeasureColumn(frame, x)));
        }

        var runs = FindDarkRuns(lines, step, frame.Width)
            .Where(run => run.Length >= Math.Max(8, frame.Width * 0.022))
            .Where(run => run.Length <= frame.Width * 0.24)
            .ToArray();

        DetectedBars? best = null;
        foreach (var left in runs.Where(run => run.End < frame.Width * 0.54))
        {
            foreach (var right in runs.Where(run => run.Start > frame.Width * 0.46))
            {
                if (right.Start <= left.End)
                {
                    continue;
                }

                var symmetry = Math.Abs(left.Length - right.Length) /
                               (double)Math.Max(left.Length, right.Length);
                if (symmetry > 0.32)
                {
                    continue;
                }

                var leftSpan = LongestDarkishSpan(frame.Height, y => Luma(frame, (left.Start + left.End) / 2, y));
                var rightSpan = LongestDarkishSpan(frame.Height, y => Luma(frame, (right.Start + right.End) / 2, y));
                var viewportTop = Math.Max(leftSpan.Start, rightSpan.Start);
                var viewportBottom = Math.Min(leftSpan.End, rightSpan.End);
                var viewportHeight = viewportBottom - viewportTop;
                var contentWidth = right.Start - left.End;
                var videoWidth = right.End - left.Start;

                if (viewportHeight < frame.Height * 0.58 ||
                    contentWidth <= 0 ||
                    videoWidth < frame.Width * 0.50)
                {
                    continue;
                }

                var barRatio = (left.Length + right.Length) / (double)videoWidth;
                if (barRatio is < 0.055 or > 0.36 ||
                    InteriorVerticalSignal(frame, left.End, right.Start) < 0.58)
                {
                    continue;
                }

                var aspect = contentWidth / (double)viewportHeight;
                if (aspect is < 0.55 or > 3.40)
                {
                    continue;
                }

                var confidence = Math.Clamp(
                    0.84 + ((1 - symmetry) * 0.07) +
                    (Math.Min(1.0, viewportHeight / (double)frame.Height) * 0.04),
                    0,
                    0.96);
                var detected = new DetectedBars(
                    VideoBlackBarOrientation.Vertical,
                    aspect,
                    RectI.FromEdges(left.Start, viewportTop, right.End, viewportBottom),
                    confidence);
                if (best is null || detected.Confidence > best.Confidence)
                {
                    best = detected;
                }
            }
        }

        return best;
    }

    private static IReadOnlyList<DarkRun> FindDarkRuns(
        IReadOnlyList<IndexedMetric> lines,
        int lineStep,
        int referenceSize)
    {
        var result = new List<DarkRun>();
        var gapTolerance = Math.Max(lineStep * 2, (int)Math.Round(referenceSize * 0.012));
        int? start = null;
        var lastDark = 0;

        foreach (var line in lines)
        {
            if (IsOverlayTolerantDark(line.Metric))
            {
                start ??= line.Index;
                lastDark = line.Index;
                continue;
            }

            if (start.HasValue && line.Index - lastDark <= gapTolerance)
            {
                // Subtitle/danmaku glyphs can temporarily brighten a few scan lines. Keep the
                // surrounding dark run intact instead of interpreting the text as video content.
                continue;
            }

            if (start is int completedStart)
            {
                result.Add(new DarkRun(completedStart, Math.Min(referenceSize, lastDark + lineStep)));
                start = null;
            }
        }

        if (start is int finalStart)
        {
            result.Add(new DarkRun(finalStart, Math.Min(referenceSize, lastDark + lineStep)));
        }

        return result;
    }

    private static bool IsOverlayTolerantDark(LineMetric metric) =>
        metric.DarkFraction >= 0.80 &&
        metric.MedianLuma <= 32 &&
        metric.BrightFraction <= 0.18;

    private static LineMetric MeasureRow(FramePixels frame, int y)
    {
        var start = (int)Math.Round(frame.Width * 0.035);
        var end = Math.Max(start + 1, (int)Math.Round(frame.Width * 0.965));
        return Measure(start, end, Math.Max(1, (end - start) / 360), x => Luma(frame, x, y));
    }

    private static LineMetric MeasureColumn(FramePixels frame, int x)
    {
        var start = (int)Math.Round(frame.Height * 0.025);
        var end = Math.Max(start + 1, (int)Math.Round(frame.Height * 0.975));
        return Measure(start, end, Math.Max(1, (end - start) / 300), y => Luma(frame, x, y));
    }

    private static LineMetric Measure(int start, int end, int step, Func<int, int> lumaAt)
    {
        var histogram = new int[256];
        var dark = 0;
        var bright = 0;
        var count = 0;
        for (var coordinate = start; coordinate < end; coordinate += step)
        {
            var luma = lumaAt(coordinate);
            histogram[luma]++;
            count++;
            if (luma <= 44)
            {
                dark++;
            }
            if (luma >= 110)
            {
                bright++;
            }
        }

        if (count == 0)
        {
            return default;
        }

        var median = PercentileFromHistogram(histogram, count, 0.50);
        var p10 = PercentileFromHistogram(histogram, count, 0.10);
        var p90 = PercentileFromHistogram(histogram, count, 0.90);

        return new LineMetric(
            dark / (double)count,
            bright / (double)count,
            median,
            Math.Max(0, p90 - p10));
    }

    private static int PercentileFromHistogram(int[] histogram, int count, double percentile)
    {
        var target = Math.Clamp((int)Math.Ceiling(count * percentile), 1, count);
        var cumulative = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
            {
                return value;
            }
        }

        return histogram.Length - 1;
    }

    private static SpanRange LongestDarkishSpan(int length, Func<int, int> lumaAt)
    {
        var step = Math.Max(1, length / 520);
        var gapTolerance = Math.Max(3, (int)Math.Round(length * 0.030));
        var best = new SpanRange(0, 0);
        int? start = null;
        var lastDark = 0;

        for (var coordinate = 0; coordinate < length; coordinate += step)
        {
            if (lumaAt(coordinate) <= 50)
            {
                start ??= coordinate;
                lastDark = coordinate;
                continue;
            }

            if (start.HasValue && coordinate - lastDark <= gapTolerance)
            {
                // Short bright gaps are usually text/control overlays inside a black strip.
                continue;
            }

            if (start is int runStart && lastDark - runStart > best.Length)
            {
                best = new SpanRange(runStart, Math.Min(length, lastDark + step));
            }
            start = null;
        }

        if (start is int finalStart && lastDark - finalStart > best.Length)
        {
            best = new SpanRange(finalStart, Math.Min(length, lastDark + step));
        }

        return best;
    }

    private static double InteriorHorizontalSignal(FramePixels frame, int top, int bottom)
    {
        var total = 0.0;
        foreach (var fraction in new[] { 0.22, 0.38, 0.50, 0.62, 0.78 })
        {
            var y = Math.Clamp(top + (int)Math.Round((bottom - top) * fraction), 0, frame.Height - 1);
            total += InteriorLineSignal(MeasureRow(frame, y));
        }
        return total / 5.0;
    }

    private static double InteriorVerticalSignal(FramePixels frame, int left, int right)
    {
        var total = 0.0;
        foreach (var fraction in new[] { 0.22, 0.38, 0.50, 0.62, 0.78 })
        {
            var x = Math.Clamp(left + (int)Math.Round((right - left) * fraction), 0, frame.Width - 1);
            total += InteriorLineSignal(MeasureColumn(frame, x));
        }
        return total / 5.0;
    }

    private static double InteriorLineSignal(LineMetric metric)
    {
        var brightnessSignal = Math.Clamp(
            (1 - metric.DarkFraction) + (metric.BrightFraction * 0.6),
            0,
            1);

        // Dark movie scenes can still contain strong spatial information. Use the robust p90-p10
        // luma spread as a second signal, so a textured dark frame is not mistaken for a blank or
        // protected capture. Pure black/near-uniform frames still have almost no contrast and fail.
        var contrastSignal = Math.Clamp((metric.ContrastLuma - 8) / 48.0, 0, 1) * 0.92;
        return Math.Max(brightnessSignal, contrastSignal);
    }

    private static int Luma(FramePixels frame, int x, int y)
    {
        x = Math.Clamp(x, 0, frame.Width - 1);
        y = Math.Clamp(y, 0, frame.Height - 1);
        var offset = (y * frame.Stride) + (x * 4);
        var b = frame.Bytes[offset];
        var g = frame.Bytes[offset + 1];
        var r = frame.Bytes[offset + 2];
        return (77 * r + 150 * g + 29 * b) >> 8;
    }

    private static bool StableAcrossFrames(IReadOnlyList<DetectedBars> items, RectI clientRect)
    {
        if (items.Count != FrameCount || items.Any(item => item.Orientation != items[0].Orientation))
        {
            return false;
        }

        var medianRect = MedianRect(items.Select(item => item.ViewportClientRect).ToArray());
        var medianAspect = MedianDouble(items.Select(item => item.ContentAspectRatio).ToArray());
        var tolerance = Math.Max(5, (int)Math.Round(Math.Min(clientRect.Width, clientRect.Height) * 0.018));

        return items.All(item =>
            Math.Abs(item.ViewportClientRect.Left - medianRect.Left) <= tolerance &&
            Math.Abs(item.ViewportClientRect.Top - medianRect.Top) <= tolerance &&
            Math.Abs(item.ViewportClientRect.Right - medianRect.Right) <= tolerance &&
            Math.Abs(item.ViewportClientRect.Bottom - medianRect.Bottom) <= tolerance &&
            Math.Abs(item.ContentAspectRatio - medianAspect) / medianAspect <= 0.035);
    }

    private static RectI MedianRect(IReadOnlyList<RectI> rects) => RectI.FromEdges(
        MedianInt(rects.Select(rect => rect.Left).ToArray()),
        MedianInt(rects.Select(rect => rect.Top).ToArray()),
        MedianInt(rects.Select(rect => rect.Right).ToArray()),
        MedianInt(rects.Select(rect => rect.Bottom).ToArray()));

    private static int MedianInt(int[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static double MedianDouble(double[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private sealed record DetectedBars(
        VideoBlackBarOrientation Orientation,
        double ContentAspectRatio,
        RectI ViewportClientRect,
        double Confidence);

    private sealed record FramePixels(int Width, int Height, int Stride, byte[] Bytes);
    private readonly record struct LineMetric(
        double DarkFraction,
        double BrightFraction,
        int MedianLuma,
        int ContrastLuma);
    private readonly record struct IndexedMetric(int Index, LineMetric Metric);
    private readonly record struct DarkRun(int Start, int End)
    {
        internal int Length => End - Start;
    }
    private readonly record struct SpanRange(int Start, int End)
    {
        internal int Length => End - Start;
    }
}
