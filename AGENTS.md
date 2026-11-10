# 项目规则

先读 README.md 与 docs/DECISIONS.md。用户决定高于历史实现；遇到冲突先纠正设计，不用旧文档反驳需求。

- C# / WPF / Win32；独立产品，不复制 Pin-It 的 Rust/Slint 实现。
- 先设计后实施。沿用 PaperTodo 暖纸配色。主界面是宽松、可缩放的两栏工作区，不恢复侧栏、宣传语、大标题、重复状态或卡片套卡片。
- 业务围绕 WindowManager；Win32 只放 Native；持久化由 StateStore 管理。不按方法拆文件，不预建框架、插件平台或数据库。
- 不透明度必须支持 0–100%；找回入口不可依赖目标可见或能接收鼠标。
- 强制最小化是一轮有限操作，验证实际状态，失败则隐藏。不加持续压后台监听或注入。
- 管理句柄前检查 PID / 启动时间 / 窗口类；只恢复我们修改的样式位。
- 危险修改之前持久化恢复记录；写失败就不修改。滑块合并请求，不逐帧写盘。
- 外部窗口同步调用不在 WPF 线程执行。同一目标不并发修改，阻塞目标不占住其他目标与主窗口交互。
- 自身置顶由 Native.SetOwnTopmost 单一管理，不能混写 WPF Topmost。失焦取消临时置顶时让到新前台之后；主动唤醒的临时抬层必须立即收尾。
- 关闭可留托盘；真正退出尽力恢复，但不能因某个目标失败或卡住而拒绝退出。未完成记录留下供下次启动恢复，不假报成功，不清空用户规则。
- 跨权限只放行无参数的专用 Show 消息，检查发送结果；不自动提权。
- 少量有价值的真实 Windows 状态转换测试优先，不堆源码字符串断言。编译通过不等于游戏或跨权限兼容认证。
- 验证：`dotnet build src/WinWindow/WinWindow.csproj -c Release`；`dotnet src/WinWindow/bin/Release/net10.0-windows/WinWindow.dll --self-test TestResults`。
