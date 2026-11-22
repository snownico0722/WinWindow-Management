using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NeatWin.Core;

namespace WinWindow;

internal static class DesktopSnapshot
{
    internal sealed record Bounds(int X, int Y, int Width, int Height)
    {
        internal static Bounds From(Native.Rect rect) =>
            new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

        internal static Bounds From(System.Drawing.Rectangle rect) =>
            new(rect.X, rect.Y, rect.Width, rect.Height);
    }

    internal sealed record Window(string Handle, uint Pid, string App, string Class, string Title, int ZOrder,
        Bounds? Frame, Bounds? NormalPosition, uint? Dpi, bool Visible, bool Minimized, bool Maximized,
        bool Cloaked, bool Topmost, bool ClickThrough, int Opacity, bool Foreground, bool Managed);

    internal sealed record Sample(long Tick, string Foreground, Window[] Windows);
    private sealed record Frame(long ElapsedMs, string Foreground, Window[] Windows);
    private sealed record Display(string Name, Bounds Screen, Bounds WorkArea, bool Primary);

    internal static Sample CaptureWindows(IReadOnlySet<nint> managed, nint workbench, Dictionary<uint, string> apps)
    {
        var tick = Environment.TickCount64;
        var foreground = Native.GetForegroundWindow();
        return new(tick, Handle(foreground), Native.CaptureDesktopWindows(managed, workbench, foreground, apps));
    }

    internal static Task<string> ExportAsync(IReadOnlySet<nint> managed, nint workbench, nint previousForeground,
        string mode, string? directory = null, int durationMs = 10_000, CancellationToken cancellation = default,
        DesktopSnapshotHistory.Capture? capture = null) =>
        Task.Run(async () =>
        {
            directory ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException(UiText.Get("SnapshotDesktopMissing"));

            var start = capture?.Time ?? DateTimeOffset.Now;
            var startTick = capture?.Tick ?? Environment.TickCount64;
            var displays = System.Windows.Forms.Screen.AllScreens.Select(s =>
                new Display(s.DeviceName, Bounds.From(s.Bounds), Bounds.From(s.WorkingArea), s.Primary)).ToArray();
            var past = capture?.PastWindows ?? [];
            var frames = past.Select(sample => new Frame(sample.Tick - startTick, sample.Foreground, sample.Windows)).ToList();
            var apps = new Dictionary<uint, string>();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var sample = CaptureWindows(managed, workbench, apps);
                long elapsed = sample.Tick - startTick;
                frames.Add(new(elapsed, sample.Foreground, sample.Windows));
                if (elapsed >= durationMs) break;
                elapsed = Environment.TickCount64 - startTick;
                int delay = (int)Math.Min(250 - elapsed % 250, durationMs - elapsed);
                await Task.Delay(Math.Max(1, delay), cancellation).ConfigureAwait(false);
            }

            var pastPlans = capture?.PastDecisions ?? [];
            var livePlans = capture?.LiveDecisions.Where(d => d.Tick - startTick <= frames[^1].ElapsedMs).ToArray() ?? [];
            var plans = pastPlans.Concat(livePlans).Select(d => d.Observation).ToArray();
            long endTick = startTick + frames[^1].ElapsedMs;
            var afterBeginSurfaces = capture?.LiveSurfaces.Where(s => s.Tick <= endTick).ToArray() ?? [];
            var pastSurfaces = (capture?.PastSurfaces ?? []).Concat(afterBeginSurfaces.Where(s => s.Tick < startTick))
                .OrderBy(s => s.Tick).ToArray();
            var liveSurfaces = afterBeginSurfaces.Where(s => s.Tick >= startTick).OrderBy(s => s.Tick).ToArray();
            int droppedSurfaces = capture?.DroppedSurfaces ?? 0;
            bool lookbackEnabled = capture?.LookbackEnabled == true;
            long lookbackCollectionMs = capture is null ? 0 : startTick - capture.EarliestTick;
            long windowHistorySpanMs = past.Length == 0 ? 0 : startTick - past[0].Tick;
            long? surfaceHistoryDroppedThrough = capture?.SurfaceDroppedThrough - startTick;
            int pastRequests = pastPlans.Select(d => d.Observation.RequestId).Distinct().Count();
            int liveRequests = livePlans.Select(d => d.Observation.RequestId).Distinct().Count();
            var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    using (var writer = Writer(zip, "summary.txt"))
                    {
                        writer.WriteLine($"WinWindow desktop snapshot | {start:O}");
                        writer.WriteLine($"Arrange mode: {mode} | Samples after click: {frames.Count - past.Length} | Duration after click: {frames[^1].ElapsedMs} ms");
                        writer.WriteLine($"Lookback: {(lookbackEnabled ? "enabled" : "disabled")} | Configured: {DesktopSnapshotHistory.LookbackMilliseconds} ms | Collection window: {lookbackCollectionMs} ms");
                        writer.WriteLine($"Samples before click: {past.Length} | Window history span: {windowHistorySpanMs} ms");
                        if (surfaceHistoryDroppedThrough is long through)
                            writer.WriteLine($"Surface history hit its limit and lost events through {through} ms relative to click.");
                        writer.WriteLine($"Previous external foreground: {Handle(previousForeground)}");
                        writer.WriteLine($"Layout requests before click: {pastRequests} | After click: {liveRequests}");
                        writer.WriteLine($"Surface transitions before click: {pastSurfaces.Length} | After click: {liveSurfaces.Length} | Live limit drops: {droppedSurfaces}");
                        writer.WriteLine($"Monitors: {displays.Length}");
                        foreach (var display in displays)
                            writer.WriteLine($"  {display.Name} | display: {display.Screen} | work area: {display.WorkArea} | primary: {display.Primary}");
                        writer.WriteLine();
                        writer.WriteLine("Foreground changes:");
                        string? last = null;
                        foreach (var frame in frames)
                        {
                            if (frame.Foreground == last) continue;
                            last = frame.Foreground;
                            var window = frame.Windows.FirstOrDefault(w => w.Handle == last);
                            writer.WriteLine($"  {frame.ElapsedMs:+0;-0;0} ms  {last}  {window?.App}  {window?.Title}");
                        }
                        WriteWindows(writer, frames[0]);
                        if (past.Length > 0) WriteWindows(writer, frames[past.Length]);
                        if (frames.Count - past.Length > 1) WriteWindows(writer, frames[^1]);
                    }
                    using (var writer = Writer(zip, "timeline.jsonl"))
                    {
                        writer.WriteLine(JsonSerializer.Serialize(new { Version = 2, SmartLayoutModel = LayoutDispatch.ModelVersion(TidyAlgorithmMode.Smart),
                            ClassicLayoutModel = LayoutDispatch.ModelVersion(TidyAlgorithmMode.Classic), LearningModel = PlacementPreferences.ModelVersion,
                            Start = start, StartTick = startTick, PreviousForeground = Handle(previousForeground), Mode = mode, Displays = displays,
                            LookbackEnabled = lookbackEnabled, LookbackMilliseconds = DesktopSnapshotHistory.LookbackMilliseconds,
                            LookbackCollectionMilliseconds = lookbackCollectionMs, WindowHistorySpanMilliseconds = windowHistorySpanMs,
                            SurfaceHistoryDroppedThroughElapsedMs = surfaceHistoryDroppedThrough, SamplesBeforeClick = past.Length, LayoutRequestsBeforeClick = pastRequests, LayoutRequestsAfterClick = liveRequests,
                            SurfaceTransitionsBeforeClick = pastSurfaces.Length, SurfaceTransitionsAfterClick = liveSurfaces.Length, DroppedLiveSurfaceTransitions = droppedSurfaces }));
                        foreach (var frame in frames) writer.WriteLine(JsonSerializer.Serialize(frame));
                    }
                    if (capture != null)
                    {
                        long earliest = capture.EarliestTick;
                        var observed = capture.PastGestures.Concat(capture.LiveGestures).Where(g =>
                            g.Event.Tick >= earliest && g.Event.Tick <= startTick + frames[^1].ElapsedMs);
                        foreach (var group in observed.GroupBy(g => g.Session))
                        {
                            using var writer = Writer(zip, $"gestures-{group.Key}.jsonl");
                            var events = group.OrderBy(g => g.Event.Tick).Select(g => g.Event.WithinLookback(earliest)).ToArray();
                            writer.WriteLine(JsonSerializer.Serialize(new GestureBatch(1, group.Key, start, events, 0)));
                        }
                    }
                    if (capture != null)
                    {
                        using var writer = Writer(zip, "surfaces-snapshot.jsonl");
                        foreach (var observation in pastSurfaces.Concat(liveSurfaces))
                            writer.WriteLine(JsonSerializer.Serialize(observation));
                    }
                    using (var writer = Writer(zip, "layout-decisions.jsonl"))
                        foreach (var plan in plans) writer.WriteLine(JsonSerializer.Serialize(plan));
                }
                cancellation.ThrowIfCancellationRequested();
                return PublishNumbered(temporary, directory);
            }
            finally { File.Delete(temporary); }
        }, cancellation);

    internal static string PublishNumbered(string temporary, string directory)
    {
        for (int edition = 1; ; edition = checked(edition + 1))
        {
            var output = Path.Combine(directory, $"第{EditionNumber(edition)}版.zip");
            if (File.Exists(output) || Directory.Exists(output)) continue;
            // ZIP 完整写好才占用版号。同名检查之后仍可能有别的导出先完成，因此以这次移动是否成功决定是否试下一个版号。
            try { File.Move(temporary, output); return output; }
            catch (IOException) when (File.Exists(output) || Directory.Exists(output)) { }
        }
    }

    private static string EditionNumber(int number)
    {
        if (number >= 10000) return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string digits = "零一二三四五六七八九";
        string[] units = ["", "十", "百", "千"];
        var text = new StringBuilder();
        bool zero = false;
        for (int place = 3, divisor = 1000; place >= 0; place--, divisor /= 10)
        {
            int digit = number / divisor % 10;
            if (digit == 0) { zero |= text.Length > 0; continue; }
            if (zero) text.Append(digits[0]);
            if (place != 1 || digit != 1 || text.Length > 0) text.Append(digits[digit]);
            text.Append(units[place]);
            zero = false;
        }
        return text.ToString();
    }

    private static StreamWriter Writer(ZipArchive archive, string entry) =>
        new(archive.CreateEntry(entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));

    private static void WriteWindows(StreamWriter writer, Frame frame)
    {
        writer.WriteLine();
        writer.WriteLine($"Windows at +{frame.ElapsedMs} ms (front to back): {frame.Windows.Length}");
        foreach (var w in frame.Windows)
        {
            var frameText = w.Frame is { } rect ? $"({rect.X},{rect.Y}) {rect.Width}×{rect.Height}" : "unavailable";
            var state = w.Cloaked ? "cloaked" : !w.Visible ? "hidden" : w.Minimized ? "minimized" : w.Maximized ? "maximized" : "visible";
            writer.WriteLine($"  #{w.ZOrder} {w.Handle} {w.App} [{w.Pid}] {frameText} @{w.Dpi}dpi | {state}{(w.Foreground ? ", foreground" : "")}{(w.Topmost ? ", topmost" : "")}{(w.Managed ? ", managed" : "")} | {w.Class} | {w.Title}");
        }
    }

    internal static string Handle(nint value) => $"0x{value.ToInt64():X}";
}
