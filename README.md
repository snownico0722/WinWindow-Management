# WinWindow Management

C# / WPF / .NET 10 窗口管理工具，采用 PaperTodo 暖纸配色。默认 1180×760 DIP，可缩放、最大化；窗口列表与操作面板两栏，小窗为可选入口。

## 功能

- 搜索与筛选窗口；置顶、拖拽置顶、鼠标穿透、不透明度 **0–100%**。
- 单次强制最小化；未成功则隐藏，保留恢复入口。不持续压后台，不注入进程。
- “恢复窗口”保留管理设置；“恢复原样”撤销修改；隐藏和完全透明的窗口仍可从托盘或快捷键找回。
- 记住窗口设置，按路径、类和标题精确匹配，同名多窗口不猜测。
- 托盘、可编辑全局快捷键、开机启动、关闭留托盘或直接退出、浅色与深色主题。
- 自身手动置顶、焦点临时置顶、小窗和可选激进模式分开管理，失焦不会主动重新激活。

## 使用

从 Actions 的 `WinWindow-preview-and-tests` 制品取得 `WinWindow-win-x64.zip`，解压运行 `WinWindow.exe`。自包含构建无需另装 .NET；未签名版本可能显示 SmartScreen 提示。

| 快捷键 | 操作 |
|---|---|
| Win + Ctrl + Space | 唤出主窗口 |
| Win + Ctrl + T | 切换当前窗口置顶 |
| Win + Ctrl + M | 强制最小化 |
| Win + Ctrl + Shift + R | 恢复全部原始状态 |
| Win + Ctrl + OemPlus / OemMinus | 增加 / 降低不透明度 |

顶栏齿轮打开设置。配置保存在 `%LOCALAPPDATA%\WinWindow-Management`；`--dev` 使用独立目录和单实例锁。

真正退出会尽力恢复目标，最多等待 900ms。未完成的记录保留在 `recovery.json`，下次启动继续恢复；不因其他程序卡住而锁住本程序退出。请勿把恢复记录手动删掉当作问题已解决。

## 构建与验证

```powershell
dotnet build src/WinWindow/WinWindow.csproj -c Release
dotnet run --project src/WinWindow/WinWindow.csproj -- --dev
dotnet src/WinWindow/bin/Release/net10.0-windows/WinWindow.dll --self-test TestResults
dotnet publish src/WinWindow/WinWindow.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/WinWindow
```

自动测试启动真实 WinForms/WPF 窗口及完整主程序，检查原生状态、失焦退让、隐藏/最小化唤醒、阻塞目标下的响应与退出、恢复日志，并输出浅色、深色、小窗和最小尺寸截图。开发测试可配合 `--dev --data-dir <隔离目录>`；不会读写正式状态。

## 边界与结构

这是 0.1 开发版，不是所有游戏的兼容认证。最小化/隐藏不保证后台渲染或挂机。跨权限唤醒已实现，但自动测试不代替真实跨权限桌面和混合 DPI 验收，见 [MANUAL_TESTS.md](docs/MANUAL_TESTS.md)。

`Native` 封装 Win32；`WindowManager` 管理操作与恢复；`DesktopIntegration` 管理托盘、快捷键和唤醒；`StateStore` 负责持久化；主窗口与主题字典负责交互。外部窗口修改不在 UI 线程同步等待。

产品决定见 [DECISIONS.md](docs/DECISIONS.md)，维护约束见 [AGENTS.md](AGENTS.md)。未移植 Pin-It 的 Rust / Slint 代码，不预建插件平台、布局引擎或数据库。此仓库尚未选择开源许可证。
