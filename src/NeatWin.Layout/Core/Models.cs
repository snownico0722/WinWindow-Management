namespace NeatWin.Core;

public readonly record struct FrameInsets(int Left, int Top, int Right, int Bottom);

public sealed record WindowSnapshot(
    nint Handle,
    RectI VisualRect,
    RectI OuterRect,
    RectI WorkArea,
    nint MonitorHandle,
    FrameInsets FrameInsets,
    bool IsResizable,
    bool IsForeground,
    bool IsManageable,
    int ZOrder,
    uint Dpi = 96,
    uint ProcessId = 0,
    bool IsTopmost = false);

public sealed record VisibleWindow(
    WindowSnapshot Window,
    long VisibleArea,
    long LargestVisibleFragmentArea,
    double VisibleRatio);

public sealed record TidyMove(WindowSnapshot Window, RectI TargetVisualRect);
