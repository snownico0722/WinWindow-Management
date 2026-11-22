namespace NeatWin.Core;

// 只分析同一客户区的三张现有截图。空间上的暗带和时间上的画面变化分别核验。
internal static class BrowserVideoFrameAnalysis
{
    internal const int FrameCount = 3;

    internal static DetectedBars? Analyze(FramePixels frame)
    {
        var horizontal = AnalyzeHorizontal(frame);
        var vertical = AnalyzeVertical(frame);
        if (horizontal is null) return vertical;
        if (vertical is null) return horizontal;
        var delta = horizontal.Confidence - vertical.Confidence;
        return Math.Abs(delta) < 0.08 ? null : delta > 0 ? horizontal : vertical;
    }

    internal static DetectedBars? StableBars(IReadOnlyList<DetectedBars> bars, RectI client) =>
        StableAcrossFrames(bars, client)
            ? new(bars[0].Orientation,
                MedianDouble(bars.Select(item => item.ContentAspectRatio).ToArray()),
                MedianRect(bars.Select(item => item.ViewportClientRect).ToArray()),
                MedianRect(bars.Select(item => item.PictureClientRect).ToArray()),
                bars.Min(item => item.Confidence))
            : null;

    internal static bool HasPictureMotion(FramePixels before, FramePixels after, DetectedBars bars)
    {
        var picture = Inset(bars.PictureClientRect, .08, .08, .08, .18);
        var viewport = bars.ViewportClientRect;
        var content = bars.PictureClientRect;
        // 留出边缘和底部控制区。真正的画面在多个内区有明显变化；文字光标、加载图标、
        // 弹幕或进度条这类局部变化，以及视口外的动画，不能单独证明播放器。
        int activeTiles = 0, rows = 0, columns = 0;
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 3; col++)
        {
            var tile = RectI.FromEdges(picture.Left + picture.Width * col / 3,
                picture.Top + picture.Height * row / 3,
                picture.Left + picture.Width * (col + 1) / 3,
                picture.Top + picture.Height * (row + 1) / 3);
            if (ChangedFraction(before, after, tile, pictureTexture: true) < .16) continue;
            activeTiles++;
            rows |= 1 << row;
            columns |= 1 << col;
        }
        // 变化必须横跨多个图片区域。调用方可以检查相邻两对，以容许一次重复的视频帧。
        if (activeTiles < 5 || (rows & (rows - 1)) == 0 || (columns & (columns - 1)) == 0) return false;

        RectI[] surround =
        [
            RectI.FromEdges(viewport.Left, viewport.Top, content.Left, viewport.Bottom),
            RectI.FromEdges(content.Right, viewport.Top, viewport.Right, viewport.Bottom),
            RectI.FromEdges(viewport.Left, viewport.Top, viewport.Right, content.Top),
            RectI.FromEdges(viewport.Left, content.Bottom, viewport.Right, viewport.Bottom),
            RectI.FromEdges(0, 0, viewport.Left, before.Height),
            RectI.FromEdges(viewport.Right, 0, before.Width, before.Height),
            RectI.FromEdges(viewport.Left, 0, viewport.Right, viewport.Top),
            RectI.FromEdges(viewport.Left, viewport.Bottom, viewport.Right, before.Height),
        ];
        // 暗带/页面外沿不能随着画面一起成片变化。容许零星字幕、图标和截屏噪声。
        return surround.All(rect => rect.Width < 8 || rect.Height < 8 ||
            ChangedFraction(before, after, Inset(rect, .1, .1, .1, .1)) <= .10) &&
            !LooksLikeScroll(before, after, picture);
    }

    internal static bool FillsViewport(FramePixels frame, RectI view)
    {
        if (view.IsEmpty || new RectI(0, 0, frame.Width, frame.Height).Intersect(view) != view) return false;
        // 有效缩放通常让暗带消失。需要在预计视口四边都读到画面细节，单纯未识别到暗带
        // 也可能是黑场、偶发覆盖或保护内容，不能作为换片时的对照。
        foreach (var fraction in new[] { .02, .08, .92, .98 })
        {
            int x = view.Left + (int)(view.Width * fraction);
            int y = view.Top + (int)(view.Height * fraction);
            var row = Measure(view.Left, view.Right, Math.Max(1, view.Width / 160), at => Luma(frame, at, y));
            var col = Measure(view.Top, view.Bottom, Math.Max(1, view.Height / 160), at => Luma(frame, x, at));
            if (row.ContrastLuma < 15 || col.ContrastLuma < 15 ||
                InteriorLineSignal(row) < .58 || InteriorLineSignal(col) < .58) return false;
        }
        return true;
    }

    private static RectI Inset(RectI rect, double left, double top, double right, double bottom) => RectI.FromEdges(
        rect.Left + (int)(rect.Width * left), rect.Top + (int)(rect.Height * top),
        rect.Right - (int)(rect.Width * right), rect.Bottom - (int)(rect.Height * bottom));

    private static double ChangedFraction(FramePixels before, FramePixels after, RectI rect, bool pictureTexture = false)
    {
        const int samples = 8;
        int changes = 0, textured = 0;
        for (int row = 0; row < samples; row++)
        for (int col = 0; col < samples; col++)
        {
            var x = rect.Left + rect.Width * (2 * col + 1) / (2 * samples);
            var y = rect.Top + rect.Height * (2 * row + 1) / (2 * samples);
            if (ColorDistance(before, after, x, y, x, y) < 24) continue;
            changes++;
            if (pictureTexture &&
                (Math.Abs(Luma(before, x, y) - Luma(before, x + 3, y)) >= 12 ||
                 Math.Abs(Luma(after, x, y) - Luma(after, x + 3, y)) >= 12 ||
                 Math.Abs(Luma(before, x, y) - Luma(before, x, y + 3)) >= 12 ||
                 Math.Abs(Luma(after, x, y) - Luma(after, x, y + 3)) >= 12)) textured++;
        }
        // 单色背景的刷新可以同时改变整页像素；变化区还需要拥有足够的局部图像细节。
        return pictureTexture && textured < 4 ? 0 : changes / (double)(samples * samples);
    }

    private static bool LooksLikeScroll(FramePixels before, FramePixels after, RectI picture)
    {
        // 网页滚动会让分散的文字同时移动，但区域内部仍是同一批像素的整体平移。
        // 检查同方向位移后变化点能否恢复原像素，忽略顶部/底部刚出现和刚消失的内容。
        var changed = new List<(int X, int Y)>();
        for (int row = 0; row < 9; row++)
        for (int col = 0; col < 9; col++)
        {
            int x = picture.Left + picture.Width * (2 * col + 1) / 18;
            int y = picture.Top + picture.Height * (2 * row + 1) / 18;
            if (ColorDistance(before, after, x, y, x, y) >= 24) changed.Add((x, y));
        }
        if (changed.Count < 10) return false;
        for (int axis = 0; axis < 2; axis++)
        {
            var limit = Math.Min(160, (axis == 0 ? picture.Width : picture.Height) / 4);
            for (int offset = -limit; offset <= limit; offset++)
            {
                if (offset == 0) continue;
                int valid = 0, restored = 0;
                foreach (var (x, y) in changed)
                {
                    int toX = x + (axis == 0 ? offset : 0);
                    int toY = y + (axis == 1 ? offset : 0);
                    if (toX < picture.Left || toX >= picture.Right || toY < picture.Top || toY >= picture.Bottom) continue;
                    valid++;
                    if (ColorDistance(before, after, x, y, toX, toY) <= 10) restored++;
                }
                if (valid >= changed.Count * .70 && restored >= valid * .88) return true;
            }
        }
        return false;
    }

    private static int ColorDistance(FramePixels before, FramePixels after, int x, int y, int toX, int toY)
    {
        var a = y * before.Stride + x * 4;
        var b = toY * after.Stride + toX * 4;
        return Math.Max(Math.Abs(before.Bytes[a] - after.Bytes[b]),
            Math.Max(Math.Abs(before.Bytes[a + 1] - after.Bytes[b + 1]),
                Math.Abs(before.Bytes[a + 2] - after.Bytes[b + 2])));
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
                    RectI.FromEdges(viewportLeft, top.End, viewportRight, bottom.Start),
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
                    RectI.FromEdges(left.End, viewportTop, right.Start, viewportBottom),
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
        var medianPicture = MedianRect(items.Select(item => item.PictureClientRect).ToArray());
        var medianAspect = MedianDouble(items.Select(item => item.ContentAspectRatio).ToArray());
        var tolerance = Math.Max(5, (int)Math.Round(Math.Min(clientRect.Width, clientRect.Height) * 0.018));

        return items.All(item =>
            Math.Abs(item.ViewportClientRect.Left - medianRect.Left) <= tolerance &&
            Math.Abs(item.ViewportClientRect.Top - medianRect.Top) <= tolerance &&
            Math.Abs(item.ViewportClientRect.Right - medianRect.Right) <= tolerance &&
            Math.Abs(item.ViewportClientRect.Bottom - medianRect.Bottom) <= tolerance &&
            Math.Abs(item.PictureClientRect.Left - medianPicture.Left) <= tolerance &&
            Math.Abs(item.PictureClientRect.Top - medianPicture.Top) <= tolerance &&
            Math.Abs(item.PictureClientRect.Right - medianPicture.Right) <= tolerance &&
            Math.Abs(item.PictureClientRect.Bottom - medianPicture.Bottom) <= tolerance &&
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

    internal sealed record DetectedBars(
        VideoBlackBarOrientation Orientation,
        double ContentAspectRatio,
        RectI ViewportClientRect,
        RectI PictureClientRect,
        double Confidence);

    internal sealed record FramePixels(int Width, int Height, int Stride, byte[] Bytes);
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
