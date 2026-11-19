# WinWindow Management

C# / WPF / .NET 10 窗口管理工具，采用低饱和暖灰石与纯白卡片视觉。默认 580×500 DIP（最小 500×380），可自由缩放、最大化；每个管理对象的操作直接聚合在同一行卡片中，悬浮胶囊小窗为可选驻留入口。

## 使用

日常测试和预发布版使用标准包 `WinWindow-win-x64.zip`。安装 **.NET 10 Desktop Runtime x64（Windows 桌面运行时）** 后，解压并运行 `WinWindow.exe`。正式版还提供 `WinWindow-win-x64-self-contained.zip`，适合没有安装运行时的电脑。[下载 .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)

在 GitHub Releases 下载发布包，在 Actions 的 `WinWindow-preview-and-tests` 制品中下载开发包。

点击 **添加窗口**，选择目标即可加入管理。要同时置顶，可点候选行的 **添加并置顶**，或从 **拖拽置顶** 拖到目标。拖动时目标会出现描边，松开后短暂闪示；按 Esc 可取消。用筛选按钮或 Ctrl+F 搜索已管理的窗口。

每张卡片提供后台保持、置顶、鼠标穿透、收起／恢复和不透明度调整。滑块取得焦点后可用滚轮微调，点击百分比可输入精确值。低于 15% 时，卡片提供开启穿透的捷径。点击标题唤回窗口，行尾 × 移出管理，右键可以记住设置或恢复原样。

点击左上角 WinWindow 可收起为 240×46 的小窗，支持拖拽置顶和整理桌面，再点一次恢复之前的尺寸。顶栏齿轮打开统一设置。快捷键可一次修改多项，点「应用」保存，按 Esc 还原当前输入框。

| 快捷键 | 操作 |
|---|---|
| Win + Ctrl + Space | 唤出主窗口 |
| Win + Ctrl + T | 切换当前窗口置顶 |
| Win + Ctrl + M | 强制最小化 |
| Win + Ctrl + Shift + R | 恢复全部原始状态 |
| Win + Ctrl + OemPlus / OemMinus | 增加 / 降低不透明度 |

配置位于 `%LOCALAPPDATA%\WinWindow-Management`。`--dev` 使用独立目录和单实例锁，测试可配合 `--data-dir <隔离目录>`。

“全部取消置顶”保留列表和其他设置；“恢复全部原样”撤销管理修改。真正退出最多等待 900ms 的恢复工作，未完成记录保留在 `recovery.json`，下次启动继续恢复，不因其他程序卡住而锁住退出。

## 桌面整理与游戏后台保持

单击主界面底栏 **整理桌面**，或在胶囊与托盘中选择同名命令。旁边的 **撤销整理** 可以还原上一轮调整。范围是整个桌面的可见普通窗口。已置顶、穿透、最大化和全屏窗口保留位置。

底栏「整个桌面」旁可以直接选择 **手动、轻度、完整、自动**。轻度辅助会在拖动后微调当前窗口，完整辅助会在拖动后整理同组窗口，自动档还会响应桌面窗口变化。选择立即生效，下次启动沿用。顶栏设置中的 **桌面整理** 可调整算法和观看偏好。**快捷键** 可批量编辑、交换后应用。**数据与记录** 可以启停、导出和清空本机布局记录。默认手动整理，记录器默认暂停。原有整理参数沿用当前 WinWindow 数据目录的 `Layout`。

在游戏所在行打开 **后台保持**。切到其他应用后，WinWindow 向目标窗口补发应用激活通知，键盘焦点仍由前台应用持有。关闭开关、移出窗口或退出时恢复正常通知状态。这个模式面向根据应用激活通知暂停的程序。自行检查真实前台窗口、后台手柄输入或最小化渲染的游戏有各自的限制。

## 构建与验证

```powershell
dotnet build src/WinWindow/WinWindow.csproj -c Release
dotnet run --project src/WinWindow/WinWindow.csproj -- --dev
dotnet src/WinWindow/bin/Release/net10.0-windows/WinWindow.dll --self-test TestResults
./scripts/package.ps1
```

默认打包命令在 `artifacts/` 生成标准 ZIP。Actions 制品包含应用包、界面截图和测试结果，省去源码压缩包。正式发版时，工作流自动将两份 ZIP 附到 Release。用户明确要求额外打包时，本地使用 `./scripts/package.ps1 -IncludeRuntime`，或手动运行 Actions 并勾选「额外打包一份内置 .NET 的 Windows x64 版本」。

自动测试使用真实 WinForms/WPF 目标及完整主程序，验证原生状态、行内操作、收起/恢复、悬浮胶囊、失焦退让、单实例唤醒、阻塞目标下的退出及恢复日志，并输出实际界面截图。

## 边界与结构

0.1 开发版，不是所有游戏的兼容认证。强制最小化只执行一次有限流程，必要时隐藏，不持续压后台，不注入。最小化/隐藏不保证游戏继续渲染或挂机。跨权限与混合 DPI 人工验收见 [MANUAL_TESTS.md](docs/MANUAL_TESTS.md)。

`Native` 封装 Win32 与图标缓存；`WindowManager` 管理操作与恢复；`WindowRow` 维护单行卡片交互与编辑状态；`DesktopIntegration` 管理托盘、快捷键和唤醒；`StateStore` 负责持久化。外部窗口修改不在 UI 线程同步等待。

产品决定见 [DECISIONS.md](docs/DECISIONS.md)，维护规则见 [AGENTS.md](AGENTS.md)。未移植 Pin-It 源码，布局程序集沿用 NeatWin 源码，不预建插件平台或数据库，尚未选择开源许可证。
