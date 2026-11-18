# Forge.Adapters.UnityMono

Unity Mono（BepInEx 5 / HarmonyX）适配器。把「Unity 游戏的 `Assembly-CSharp.dll`」只读扫描成候选文本入口，产出可交给 `Forge.Generators` 的钩子计划。

- 引擎 id：`unity_mono`
- 适配器版本：`0.1.0.0`
- 命名空间：`GalA11yForge.Adapters.UnityMono`
- 目标框架：`net8.0`

## 接口实现

| 接口 | 实现 | 说明 |
|---|---|---|
| `IEngineDetector` | `UnityMonoDetector` | 包装 `Forge.Detection.EngineDetector`，返回 `Primary` |
| `IEngineAnalyzer` | `UnityMonoAnalyzer` | 定位程序集 → Cecil 扫描 → 规则打分 → `AnalysisReport` |
| `IPlanner` | `UnityMonoPlanner` | 候选 → `AutomaticWithConfirmation` 的 Hook 计划 |
| `ICodeGenerator` | `UnityMonoGenerationBridge` | 路径安全校验后委托 `UnityMonoGenerator` |

`UnityMonoAdapter` 同时实现以上四个接口，每个方法都捕获异常并转成 `ForgeResult`，不向调用方抛未捕获异常。

## 设计要点

1. **只读发现**：优先找 `<game>/*_Data/Managed/Assembly-CSharp.dll`，支持直接传 `_Data`；找不到时限深 3 递归。`Managed/Assembly-CSharp-firstpass.dll`、`Managed/Plugins/**`、`Plugins/**` 作为附加程序集，排除了 Unity/System/BepInEx/Harmony 等运行库，单文件 >64MB 跳过。
2. **Cecil 扫描**：`ReadingMode.Deferred` + 自定义 resolver，从 `Managed` 与 `Managed/Plugins` 解析 `UnityEngine.CoreModule` 等引用；缺引用只记录，不中断整个分析。
3. **文本 sink**：
   - 直接写入：`set_text` / `SetText`，声明类型是 `TMPro.TMP_Text`、`TextMeshProUGUI`、`UnityEngine.UI.Text`、`TextMesh` 等。
   - 打字机 sink：`DG.Tweening.ShortcutExtensionsTMPText::DOText`，或方法名含 `TypeText` / `Typewriter`。
4. **S9**：方法参数含 `System.String`，且方法体调用打字机/Tween sink → `S9 +20`。这条规则专门修复静态原型漏掉 `UIText::AnimateText` 的问题（R17 实测主通道）。
5. **类型级对话管线（S1/S2 补充）**：同一类型同时具备「TMP 文本字段 + 直接文本写入者 + 带 string 的打字机入口」时，该类型内方法获得 S1；这样 `DOText(CharaStatus)` 这种无 string 参数、无直接 sink 的每句入口也能进候选。
6. **确定性**：不按墙钟截断；类型/方法/指令数都有固定上限；同输入同输出，候选 Id 按最终顺序生成。排序为：已知入口优先 → 分数 → S9 → 打字机调用数 → 文本 sink 数 → 完整方法名 ordinal。

## 规则表

| 规则 | 分值 | 命中条件 |
|---|---:|---|
| S1 | +25 | 已知对话框架 / 自定义 dialog-trace 类型 / 类型级对话管线 |
| S2 | +20 | 方法名含对话语义 token；类型级管线内方法名含 text token |
| S3 | +20 | 方法体直接写文本组件 |
| S4 | +10 | 声明类型持有文本组件字段 |
| S5 | +5 | UI/Text 命名 |
| S6 | +10 | 方法体含 CJK 字面量（只计数，不保存原文） |
| S7 | +10 | 动态文本 sink；或打字机 sink 的文本来自 string 参数 |
| S8 | +5 | 程序集内调用者 >1，或调用者名含 choice |
| **S9** | **+20** | **参数含 string 且调用打字机/Tween sink** |
| N1 | −20 | debug/log/version/test 等非台词命名 |
| N2 | −15 | 非台词字面量占比 ≥ 50% |
| N3 | −10 | 调用者全部是初始化/生命周期方法 |
| N4 | −10 | 全部字面量都短于 2 字符 |

分数 clamp 到 0–100；Tier：A ≥70 且 S1>0，B ≥50，C ≥30。候选上限 50，最低分 30。

## 真实游戏结果

游戏：`D:\Steam\steamapps\common\Build`（Unity Mono `6000.0.49f1`），程序集 `5omeday_Data\Managed\Assembly-CSharp.dll`（190,464 B，SHA-256 `87f744d3…63ebf`，只读）。

`D:\dotnet\dotnet.exe test tests\Forge.Adapters.UnityMono.Tests\... --filter RealGame` 实测（2026-11-14）：

| 排名 | 候选 | 分数 | 关键规则 |
|---:|---|---:|---|
| 1 | `UIText::AnimateText(TMPro.TextMeshProUGUI, System.String, System.Single, UIText/CharaStatus, System.Int32)` | 95 | S1 25 + S2 20 + S4 10 + S5 5 + S7 10 + S8 5 + **S9 20** |
| 2 | `UIText::DOText(UIText/CharaStatus)` | 95 | S1 25 + S2 20 + S3 20 + S4 10 + S5 5 + S7 10 + S8 5 |
| 3 | `UIText::Choice(ChoiceAsset, System.Int32)` | 70 | S1/S2/S3/S4/S5/S7/S8；N2 −15、N4 −10 |
| 4 | `UIText::HandleMessageText(UIText/CharaStatus, System.Single)` | 55 | S1/S2/S3/S4/S5；N2 −15、N4 −10 |
| 5 | `UIText::HandleMainText(UIText/CharaStatus, System.Single)` | 55 | S1/S2/S3/S4/S5；N2 −15、N4 −10 |

- 计划只勾选这 5 个运行期实证/高置信入口；`PushButton`、`TalkEnd*`、编译器生成的 `<Choice>g__*` 不进入 Hook 计划，但仍保留在分析候选里供人工查看。
- 生成阶段实测可写出 BepInEx 工程（`Plugin.cs`、`Hooks/Hook_*.cs`、`forge.manifest.json` 等），且游戏程序集哈希前后不变。
- 备选证据：`research/19-re-runtime-confirm/REPORT.md`、`research/19-re-runtime-confirm/evidence/candidates.json`。

## 测试

- `tests/Forge.Adapters.UnityMono.Tests`：31 个 xunit 测试。
- Debug：31 通过 / 0 失败；Release：31 通过 / 0 失败（2026-11-14 实测）。
- 覆盖：S9 与 N1–N4、空目录/坏程序集、路径越界/游戏目录拒绝、计划生成、生成器委托与幂等、真实游戏只读回归（4 个 UIText 入口 + AnimateText 的 S9 证据 + 哈希不变）。
- 真实游戏不存在时，`RealGameFactAttribute` 自动 `Skip` 并写「未实测」。

## CLI 注册建议

```json
{
  "name": "unity_mono",
  "engine": "unity_mono",
  "priority": 100,
  "assembly": "Forge.Adapters.UnityMono.dll",
  "type": "GalA11yForge.Adapters.UnityMono.UnityMonoAdapter",
  "capabilities": ["detect", "analyze", "plan", "generate"]
}
```

## 第三方依赖

- `Mono.Cecil 0.11.6`（MIT）：只读解析 .NET 元数据，不修改程序集。
- 生成阶段引用的 `BepInEx 5` / `HarmonyX` 由生成器模板负责，本适配器不重分发二进制。

## 限制与未验证

- **运行期未重测**：本适配器只做静态分析；R17 的运行期命中结论来自既有研究，未在本轮重新启动游戏/BepInEx。
- **Hook 目标省略参数类型**：`Forge.Generators.HookTargetParser` 的参数白名单只有 C# 基础类型，`AnimateText` 的 `TMPro.TextMeshProUGUI` / `UIText+CharaStatus` 不能写进 `Target`。计划使用 `Assembly-CSharp.dll!/UIText:AnimateText`，完整签名写入 `Description`/证据；Harmony 的 `TargetMethod` 仍能按方法名解析，但重载需人工确认。
- **类型级规则是启发式**：S1/S2 的类型级命中依据 R17 单作实证，换作其它 Unity 游戏可能误报；`PushButton` 因直接写文本拿到高分即为例。
- **language 配置**：读取 `Profile.Options` 的 `language` / `lang` / `unity_mono.language` / `a11y.language`，默认 `sc`，支持 `sc|tc|ja|en` 及常见别名；未知值回退 `sc` 并列入 `unverified`。未做按语言分层的文本匹配。
- **覆盖不到**：IL2CPP、加壳/加密程序集、TextAsset/本地化表、BepInEx 插件里的文本、运行期参数为空/多语言切换。
- **附加程序集**：读取失败只记录到 `Notes`，不保证其类型被分析；最大 16 个。
- **未做**：真实 BepInEx 加载、Harmony patch 运行、朗读接线（生成器只产 Resolve/Postfix 骨架）。

## 下一步建议

1. 用生成的 BepInEx 工程在《五天一夜》做一次 manual 模式运行期验证，重点看 `AnimateText` 的 `text` 参数与 `Num` 配对。
2. 把 `HookTarget` 参数白名单扩展到「已知游戏类型 + 嵌套类型」，或在生成器侧支持完整签名；当前只能靠方法名 + 人工确认重载。
3. 为编译器生成方法（`<...>g__`、`<...>b__`）增加负规则，降低 `TalkEnd*` / `PushButton` 噪声。
4. 用第二部 Unity Mono 游戏回归，检验类型级管线规则是否只对 `UIText` 成立。
