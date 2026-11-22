using System.Runtime.InteropServices;
using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.App;

/// <summary>
/// Preserves the useful part of Windows snap restore for NeatWin's vertical-fill preference.
/// The shared pointer and move-start streams identify potential title-bar restores.
/// </summary>
internal sealed class ReversibleVerticalFillManager : IDisposable
{
    private readonly AutoTidyManager _input;
    private readonly Func<bool> _leftHeld;
    private readonly Func<nint, bool> _preserveTextView;
    private readonly Dictionary<nint, RestoreEntry> _entries = [];
    private int _sequence;
    private bool _disposed;

    internal ReversibleVerticalFillManager(AutoTidyManager input, Func<nint, bool> preserveTextView, Func<bool>? leftHeld = null)
    {
        _input = input;
        _leftHeld = leftHeld ?? (() => (NativeMethods.GetAsyncKeyState(1) & 0x8000) != 0);
        _preserveTextView = preserveTextView;
        input.RestorePointer += OnPress;
        input.GestureStarted += OnMoveStart;
    }

    internal void Track(IReadOnlyList<TidyMove> plan, bool enabled)
    {
        if (!enabled)
        {
            _entries.Clear();
            _sequence++;
        }
        else
        {
            foreach (var move in plan)
            {
                var window = move.Window;
                var target = move.TargetVisualRect;
                var workArea = window.WorkArea;
                var targetFillsVertically =
                    Math.Abs(target.Top - workArea.Top) <= 1 &&
                    Math.Abs(target.Bottom - workArea.Bottom) <= 1;
                var originalAlreadyFilled =
                    Math.Abs(window.VisualRect.Top - workArea.Top) <= 1 &&
                    Math.Abs(window.VisualRect.Bottom - workArea.Bottom) <= 1;

                if (window.IsResizable && targetFillsVertically && !originalAlreadyFilled)
                {
                    _entries[window.Handle] = new RestoreEntry(
                        window.ProcessId,
                        window.VisualRect,
                        target,
                        workArea,
                        window.FrameInsets);
                }
                else
                {
                    _entries.Remove(window.Handle);
                }
            }
        }
        _input.SetRestorePointer(_entries.Count != 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _input.RestorePointer -= OnPress;
        _input.GestureStarted -= OnMoveStart;
        _input.SetRestorePointer(false);
        _entries.Clear();
        _sequence++;
    }

    private async void OnPress(PointerObservation input) => await PointerChangedAsync(input);

    internal Task PointerChangedAsync(PointerObservation input)
    {
        _sequence++; // 松手或开始另一笔操作后，迟到的跨进程命中结果不能再还原上一扇窗。
        return input.Kind == "left-down" ? ProbeAsync(input.Target, new() { X = input.Point.X, Y = input.Point.Y }, false, input.TargetPid) : Task.CompletedTask;
    }

    private async void OnMoveStart(nint hwnd)
    {
        if (_entries.ContainsKey(hwnd) && NativeMethods.GetCursorPos(out var cursor))
            await ProbeAsync(hwnd, cursor, true, null);
    }

    private async Task ProbeAsync(nint hwnd, NativeMethods.Point cursor, bool allowClientTitleFallback, uint? pid)
    {
        if (!_entries.TryGetValue(hwnd, out var entry) || pid.HasValue && pid != entry.ProcessId) return;
        int ticket = ++_sequence;
        // 客户区自绘标题要询问目标线程。慢窗口最多占用一次后台命中测试，布局消息泵可继续处理鼠标松手。
        bool title = await Task.Run(() => IsTitleBarPoint(hwnd, entry.TargetVisualRect, cursor, allowClientTitleFallback));
        if (_disposed || _sequence != ticket) return;
        if (!title || !_entries.TryGetValue(hwnd, out var current) || !ReferenceEquals(entry, current) ||
            !_leftHeld() || NativeMethods.GetForegroundWindow() != hwnd) return;
        TryRestoreFromPointer(hwnd, cursor, entry);
    }

    private bool TryRestoreFromPointer(nint hwnd, NativeMethods.Point cursor, RestoreEntry entry)
    {
        // Reading at an exact location takes priority over restoring a previously filled
        // window on title-bar mouse-down. An actual release still goes through gesture assist.
        if (hwnd == NativeMethods.GetForegroundWindow() && _preserveTextView(hwnd)) return false;
        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        if (owner != entry.ProcessId || (style & (NativeMethods.WsExTopmost | NativeMethods.WsExTransparent)) != 0 ||
            NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
        {
            RemoveEntry(hwnd);
            return false;
        }

        if (!TryGetVisualRect(hwnd, out var currentVisual))
        {
            return false;
        }

        if (!ApproximatelyMatches(currentVisual, entry.TargetVisualRect, tolerance: 12))
        {
            // 慢速标题命中返回时，系统可能已开始移动。仅在同一窗口仍处于移动循环、尺寸没被用户改动时跟上当前指针。
            var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
            if (currentVisual.Width != entry.TargetVisualRect.Width || currentVisual.Height != entry.TargetVisualRect.Height ||
                !NativeMethods.GetGUIThreadInfo(thread, ref info) || info.MoveSize != hwnd || !NativeMethods.GetCursorPos(out cursor))
            {
                RemoveEntry(hwnd);
                return false;
            }
        }

        var restoredVisual = PlaceRestoreRectUnderCursor(entry, currentVisual, cursor);
        var outer = ToOuterRect(restoredVisual, entry.FrameInsets);
        AutomationGuard.Mark(hwnd);
        var success = NativeMethods.SetWindowPos(
            hwnd,
            nint.Zero,
            outer.X,
            outer.Y,
            outer.Width,
            outer.Height,
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpAsyncWindowPos);

        if (success)
        {
            RemoveEntry(hwnd);
        }

        return success;
    }

    private void RemoveEntry(nint hwnd)
    {
        _entries.Remove(hwnd);
        _input.SetRestorePointer(_entries.Count != 0);
    }

    private static bool IsTitleBarPoint(
        nint hwnd,
        RectI visualRect,
        NativeMethods.Point cursor,
        bool allowClientTitleFallback)
    {
        if (visualRect.IsEmpty ||
            cursor.X < visualRect.Left || cursor.X >= visualRect.Right ||
            cursor.Y < visualRect.Top || cursor.Y >= visualRect.Bottom ||
            !TryHitTest(hwnd, cursor, out var hitTest))
        {
            return false;
        }

        if (hitTest == NativeMethods.HtCaption)
        {
            return true;
        }

        if (!allowClientTitleFallback || hitTest != NativeMethods.HtClient)
        {
            return false;
        }

        var dpi = Math.Max(96u, NativeMethods.GetDpiForWindow(hwnd));
        var scale = dpi / 96.0;
        var titleBand = Math.Max(44, (int)Math.Round(76 * scale));
        var horizontalInset = Math.Max(10, (int)Math.Round(12 * scale));
        var topOffset = cursor.Y - visualRect.Top;

        return topOffset >= 0 &&
               topOffset <= titleBand &&
               cursor.X - visualRect.Left >= horizontalInset &&
               visualRect.Right - cursor.X >= horizontalInset;
    }

    private static bool TryHitTest(
        nint hwnd,
        NativeMethods.Point cursor,
        out int hitTest)
    {
        var packedPoint = PackPoint(cursor);
        var sent = NativeMethods.SendMessageTimeout(
            hwnd,
            NativeMethods.WmNcHitTest,
            nint.Zero,
            packedPoint,
            NativeMethods.SmtoBlock | NativeMethods.SmtoAbortIfHung,
            40,
            out var result);

        if (sent == nint.Zero)
        {
            hitTest = NativeMethods.HtNowhere;
            return false;
        }

        hitTest = unchecked((int)result);
        return true;
    }

    private static nint PackPoint(NativeMethods.Point point)
    {
        var x = unchecked((ushort)(short)point.X);
        var y = unchecked((ushort)(short)point.Y);
        return unchecked((nint)(int)(x | (y << 16)));
    }

    private static RectI PlaceRestoreRectUnderCursor(
        RestoreEntry entry,
        RectI currentVisual,
        NativeMethods.Point cursor)
    {
        var original = entry.OriginalVisualRect;
        var workArea = entry.WorkArea;
        var horizontalFraction = currentVisual.Width <= 0
            ? 0.5
            : Math.Clamp((double)(cursor.X - currentVisual.Left) / currentVisual.Width, 0.08, 0.92);
        var currentTopOffset = Math.Max(8, cursor.Y - currentVisual.Top);
        var restoredTopOffset = Math.Min(currentTopOffset, Math.Max(12, original.Height / 5));

        var left = (int)Math.Round(cursor.X - (original.Width * horizontalFraction));
        var top = cursor.Y - restoredTopOffset;

        if (original.Width <= workArea.Width)
        {
            left = Math.Clamp(left, workArea.Left, workArea.Right - original.Width);
        }
        if (original.Height <= workArea.Height)
        {
            top = Math.Clamp(top, workArea.Top, workArea.Bottom - original.Height);
        }

        return new RectI(left, top, original.Width, original.Height);
    }

    private static bool TryGetVisualRect(nint hwnd, out RectI rect)
    {
        var status = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaExtendedFrameBounds,
            out NativeMethods.Rect nativeRect,
            Marshal.SizeOf<NativeMethods.Rect>());

        if (status == 0)
        {
            rect = RectI.FromEdges(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            return !rect.IsEmpty;
        }

        rect = default;
        return false;
    }

    private static bool ApproximatelyMatches(RectI a, RectI b, int tolerance) =>
        Math.Abs(a.Left - b.Left) <= tolerance &&
        Math.Abs(a.Top - b.Top) <= tolerance &&
        Math.Abs(a.Right - b.Right) <= tolerance &&
        Math.Abs(a.Bottom - b.Bottom) <= tolerance;

    private static RectI ToOuterRect(RectI visualRect, FrameInsets insets) =>
        RectI.FromEdges(
            visualRect.Left - insets.Left,
            visualRect.Top - insets.Top,
            visualRect.Right + insets.Right,
            visualRect.Bottom + insets.Bottom);

    private sealed record RestoreEntry(
        uint ProcessId,
        RectI OriginalVisualRect,
        RectI TargetVisualRect,
        RectI WorkArea,
        FrameInsets FrameInsets);
}
