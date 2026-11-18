using System.Runtime.InteropServices;
using NeatWin.Core;
using NeatWin.Windows;

namespace NeatWin.App;

/// <summary>
/// Preserves the useful part of Windows snap restore for NeatWin's vertical-fill preference.
/// A low-level mouse hook is enabled only while at least one reversible window is tracked, so the
/// original rectangle can be restored before the target application starts its drag loop. The
/// existing move/size WinEvent remains as a fallback for client-drawn title bars.
/// </summary>
internal sealed class ReversibleVerticalFillManager : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, RestoreEntry> _entries = new();
    private readonly NativeMethods.WinEventProc _winEventProc;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly nint _winEventHook;
    private nint _mouseHook;
    private bool _disposed;

    internal ReversibleVerticalFillManager()
    {
        _winEventProc = OnWinEvent;
        _mouseProc = OnLowLevelMouse;
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemMoveSizeStart,
            NativeMethods.EventSystemMoveSizeStart,
            nint.Zero,
            _winEventProc,
            0,
            0,
            NativeMethods.WinEventOutOfContext | NativeMethods.WinEventSkipOwnProcess);
    }

    internal void Track(IReadOnlyList<TidyMove> plan, bool enabled)
    {
        bool shouldHookMouse;

        lock (_gate)
        {
            if (!enabled)
            {
                _entries.Clear();
                shouldHookMouse = false;
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

                shouldHookMouse = _entries.Count > 0;
            }
        }

        SetMouseHookEnabled(shouldHookMouse);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SetMouseHookEnabled(false);

        if (_winEventHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_winEventHook);
        }

        lock (_gate)
        {
            _entries.Clear();
        }

        GC.SuppressFinalize(this);
    }

    private void SetMouseHookEnabled(bool enabled)
    {
        if (_disposed && enabled)
        {
            return;
        }

        if (enabled)
        {
            if (_mouseHook == nint.Zero)
            {
                _mouseHook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WhMouseLl,
                    _mouseProc,
                    nint.Zero,
                    0);
            }

            return;
        }

        if (_mouseHook != nint.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }
    }

    private nint OnLowLevelMouse(int code, nint message, nint dataPointer)
    {
        try
        {
            if (!_disposed &&
                code >= NativeMethods.HcAction &&
                message == NativeMethods.WmLButtonDown)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MsllHookStruct>(dataPointer);
                var hitWindow = NativeMethods.WindowFromPoint(data.Point);
                var root = hitWindow == nint.Zero
                    ? nint.Zero
                    : NativeMethods.GetAncestor(hitWindow, NativeMethods.GaRoot);

                if (root != nint.Zero)
                {
                    _ = TryRestoreFromPointer(root, data.Point, allowClientTitleFallback: false);
                }
            }
        }
        catch
        {
            // Never let a managed exception escape a global input hook.
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || hwnd == nint.Zero || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        // Primary path restores on mouse-down before dragging begins. This fallback covers apps
        // that implement a custom client-area DragMove and therefore report HTCLIENT instead of
        // HTCAPTION during the initial hit test.
        _ = TryRestoreFromPointer(hwnd, cursor, allowClientTitleFallback: true);
    }

    private bool TryRestoreFromPointer(
        nint hwnd,
        NativeMethods.Point cursor,
        bool allowClientTitleFallback)
    {
        RestoreEntry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(hwnd, out entry))
            {
                return false;
            }
        }

        if (!TryGetVisualRect(hwnd, out var currentVisual))
        {
            return false;
        }

        if (!ApproximatelyMatches(currentVisual, entry.TargetVisualRect, tolerance: 12))
        {
            RemoveEntry(hwnd);
            return false;
        }

        if (!IsTitleBarPoint(hwnd, currentVisual, cursor, allowClientTitleFallback))
        {
            return false;
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
            NativeMethods.SwpNoOwnerZOrder);

        if (success)
        {
            RemoveEntry(hwnd);
        }

        return success;
    }

    private void RemoveEntry(nint hwnd)
    {
        var noEntriesRemain = false;
        lock (_gate)
        {
            _entries.Remove(hwnd);
            noEntriesRemain = _entries.Count == 0;
        }

        if (noEntriesRemain)
        {
            SetMouseHookEnabled(false);
        }
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

    private readonly record struct RestoreEntry(
        RectI OriginalVisualRect,
        RectI TargetVisualRect,
        RectI WorkArea,
        FrameInsets FrameInsets);
}
