# Unity Mono 文本钩子静态分析报告

> 工具：unity-mono-analyzer 0.1.0.0（Mono.Cecil 0.11.6） · schema 1.0
> 生成时间（UTC）：2026-11-14T09:20:06.4974769Z
> 复现命令：`unity-mono-analyzer --game D:\Steam\steamapps\common\GalMaster --out D:\DSHWorkSpace\src\experiments\unity-mono-analyzer\evidence\runs\galmaster`

## 1. 结论摘要

- 游戏：GalMaster
- 游戏目录：`D:\Steam\steamapps\common\GalMaster`
- 数据目录：`GAL PRO MASTER_Data`（`D:\Steam\steamapps\common\GalMaster\GAL PRO MASTER_Data`）
- 启动入口：`GAL PRO MASTER.exe`（与数据目录 GAL PRO MASTER_Data 同名）
- Unity 版本：2022.3.62f3（来源 globalgamemanagers）
- 程序集：`Assembly-CSharp.dll` 358 KB（sha256 `2de4956900ff…`）
- 规模：235 类型 / 2,108 方法（2,096 有方法体）/ 709 条 ldstr
- 文本写入点：31 处，来自 15 个方法（TMPro.TMP_Text::set_text×30、UnityEngine.UI.Text::set_text×1）
- 框架痕迹：自定义 *Dialog*/*Talk* 命名（77 类型）
- 候选分层：A 4 / B 6 / C 6（入选 16 条，低于阈值 3 条；共打分 19 条）
- CJK 字面量：83 个方法命中
- 分析耗时：1330 ms（发现 31 / 候选扫描 709 / 全量扫描 476 / 打分 57）
- **建议优先确认**：`System.Void DialogueHandle::ShowDialogue(System.Int32,System.Boolean)`（A 级 105 分，dialogue）
- 退出码：0（0 成功 / 4 不是 Unity Mono / 5 部分成功）

## 2. 候选钩子点（Top 16）

| # | 级别 | 分数 | 置信 | 类型 | 方法 | 写入点 | 标记 | 证据摘要 |
|---|---|---|---|---|---|---|---|---|
| 1 | A | 105 | high | dialogue | `System.Void DialogueHandle::ShowDialogue(System.Int32,System.Boolean)` | 1 |  | S1+S2+S3+S4+S5+S6+S7+S8；自定义 *Dialog*/*Talk*；CJK×1；来源 field |
| 2 | A | 90 | high | dialogue | `System.Void DialogueHandle::ClearDialogueTextState()` | 3 | multi-write | S1+S2+S3+S4+S5+S7；自定义 *Dialog*/*Talk*；来源 field |
| 3 | A | 75 | medium | unknown | `System.Void DialogueHandle::JumpToDialogueIndex(System.Int32,System.Boolean)` | 0 | upstream-only | S1+S2+S4+S5+S6+S8；自定义 *Dialog*/*Talk*；CJK×2；来源 none |
| 4 | A | 70 | medium | dialogue | `System.Void DialogueHandle::SwitchCharacterInfo()` | 4 | multi-write | S1+S3+S4+S5+S7；自定义 *Dialog*/*Talk*；来源 field |
| 5 | B | 65 | medium | choice | `System.Void SelectionAction::Open(System.Collections.Generic.List`1<System.String>,System.Int32)` | 1 |  | S1+S3+S6+S7；调用者位于对话框架类型；CJK×1；来源 call |
| 6 | B | 60 | low | unknown | `System.Void DialogueHandle::ShowDialogueDeferredPlayback(System.Int32,System.Boolean)` | 0 | upstream-only | S1+S2+S4+S5；自定义 *Dialog*/*Talk*；来源 none |
| 7 | B | 60 | low | unknown | `System.Void DialogueHandle::TrySkipToNextDialogue()` | 0 | upstream-only | S1+S2+S4+S5；自定义 *Dialog*/*Talk*；来源 none |
| 8 | B | 55 | low | dialogue | `System.Boolean DialogueHandle/<TextAnim>d__253::MoveNext()` | 1 |  | S1+S3+S7；自定义 *Dialog*/*Talk*；来源 call |
| 9 | B | 55 | low | debug | `System.Void DialogueHandle::SetDebugConsoleStatus(System.String)` | 1 |  | S1+S3+S4+S5+S7+S8+N1；自定义 *Dialog*/*Talk*；来源 field |
| 10 | B | 55 | low | debug | `TMPro.TextMeshProUGUI DialogueHandle::CreateDebugConsoleText(System.String,UnityEngine.Transform,System.String,System.Single,TMPro.TextAlignmentOptions,UnityEngine.Color)` | 1 |  | S1+S3+S4+S5+S7+S8+N1；自定义 *Dialog*/*Talk*；来源 parameter |
| 11 | C | 40 | low | unknown | `System.Void ConfirmTipAction::Show(System.String,System.Action,System.Action)` | 1 | system-ui | S3+S4+S7；来源 parameter |
| 12 | C | 40 | low | unknown | `System.Void QLSlot::Init(QLPanel,System.Int32,SaveLoadData,System.Boolean)` | 1 |  | S3+S4+S7；来源 call |
| 13 | C | 40 | low | menu | `System.Void SaveloadAction::ShowLoadTip(System.String,System.Action)` | 1 | system-ui | S3+S4+S7；来源 parameter |
| 14 | C | 35 | low | menu | `System.Boolean SettingAction/<TextPreviewAnim>d__105::MoveNext()` | 2 | multi-write | S3+S5+S7；来源 field |
| 15 | C | 30 | low | menu | `System.Void SaveLoadSlot::RefreshUI(System.Int32)` | 4 | system-ui,multi-write | S3+S4+S7+S8+N2；来源 mixed |
| 16 | C | 30 | low | unknown | `System.Void CGAction::SetButtonLabel(UnityEngine.UI.Button,System.String)` | 2 | multi-write | S3+S7；来源 parameter |

> 级别：A≥70 且 S1>0（可生成 Hook 草案 + 运行期回放）；B≥50（降级，人工勾选）；C≥30（只记录观察）。
> 分数是静态启发式，不等于可 Hook 性；A 级同样必须过 §8 的运行期确认。

### 2.1 建议优先复核（已排除系统 UI / 示例代码 / 本地化刷新）

| # | 级别 | 分数 | 方法 | 标记 | 为什么被挑出来 |
|---|---|---|---|---|---|
| 1 | A | 105 | `System.Void DialogueHandle::ShowDialogue(System.Int32,System.Boolean)` |  | 无噪声标记，直接写入文本 |
| 2 | A | 90 | `System.Void DialogueHandle::ClearDialogueTextState()` | multi-write | 同一方法多次写文本（需去重） |
| 3 | A | 75 | `System.Void DialogueHandle::JumpToDialogueIndex(System.Int32,System.Boolean)` | upstream-only | 调用链上游包装方法 |

> 标记含义：system-ui=确认框/设置/存档/暂停等系统 UI；i18n-refresh=换语言/初始化时整窗重写；sample-code=框架自带 Examples/Samples；framework-internal=框架内部实现（游戏侧入口通常在其上游）；multi-write=一处方法多次写文本；hardcoded-text=全是硬编码字面量；upstream-only=只在调用链上游包了一层。标记只做证据标注，**不参与** R4 打分。

## 3. 候选详情（前 8 条）

### #1 `DialogueHandle.ShowDialogue` · A 级 · 105 分 · dialogue · 置信 high

- 方法全名：`System.Void DialogueHandle::ShowDialogue(System.Int32,System.Boolean)`
- 签名：`ShowDialogue(Int32, Boolean)`（1 处文本写入，来源 field）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S2 +20 / S3 +20 / S4 +10 / S5 +5 / S6 +10 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S2 +20 方法名 ShowDialogue 命中对话语义关键词
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S4 +10 所属类型持有 4 个 Text/TMP 字段
  - S5 +5 类名 DialogueHandle 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S6 +10 方法内有 1 条 CJK 字面量
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
  - S8 +5 调用者类型含 Choice/Select/Answer 语义
- 调用者（12）：`DialogueHandle::JumpToDialogueIndex`、`DialogueHandle::OnQuickLoad`、`DialogueHandle::ShowDialogueDeferredPlayback`、`DialogueHandle::InputLogic`
- 所属类型持有文本字段：4 个

### #2 `DialogueHandle.ClearDialogueTextState` · A 级 · 90 分 · dialogue · 置信 high

- 方法全名：`System.Void DialogueHandle::ClearDialogueTextState()`
- 签名：`ClearDialogueTextState()`（3 处文本写入，来源 field）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S2 +20 / S3 +20 / S4 +10 / S5 +5 / S7 +10
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S2 +20 方法名 ClearDialogueTextState 命中对话语义关键词
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 3 处
  - S4 +10 所属类型持有 4 个 Text/TMP 字段
  - S5 +5 类名 DialogueHandle 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 3/3 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`DialogueHandle::ResetRuntimeVisualState`
- 所属类型持有文本字段：4 个

### #3 `DialogueHandle.JumpToDialogueIndex` · A 级 · 75 分 · unknown · 置信 medium

- 方法全名：`System.Void DialogueHandle::JumpToDialogueIndex(System.Int32,System.Boolean)`
- 签名：`JumpToDialogueIndex(Int32, Boolean)`（0 处文本写入，来源 none）
- 发现途径：upstream-of-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S2 +20 / S4 +10 / S5 +5 / S6 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S2 +20 方法名 JumpToDialogueIndex 命中对话语义关键词
  - S4 +10 所属类型持有 4 个 Text/TMP 字段
  - S5 +5 类名 DialogueHandle 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S6 +10 方法内有 2 条 CJK 字面量
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`DialogueHandle::TestJumpToDialogueIndex`、`DialogueHandle::TrySubmitDebugConsoleJump`
- 所属类型持有文本字段：4 个

### #4 `DialogueHandle.SwitchCharacterInfo` · A 级 · 70 分 · dialogue · 置信 medium

- 方法全名：`System.Void DialogueHandle::SwitchCharacterInfo()`
- 签名：`SwitchCharacterInfo()`（4 处文本写入，来源 field）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 4 处
  - S4 +10 所属类型持有 4 个 Text/TMP 字段
  - S5 +5 类名 DialogueHandle 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 4/4 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`DialogueHandle::ShowDialogue`
- 所属类型持有文本字段：4 个

### #5 `SelectionAction.Open` · B 级 · 65 分 · choice · 置信 medium

- 方法全名：`System.Void SelectionAction::Open(System.Collections.Generic.List`1<System.String>,System.Int32)`
- 签名：`Open(List`1, Int32)`（1 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：调用者位于对话框架类型
- 规则得分：S1 +25 / S3 +20 / S6 +10 / S7 +10
  - S1 +25 方法的调用者位于已知对话框架类型（调用图上游命中）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S6 +10 方法内有 1 条 CJK 字面量
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`DialogueHandle::ExecuteExAction`

### #6 `DialogueHandle.ShowDialogueDeferredPlayback` · B 级 · 60 分 · unknown · 置信 low

- 方法全名：`System.Void DialogueHandle::ShowDialogueDeferredPlayback(System.Int32,System.Boolean)`
- 签名：`ShowDialogueDeferredPlayback(Int32, Boolean)`（0 处文本写入，来源 none）
- 发现途径：upstream-of-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S2 +20 / S4 +10 / S5 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S2 +20 方法名 ShowDialogueDeferredPlayback 命中对话语义关键词
  - S4 +10 所属类型持有 4 个 Text/TMP 字段
  - S5 +5 类名 DialogueHandle 含 Window/UI/Dialog/Box/Panel 等 UI 语义
- 调用者（1）：`SceneLoadManager::StartDialogueFlow`
- 所属类型持有文本字段：4 个

### #7 `DialogueHandle.TrySkipToNextDialogue` · B 级 · 60 分 · unknown · 置信 low

- 方法全名：`System.Void DialogueHandle::TrySkipToNextDialogue()`
- 签名：`TrySkipToNextDialogue()`（0 处文本写入，来源 none）
- 发现途径：upstream-of-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S2 +20 / S4 +10 / S5 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S2 +20 方法名 TrySkipToNextDialogue 命中对话语义关键词
  - S4 +10 所属类型持有 4 个 Text/TMP 字段
  - S5 +5 类名 DialogueHandle 含 Window/UI/Dialog/Box/Panel 等 UI 语义
- 调用者（1）：`DialogueHandle::InputLogic`
- 所属类型持有文本字段：4 个

### #8 `DialogueHandle/<TextAnim>d__253.MoveNext` · B 级 · 55 分 · dialogue · 置信 low

- 方法全名：`System.Boolean DialogueHandle/<TextAnim>d__253::MoveNext()`
- 签名：`MoveNext()`（1 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S7 +10
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
- 调用者（0）：无（入口方法或事件回调）

## 4. 识别到的文本写入 API

| API | 调用次数 | 调用者方法数 | 是否参与打分 |
|---|---|---|---|
| `TMPro.TMP_Text::set_text` | 30 | 15 | 是（S3） |
| `UnityEngine.UI.Text::set_text` | 1 | 1 | 是（S3） |


### 4.1 文本写入最集中的类型（Top 12）

> 这里统计**全部** set_text 调用点（不限于候选），用来回答"文本到底从哪个类流出"。
> 候选漏掉的主对话类常常出现在这张表里，值得对照 §2 检查是否漏报。

| 类型 | 写入点 | 方法数 | 示例方法 |
|---|---|---|---|
| `DialogueHandle` | 10 | 5 | `ClearDialogueTextState` |
| `HistoryObj` | 7 | 1 | `InitHistoryObj` |
| `SaveLoadSlot` | 4 | 1 | `RefreshUI` |
| `CGAction` | 3 | 2 | `SetButtonLabel` |
| `SettingAction/<TextPreviewAnim>d__105` | 2 | 1 | `MoveNext` |
| `ConfirmTipAction` | 1 | 1 | `Show` |
| `DialogueHandle/<TextAnim>d__253` | 1 | 1 | `MoveNext` |
| `QLSlot` | 1 | 1 | `Init` |
| `SaveloadAction` | 1 | 1 | `ShowLoadTip` |
| `SelectionAction` | 1 | 1 | `Open` |

## 5. 对话框架痕迹

| 框架 | 类别 | 类型数 | 方法数 | 样例类型 |
|---|---|---|---|---|
| 自定义 *Dialog*/*Talk* 命名 | 自定义命名 | 77 | 691 | `CharacterTalkData`<br>`DialogueBGM`<br>`DialogueBackground` |

## 6. 覆盖声明

### 能分析
- 定位 `*_Data\Managed\Assembly-CSharp.dll`：支持多层子目录、一个安装目录内多个 Unity 程序集（启动器/多章节）并给出排名理由
- 识别 Unity 版本（`globalgamemanagers` 优先，`UnityPlayer.dll` 版本资源兜底）并拒绝 IL2CPP 目标
- 统计 `TMPro.TMP_Text::set_text` / `SetText` 与 `UnityEngine.UI.Text::set_text` 的调用指令、调用者方法与类型
- 用 IL 反向栈分析判定写入实参来源（常量 / 参数 / 字段 / 局部变量 / 方法调用），作为 S7 依据
- 已知对话框架痕迹统计：Naninovel、NodeCanvas DialogueTrees、ScenarioForge、Fungus、Yarn、Ink、PixelCrushers；以及自定义 `*Dialog*`/`*Talk*` 命名
- CJK 字面量分布（含平假名/片假名/汉字），只输出计数与所在方法，不导出任何字面量内容
- 按 R4 规则打分并分层（A≥70 且 S1>0 / B≥50 / C≥30），输出可交人工确认的候选表与逐条证据
- 识别已部署补丁痕迹（BepInEx / doorstop / nvdaControllerClient）作为环境事实，不据此推断可 Hook 性

### 不能分析（明确的盲区）
- 看不到 `set_text` 之外的文本通道：图片字、TextMesh(3D)、自绘文本、以及游戏自定义的 "SetString" 式非标准 API（只有方法名里带 Text/SetText 的才会被统计）
- 不解析 TextAsset、本地化表、字体/图集资源里的台词；只统计代码内字面量（因此文本量被低估是正常的）
- 静态无法判断可见性：CanvasGroup.alpha、activeInHierarchy、面板是否在当前场景，菜单/设置/存档面板的文本会被当候选
- 静态无法判断说话人（who）与正文的先后、打字机逐字多次 `set_text` 的去重、富文本标签清洗 —— 这些必须运行期确认
- 只做一层语义上游展开：Manager→Presenter→View 这类跨 2 层以上的包装器可能漏检（可通过 `--top` 放宽后人工翻查）
- 不分析插件 DLL（BepInEx 插件、第三方 UI 库）与 IL2CPP 目标；只测 `Assembly-CSharp.dll`
- 没有运行期证据：全部分数都是静态启发式，A 级候选也必须经运行期回放确认后才可生成补丁

## 7. 人工确认清单（运行期必做，静态拿不到）

- [ ] 1. 目标游戏用 TMP 还是 uGUI.Text？两者是否同时存在（存在则需双重 Hook 并按 API 去重）
- [ ] 2. 打字机效果是否对同一句反复调用 set_text（需按内容+时间窗口去重）
- [ ] 3. 说话人（who）与正文（text）的写入先后顺序，能否在同一次 Hook 中拼出完整朗读串
- [ ] 4. 选项列表：按钮文本来源、顺序、禁用项（不可选）如何表达
- [ ] 5. 菜单/设置/存档面板：确认哪些写入点应被静音或排除
- [ ] 6. 可见性：CanvasGroup.alpha / activeInHierarchy / 是否能读到当前激活面板
- [ ] 7. 富文本清洗：`<color>`、`<size>`、`<ruby>`、`<sprite>` 等标签的剥离规则
- [ ] 8. 事件频率与去重窗口：每秒最多朗读次数、重复句抑制

### 本作专项检查项
- [ ] 本作同时存在 TMP 与 uGUI.Text 两条写入路径：必须两条都 Hook 并按内容去重（否则会漏句/重复朗读）
- [ ] 从候选 `System.Void DialogueHandle::ShowDialogue(System.Int32,System.Boolean)` 下断点，确认一次对话会产生几次 set_text 调用
- [ ] 确认 CJK 字面量所在的候选是否就是台词路径（有些只是界面按钮文案）
- [ ] 存在 4 个语义上游候选：确认 Hook 上游（一次拿到完整句子）还是 Hook setter（拿最终渲染文本）

## 8. 备注与告警

- ℹ️ 未解析程序集：0；主文本写入 API：TMPro.TMP_Text::set_text×30, UnityEngine.UI.Text::set_text×1

## 9. 复现与产物

```pwsh
# 在 src\experiments\unity-mono-analyzer 目录下
D:\dotnet\dotnet.exe run -- --game "D:\Steam\steamapps\common\GalMaster" --out <输出目录>
```

- 机器可读产物：`analysis.json`（schemaVersion 1.0，字段见 README）
- 程序集 sha256：`2de4956900ff3a1f261b7564bafe541291c61e2dd953d5859f8f4723b0de37b0`
- 候选上限：16 条入选 / 3 条低于阈值未展开

