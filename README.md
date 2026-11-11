# WinWindow Management

C# / WPF / .NET 10 窗口管理工具，采用 PaperTodo 暖纸配色。默认 1180×760 DIP，可缩放、最大化；每个管理对象的操作直接放在同一行，小窗为可选入口。

## 使用

从 Actions 的 `WinWindow-preview-and-tests` 制品取得 `WinWindow-win-x64.zip`，解压运行 `WinWindow.exe`。自包含构建无需另装 .NET；未签名版本可能显示 SmartScreen 提示。

“置顶窗口”选择目标，或从“拖动置顶”拖到目标。每行直接调整 0–100% 不透明度、鼠标穿透和置顶；单击标题唤回目标，收起按钮随状态变为恢复。图钉关闭仍保留管理，行尾 × 取消置顶并移出，不关闭目标程序。右键提供记住设置、恢复原样和复制信息。

点击左上角名称收起为小窗，再次点击展开；拖动名称或标题栏移动。展开保留原窗口尺寸、最大化和长期置顶选择。顶栏齿轮打开设置，支持浅色/深色、关闭退出、开机启动与快捷键编辑。

| 快捷键 | 操作 |
|---|---|
| Win + Ctrl + Space | 唤出主窗口 |
| Win + Ctrl + T | 切换当前窗口置顶 |
| Win + Ctrl + M | 强制最小化 |
| Win + Ctrl + Shift + R | 恢复全部原始状态 |
| Win + Ctrl + OemPlus / OemMinus | 增加 / 降低不透明度 |

配置位于 `%LOCALAPPDATA%\WinWindow-Management`。`--dev` 使用独立目录和单实例锁，测试可配合 `--data-dir <隔离目录>`。

“全部取消置顶”保留列表和其他设置；“恢复全部原样”撤销管理修改。真正退出最多等待 900ms 的恢复工作，未完成记录保留在 `recovery.json`，下次启动继续恢复，不因其他程序卡住而锁住退出。

## 构建与验证

```powershell
dotnet build src/WinWindow/WinWindow.csproj -c Release
dotnet run --project src/WinWindow/WinWindow.csproj -- --dev
dotnet src/WinWindow/bin/Release/net10.0-windows/WinWindow.dll --self-test TestResults
dotnet publish src/WinWindow/WinWindow.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/WinWindow
```

自动测试使用真实 WinForms/WPF 目标及完整主程序，验证原生状态、行内操作、收起/恢复、小窗、失焦退让、单实例唤醒、阻塞目标下的退出及恢复日志，并输出实际界面截图。

## 边界与结构

0.1 开发版，不是所有游戏的兼容认证。强制最小化只执行一次有限流程，必要时隐藏，不持续压后台，不注入。最小化/隐藏不保证游戏继续渲染或挂机。跨权限与混合 DPI 人工验收见 [MANUAL_TESTS.md](docs/MANUAL_TESTS.md)。

`Native` 封装 Win32；`WindowManager` 管理操作与恢复；`WindowRow` 维护单行直接交互与编辑状态；`DesktopIntegration` 管理托盘、快捷键和唤醒；`StateStore` 负责持久化。外部窗口修改不在 UI 线程同步等待。

产品决定见 [DECISIONS.md](docs/DECISIONS.md)，维护规则见 [AGENTS.md](AGENTS.md)。未移植 Pin-It 源码，不预建插件平台、布局引擎或数据库，尚未选择开源许可证。
