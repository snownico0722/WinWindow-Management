# NoFocusLoss 原生组件

`NoFocusLoss.cpp`、版本资源和 `LICENSE.txt` 来自 [NoFocusLoss a88dd66](https://github.com/snownico0722/NoFocusLoss/tree/a88dd66c22dcba4581f6f01b55ab4fc9ebafc67e/NoFocusLoss)。`minhook/` 包含原版所用 [MinHook f5485b8](https://github.com/TsudaKageyu/minhook/tree/f5485b8454544c2f034c78f8f127c1d03dea3636) 的全部 C 源文件和头文件。许可文件为 `MinHook-LICENSE.txt`。

仓库根目录执行 `./scripts/package.ps1 -TestNative` 后，CMake 分别构建 Win32 和 x64 目标，将对应指令解码器和 MinHook 静态链接进 DLL。打包脚本把两份原生 DLL 嵌入 WinWindow.exe。修改焦点消息、查询或鼠标处理后，重新运行同一命令即可测试并打包修改。

Windows 测试由每种架构的测试窗口在自身进程中加载对应 DLL，核对失焦消息、焦点与 `GetGUIThreadInfo` 查询（真实前台透传）、`SetCursorPos`/`ClipCursor` 拦截、失焦按键隔离、可选窗口内光标坐标锁定及卸载后的恢复行为。主界面默认采用无注入激活通知；开启常规设置中的增强注入后，按目标进程架构释放并加载对应原生 DLL，在关闭开关、切换回默认模式、恢复原样、移出窗口、异常恢复和退出时卸载还原。
