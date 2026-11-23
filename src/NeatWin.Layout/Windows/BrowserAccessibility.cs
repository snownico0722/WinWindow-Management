using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;
using NeatWin.Core;

namespace NeatWin.Windows;

internal sealed record BrowserWindowTarget(nint Handle, uint Pid, string Caption);
internal sealed record AccessibleBrowserPage(BrowserWindowTarget Target, nint Renderer, RectI Viewport, long Started,
    string? UrlKey, RectI Reading, bool Editing);

internal static class BrowserAccessibility
{
    private const string HostClass = "Chrome_WidgetWin_1";
    private const string RendererClass = "Chrome_RenderWidgetHostHWND";
    private const uint ObjidClient = 0xfffffffc;

    internal static BrowserWindowTarget? Target(nint handle)
    {
        if (handle == 0 || ClassName(handle) != HostClass) return null;
        NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
        return pid == 0 ? null : new(handle, pid, Caption(handle));
    }

    internal static string Caption(nint handle)
    {
        var title = new StringBuilder(2048);
        NativeMethods.GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    internal static bool IsSupported(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant() is "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or "opera_gx";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static (nint Renderer, RectI Viewport) FindViewport(nint root)
    {
        if (ClassName(root) != HostClass || !NativeMethods.GetWindowRect(root, out var outer)) return default;
        var frame = Rect(outer);
        (nint Renderer, RectI Viewport) found = default;
        bool ambiguous = false;
        NativeMethods.EnumChildWindows(root, (child, _) =>
        {
            if (!NativeMethods.IsWindowVisible(child) || ClassName(child) != RendererClass ||
                !NativeMethods.GetWindowRect(child, out var bounds)) return true;
            var region = Rect(bounds).Intersect(frame);
            if (region.Width < 80 || region.Height < 60 || region.Area < frame.Area / 5) return true;
            // 分屏、开发者工具和弹层可能同时提供文档。无法确定主视口时保留通用窗口逻辑。
            if (found.Renderer != 0) ambiguous = true;
            else found = (child, region);
            return true;
        }, 0);
        return ambiguous ? default : found;
    }

    internal static AccessibleBrowserPage? Read(BrowserWindowTarget target, nint expectedForeground)
    {
        try
        {
            if (!IsSupported(target.Pid) || NativeMethods.GetForegroundWindow() != expectedForeground) return null;
            using var process = Process.GetProcessById((int)target.Pid);
            long started = process.StartTime.ToUniversalTime().Ticks;
            var (renderer, viewport) = FindViewport(target.Handle);
            if (renderer == 0) return null;
            var iid = typeof(IAccessible).GUID;
            if (NativeMethods.AccessibleObjectFromWindow(renderer, ObjidClient, ref iid, out var document) < 0 ||
                document == null || document.get_accRole(0) is not (int)AccessibleRole.Document) return null;
            string? page = BrowserPageEvidence.UrlKey(document.get_accValue(0));
            if (page == null) return null;

            RectI reading = default;
            bool editing = false;
            try
            {
                editing = expectedForeground == target.Handle && HasEditableFocus(document);
                reading = VisibleText(document, viewport);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException) { }

            // 可访问树可能正在重载。前后文档、标题和视口都相符时才带出区域。
            if (NativeMethods.GetForegroundWindow() != expectedForeground || Target(target.Handle) != target ||
                FindViewport(target.Handle) != (renderer, viewport) || BrowserPageEvidence.UrlKey(document.get_accValue(0)) != page) return null;
            return new(target, renderer, viewport, started, page, reading, editing);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotImplementedException or ArgumentException or
            InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static bool HasEditableFocus(IAccessible document)
    {
        var node = document;
        for (int depth = 0; depth < 6; depth++)
        {
            var focus = node.accFocus;
            if (focus is IAccessible child && !ReferenceEquals(child, node)) { node = child; continue; }
            if (focus is not int id) return false;
            return IsEditable(node, id);
        }
        return false;
    }

    private static bool IsEditable(IAccessible node, int id) => node.get_accRole(id) is (int)AccessibleRole.Text &&
        node.get_accState(id) is int state && (state & (int)AccessibleStates.Focused) != 0 &&
        (state & (int)(AccessibleStates.ReadOnly | AccessibleStates.Protected)) == 0;

    private static RectI VisibleText(IAccessible document, RectI viewport)
    {
        var nodes = new Queue<(IAccessible Node, int Child)>();
        nodes.Enqueue((document, 0));
        RectI result = default;
        int lines = 0;
        // 一次只读有限节点。用可见长文字的并集作粗略阅读区，正文原文不离开本次调用。
        for (int visited = 0; nodes.Count > 0 && visited < 72; visited++)
        {
            var (node, id) = nodes.Dequeue();
            var role = node.get_accRole(id);
            if (role is (int)AccessibleRole.StaticText or (int)AccessibleRole.Text && node.get_accName(id)?.Length >= 28)
            {
                node.accLocation(out int x, out int y, out int w, out int h, id);
                var visible = new RectI(x, y, w, h).Intersect(viewport);
                if (visible.Width >= Math.Max(100, viewport.Width / 6) && visible.Height >= 10)
                {
                    result = result.IsEmpty ? visible : RectI.FromEdges(Math.Min(result.Left, visible.Left),
                        Math.Min(result.Top, visible.Top), Math.Max(result.Right, visible.Right), Math.Max(result.Bottom, visible.Bottom));
                    lines++;
                }
            }
            if (id != 0 || nodes.Count > 56 || node.accChildCount <= 0) continue;
            var children = new object[Math.Min(node.accChildCount, 16)];
            if (NativeMethods.AccessibleChildren(node, 0, children.Length, children, out int read) < 0) continue;
            foreach (object child in children.Take(read))
            {
                if (child is IAccessible nested) nodes.Enqueue((nested, 0));
                else if (child is int index && index > 0) nodes.Enqueue((node, index));
            }
        }
        return lines >= 2 ? result : default;
    }

    private static RectI Rect(NativeMethods.Rect value) => RectI.FromEdges(value.Left, value.Top, value.Right, value.Bottom);

    private static string ClassName(nint handle)
    {
        var name = new StringBuilder(128);
        NativeMethods.GetClassName(handle, name, name.Capacity);
        return name.ToString();
    }
}
