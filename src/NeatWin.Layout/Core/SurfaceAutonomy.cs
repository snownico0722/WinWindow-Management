using System.Text.RegularExpressions;

namespace NeatWin.Core;

internal static partial class SurfaceAutonomy
{
    [GeneratedRegex(@"^HwndWrapper\[([^;]*);([^;]*);[0-9a-fA-F\-]{36}\]$", RegexOptions.CultureInvariant)]
    private static partial Regex WpfWrapperGuidRegex();

    [GeneratedRegex(@"^WindowsForms10\.Window\.8\.app\.0\.[0-9a-fA-F_a-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex WinFormsHashRegex();

    [GeneratedRegex(@"^ATL:[0-9A-Fa-f]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AtlHexRegex();

    [GeneratedRegex(@"^Afx:[0-9A-Fa-f:]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AfxHexRegex();

    [GeneratedRegex(@"^Qt[0-9]+QWindow(Icon|ToolSaveBits|PopupSaveBits|ToolTipSaveBits)$", RegexOptions.CultureInvariant)]
    private static partial Regex QtVersionedClassRegex();

    private static readonly HashSet<string> IgnoredWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "IME", "MSCTFIME UI", "OimeDirectUIWindow", "OimeVoiceWaveWindow", "OimeTsfNotifyWindow",
        "SoPY_Comp", "SoPY_Status", "SoPY_UI", "SoPY_Hint", "Sogou_TSF_UI",
        "CherryWindowClass", "Tao Thread Event Target", "Winit Thread Event Target",
        "com.ccswitch.desktop-sic", "SysDragImage", "SysShadow", "tooltips_class32",
        "MicrosoftWindowsTooltip", "XSkinTooltip", "Xaml_WindowedPopupClass", "#32768",
        "ComboLBox", "Auto-Suggest Dropdown", "EVERYTHING_DROPDOWNLIST_BORDER",
        "OleMainThreadWndClass", "CicMarshalWndClass", "CiceroUIWndFrame", "CLIPBRDWNDCLASS",
        "Chrome_MessageWindow", "MessageWindowClass", "URL Moniker Notification Window",
        "Windows.Internal.Shell.TabProxyWindow", "ForegroundStaging", "EdgeUiInputTopWndClass",
        "EdgeUiInputWndClass", "ThumbnailDeviceHelperWnd", "LivePreview", "RCShadowWnd",
        "RCTransparentWnd", "ScreenShareTrackerWindow", "MilMagnifierHwndReimpl",
        "Qt*QWindowToolSaveBits", "Qt*QWindowPopupSaveBits", "MozillaDropShadowWindowClass",
        "PopupHost", "DropDown",
    };

    // 剥除每次进程重启随机变化的 GUID / ATL 实例地址，同时保留 Chrome_WidgetWin_1、CabinetWClass 等固定类名。
    internal static string CanonicalRuleClass(string? windowClass)
    {
        if (string.IsNullOrEmpty(windowClass)) return string.Empty;
        if (WpfWrapperGuidRegex().Match(windowClass) is { Success: true } wpf)
            return $"HwndWrapper[{wpf.Groups[1].Value};{wpf.Groups[2].Value}]";
        if (WinFormsHashRegex().IsMatch(windowClass)) return "WindowsForms10.Window.8";
        if (AtlHexRegex().IsMatch(windowClass)) return "ATL:*";
        if (AfxHexRegex().IsMatch(windowClass)) return "Afx:*";
        return windowClass;
    }

    internal static string CanonicalAutonomyClass(string? windowClass)
    {
        string normalized = CanonicalRuleClass(windowClass);
        return QtVersionedClassRegex().Match(normalized) is { Success: true } qt
            ? $"Qt*QWindow{qt.Groups[1].Value}"
            : normalized;
    }

    // Tier 0A：根本不是工作面或真实桌面遮挡物（如 18×18 事件靶、豆包输入法浮条、浏览器左下角无标题 URL 悬浮状态条、右下角通知气泡、底层壁纸挂件）。
    // 在采集入口直接丢弃，既防止污染 BeforeExposure 诱导主窗口跳变，也避免破坏 SolverFeedback 同伴窗口比对。
    internal static bool IsGhostSurface(string? executable, string? windowClass, RectI visualRect, RectI outerRect,
        RectI workArea, uint dpi, bool isForeground, bool isTopmost, bool isResizable, bool isToolWindow,
        bool clickThrough, bool isLayered, bool hasCaption, bool hasTitleText, nint owner = 0, bool noActivate = false)
    {
        if (outerRect.Width <= 24 && outerRect.Height <= 24) return true;
        string cls = windowClass ?? string.Empty;
        // 显式保护 PaperTodo / Sticky Notes 等桌面角落常驻便签锚点（其样式含 WS_EX_NOACTIVATE 且无标题栏，绝不可误判为弹出菜单丢弃）。
        if (WindowClassification.Identify(executable, cls) == WindowUseRole.Note) return false;
        if (clickThrough && isToolWindow && isLayered) return true;
        string canonical = CanonicalAutonomyClass(cls);
        // 通用下拉菜单、展开书签夹与无边框工具浮层剥离（覆盖 Chromium/Firefox/Electron/WPF/Qt，无论是否带标题或面积是否超宿主 30%）：
        // 有真实宿主的可见菜单需要保留几何，以便计算它对其他窗口的遮挡。
        if (owner != 0 && !hasCaption && !isResizable && !clickThrough) return false;
        if (noActivate && isToolWindow && !hasCaption && !isResizable) return true;
        if (cls is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow")
            return !isForeground; // 非前台时忽略，夺得前台时交给 Tier 0B 冻结工作区
        if (IgnoredWindowClasses.Contains(cls) || IgnoredWindowClasses.Contains(canonical)) return true;
        if (cls.EndsWith("ToolTipSaveBits", StringComparison.Ordinal) ||
            cls.StartsWith("QEventDispatcherWin32_Internal_Widget", StringComparison.Ordinal))
            return true;

        string app = WindowClassification.ProcessKey(executable);
        if (app == "imeservice") return true;
        double scale = Math.Max(96u, dpi) / 96d;
        // Chrome / Edge 底部无标题 URL 悬浮状态条与轻量弹出浮层，曾误作障碍物导致父浏览器平白横跳 389px。
        if (!isForeground && WindowClassification.IsBrowserProcess(executable) && cls == "Chrome_WidgetWin_1" && !hasTitleText &&
            (visualRect.Height <= (int)Math.Ceiling(52 * scale) || !hasCaption || !isResizable))
            return true;
        if (app == "steamwebhelper" && isTopmost && visualRect.Height <= (int)Math.Ceiling(140 * scale))
            return true;
        if (app is "qq" or "tim" or "weixin" or "wechat" && isTopmost && !isResizable && !workArea.IsEmpty &&
            visualRect.Area <= workArea.Area * .06)
            return true;
        // Portals 等桌面壁纸层挂件位于普通应用下方，不应充当遮挡障碍物切割可用走廊。
        if (app == "portals" && canonical.StartsWith("HwndWrapper[Portals", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    // Tier 0B：瞬态全屏或半屏系统/启动器浮层（Win11 顶部拖拽贴靠栏、Alt+Tab、CLaunch 快捷面板、全屏选区截图）。
    // 前台激活时冻结自动整理，非前台时绝不充当永久障碍物腰斩后台浏览器高度。
    internal static bool IsTransientWorkspaceFreezeOverlay(string? executable, string? windowClass,
        RectI visualRect, RectI workArea, bool isTopmost, bool isToolWindow)
    {
        string cls = windowClass ?? string.Empty;
        string canonical = CanonicalAutonomyClass(cls);
        string app = WindowClassification.ProcessKey(executable);
        if (cls is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow" or
            "XamlExplorerHostIslandWindow" or "Shell_InputSwitchTopLevelWindow" or
            "Shell_InputSwitchDismissOverlay" or "Shell_LightDismissOverlay")
            return true;
        if (app == "claunch" && cls == "CLaunchWndClass") return true;
        if (app is "startmenuexperiencehost" or "searchhost" or "gamebar" or "xboxgamebarwidgets" &&
            cls == "Windows.UI.Core.CoreWindow")
            return true;
        if (app == "shellhost" && cls == "ControlCenterWindow") return true;
        if (app == "consent" && cls == "Credential Dialog Xaml Host") return true;
        if (canonical.StartsWith("HwndWrapper[ScreenToGif", StringComparison.OrdinalIgnoreCase) && isTopmost)
            return true;
        if (!workArea.IsEmpty && isTopmost && (isToolWindow || WindowClassification.IsCaptureToolProcess(executable)) &&
            visualRect.Intersect(workArea).Area >= workArea.Area * .90)
            return true;
        return false;
    }

    // Tier 1A：免排浮动弹窗、模态对话框、挂起幽灵窗及跨进程媒体/历史查看器（如 QQKit『图片和视频』、微信『图片和视频』、文件冲突框）。
    internal static bool IsExemptFloatingPopup(string? executable, string? windowClass, bool isResizable,
        bool hasMaximizeBox, long exStyle, RectI visualRect, uint dpi, SurfacePurpose chatPurpose = SurfacePurpose.Unknown,
        string? parentExecutable = null)
    {
        const long wsExDlgModalFrame = 0x00000001L;
        string cls = windowClass ?? string.Empty;
        string canonical = CanonicalAutonomyClass(cls);
        string app = WindowClassification.ProcessKey(executable);
        string parentApp = WindowClassification.ProcessKey(parentExecutable);

        if (app == "dwm" && cls is "Ghost" or "#32770") return true;
        if (cls is "#32770" or "OperationStatusWindow" || (exStyle & wsExDlgModalFrame) != 0) return true;
        // 不可缩放固定尺寸窗口（如豆包输入法设置、登录框、驱动控制面板）严禁参与自动分栏推挤。
        if (!isResizable && app is "doubaoimesettings" or "pinyinup") return true;
        if (canonical is "Qt*QWindowToolSaveBits" or "Qt*QWindowPopupSaveBits") return true;
        if (app is "hipstray" or "sgtool" or "pinyinup" && canonical == "ATL:*") return true;

        // 跨进程附属查看器进程：NTQQ 唤起的 QQKit.exe、微信唤起的 WeChatAppEx.exe、腾讯会议外部浮窗进程。
        if (chatPurpose == SurfacePurpose.IndependentTask) return false;
        if (WindowClassification.IsCrossProcessChatHelper(executable, parentExecutable) &&
            (chatPurpose != SurfacePurpose.Unknown || !hasMaximizeBox && !isResizable))
            return true;
        if (parentApp is "qq" or "tim" && app != parentApp && canonical == "Qt*QWindowIcon" && !hasMaximizeBox && !isResizable) return true;

        // 同进程聊天附属查看器（『图片和视频』、『群聊的聊天记录』、『收藏』、『设置』），独立于宿主面积比例直接免排；
        // 但保留独立双人/群聊子窗口（SurfacePurpose.Unknown 且具备完整可缩放框）继续作为正常聊天任务参与手势并排。
        if (chatPurpose is SurfacePurpose.LocalDetail or SurfacePurpose.CaptureTool) return true;
        double scale = Math.Max(96u, dpi) / 96d;
        if (app is "qq" or "tim" && !isResizable && !hasMaximizeBox &&
            visualRect.Width <= (int)Math.Ceiling(500 * scale) && visualRect.Height <= (int)Math.Ceiling(580 * scale))
            return true;
        if (app == "wemeetapp" && !hasMaximizeBox &&
            visualRect.Width <= (int)Math.Ceiling(360 * scale) && visualRect.Height <= (int)Math.Ceiling(500 * scale))
            return true;
        return false;
    }

    // Tier 2：准全屏窗口化游戏（如 2528×1372 的魔兽世界、原神），未挂 WS_MAXIMIZE 但已占满工作区 88% 以上，禁止被切窗排布腰斩。
    internal static bool IsProtectedNearFullscreenSurface(string? executable, RectI visualRect, RectI workArea)
    {
        if (workArea.IsEmpty || visualRect.IsEmpty) return false;
        string app = WindowClassification.ProcessKey(executable);
        bool gameApp = app is "wow" or "yuanshen" or "starrail" or "zenlesszonezero" or "cs2" or "dota2";
        return gameApp && visualRect.Width >= workArea.Width * .90 && visualRect.Height >= workArea.Height * .88;
    }
}
