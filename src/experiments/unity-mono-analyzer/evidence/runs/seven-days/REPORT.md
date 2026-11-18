# Unity Mono 文本钩子静态分析报告

> 工具：unity-mono-analyzer 0.1.0.0（Mono.Cecil 0.11.6） · schema 1.0
> 生成时间（UTC）：2026-11-14T09:19:57.1694309Z
> 复现命令：`unity-mono-analyzer --game D:\Steam\steamapps\common\我与你共度的七日 --out D:\DSHWorkSpace\src\experiments\unity-mono-analyzer\evidence\runs\seven-days`

## 1. 结论摘要

- 游戏：我与你共度的七日
- 游戏目录：`D:\Steam\steamapps\common\我与你共度的七日\game`
- 数据目录：`The seven days i spent with you_Data`（`D:\Steam\steamapps\common\我与你共度的七日\game\The seven days i spent with you_Data`）
- 启动入口：`The seven days i spent with you.exe`（与数据目录 The seven days i spent with you_Data 同名）
- Unity 版本：2021.3.31f1（来源 globalgamemanagers）
- 程序集：`Assembly-CSharp.dll` 205.5 KB（sha256 `d08263a17ee0…`）
- 规模：325 类型 / 1,602 方法（1,594 有方法体）/ 373 条 ldstr
- 文本写入点：26 处，来自 17 个方法（TMPro.TMP_Text::set_text×20、UnityEngine.UI.Text::set_text×6）
- 框架痕迹：NodeCanvas DialogueTrees（20 类型）、自定义 *Dialog*/*Talk* 命名（8 类型）
- 候选分层：A 3 / B 2 / C 10（入选 15 条，低于阈值 3 条；共打分 18 条）
- CJK 字面量：23 个方法命中
- 分析耗时：1765 ms（发现 45 / 候选扫描 1368 / 全量扫描 195 / 打分 64）
- **建议优先确认**：`System.String[] SpeecController::GetStringLine(System.String)`（A 级 85 分，dialogue）
- 退出码：0（0 成功 / 4 不是 Unity Mono / 5 部分成功）

## 2. 候选钩子点（Top 15）

| # | 级别 | 分数 | 置信 | 类型 | 方法 | 写入点 | 标记 | 证据摘要 |
|---|---|---|---|---|---|---|---|---|
| 1 | A | 90 | high | choice | `System.Void NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::OnMultipleChoiceRequest(NodeCanvas.DialogueTrees.MultipleChoiceRequestInfo)` | 1 | sample-code,framework-internal | S1+S2+S3+S4+S5+S7；NodeCanvas DialogueTrees；来源 call |
| 2 | A | 85 | high | dialogue | `System.String[] SpeecController::GetStringLine(System.String)` | 1 |  | S1+S2+S3+S4+S7；调用者位于对话框架类型；来源 parameter |
| 3 | A | 75 | medium | dialogue | `System.Void GameProject.YesNoDialogue::ShowYesNo(System.String,System.Action,System.Action)` | 1 | system-ui | S1+S3+S4+S5+S7+S8；自定义 *Dialog*/*Talk*；来源 parameter |
| 4 | B | 60 | low | unknown | `System.Void NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::OnSubtitlesRequest(NodeCanvas.DialogueTrees.SubtitlesRequestInfo)` | 0 | system-ui,sample-code,framework-internal,upstream-only | S1+S2+S4+S5；NodeCanvas DialogueTrees；来源 none |
| 5 | B | 55 | low | dialogue | `System.Boolean NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI/<Internal_OnSubtitlesRequestInfo>d__76::MoveNext()` | 1 | system-ui,sample-code,framework-internal | S1+S3+S7；NodeCanvas DialogueTrees；来源 call |
| 6 | C | 45 | low | unknown | `System.Void OnLangaugeHanddle::OnChangeLangaugeHanddle()` | 2 | i18n-refresh,multi-write | S3+S4+S7+S8；来源 field |
| 7 | C | 40 | low | dialogue | `System.Void LoveStoryProject.BacklogItem::Init(LoveStoryProject.BacklogData,System.Int32,System.Action`1<LoveStoryProject.BacklogItem>)` | 2 | system-ui,multi-write | S3+S4+S7；来源 call |
| 8 | C | 40 | low | unknown | `System.Void DG.Tweening.DOTweenModuleUI/<>c__DisplayClass35_0::<DOCounter>b__1(System.Int32)` | 1 |  | S3+S4+S7；来源 call |
| 9 | C | 40 | low | unknown | `System.Void DG.Tweening.DOTweenModuleUI/<>c__DisplayClass37_0::<DOText>b__1(System.String)` | 1 |  | S3+S4+S7；来源 parameter |
| 10 | C | 40 | low | menu | `System.Void GameProject.ConfigDropdown/<>c__DisplayClass12_0::<Create>b__0()` | 1 | system-ui | S3+S4+S7；来源 call |
| 11 | C | 40 | low | unknown | `System.Void SpeechControl::InputText(System.String)` | 1 |  | S3+S4+S7；来源 parameter |
| 12 | C | 40 | low | menu | `TMPro.TextMeshProUGUI GameProject.ConfigDropdown::MakeText(UnityEngine.Transform,System.String,TMPro.TMP_FontAsset)` | 1 | system-ui | S3+S4+S7；来源 parameter |
| 13 | C | 35 | low | menu | `System.Void GameProject.ConfigDropdown::SetValue(System.Int32)` | 1 | system-ui | S3+S4+S8；来源 other |
| 14 | C | 35 | low | dialogue | `System.Void NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::StartPlay()` | 1 | i18n-refresh,sample-code,framework-internal,hardcoded-text | S1+S3+S4+S5+N2+N4；NodeCanvas DialogueTrees；来源 constant |
| 15 | C | 35 | low | dialogue | `System.Void SpeecController::ClearInput()` | 1 | hardcoded-text | S1+S3+S4+S8+N2+N4；调用者位于对话框架类型；来源 constant |

> 级别：A≥70 且 S1>0（可生成 Hook 草案 + 运行期回放）；B≥50（降级，人工勾选）；C≥30（只记录观察）。
> 分数是静态启发式，不等于可 Hook 性；A 级同样必须过 §8 的运行期确认。

### 2.1 建议优先复核（已排除系统 UI / 示例代码 / 本地化刷新）

| # | 级别 | 分数 | 方法 | 标记 | 为什么被挑出来 |
|---|---|---|---|---|---|
| 1 | A | 85 | `System.String[] SpeecController::GetStringLine(System.String)` |  | 无噪声标记，直接写入文本 |

> 标记含义：system-ui=确认框/设置/存档/暂停等系统 UI；i18n-refresh=换语言/初始化时整窗重写；sample-code=框架自带 Examples/Samples；framework-internal=框架内部实现（游戏侧入口通常在其上游）；multi-write=一处方法多次写文本；hardcoded-text=全是硬编码字面量；upstream-only=只在调用链上游包了一层。标记只做证据标注，**不参与** R4 打分。

## 3. 候选详情（前 8 条）

### #1 `DialogueUGUI.OnMultipleChoiceRequest` · A 级 · 90 分 · choice · 置信 high

- 方法全名：`System.Void NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::OnMultipleChoiceRequest(NodeCanvas.DialogueTrees.MultipleChoiceRequestInfo)`
- 签名：`OnMultipleChoiceRequest(MultipleChoiceRequestInfo)`（1 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：NodeCanvas DialogueTrees
- 规则得分：S1 +25 / S2 +20 / S3 +20 / S4 +10 / S5 +5 / S7 +10
  - S1 +25 类型属于已知对话框架 NodeCanvas DialogueTrees
  - S2 +20 方法名 OnMultipleChoiceRequest 命中对话语义关键词
  - S3 +20 直接调用 UnityEngine.UI.Text::set_text 共 1 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 DialogueUGUI 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
- 调用者（0）：无（入口方法或事件回调）
- 所属类型持有文本字段：1 个

### #2 `SpeecController.GetStringLine` · A 级 · 85 分 · dialogue · 置信 high

- 方法全名：`System.String[] SpeecController::GetStringLine(System.String)`
- 签名：`GetStringLine(String)`（1 处文本写入，来源 parameter）
- 发现途径：direct-text-setter；框架：调用者位于对话框架类型
- 规则得分：S1 +25 / S2 +20 / S3 +20 / S4 +10 / S7 +10
  - S1 +25 方法的调用者位于已知对话框架类型（调用图上游命中）
  - S2 +20 方法名 GetStringLine 命中对话语义关键词
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::OnSubtitlesRequest`
- 所属类型持有文本字段：1 个

### #3 `YesNoDialogue.ShowYesNo` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void GameProject.YesNoDialogue::ShowYesNo(System.String,System.Action,System.Action)`
- 签名：`ShowYesNo(String, Action, Action)`（1 处文本写入，来源 parameter）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 YesNoDialogue 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 5 个不同调用者调用
- 调用者（5）：`DataController::OnTapDelete`、`DataController::OnTapItem`、`GameProject.Config::OnTapDefaults`、`GameProject.GameEventListener::OnMMEvent`
- 所属类型持有文本字段：1 个

### #4 `DialogueUGUI.OnSubtitlesRequest` · B 级 · 60 分 · unknown · 置信 low

- 方法全名：`System.Void NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::OnSubtitlesRequest(NodeCanvas.DialogueTrees.SubtitlesRequestInfo)`
- 签名：`OnSubtitlesRequest(SubtitlesRequestInfo)`（0 处文本写入，来源 none）
- 发现途径：upstream-of-text-setter；框架：NodeCanvas DialogueTrees
- 规则得分：S1 +25 / S2 +20 / S4 +10 / S5 +5
  - S1 +25 类型属于已知对话框架 NodeCanvas DialogueTrees
  - S2 +20 方法名 OnSubtitlesRequest 命中对话语义关键词
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 DialogueUGUI 含 Window/UI/Dialog/Box/Panel 等 UI 语义
- 调用者（0）：无（入口方法或事件回调）
- 所属类型持有文本字段：1 个

### #5 `DialogueUGUI/<Internal_OnSubtitlesRequestInfo>d__76.MoveNext` · B 级 · 55 分 · dialogue · 置信 low

- 方法全名：`System.Boolean NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI/<Internal_OnSubtitlesRequestInfo>d__76::MoveNext()`
- 签名：`MoveNext()`（1 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：NodeCanvas DialogueTrees
- 规则得分：S1 +25 / S3 +20 / S7 +10
  - S1 +25 类型属于已知对话框架 NodeCanvas DialogueTrees
  - S3 +20 直接调用 UnityEngine.UI.Text::set_text 共 1 处
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
- 调用者（0）：无（入口方法或事件回调）

### #6 `OnLangaugeHanddle.OnChangeLangaugeHanddle` · C 级 · 45 分 · unknown · 置信 low

- 方法全名：`System.Void OnLangaugeHanddle::OnChangeLangaugeHanddle()`
- 签名：`OnChangeLangaugeHanddle()`（2 处文本写入，来源 field）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S3 +20 / S4 +10 / S7 +10 / S8 +5
  - S3 +20 直接调用 UnityEngine.UI.Text::set_text, TMPro.TMP_Text::set_text 共 2 处
  - S4 +10 所属类型持有 2 个 Text/TMP 字段
  - S7 +10 文本写入有 2/2 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 4 个不同调用者调用
- 调用者（4）：`GameProject.Config::RefreshLocalizedUI`、`GameProject.Config::OnLanguageChanged`、`LanguageLoader/<LoadLocalizationTable>d__20::MoveNext`、`OnLangaugeHanddle::Start`
- 所属类型持有文本字段：2 个

### #7 `BacklogItem.Init` · C 级 · 40 分 · dialogue · 置信 low

- 方法全名：`System.Void LoveStoryProject.BacklogItem::Init(LoveStoryProject.BacklogData,System.Int32,System.Action`1<LoveStoryProject.BacklogItem>)`
- 签名：`Init(BacklogData, Int32, Action`1)`（2 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S3 +20 / S4 +10 / S7 +10
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 2 处
  - S4 +10 所属类型持有 2 个 Text/TMP 字段
  - S7 +10 文本写入有 2/2 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`LoveStoryProject.Backlog::Open`
- 所属类型持有文本字段：2 个

### #8 `DOTweenModuleUI/<>c__DisplayClass35_0.<DOCounter>b__1` · C 级 · 40 分 · unknown · 置信 low

- 方法全名：`System.Void DG.Tweening.DOTweenModuleUI/<>c__DisplayClass35_0::<DOCounter>b__1(System.Int32)`
- 签名：`<DOCounter>b__1(Int32)`（1 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S3 +20 / S4 +10 / S7 +10
  - S3 +20 直接调用 UnityEngine.UI.Text::set_text 共 1 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
- 调用者（0）：无（入口方法或事件回调）
- 所属类型持有文本字段：1 个

## 4. 识别到的文本写入 API

| API | 调用次数 | 调用者方法数 | 是否参与打分 |
|---|---|---|---|
| `TMPro.TMP_Text::set_text` | 20 | 12 | 是（S3） |
| `UnityEngine.UI.Text::set_text` | 6 | 6 | 是（S3） |


### 4.1 文本写入最集中的类型（Top 12）

> 这里统计**全部** set_text 调用点（不限于候选），用来回答"文本到底从哪个类流出"。
> 候选漏掉的主对话类常常出现在这张表里，值得对照 §2 检查是否漏报。

| 类型 | 写入点 | 方法数 | 示例方法 |
|---|---|---|---|
| `DataCheck` | 7 | 1 | `Init` |
| `DefaultNamespace.CgGalleryItem` | 2 | 1 | `Init` |
| `GameProject.ConfigDropdown` | 2 | 2 | `SetValue` |
| `LoveStoryProject.BacklogItem` | 2 | 1 | `Init` |
| `NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI` | 2 | 2 | `StartPlay` |
| `OnLangaugeHanddle` | 2 | 1 | `OnChangeLangaugeHanddle` |
| `SpeecController` | 2 | 2 | `GetStringLine` |
| `DG.Tweening.DOTweenModuleUI/<>c__DisplayClass35_0` | 1 | 1 | `<DOCounter>b__1` |
| `DG.Tweening.DOTweenModuleUI/<>c__DisplayClass37_0` | 1 | 1 | `<DOText>b__1` |
| `FadeTextAction` | 1 | 1 | `Show` |
| `GameProject.ConfigDropdown/<>c__DisplayClass12_0` | 1 | 1 | `<Create>b__0` |
| `GameProject.YesNoDialogue` | 1 | 1 | `ShowYesNo` |

## 5. 对话框架痕迹

| 框架 | 类别 | 类型数 | 方法数 | 样例类型 |
|---|---|---|---|---|
| NodeCanvas DialogueTrees | 已知框架 | 20 | 153 | `NodeCanvas.DialogueTrees.DialogueActor`<br>`NodeCanvas.DialogueTrees.MultipleChoiceNode`<br>`NodeCanvas.DialogueTrees.MultipleChoiceNode/<>c__DisplayClass10_0` |
| 自定义 *Dialog*/*Talk* 命名 | 自定义命名 | 8 | 37 | `ChangeDialogueActorDisplayName`<br>`ChangeDialogueActorDisplayName/<LoadLanguageCoroutine>d__4`<br>`DialogueTreeControllerExtensions` |

## 6. 候选数据目录排名

| 选择 | 数据目录 | 深度 | 分数 | 类型/方法 | 写入方法 | CJK 方法 | 框架类型 | 理由 |
|---|---|---|---|---|---|---|---|---|
| ✅ | `The seven days i spent with you_Data` | 1 | 2655 | 325/1602 | 17 | 23 | 20 | +1000 已知对话框架类型×20；+1200 对话命名类型×43；+200 CJK 方法×23；+255 文本写入方法×17 |
|  | `SevenDaysLauncher_Data` | 0 | 950 | 326/2090 | 24 | 21 | 234 | +1000 已知对话框架类型×234；+1200 对话命名类型×24；+200 CJK 方法×21；+300 文本写入方法×24；+150 目录名与输入目录匹配；-900 启动器/更新器可疑类型×42；-1000 数据目录名 SevenDaysLauncher_Data 像启动器/更新器 |

> 启动器的文本写入点常常多于真游戏（research/01-norna 教训），因此排序权重里框架/对话命名/CJK 远高于写入点数量。

## 7. 覆盖声明

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

## 8. 人工确认清单（运行期必做，静态拿不到）

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
- [ ] 从候选 `System.Void NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::OnMultipleChoiceRequest(NodeCanvas.DialogueTrees.MultipleChoiceRequestInfo)` 下断点，确认一次对话会产生几次 set_text 调用
- [ ] 确认 CJK 字面量所在的候选是否就是台词路径（有些只是界面按钮文案）
- [ ] 游戏目录已有注入痕迹（BepInEx, doorstop_config.ini, .doorstop_version），需确认是否与本工具产物冲突
- [ ] 存在 1 个语义上游候选：确认 Hook 上游（一次拿到完整句子）还是 Hook setter（拿最终渲染文本）

## 9. 备注与告警

- ℹ️ 该游戏目录已存在补丁/注入痕迹（BepInEx, doorstop_config.ini, .doorstop_version, winhttp.dll, nvdaControllerClient.dll），只做静态只读分析，未改动任何文件
- ℹ️ 未解析程序集：0；主文本写入 API：TMPro.TMP_Text::set_text×20, UnityEngine.UI.Text::set_text×6

## 10. 复现与产物

```pwsh
# 在 src\experiments\unity-mono-analyzer 目录下
D:\dotnet\dotnet.exe run -- --game "D:\Steam\steamapps\common\我与你共度的七日" --out <输出目录>
```

- 机器可读产物：`analysis.json`（schemaVersion 1.0，字段见 README）
- 程序集 sha256：`d08263a17ee0a0c021d698235dba62d94aaa0290b83fc8b4694c939dd7c7a443`
- 候选上限：15 条入选 / 3 条低于阈值未展开

