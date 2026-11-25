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
    bool IsTopmost = false, string? SurfaceState = null)
{
    public nint Owner { get; init; }
    // 从属菜单属于宿主的交互区域，仍会实际遮住邻窗。
    internal bool IsLocalTo(WindowSnapshot host) => !IsManageable && Owner != 0 && Owner == host.Handle;
    internal bool NativeUtility { get; init; }
    internal bool HasCaption { get; init; }
    internal bool IndependentFrame { get; init; }
}

public sealed record VisibleWindow(
    WindowSnapshot Window,
    long VisibleArea,
    long LargestVisibleFragmentArea,
    double VisibleRatio);

public sealed record TidyMove(WindowSnapshot Window, RectI TargetVisualRect);
