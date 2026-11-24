namespace NeatWin.Core;

// 进程、窗口类和标题只在这里解释为应用角色。浏览器角色与可读取渲染视口的进程各有适用范围。
internal static class WindowClassification
{
    // 托盘溢出和输入法属于系统辅助表面。按窗口类识别，普通资源管理器和浏览器仍是工作窗口。
    internal static bool IsSystemSurface(string? windowClass) => windowClass is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
        "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow" or "IME" or "MSCTFIME UI";

    private static string ProcessKey(string? executable)
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
        ProcessKey(executable) is "qq" or "tim" or "wechat" or "weixin" or "qqscreenshot";

    private static bool QtFrame(string? name) => name?.StartsWith("Qt", StringComparison.Ordinal) == true &&
        name.EndsWith("QWindowIcon", StringComparison.Ordinal);
    private static bool IsOfficeApp(string app) => app is "wps" or "et" or "wpp";
    internal static bool IsSharedOfficeFrame(string? executable, string? windowClass) =>
        IsOfficeApp(ProcessKey(executable)) && QtFrame(windowClass);

    internal static WindowUseRole Identify(string? executable, string? windowClass,
        FocusedOfficeSurface surface = FocusedOfficeSurface.Unknown)
    {
        if (windowClass is "#32770" or "Progman" or "WorkerW") return WindowUseRole.Unknown;
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

    internal static SurfacePurpose ChatCaption(string caption) => caption.Trim() switch
    {
        "微信音视频通话" or "语音通话" or "视频通话" or "WeChat Call" or "Voice Call" or "Video Call" => SurfacePurpose.IndependentTask,
        "QQ截图" or "QQ截屏" or "微信截图" or "微信截屏" or "截图翻译" or "截屏翻译" or "屏幕翻译" or
            "截图" or "截屏" or "贴图" or "Screen Capture" or "Screenshot Translation" => SurfacePurpose.CaptureTool,
        "个人资料" or "详细资料" or "联系人资料" or "群成员资料" or "Contact Info" or "Profile Card" => SurfacePurpose.LocalDetail,
        _ => SurfacePurpose.Unknown
    };
}
