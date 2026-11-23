using System.Security.Cryptography;
using System.Text;

namespace NeatWin.Core;

internal sealed record BrowserRegion(double X, double Y, double Width, double Height)
{
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) &&
        double.IsFinite(Height) && X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        X + Width <= 1.000001 && Y + Height <= 1.000001;

    internal RectI Project(RectI viewport) => RectI.FromEdges(
        viewport.Left + (int)Math.Round(X * viewport.Width), viewport.Top + (int)Math.Round(Y * viewport.Height),
        viewport.Left + (int)Math.Round((X + Width) * viewport.Width),
        viewport.Top + (int)Math.Round((Y + Height) * viewport.Height)).Intersect(viewport);
}

internal sealed record BrowserExtensionSample(int Version, long SampledAt, Guid Session, int TabId, int WindowId,
    string? UrlKey, string? DocumentId, int Left, int Top, int Width, int Height, bool Focused, bool Audible,
    bool HasContent, double ViewportAspect, BrowserRegion? Reading, bool Editing, BrowserRegion? Video, bool Playing)
{
    internal bool IsFresh(long now) => Version == 1 && Session != Guid.Empty && TabId >= 0 && WindowId >= 0 &&
        SampledAt >= now - 1500 && SampledAt <= now + 150 && Width is >= 80 and <= 32768 &&
        Height is >= 80 and <= 32768 && UrlKey is { Length: 64 } && UrlKey.All(Uri.IsHexDigit) &&
        (DocumentId == null || Guid.TryParse(DocumentId, out _)) &&
        (Reading == null || Reading.IsValid) && (Video == null || Video.IsValid) &&
        (!HasContent || ViewportAspect is >= .05 and <= 20);

    internal bool MatchesFrame(WindowSnapshot window)
    {
        var frame = window.OuterRect;
        double scale = window.Dpi >= 96 ? window.Dpi / 96d : 1;
        // Chromium reports browser coordinates in DIPs. On mixed-DPI desktops the global
        // origin can differ. Accept only a matching full frame, and fall back when uncertain.
        return Matches(1) || scale != 1 && Matches(scale);

        bool Matches(double ratio) => Math.Abs(frame.Left - Left * ratio) <= 18 * ratio &&
            Math.Abs(frame.Top - Top * ratio) <= 18 * ratio && Math.Abs(frame.Width - Width * ratio) <= 18 * ratio &&
            Math.Abs(frame.Height - Height * ratio) <= 18 * ratio;
    }
}

internal static class BrowserPageEvidence
{
    internal static string? UrlKey(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url) &&
        url.Scheme is "https" or "http" ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value!))) : null;

    internal static bool CanUseContent(BrowserExtensionSample sample, RectI viewport) =>
        sample.HasContent && !viewport.IsEmpty && Math.Abs(viewport.Width / (double)viewport.Height / sample.ViewportAspect - 1) <= .07;

    internal static TaskWindowHint Apply(TaskWindowHint hint, WindowSnapshot window, RectI reading,
        RectI video, bool editing, bool playing)
    {
        var regions = hint.ProtectedRegions ?? [];
        // 离开编辑焦点后仍能看见正文，但页面浏览不等于用户已经开始编辑。
        var content = playing ? video : reading;
        var frame = window.VisualRect;
        content = content.Intersect(frame);
        if (!content.IsEmpty)
        {
            double x = (content.Left - frame.Left) / (double)frame.Width;
            double y = (content.Top - frame.Top) / (double)frame.Height;
            var region = new TaskContentRegion(x, y, Math.Min(1 - x, content.Width / (double)frame.Width),
                Math.Min(1 - y, content.Height / (double)frame.Height), playing ? .9 : .8);
            if (region.IsValid) regions = [.. regions, region];
        }
        return hint with
        {
            TextFocus = hint.TextFocus || editing && window.IsForeground && !hint.InitialTextPlacement,
            MediaCompanion = hint.MediaCompanion || playing,
            PassiveVisual = hint.PassiveVisual || playing,
            ProtectedRegions = regions.Length == 0 ? null : regions,
        };
    }
}
