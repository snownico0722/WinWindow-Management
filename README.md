# WinWindow Management

**Windows 窗口管理工作台 · C# / WPF 独立实现 · PaperTodo 暖纸视觉语言**

不是把 Pin-It 的狭小面板换一种语言，而是以窗口管理系统为目标重新设计交互。默认 1180×760 DIP，支持自由缩放、最大化；小窗只是可选入口。

## 首版功能

- 浏览与搜索当前应用窗口，分为全部窗口、已管理、已收起。
- 窗口置顶、拖拽置顶、不透明度 **0–100%**、鼠标点击穿透。
- **强制最小化 → 检查真实最小化状态 → 隐藏兜底 → 一键恢复**。单次操作，不反复压后台、不注入游戏。
- 恢复窗口保留管理设置；恢复原始状态撤销本程序的修改。隐藏与完全透明的窗口仍可从列表、托盘和恢复快捷键找回。
- 保存窗口设置，按程序路径、窗口类、标题精确匹配；同名多窗口不猜测。
- 托盘、全局快捷键、开机启动、关闭藏托盘或直接退出。
- 主程序长期置顶、焦点临时置顶、可选小窗及可选激进置顶；与被管理窗口状态分开。
- 浅色 / 深色暖纸主题；JSON 设置与异常中断恢复记录。

## 使用

从 Actions 的 `WinWindow-preview-and-tests` 制品取得 `WinWindow-win-x64.zip`，解压运行 `WinWindow.exe`。自包含构建不要求另装 .NET；未签名版本可能显示 SmartScreen 提示。

| 默认快捷键 | 操作 |
|---|---|
| Win + Ctrl + Space | 唤出工作台 |
| Win + Ctrl + T | 切换当前窗口置顶 |
| Win + Ctrl + M | 强制最小化当前窗口 |
| Win + Ctrl + Shift + R | 恢复全部窗口原始状态 |
| Win + Ctrl + OemPlus / OemMinus | 增加 / 降低不透明度 |

设置页可重新录入组合键或留空停用。配置位于 `%LOCALAPPDATA%\WinWindow-Management`；`--dev` 使用单独的数据目录与单实例锁。

## 构建

Windows + .NET 10 SDK：

```powershell
dotnet build src/WinWindow/WinWindow.csproj -c Release
dotnet run --project src/WinWindow/WinWindow.csproj -- --dev
dotnet src/WinWindow/bin/Release/net10.0-windows/WinWindow.dll --self-test TestResults
dotnet publish src/WinWindow/WinWindow.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/WinWindow
```

自动测试使用独立原生测试窗口，检查透明度、置顶、穿透、真实最小化、隐藏兜底、恢复与界面布局，并输出浅色/深色 WPF 截图。实机游戏验收见 [MANUAL_TESTS.md](docs/MANUAL_TESTS.md)。

## 边界

最小化/隐藏不保证游戏继续渲染或正常挂机；权限限制、独占全屏和逐像素透明窗口仍受 Windows / 应用行为约束。失败会报告并保留恢复入口，不能把“已发送 API 请求”当作“操作成功”。

这是可审查的 **0.1 开发首版**，不是已完成所有游戏兼容性验证的正式发布。当前不预建自动布局引擎、插件平台或数据库。

## 结构

- `Native.cs`：Win32 与窗口身份校验。
- `WindowManager.cs`：被管理窗口、可恢复操作、精确保存规则。
- `DesktopIntegration.cs`：托盘、快捷键、窗口事件与单实例唤醒消息。
- `MainWindow.xaml` / `.cs`：宽松工作台与交互。
- `Theme.xaml` / `Palette.cs`：统一控件和暖纸配色。
- `StateStore.cs`：设置与操作前恢复记录。

产品决定见 [DECISIONS.md](docs/DECISIONS.md)，维护约束见 [AGENTS.md](AGENTS.md)。配色参考 PaperTodo `32bfe9e0533c704d07691a50a00f8b5345dc9a2e`；未移植 Pin-It 的 Rust / Slint 代码。此仓库尚未选择开源许可证。
