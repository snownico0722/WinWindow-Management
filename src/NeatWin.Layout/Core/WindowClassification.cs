namespace NeatWin.Core;

// 进程、窗口类和标题只在这里解释为应用角色。浏览器角色与可读取渲染视口的进程各有适用范围。
internal static class WindowClassification
{
    // 托盘溢出、输入法、Win11 贴靠栏与瞬态启动面板属于系统/全局辅助表面。按窗口类识别，普通资源管理器和浏览器仍是工作窗口。
    internal static bool IsSystemSurface(string? windowClass) => windowClass is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
        "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow" or
        "XamlExplorerHostIslandWindow" or "Shell_InputSwitchTopLevelWindow" or
        "Shell_InputSwitchDismissOverlay" or "Shell_LightDismissOverlay" or
        "IME" or "MSCTFIME UI" or "OimeDirectUIWindow" or "OimeVoiceWaveWindow" or "CLaunchWndClass";

    internal static string ProcessKey(string? executable)
    {
        // 现场可传 ProcessName，离线记录也可传 Windows 路径。仅剥一层 .exe，保留 chrome.helper 等名称。
        var path = executable ?? "";
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        return (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).ToLowerInvariant();
    }

    private static bool IsChromiumApp(string app) =>
        app is "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or "opera_gx";
    private static bool IsBrowserApp(string app) => IsChromiumApp(app) || app is "firefox" or "arc";
    internal static bool IsBrowserProcess(string? executable) => IsBrowserApp(ProcessKey(executable));
    internal static bool SupportsChromiumViewport(string? executable) => IsChromiumApp(ProcessKey(executable));
    internal static bool IsCaptureToolProcess(string? executable) =>
        ProcessKey(executable) is "qq" or "qqkit" or "tim" or "wechat" or "weixin" or "qqscreenshot" or "wemeetapp" or "screentogif";

    internal static bool IsCrossProcessChatHelper(string? childExecutable, string? parentExecutable)
    {
        string child = ProcessKey(childExecutable), parent = ProcessKey(parentExecutable);
        return child is "qqkit" or "qqscreenshot" && (parent is "" or "qq" or "tim") ||
            child is "wechatappex" or "wechatplayer" or "wechatutility" && (parent is "" or "weixin" or "wechat") ||
            child is "wmexternal" or "xnnexternal" && (parent is "" or "wemeetapp");
    }

    private static bool QtFrame(string? name) => name?.StartsWith("Qt", StringComparison.Ordinal) == true &&
        name.EndsWith("QWindowIcon", StringComparison.Ordinal);
    private static bool IsOfficeApp(string app) => app is "wps" or "et" or "wpp";
    internal static bool IsSharedOfficeFrame(string? executable, string? windowClass) =>
        IsOfficeApp(ProcessKey(executable)) && QtFrame(windowClass);

    internal static WindowUseRole Identify(string? executable, string? windowClass,
        FocusedOfficeSurface surface = FocusedOfficeSurface.Unknown)
    {
        if (windowClass is "#32770" or "OperationStatusWindow" or "Ghost" or "Progman" or "WorkerW")
            return WindowUseRole.Unknown;
        if (windowClass is "CabinetWClass" or "ExploreWClass") return WindowUseRole.FileManager;
        if (windowClass is "Notepad" or "Notepad++" or "WordPadClass") return WindowUseRole.Editor;
        string app = ProcessKey(executable);
        if (IsOfficeApp(app) && QtFrame(windowClass))
            return surface switch
            {
                FocusedOfficeSurface.Grid when app is "wps" or "et" => WindowUseRole.Spreadsheet,
                FocusedOfficeSurface.Canvas when app is "wps" or "wpp" => WindowUseRole.Presentation,
                FocusedOfficeSurface.Document when app == "wpp" => WindowUseRole.Presentation,
                FocusedOfficeSurface.Document when app == "wps" => WindowUseRole.Document,
                _ => WindowUseRole.Unknown
            };
        return app switch
        {
            _ when IsBrowserApp(app) => WindowUseRole.Browser,
            "notepad" or "notepad++" or "wordpad" or "typora" or "sublime_text" => WindowUseRole.Editor,
            "winword" or "wps" when windowClass == "OpusApp" => WindowUseRole.Document,
            "excel" or "et" when windowClass == "XLMAIN" => WindowUseRole.Spreadsheet,
            "powerpnt" or "wpp" when windowClass == "PPTFrameClass" => WindowUseRole.Presentation,
            "wechat" or "weixin" when windowClass is "WeChatMainWndForPC" or "ChatWnd" || QtFrame(windowClass) => WindowUseRole.Chat,
            "qq" or "tim" when windowClass is "TXGuiFoundation" or "Chrome_WidgetWin_1" => WindowUseRole.Chat,
            "qqkit" when QtFrame(windowClass) => WindowUseRole.Chat,
            "code" or "code - insiders" or "cursor" or "obsidian" when windowClass == "Chrome_WidgetWin_1" => WindowUseRole.Editor,
            "devenv" or "rider64" or "idea64" when windowClass?.StartsWith("HwndWrapper", StringComparison.Ordinal) == true ||
                windowClass == "SunAwtFrame" => WindowUseRole.Editor,
            "windowsterminal" when windowClass == "CASCADIA_HOSTING_WINDOW_CLASS" => WindowUseRole.Editor,
            "papertodo" when windowClass?.StartsWith("HwndWrapper", StringComparison.Ordinal) == true => WindowUseRole.Note,
            "microsoft.notes" when windowClass == "Windows.UI.Core.CoreWindow" => WindowUseRole.Note,
            "lark" or "feishu" or "dingtalk" or "discord" when windowClass == "Chrome_WidgetWin_1" => WindowUseRole.Chat,
            "telegram" when QtFrame(windowClass) => WindowUseRole.Chat,
            "totalcmd64" or "totalcmd" or "doublecmd" => WindowUseRole.FileManager,
            _ => WindowUseRole.Unknown
        };
    }

    internal static SurfacePurpose ChatCaption(string caption)
    {
        string trimmed = caption.Trim();
        // 仅在内存中分类聊天附属表面，绝不写盘：媒体预览、聊天记录查询、收藏与设置均属临时查看表面（LocalDetail），
        // 而独立弹出的单人/群聊会话与音视频通话保留为任务表面，用户手动并排拉宽时可正常参与分栏。
        if (trimmed.EndsWith("的聊天记录", StringComparison.Ordinal)) return SurfacePurpose.LocalDetail;
        return trimmed switch
        {
            "微信音视频通话" or "语音通话" or "视频通话" or "WeChat Call" or "Voice Call" or "Video Call" => SurfacePurpose.IndependentTask,
            "QQ截图" or "QQ截屏" or "微信截图" or "微信截屏" or "截图翻译" or "截屏翻译" or "屏幕翻译" or
                "截图" or "截屏" or "贴图" or "Screen Capture" or "Screenshot Translation" => SurfacePurpose.CaptureTool,
            "图片和视频" or "个人资料" or "详细资料" or "联系人资料" or "群成员资料" or
                "设置" or "收藏" or "文件管理器" or "朋友圈" or "小程序" or "搜一搜" or "视频号" or
                "自定义共享画质" or "元宝笔记" or "Contact Info" or "Profile Card" or "Photos and Videos" => SurfacePurpose.LocalDetail,
            _ => SurfacePurpose.Unknown
        };
    }
}
