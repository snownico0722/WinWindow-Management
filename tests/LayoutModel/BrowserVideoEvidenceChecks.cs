using NeatWin.Core;
using static NeatWin.Core.BrowserVideoFrameAnalysis;

internal static class BrowserVideoEvidenceChecks
{
    private const int Width = 1100, Height = 780;
    private static readonly RectI SideBarViewport = new(0, 88, Width, 692);
    private static readonly RectI SideBarPicture = new(100, 88, 900, 692);

    internal static void Run(Action<bool, string> check)
    {
        var client = new RectI(-1190, 200, Width, Height);
        var staticPage = new[] { Frame(), Frame(), Frame() };
        var bars = Measure(staticPage, client);
        check(bars is { Orientation: VideoBlackBarOrientation.Vertical, Confidence: >= .90 } &&
            !Motion(staticPage, bars), "static dark-browser gutters can score as symmetric bars without proving a player");

        var unbarred = Frame(picturePhase: 1, picture: SideBarViewport);
        var blank = new FramePixels(Width, Height, Width * 4, new byte[Width * Height * 4]);
        check(Analyze(unbarred) == null && FillsViewport(unbarred, SideBarViewport) &&
            !FillsViewport(staticPage[0], SideBarViewport) && !FillsViewport(blank, SideBarViewport),
            "dark blank captures and remaining gutters do not look like a picture reaching the fitted viewport edges");

        var animatedChrome = Frames(i => Frame(outsidePhase: 1 + i));
        bars = Measure(animatedChrome, client);
        check(bars != null && !Motion(animatedChrome, bars),
            "animation outside the bar-bounded rectangle cannot establish picture motion");

        var localAnimation = Frames(i => Frame(localPhase: 1 + i));
        bars = Measure(localAnimation, client);
        check(bars != null && !Motion(localAnimation, bars),
            "a spinner in the center of the dark browser page cannot establish picture motion");

        var scrollingText = Frames(i => Frame(scroll: i * 31));
        bars = Measure(scrollingText, client);
        check(bars != null && !Motion(scrollingText, bars),
            "a coherent scroll of textured content between unchanged gutters is identified as page scrolling");

        var typingLine = Frames(i => Frame(typingPhase: 1 + i));
        bars = Measure(typingLine, client);
        check(bars != null && !Motion(typingLine, bars),
            "a streaming text row cannot establish picture motion across the viewport");

        var movingPicture = Frames(i => Frame(picturePhase: 1 + Math.Min(i, 1)));
        bars = Measure(movingPicture, client);
        check(bars is { Orientation: VideoBlackBarOrientation.Vertical } && Motion(movingPicture, bars),
            "a silent pillarboxed picture with detailed motion across the viewport qualifies even with a duplicate frame");

        var flashingPage = Frames(i => Frame(picturePhase: 1 + i, outsidePhase: 1 + i));
        bars = Measure(flashingPage, client);
        check(bars != null && !Motion(flashingPage, bars),
            "distributed changes inside and outside the candidate viewport cannot establish a confined picture");

        var letterbox = new RectI(0, 108, Width, 650);
        var widePicture = new RectI(0, 184, Width, 498);
        var horizontalVideo = Frames(i => Frame(picturePhase: 1 + i, viewport: letterbox, picture: widePicture));
        bars = Measure(horizontalVideo, client);
        check(bars is { Orientation: VideoBlackBarOrientation.Horizontal } && Motion(horizontalVideo, bars),
            "a wide moving picture is verified against stable horizontal bars");

        var reflow = Frames(i => Frame(picturePhase: 1 + i, picture: new(100 + i * 28, 88, 900 - i * 56, 692)));
        check(Measure(reflow, client) == null, "motion during responsive reflow cannot compensate for moving picture boundaries");
    }

    private static bool Motion(FramePixels[] frames, DetectedBars bars) =>
        frames.Zip(frames.Skip(1)).Any(pair => HasPictureMotion(pair.First, pair.Second, bars));

    private static FramePixels[] Frames(Func<int, FramePixels> create) =>
        Enumerable.Range(0, FrameCount).Select(create).ToArray();

    private static DetectedBars? Measure(FramePixels[] frames, RectI client)
    {
        var detected = frames.Select(Analyze).OfType<DetectedBars>().ToArray();
        return StableBars(detected, client);
    }

    private static FramePixels Frame(int picturePhase = 0, int outsidePhase = 0, int localPhase = 0,
        RectI? viewport = null, RectI? picture = null, int scroll = 0, int typingPhase = 0)
    {
        var view = viewport ?? SideBarViewport;
        var content = picture ?? SideBarPicture;
        var bytes = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var local = x is > 510 and < 590 && y is > 385 and < 445;
            var phase = local && localPhase != 0 ? localPhase : y is > 385 and < 410 && typingPhase != 0 ? typingPhase : picturePhase;
            var luma = Inside(content, x, y)
                ? Texture(x, y + scroll, phase)
                : Inside(view, x, y) ? 15
                : Texture(x, y, outsidePhase);
            var offset = (y * Width + x) * 4;
            bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = (byte)luma;
        }
        return new(Width, Height, Width * 4, bytes);
    }

    private static int Texture(int x, int y, int phase) =>
        59 + ((x * 9 + y * 3 + (x / 11 + y / 17) * phase * 17) % 136);

    private static bool Inside(RectI rect, int x, int y) =>
        x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
}
