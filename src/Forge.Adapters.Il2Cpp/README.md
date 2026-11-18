# Forge.Adapters.Il2Cpp —— Unity IL2CPP 适配器（BepInEx 6 / Il2CppInterop）

面向 Unity **IL2CPP** 后端 Galgame 的四接口适配器：识别 → 静态分析 → 补丁规划 → 生成 BepInEx 6 插件工程。
Mono 路线（BepInEx 5）见 `src/Forge.Adapters.UnityMono`；两者互不依赖，按指纹选择。

- 程序集：`Forge.Adapters.Il2Cpp.dll`（net8.0）
- 类型：`GalA11yForge.Adapters.Il2Cpp.Il2CppAdapter`
- EngineId：`unity_il2cpp`
- 版本：`0.1.0.0`

## 1. 四接口

| 接口 | 方法 | 行为 |
|---|---|---|
| `IEngineDetector` | `DetectAsync` | 先跑 `GalA11yForge.Detection.EngineDetector`，族必须是 `unity_il2cpp`；指纹漏检但存在 `GameAssembly.dll` 时按 IL2CPP 接受并把置信度压到 0.40–0.70 |
| `IEngineAnalyzer` | `AnalyzeAsync` | 探测布局 → 扫描 interop 或 dump.cs → 打分 → `AnalysisReport`（候选 ≤40，含证据行与未验证项） |
| `IPlanner` | `PlanAsync` | 只把 ≥70 分且能声明形参的候选变成补丁点（≤6）；事件/选项/不可声明者变成人工待办 |
| `ICodeGenerator` | `GenerateAsync` | 在 workspace 内生成 BepInEx 6 插件工程；无 interop 时只写骨架并返回 `E_PARTIAL` |

所有方法不向调用方抛异常（`OperationCanceledException` 除外），统一转成 `ForgeResult<T>.Failure`。

## 2. 静态原料与优先级

| 优先级 | 原料 | 位置 | 能拿到什么 |
|---|---|---|---|
| 高 | BepInEx 6 interop | `<游戏>\BepInEx\interop\*.dll` | 类型/基类/方法名/形参名与类型/属性/`add_` 事件方法；**方法体是 IL2CPP 调用桩，没有调用关系与字符串字面量** |
| 低 | Il2CppDumper `dump.cs` | `--option il2cpp.dumpPath=…` 或常见路径 | 类型/方法/签名/RVA/Slot；无属性与事件结构；只能静态推断 |

没有 interop 也没有 dump 时返回 `E_DEPENDENCY_MISSING`，下一步写明「装 BepInEx 6 启动一次（首启约 229s）」或指定 dump 路径。

## 3. 打分规则（`il2cpp-w2q-1`）

正向：S1 已知框架 +25、S2 语义方法名 +20、S3 直接文本 setter +20、S4 有 string 形参 +10、S5 string+数值 +5、S6 类型持有文本成员 +10、S7 类型上有文本事件 +10、S8 UI/选项 +5、S9 运行时实测非空 +10、S10 事件 adder +15。
负向：N1 基础控件/管道 −20、N2 非文本入口 −15、N3 构造函数 −40、N4 实测零非空 −25（并把总分压到 ≤60）、N5 调试名 −20、N6 混淆名 −10、N7 `AuthorNamePanel::set_Text` −25、N8 已知框架但不在实测表内的 setter −15。

两条硬覆盖：
1. **实测表优先**：命中 `Il2CppKnownTargets`（R18 的 9 个 Naninovel 通道）时直接采用实测分与名次，通用规则只作旁证。
2. **dump 封顶**：只有 dump.cs 时分数封顶 85、等级 A 降为 B —— 静态推断不允许拿到「实测」等级。

## 4. 实测通道表（来自 R18，探针非空命中）

| 名次 | 目标 | 分 | 探针 |
|---|---|---|---|
| 0 | `Naninovel.TextPrinterManager::PrintTextAsync` | 100 | G |
| 1 | `Naninovel.UITextPrinter::set_Text` | 100 | A |
| 2 | `Naninovel.UI.RevealableTextPrinterPanel::set_PrintedText` | 95 | B |
| 3 | `Naninovel.UI.RevealableUIText::set_Text` | 90 | I |
| 4 | `Naninovel.UI.RevealableTMProText::set_Text` | 60 | D（0 次调用，死路） |
| 5 | `Naninovel.UI.RevealableTextPrinterPanel::set_AuthorNameText` | 75 | C |
| 6 | `Naninovel.UI.UITextPrinterPanel::set_AuthorNameText` | 75 | C' |
| 7 | `Naninovel.TextPrinterManager::add_OnPrintTextStarted` | 90 | H（人工订阅） |
| 8 | `Naninovel.UI.ChoiceHandlerPanel::AddChoiceButton` | 75 | 选项（v1 人工） |

## 5. 路线与产物

| 原料 | Route | 补丁点 | 生成结果 |
|---|---|---|---|
| interop | `AutomaticWithConfirmation` | ≤6 个 Hook | 完整工程，可编译、可真机安装 |
| 仅 dump.cs | `SemiAutomatic` | 0（全部转人工待办） | 骨架工程（能编译，不朗读）并返回 `E_PARTIAL` |
| 都没有 | `SemiAutomatic` | 0 | 阻塞待办「先装 BepInEx 6」 |

生成物：`{slug}.il2cpp.a11y.csproj`、`Plugin.cs`、`A11y/A11yRuntime.cs`、`A11y/Tolk.cs`、`Hooks/Hook_00N.cs`、`README.md`、`build.ps1`。

- 钩子只声明**简单类型形参**（string / float 等），结构体与泛型形参（如 `AsyncToken`）不参与声明，靠 `AccessTools.GetDeclaredMethods` 按「方法名 + 形参个数」解析，重载歧义时主动放弃并记日志。
- 补丁挂**声明类型**（R18 教训：子类 + 继承方法会 `Patching exception in method null`）。
- `A11yRuntime`：空串过滤（R18 实测多数 setter 收到空串）、FNV-1a 64 + 250ms 环形去重（同一行会从 6 个通道重复到达）、富文本标签剥离、Tolk 朗读（缺失时降级为日志行）、只记录长度/哈希/CJK 数，原文不落盘。

## 6. 实测结果（2026-11-14，SiliconHeart 3012630）

- interop：13 程序集 / 4155 类型 / 75513 方法 / 241 事件，2.9 s；6 个补丁点、5 条人工待办、3 条风险。
- dump.cs：12337 类型 / 93343 方法，3.3 s；0 补丁点、9 条待办，骨架工程可编译。
- 测试：47 个 xunit 用例全绿（含 3 个用真实 interop/dump 的集成用例）。
- 生成插件用真实 BepInEx 6.0.0-be.788 + Il2CppInterop 引用编译：`0 警告 0 错误`。

命令与日志见 `research/24-il2cpp-adapter/REPORT.md` 与 `research/24-il2cpp-adapter/evidence/`。

## 7. 选项（Profile `Options`）

| 键 | 默认 | 说明 |
|---|---|---|
| `il2cpp.dumpPath` / `dumpPath` / `il2cpp.dump` | 自动探测 | 指定 dump.cs |
| `il2cpp.runtimeEvidence` / `il2cpp.evidence` | 无 | 运行时探针 CSV（列 `target,type,runtime_calls,runtime_nonempty,runtime_verdict,confidence`） |
| `il2cpp.language` / `language` / `lang` | `sc` | 证据里的语言标记 |
| `il2cpp.minConfidence` | 0.30 | 候选阈值 |
| `il2cpp.maxCandidates` | 40 | 候选上限 |
| `il2cpp.allowBaseWidgetHooks` | false | 是否允许基础控件通道（默认禁止） |
| `il2cpp.speech` | true | 生成插件里是否启用 Tolk |
| `il2cpp.projectName` | `{slug}.il2cpp.a11y` | 工程名 |

## 8. CLI 注册（供 Forge.Cli 读取，本卡不改 CLI）

```json
{
  "name": "unity_il2cpp",
  "engine": "unity_il2cpp",
  "priority": 95,
  "assembly": "Forge.Adapters.Il2Cpp.dll",
  "type": "GalA11yForge.Adapters.Il2Cpp.Il2CppAdapter",
  "capabilities": ["detect", "analyze", "plan", "generate"]
}
```

## 9. 已知边界

- IL2CPP interop 是**调用桩**：没有调用图、没有字符串字面量，所有「会收到台词」的结论都是静态推断 + R18 探针佐证，仍标注未验证。
- 首次生成 interop 需要手动装 BepInEx 6 并启动一次（约 229 s，需显卡窗口）。
- 事件订阅（`add_OnPrintTextStarted`）与选项（`AddChoiceButton`）不自动生成，产出带 `DelegateSupport.ConvertDelegate<T>` 片段的人工待办。
- 连续多行对话、Tolk 在 IL2CPP 进程内的实际朗读，均未真机实测。
