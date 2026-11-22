using System.Diagnostics;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NeatWin.Core;
using NeatWin.Windows;
using static NeatWin.Core.BrowserVideoFrameAnalysis;

namespace NeatWin.App;

/// <summary>
/// Conservative screen-space browser-video detector. It never injects into the browser. Subtitles,
/// danmaku and transient controls are treated as sparse bright noise, and three captures must agree
/// before a black-bar hint is emitted.
/// </summary>
internal sealed class BrowserVideoBlackBarDetector
{
    private const int FrameDelayMilliseconds = 28;

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "opera_gx", "arc",
    };

    internal VideoFitSession Fits { get; } = new();
    internal BrowserBarObservation? LastObservation { get; private set; }
    internal nint ObservationWindow { get; private set; }

    private (VideoBlackBarHint Hint, WindowSnapshot Window, VideoPageIdentity Page, long Tick)? _lastDetection;

    internal VideoBlackBarHint? TryDetect(IReadOnlyList<VisibleWindow> visibleWindows, bool cachedOnly = false)
    {
        LastObservation = null;
        ObservationWindow = 0;
        // 松手或开窗时只复用短期、已确认的测量。即使 HWND 相同，标签、位置或遮挡变化也会让测量失效。
        if (cachedOnly)
        {
            if (_lastDetection is { } last && Environment.TickCount64 - last.Tick <= 1500 &&
                visibleWindows.Any(v => v.Window.Handle == last.Window.Handle && v.Window.IsManageable && v.Window.IsResizable &&
                    v.Window.ProcessId == last.Window.ProcessId && v.Window.VisualRect == last.Window.VisualRect &&
                    v.Window.OuterRect == last.Window.OuterRect && v.Window.WorkArea == last.Window.WorkArea &&
                    v.Window.MonitorHandle == last.Window.MonitorHandle && v.Window.SurfaceState == last.Window.SurfaceState &&
                    v.Window.Dpi == last.Window.Dpi && v.VisibleRatio >= .72 && GeometryMatches(v.Window) &&
                    ReadPage(v.Window) == last.Page && OwnsViewport(v.Window.Handle, last.Hint.ViewportVisualRect)))
            {
                Observe(last.Hint, "confirmed-picture");
                return last.Hint;
            }
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
            !GeometryMatches(candidate.Window) ||
            !TryGetClientScreenRect(candidate.Window.Handle, out var clientRect) ||
            clientRect.Width < 640 ||
            clientRect.Height < 360)
        {
            return null;
        }

        var window = candidate.Window;
        var workArea = window.WorkArea;
        if (!workArea.IsEmpty &&
            (clientRect.Left < workArea.Left - 2 || clientRect.Top < workArea.Top - 2 ||
             clientRect.Right > workArea.Right + 2 || clientRect.Bottom > workArea.Bottom + 2))
        {
            return null;
        }

        var page = ReadPage(window);
        if (page is null) return null;
        var foreground = NativeMethods.GetForegroundWindow();
        var inputBefore = LastInputTime();
        bool CaptureStillCurrent() => !NativeWindowActivity.IsMoving &&
            NativeMethods.GetForegroundWindow() == foreground && GeometryMatches(window) &&
            TryGetClientScreenRect(window.Handle, out var current) && current == clientRect;
        RectI ToScreen(RectI rect) => rect with { X = rect.X + clientRect.X, Y = rect.Y + clientRect.Y };

        FramePixels? previous = null;
        bool pictureMotion = false;
        var detections = new List<DetectedBars>(FrameCount);
        for (var i = 0; i < FrameCount; i++)
        {
            if (!CaptureStillCurrent() ||
                (i > 0 && !OwnsViewport(window.Handle, ToScreen(detections[i - 1].ViewportClientRect))) ||
                Capture(clientRect) is not { } frame) return null;

            var detected = Analyze(frame);
            if (detected is null)
            {
                // 原本就会在第一张无暗带截图后返回。只利用这张图核验已落地的旧视口，无需补拍。
                var now = Environment.TickCount64;
                if (i == 0 && Fits.SettledViewport(window, page, now) is { } settled &&
                    CaptureStillCurrent() && LastInputTime() == inputBefore && ReadPage(window) == page &&
                    OwnsViewport(window.Handle, settled) &&
                    FillsViewport(frame, settled with { X = settled.X - clientRect.X, Y = settled.Y - clientRect.Y }))
                    Fits.ObserveUnbarred(window, page, now);
                return null;
            }
            if (!CaptureStillCurrent() || !OwnsViewport(window.Handle, ToScreen(detected.ViewportClientRect))) return null;

            // 只留上一帧。三张截图仍必须同时通过边界检查，但两对相邻图像中有一对含画面变化即可。
            if (!pictureMotion && previous != null && HasPictureMotion(previous, frame, detected)) pictureMotion = true;
            previous = pictureMotion ? null : frame;
            detections.Add(detected);
            if (i + 1 < FrameCount) Thread.Sleep(FrameDelayMilliseconds);
        }

        if (StableBars(detections, clientRect) is not { } bars ||
            page != ReadPage(window) || !CaptureStillCurrent()) return null;
        var hint = new VideoBlackBarHint(window.Handle, bars.Orientation, bars.ContentAspectRatio,
            ToScreen(bars.ViewportClientRect), bars.Confidence);
        if (VideoViewportFit.From(window.VisualRect, hint) is not { } fit) return null;
        // 采样期间有滚轮、键盘或触控输入时，画面变化可能正由用户造成。这里直接放弃本次证据。
        if (LastInputTime() != inputBefore || !pictureMotion)
        {
            Observe(hint, "unconfirmed-picture");
            return null;
        }

        var tick = Environment.TickCount64;
        if (Fits.Repeat(window, page, fit, tick))
        {
            Observe(hint, "settled-fit");
            return null;
        }
        Observe(hint, "confirmed-picture");
        _lastDetection = (hint, window, page, tick);
        return hint;
    }

    private void Observe(VideoBlackBarHint hint, string decision)
    {
        ObservationWindow = hint.WindowHandle;
        LastObservation = new(decision, hint.ContentAspectRatio, hint.Confidence);
    }

    private static uint? LastInputTime()
    {
        var info = new NativeMethods.LastInput { Size = (uint)Marshal.SizeOf<NativeMethods.LastInput>() };
        return NativeMethods.GetLastInputInfo(ref info) ? info.Time : null;
    }

    private static bool GeometryMatches(WindowSnapshot window) =>
        WindowManager.TryGetRect(window.Handle, out var outer) && outer == window.OuterRect;

    private static bool OwnsViewport(nint hwnd, RectI view)
    {
        // 截屏读到的是桌面合成结果。先确认测量矩形内的各区都属于同一浏览器窗口，
        // 以免把叠在它上方的窗口或另一个播放器的画面算给当前标签。
        const int samples = 5;
        for (int row = 0; row < samples; row++)
        for (int col = 0; col < samples; col++)
        {
            var point = new NativeMethods.Point
            {
                X = view.Left + view.Width * (2 * col + 1) / (2 * samples),
                Y = view.Top + view.Height * (2 * row + 1) / (2 * samples),
            };
            var hit = NativeMethods.WindowFromPoint(point);
            if (hit != hwnd && NativeMethods.GetAncestor(hit, NativeMethods.GaRoot) != hwnd) return false;
        }
        return true;
    }

    internal void RecordApplied(TidyMove move)
    {
        if (_lastDetection is { } last && last.Window.Handle == move.Window.Handle &&
            last.Window.ProcessId == move.Window.ProcessId && last.Window.VisualRect == move.Window.VisualRect &&
            VideoViewportFit.From(move.Window.VisualRect, last.Hint) is { } fit)
            Fits.Submitted(move.Window, move.TargetVisualRect, last.Page, fit, Environment.TickCount64);
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

    private static FramePixels? Capture(RectI clientRect)
    {
        try
        {
            using var bitmap = new Bitmap(clientRect.Width, clientRect.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(clientRect.Left, clientRect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            }
            return CopyPixels(bitmap);
        }
        catch
        {
            // Protected video, HDR/display-path peculiarities and capture failures disable this optional hint.
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

}
