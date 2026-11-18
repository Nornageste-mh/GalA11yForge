# Forge.ExternalReader 第三方依赖与许可证

本原型只做“外部只读附着”，**不注入目标进程、不改目标目录**。

| 依赖 | 版本 | 用途 | 许可证 | 分发方式 |
|---|---|---|---|---|
| FlaUI.UIA3 | 4.0.0 | UI Automation 客户端封装（`AutomationElement`/Patterns） | MIT（`~/.nuget/packages/flaui.uia3/4.0.0/LICENSE.txt`） | NuGet 引用，随原型 bin 输出 |
| FlaUI.Core | 4.0.0（传递依赖） | FlaUI 核心类型 | MIT | 同上 |
| Interop.UIAutomationClient | 10.19041.0（传递依赖） | UIA COM 互操作程序集（FlaUI 作者打包的 UIAutomationClient 互操作） | MIT | 同上 |
| Microsoft.Windows.SDK.NET.Ref / WinRT.Runtime | 随 .NET 8 SDK（TFM `net8.0-windows10.0.19041.0`） | CsWinRT 投影，供 `Windows.Media.Ocr` | MIT（.NET 运行时） | 构建期引用 + 输出目录 |
| System.Drawing.Common | 随 `Microsoft.WindowsDesktop.App` 共享框架 | 截屏 / PNG 取证 | MIT | .NET 共享框架自带，不单独分发 |
| Windows.Media.Ocr | 系统组件（`C:\Windows\OCR\<lang>\MsOcrRes.orp`） | 视觉兜底识别 | 系统 API | 不随包，不用即无 |
| nvdaControllerClient64.dll | 用户环境已有（LGPL-2.1，R6 已记） | 可选 NVDA 直连 | LGPL-2.1 | **不随包分发**，运行时从用户机器探测加载 |
| SAPI5（ISpVoice） | 系统组件 | 最后兜底朗读 | 系统 API | 纯 vtable P/Invoke，无 DLL 依赖 |

口径：
- 本原型**没有**链接 Textractor / LunaTranslator / NVDA 的源码（均为 GPL/copyleft），
  NVDA 只经其官方 controller client 的导出函数调用（LGPL-2.1，允许动态调用）。
- FlaUI 是 MIT，可随包；`Interop.UIAutomationClient` 同为 MIT（同一作者打包）。
- 报告中引用上面 3 个 NuGet 包时，只写“MIT + 版本 + 包路径”，不复制其源码。
