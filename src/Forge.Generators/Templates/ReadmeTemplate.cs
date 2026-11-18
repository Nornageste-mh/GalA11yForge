namespace GalA11yForge.Generators.Templates;

/// <summary>面向使用者的 README：说人话，讲清「能做什么、不能做什么」。</summary>
internal static class ReadmeTemplate
{
    internal const string Text = """
# {{DISPLAY}}

由 GalA11y Forge（无障碍补丁工坊）生成 · 生成器版本 {{GENERATOR_VERSION}}
目标游戏：{{GAME_NAME}} · 引擎：Unity Mono（BepInEx 5 / HarmonyX）

## 这是什么

这是一个**可编译的补丁工程**：把游戏的 BepInEx 插件壳、语音桥（屏幕阅读器/系统语音）和
分析阶段给出的 Harmony 钩子拼在一起。生成器会按分析证据里每个钩子的完整签名，把带
字符串参数的钩子接到朗读出口（主钩子朗读，其余只进重读缓冲）；没有字符串参数、但证据
给出实例文本字段（TMP_Text/UI.Text）的钩子，会反射读字段兜底朗读；两者都没有的目标只做
命中计数。

它**不是**「一键让这个游戏完全可读屏」的成品：选项、菜单、图片描述、UI 导航不在本工程
范围；哪些调用才是真正的台词，仍要靠真机日志与人工验收确认。

## 现在能做什么

| 能力 | 状态 |
|---|---|
| BepInEx 5 插件加载、配置项生成 | 已生成，可编译 |
| 语音桥初始化（NVDA/Tolk → 系统语音的降级链） | 已内嵌，来自 Framework（MIT） |
| `=` 键重读上一段 | 已接线，可在配置里改键 |
| 钩子目标存在性校验 + 命中日志 | 已生成 {{HOOK_COUNT}} 个钩子 |
| 把游戏台词自动送进朗读 | 分析证据里带字符串参数或实例文本字段的钩子已接线；其余只记命中，需人工 |
| UI 导航、选项朗读、图片描述 | **未实现** |
| 自动构建、自动部署到游戏目录 | **不做**；构建要你手动跑脚本，安装要你手动复制 |

## 目录里有什么

| 路径 | 作用 |
|---|---|
| `{{ASSEMBLY}}.csproj` | 插件工程（netstandard2.1，引用 BepInEx/UnityEngine，不引用 Assembly-CSharp） |
| `Plugin.cs` | 插件入口：配置、语音桥初始化、钩子挂载、重读键 |
| `Hooks/Hook_*.cs` | 每个 Hook 补丁点一个钩子：带字符串参数或实例文本字段的按签名/字段接朗读，其余只记命中 |
| `Platform/*.cs` | 内嵌语音桥（Framework 的 L1 + A11yHost 契约层 + 生成的 SpeechGate 朗读闸门），命名空间已替换为 `{{NS}}` |
| `profile.json` | 生成结果清单：钩子、跳过的补丁点、待人工项计数、配置默认值 |
| `forge.manifest.json` | 生成器账本：本次写了哪些文件、各自 SHA-256；重新生成时据此清理旧文件 |
| `build.ps1` | 只编译、不写游戏目录的构建脚本 |
| `README.md` / `安装说明.md` / `卸载说明.md` / `NOTICE.md` | 说明与许可 |

## 补丁点清单

{{PATCH_POINT_TABLE}}

## 构建（三步）

1. 自行安装 **BepInEx 5.4.x x64** 到游戏根目录（本工程不附带 BepInEx）。
2. 安装 **.NET SDK 8**（`dotnet --version` 能看到 8.x）。
3. 在本目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

游戏不在默认路径时：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -GameDir "D:\Games\你的游戏" -BepInExDir "D:\Games\你的游戏\BepInEx\core"
```

构建产物：`bin\Release\{{ASSEMBLY}}.dll`。

## 安装与卸载

- 安装：见 `安装说明.md`（手动复制 dll，脚本不替你写游戏目录）。
- 卸载：见 `卸载说明.md`（删插件目录与配置文件，不动游戏本体）。

## 怎么确认它真的生效了

1. 启动游戏，看 BepInEx 控制台/日志里应出现 `[Hook] 已挂载 ...`，说明钩子挂上了。
2. 对话时日志应出现 `[Speech] ...`；没有的话先确认这个钩子的目标是否带字符串参数（见 `profile.json` 的 `hooks[].status`）。
3. 若出现 `目标不在当前游戏程序集里，已跳过`：说明游戏版本和分析时不一致，**不要**当成成功。
4. 按 `=` 应该重读上一段；没声音时先看日志里的「语音后端」一行。
5. 配置在 `BepInEx\config\{{GUID}}.cfg`；把「诊断 → 详细日志」设为 `true` 可看到钩子命中与朗读细节。

## 已知限制（如实说明）

- 自动朗读只覆盖「分析证据里给出字符串参数或实例文本字段」的钩子；主朗读钩子固定取 plan 里第一个带字符串参数的钩子（没有则取第一个带实例文本字段的）。若游戏真正的台词来自别的方法或别的字段，需要人工按日志补钩子或调整 plan 后重新生成。
- 证据里既没有字符串参数、也没有实例文本字段的钩子只做命中计数，**不会**把台词送去朗读。
- 计划里有 {{SKIPPED_COUNT}} 个非 Hook 补丁点（配置/脚本/资源等）本生成器未实现，见 `profile.json` 的 `skippedPatchPoints`。
- 钩子目标来自分析阶段的反编译证据；游戏更新后目标可能改名/消失，需要重新分析生成。
- 不含任何游戏台词、图片、音频、字体或游戏原始文件；也不捆绑 BepInEx、Tolk、NVDA 控制客户端、ZDSR 等第三方二进制。
- 生成器不写游戏目录：构建只写本工程 `bin/`，安装复制动作由你手动完成。

## 来源与许可

- 语音桥来自无障碍补丁工坊 Framework（MIT，Copyright (c) 2026 Nornageste-mh）；
  本次内嵌内容的指纹（按文件排序后取 SHA-256）为 `{{FRAMEWORK_FINGERPRINT}}`。
- BepInEx 5.x：MIT；HarmonyX：MIT。详见 `NOTICE.md`。
- 本工程由 GalA11y Forge 生成，不包含游戏厂商资源；游戏本身仍需你合法拥有。

## 生成参数（便于复现）

| 项 | 值 |
|---|---|
| 游戏 slug | `{{SLUG}}` |
| 命名空间 | `{{NS}}` |
| 程序集 | `{{ASSEMBLY}}` |
| 插件 GUID | `{{GUID}}` |
| 插件版本 | `{{VERSION}}` |
| 生成器版本 | `{{GENERATOR_VERSION}}` |
""";
}
