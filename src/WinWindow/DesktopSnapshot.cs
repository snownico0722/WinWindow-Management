using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

    private sealed record Frame(long ElapsedMs, string Foreground, Window[] Windows);
    private sealed record Display(string Name, Bounds Screen, Bounds WorkArea, bool Primary);

    internal static Task<string> ExportAsync(IReadOnlySet<nint> managed, nint workbench, nint previousForeground,
        string mode, string? directory = null, int durationMs = 10_000, CancellationToken cancellation = default,
        ConcurrentQueue<LayoutRunObservation>? decisions = null) =>
        Task.Run(async () =>
        {
            directory ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException(UiText.Get("SnapshotDesktopMissing"));

            var start = DateTimeOffset.Now;
            var timer = Stopwatch.StartNew();
            var displays = System.Windows.Forms.Screen.AllScreens.Select(s =>
                new Display(s.DeviceName, Bounds.From(s.Bounds), Bounds.From(s.WorkingArea), s.Primary)).ToArray();
            var frames = new List<Frame>();
            var apps = new Dictionary<uint, string>();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                long elapsed = timer.ElapsedMilliseconds;
                var foreground = Native.GetForegroundWindow();
                frames.Add(new(elapsed, Handle(foreground), Native.CaptureDesktopWindows(managed, workbench, foreground, apps)));
                if (elapsed >= durationMs) break;
                int delay = (int)Math.Min(250 - elapsed % 250, durationMs - elapsed);
                await Task.Delay(Math.Max(1, delay), cancellation).ConfigureAwait(false);
            }

            var plans = decisions?.ToArray() ?? [];
            var fileName = $"WinWindow-snapshot-{start:yyyyMMdd-HHmmss-fff}.zip";
            var output = Path.Combine(directory, fileName);
            var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    using (var writer = Writer(zip, "summary.txt"))
                    {
                        writer.WriteLine($"WinWindow desktop snapshot | {start:O}");
                        writer.WriteLine($"Arrange mode: {mode} | Samples: {frames.Count} | Duration: {frames[^1].ElapsedMs} ms");
                        writer.WriteLine($"Previous external foreground: {Handle(previousForeground)}");
                        writer.WriteLine($"Layout requests during capture: {plans.Select(p => p.RequestId).Distinct().Count()}");
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
                            writer.WriteLine($"  +{frame.ElapsedMs} ms  {last}  {window?.App}  {window?.Title}");
                        }
                        WriteWindows(writer, frames[0]);
                        if (frames.Count > 1) WriteWindows(writer, frames[^1]);
                    }
                    using (var writer = Writer(zip, "timeline.jsonl"))
                    {
                        writer.WriteLine(JsonSerializer.Serialize(new { Version = 1, Start = start, PreviousForeground = Handle(previousForeground), Mode = mode, Displays = displays }));
                        foreach (var frame in frames) writer.WriteLine(JsonSerializer.Serialize(frame));
                    }
                    using (var writer = Writer(zip, "layout-decisions.jsonl"))
                        foreach (var plan in plans) writer.WriteLine(JsonSerializer.Serialize(plan));
                }
                File.Move(temporary, output);
                return output;
            }
            finally { File.Delete(temporary); }
        }, cancellation);

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
