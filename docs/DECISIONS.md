# 产品与实现决定

## 2026-11-10 · 独立 WPF 重写

用户决定：用 C# / WPF 在 WinWindow-Management 新仓库实现 Windows 窗口管理系统，视觉语言使用 PaperTodo；取消透明度下限；加入此前讨论的强制最小化；界面不要局促。

### 确认的边界

1. 新产品不是 Pin-It 换壳，也不把旧版本的全部架构搬进来。现有交互经验是需求来源，不复制其实现。
2. 以 1180×760 DIP 工作台为默认；最小 880×560；没有最大尺寸限制。左侧导航、中间窗口列表、右侧操作面板；设置独立页。小窗只是可选模式，恢复时保持原尺寸和长期置顶选择。
3. 暖纸默认色从 PaperTodo 的配色语义取得：纸面 #FFF9EA、边框 #E0CEA7、正文 #33291E、弱文字 #8A7A63、强调 #8C7350。深色同属暖纸系。只采用配色/交互语言，不依赖 PaperTodo 运行时。
4. UI 名称使用“不透明度”，0% 为完全透明，100% 为完全不透明，避免滑块方向歧义。允许 0%；管理列表、托盘找回窗口、恢复全部快捷键不依赖目标窗口可见或能接收鼠标。
5. 用户说明的问题是“显示桌面后打开其他窗口，游戏也重新出现”，不是游戏主动抢前台。强制最小化执行一次有限流程：保存 WINDOWPLACEMENT → ShowWindowAsync(SW_FORCEMINIMIZE) → IsIconic → 异步 SetWindowPlacement 尝试 → 隐藏兜底 → 明确显示隐藏状态 → 一键恢复。不增加反复压后台的监听。
6. 用 WinEvent 事件合并更新列表和发现保存的规则，不以定时器持续全量扫描。激进置顶的 500ms 检查只针对本程序，且只有显式启用、手动置顶/小窗、可见且未最小化时才运行。
7. JSON 保存用户设置和精确窗口规则。规则按程序路径、窗口类、标题匹配；匹配不唯一时不猜测。收起操作不作为永久自动规则保存。
8. 异常中断的恢复记录只针对仍存在且 PID / 进程启动时间 / 窗口类匹配的目标。记录在修改前落盘。正常退出恢复目标，但保留用户保存的规则；用户主动“恢复全部”会更新对应规则为恢复后的值。
9. 权限不自动提权，不注入或结束被管理进程。目标使用自有逐像素透明且无法读取原状态时，只拒绝不透明度修改，其他操作仍可用。
10. 不凭构建成功宣称游戏兼容性。真实独占全屏、无边框、跨 DPI、提权目标与挂机行为列入人工验收。

## 本轮未扩展

不实现自动布局规则引擎、工作区编排、插件平台、窗口内嵌、远程管理或驱动级干预。这些不是首版验收前提，也不预留空实现。

## 实现参考

- PaperTodo: https://github.com/snownico0722/PaperTodo/blob/32bfe9e0533c704d07691a50a00f8b5345dc9a2e/src/Theme.cs
- ShowWindowAsync: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-showwindowasync
- SetWindowPlacement: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowplacement
- ShowWindow / SW_FORCEMINIMIZE: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-showwindow

SW_FORCEMINIMIZE 主要面向其他线程无响应的情况，不是绕过所有游戏窗口策略的特权。隐藏也不保证后台帧率、逻辑更新或不断线。
