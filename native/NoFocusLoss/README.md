原生源码、版本资源及 LICENSE.txt 取自 [snownico0722/NoFocusLoss 的 a88dd66](https://github.com/snownico0722/NoFocusLoss/tree/a88dd66c22dcba4581f6f01b55ab4fc9ebafc67e/NoFocusLoss)。构建使用该提交所固定的 MinHook，MinHook-LICENSE.txt 包含其依赖的许可声明。

打包脚本把两个架构的 DLL 嵌入 WinWindow.exe。Windows 测试由每种架构的独立测试窗口在自身进程中加载对应 DLL，并在卸载前后核对行为。本阶段主界面的后台保持开关仍使用应用激活通知。
