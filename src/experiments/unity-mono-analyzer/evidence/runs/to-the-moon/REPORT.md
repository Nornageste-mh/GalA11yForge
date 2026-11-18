# Unity Mono 文本钩子静态分析报告

> 工具：unity-mono-analyzer 0.1.0.0（Mono.Cecil 0.11.6） · schema 1.0
> 生成时间（UTC）：2026-11-14T09:20:08.4066423Z
> 复现命令：`unity-mono-analyzer --game "D:\Steam\steamapps\common\To the Moon" --out D:\DSHWorkSpace\src\experiments\unity-mono-analyzer\evidence\runs\to-the-moon`

## 1. 结论摘要

- 游戏：To the Moon
- 游戏目录：`D:\Steam\steamapps\common\To the Moon\To the Moon (SerenityForge)`
- 数据目录：`To the Moon_Data`（`D:\Steam\steamapps\common\To the Moon\To the Moon (SerenityForge)\To the Moon_Data`）
- 启动入口：`To the Moon.exe`（与数据目录 To the Moon_Data 同名）
- Unity 版本：2022.3.28f1（来源 globalgamemanagers）
- 程序集：`Assembly-CSharp.dll` 1382 KB（sha256 `5253c4a50e25…`）
- 规模：1,318 类型 / 7,477 方法（7,368 有方法体）/ 2,494 条 ldstr
- 文本写入点：245 处，来自 93 个方法（TMPro.TMP_Text::set_text×238、TMPro.TMP_Text::SetText×4、UnityEngine.UI.Text::set_text×3）
- 框架痕迹：ScenarioForge（446 类型）、自定义 *Dialog*/*Talk* 命名（22 类型）
- 候选分层：A 4 / B 24 / C 43（入选 71 条，低于阈值 29 条；共打分 100 条）
- CJK 字面量：未检出（英文游戏或文本在资源内）
- 分析耗时：5830 ms（发现 42 / 候选扫描 4690 / 全量扫描 836 / 打分 167）
- **建议优先确认**：`System.Void ScenarioForge.SetTextFromGameVariable::SetText(System.String)`（A 级 85 分，dialogue）
- 退出码：0（0 成功 / 4 不是 Unity Mono / 5 部分成功）

## 2. 候选钩子点（Top 20）

| # | 级别 | 分数 | 置信 | 类型 | 方法 | 写入点 | 标记 | 证据摘要 |
|---|---|---|---|---|---|---|---|---|
| 1 | A | 85 | high | dialogue | `System.Void ScenarioForge.SetTextFromGameVariable::SetText(System.String)` | 2 | framework-internal,multi-write | S1+S2+S3+S4+S7；ScenarioForge；来源 parameter |
| 2 | A | 75 | medium | dialogue | `System.Void ScenarioForge.UILeftRightSelector::SetCurValueInRange(System.Single,System.Boolean)` | 4 | framework-internal,multi-write | S1+S3+S4+S5+S7+S8；ScenarioForge；来源 call |
| 3 | A | 75 | medium | dialogue | `System.Void ScenarioForge.UILeftRightSelector::OverrideSelectorDisplayText(System.String)` | 1 | framework-internal | S1+S3+S4+S5+S7+S8；ScenarioForge；来源 parameter |
| 4 | B | 70 | medium | choice | `System.Void CentralPauseMenuManager::BackOutOfCentralSelection()` | 1 | system-ui | S2+S3+S4+S5+S7+S8；来源 field |
| 5 | A | 70 | medium | dialogue | `System.Void ScenarioForge.TextTyper::SetContents(System.String,System.Boolean)` | 1 | framework-internal | S1+S3+S4+S7+S8；ScenarioForge；来源 parameter |
| 6 | B | 65 | medium | choice | `System.Void BattleUIState::RefreshScopeSelection()` | 7 | multi-write | S2+S3+S4+S5+S7；来源 call |
| 7 | B | 65 | medium | dialogue | `System.Void ScenarioForge.SetSpriteByInput::SetSprite(UnityEngine.Sprite)` | 2 | framework-internal,multi-write | S1+S3+S4+S7；ScenarioForge；来源 call |
| 8 | B | 65 | medium | dialogue | `System.Boolean MenuButtonElement::TrySetText(System.String)` | 1 |  | S2+S3+S4+S5+S7；来源 parameter |
| 9 | B | 65 | medium | choice | `System.Void BattleUIState::OnSelect_CharacterStatusPanelElement(System.Int32)` | 1 |  | S2+S3+S4+S5+S7；来源 call |
| 10 | B | 65 | medium | choice | `System.Void EquipmentMenuUIState::OnInventorySelect(System.ValueTuple`2<System.Int32,System.Int32>)` | 1 |  | S2+S3+S4+S5+S7；来源 call |
| 11 | B | 65 | medium | choice | `System.Void ItemsMenuUIState::OnInventorySelect(System.ValueTuple`2<System.Int32,System.Int32>)` | 1 |  | S2+S3+S4+S5+S7；来源 call |
| 12 | B | 65 | medium | choice | `System.Void SaveMenuUIState::EnterCopySelection(SaveSlotUIElement)` | 1 |  | S2+S3+S4+S5+S7；来源 call |
| 13 | B | 65 | medium | choice | `System.Void ScenarioForge.UILeftRightSelector::SetCurOption(System.Int32,System.Boolean)` | 1 | system-ui,framework-internal | S1+S3+S4+S5+S8；ScenarioForge；来源 other |
| 14 | B | 65 | medium | choice | `System.Void ShopMenuUIState::OnBuyInventoryMenuSelect(System.ValueTuple`2<System.Int32,System.Int32>)` | 1 |  | S2+S3+S4+S5+S7；来源 call |
| 15 | B | 65 | medium | choice | `System.Void ShopMenuUIState::OnSellInventoryMenuSelect(System.ValueTuple`2<System.Int32,System.Int32>)` | 1 |  | S2+S3+S4+S5+S7；来源 call |
| 16 | B | 65 | medium | choice | `System.Void SkillsMenuUIState::OnInventorySelect(System.ValueTuple`2<System.Int32,System.Int32>)` | 1 |  | S2+S3+S4+S5+S7；来源 call |
| 17 | B | 55 | low | dialogue | `System.Boolean ScenarioForge.TextTyper/<TypeNextCharacter>d__99::MoveNext()` | 4 | framework-internal,multi-write | S1+S3+S7；ScenarioForge；来源 call |
| 18 | B | 55 | low | choice | `System.Boolean RPGDialogueChoiceContainer/<AppearWithChoices>d__2::MoveNext()` | 3 | multi-write | S1+S3+S7；自定义 *Dialog*/*Talk*；来源 mixed |
| 19 | B | 55 | low | dialogue | `System.Void ScenarioForge.SimpleTextAnimator::SetFrame()` | 2 | framework-internal,multi-write | S1+S3+S4；ScenarioForge；来源 other |
| 20 | B | 55 | low | choice | `System.Void BattleUIState::OnSelect_TroopBattleDisplayElement(System.Int32)` | 1 |  | S2+S3+S4+S5+S7+S8+N2；来源 call |

> 级别：A≥70 且 S1>0（可生成 Hook 草案 + 运行期回放）；B≥50（降级，人工勾选）；C≥30（只记录观察）。
> 分数是静态启发式，不等于可 Hook 性；A 级同样必须过 §8 的运行期确认。

### 2.1 建议优先复核（已排除系统 UI / 示例代码 / 本地化刷新）

| # | 级别 | 分数 | 方法 | 标记 | 为什么被挑出来 |
|---|---|---|---|---|---|
| 1 | A | 85 | `System.Void ScenarioForge.SetTextFromGameVariable::SetText(System.String)` | framework-internal,multi-write | 框架内部实现，游戏侧入口在其上游；同一方法多次写文本（需去重） |
| 2 | A | 75 | `System.Void ScenarioForge.UILeftRightSelector::SetCurValueInRange(System.Single,System.Boolean)` | framework-internal,multi-write | 框架内部实现，游戏侧入口在其上游；同一方法多次写文本（需去重） |
| 3 | A | 75 | `System.Void ScenarioForge.UILeftRightSelector::OverrideSelectorDisplayText(System.String)` | framework-internal | 框架内部实现，游戏侧入口在其上游 |

> 标记含义：system-ui=确认框/设置/存档/暂停等系统 UI；i18n-refresh=换语言/初始化时整窗重写；sample-code=框架自带 Examples/Samples；framework-internal=框架内部实现（游戏侧入口通常在其上游）；multi-write=一处方法多次写文本；hardcoded-text=全是硬编码字面量；upstream-only=只在调用链上游包了一层。标记只做证据标注，**不参与** R4 打分。

## 3. 候选详情（前 8 条）

### #1 `SetTextFromGameVariable.SetText` · A 级 · 85 分 · dialogue · 置信 high

- 方法全名：`System.Void ScenarioForge.SetTextFromGameVariable::SetText(System.String)`
- 签名：`SetText(String)`（2 处文本写入，来源 parameter）
- 发现途径：direct-text-setter；框架：ScenarioForge
- 规则得分：S1 +25 / S2 +20 / S3 +20 / S4 +10 / S7 +10
  - S1 +25 类型属于已知对话框架 ScenarioForge
  - S2 +20 方法名 SetText 命中对话语义关键词
  - S3 +20 直接调用 TMPro.TMP_Text::set_text, UnityEngine.UI.Text::set_text 共 2 处
  - S4 +10 所属类型持有 2 个 Text/TMP 字段
  - S7 +10 文本写入有 2/2 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`ScenarioForge.SetTextFromGameVariable::RefreshText`
- 所属类型持有文本字段：2 个

### #2 `UILeftRightSelector.SetCurValueInRange` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void ScenarioForge.UILeftRightSelector::SetCurValueInRange(System.Single,System.Boolean)`
- 签名：`SetCurValueInRange(Single, Boolean)`（4 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：ScenarioForge
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型属于已知对话框架 ScenarioForge
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 4 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 UILeftRightSelector 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 4/4 处来自参数/字段/调用（非硬编码）
  - S8 +5 调用者类型含 Choice/Select/Answer 语义
- 调用者（5）：`ScenarioForge.UILeftRightSelector::RefreshDisplay`、`ScenarioForge.UILeftRightSelector::SetCurValueInRangeFromMouse`、`ScenarioForge.UILeftRightSelector::OnToggleLeft`、`ScenarioForge.UILeftRightSelector::OnToggleRight`
- 所属类型持有文本字段：1 个

### #3 `UILeftRightSelector.OverrideSelectorDisplayText` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void ScenarioForge.UILeftRightSelector::OverrideSelectorDisplayText(System.String)`
- 签名：`OverrideSelectorDisplayText(String)`（1 处文本写入，来源 parameter）
- 发现途径：direct-text-setter；框架：ScenarioForge
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型属于已知对话框架 ScenarioForge
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 UILeftRightSelector 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
  - S8 +5 调用者类型含 Choice/Select/Answer 语义
- 调用者（1）：`OptionsMenuUIState::UpdateFrameTargetButtonVisuals`
- 所属类型持有文本字段：1 个

### #4 `CentralPauseMenuManager.BackOutOfCentralSelection` · B 级 · 70 分 · choice · 置信 medium

- 方法全名：`System.Void CentralPauseMenuManager::BackOutOfCentralSelection()`
- 签名：`BackOutOfCentralSelection()`（1 处文本写入，来源 field）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S2 +20 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S2 +20 方法名 BackOutOfCentralSelection 命中对话语义关键词
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S4 +10 所属类型持有 2 个 Text/TMP 字段
  - S5 +5 类名 CentralPauseMenuManager 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`CentralPauseMenuManager::Update`、`PauseMenuUIState::OnClick_QuitCancelButton`
- 所属类型持有文本字段：2 个

### #5 `TextTyper.SetContents` · A 级 · 70 分 · dialogue · 置信 medium

- 方法全名：`System.Void ScenarioForge.TextTyper::SetContents(System.String,System.Boolean)`
- 签名：`SetContents(String, Boolean)`（1 处文本写入，来源 parameter）
- 发现途径：direct-text-setter；框架：ScenarioForge
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S7 +10 / S8 +5
  - S1 +25 类型属于已知对话框架 ScenarioForge
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 3 个不同调用者调用
- 调用者（3）：`RPGDialogueBoxManager::OnLocalizationRefresh`、`ScenarioForge.DialogueContainer::SetInitialContents`、`ScenarioForge.DialogueContainer/<TypeText>d__39::MoveNext`
- 所属类型持有文本字段：1 个

### #6 `BattleUIState.RefreshScopeSelection` · B 级 · 65 分 · choice · 置信 medium

- 方法全名：`System.Void BattleUIState::RefreshScopeSelection()`
- 签名：`RefreshScopeSelection()`（7 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S2 +20 / S3 +20 / S4 +10 / S5 +5 / S7 +10
  - S2 +20 方法名 RefreshScopeSelection 命中对话语义关键词
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 7 处
  - S4 +10 所属类型持有 2 个 Text/TMP 字段
  - S5 +5 类名 BattleUIState 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 7/7 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`BattleUIState::SetUIState`
- 所属类型持有文本字段：2 个

### #7 `SetSpriteByInput.SetSprite` · B 级 · 65 分 · dialogue · 置信 medium

- 方法全名：`System.Void ScenarioForge.SetSpriteByInput::SetSprite(UnityEngine.Sprite)`
- 签名：`SetSprite(Sprite)`（2 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：ScenarioForge
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S7 +10
  - S1 +25 类型属于已知对话框架 ScenarioForge
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 2 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S7 +10 文本写入有 2/2 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`ScenarioForge.SetSpriteByInput::Refresh`
- 所属类型持有文本字段：1 个

### #8 `MenuButtonElement.TrySetText` · B 级 · 65 分 · dialogue · 置信 medium

- 方法全名：`System.Boolean MenuButtonElement::TrySetText(System.String)`
- 签名：`TrySetText(String)`（1 处文本写入，来源 parameter）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S2 +20 / S3 +20 / S4 +10 / S5 +5 / S7 +10
  - S2 +20 方法名 TrySetText 命中对话语义关键词
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 1 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 MenuButtonElement 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 1/1 处来自参数/字段/调用（非硬编码）
- 调用者（1）：`EquipmentMenuUIState::RefreshShieldEquipText`
- 所属类型持有文本字段：1 个

## 4. 识别到的文本写入 API

| API | 调用次数 | 调用者方法数 | 是否参与打分 |
|---|---|---|---|
| `TMPro.TMP_Text::set_text` | 238 | 90 | 是（S3） |
| `TMPro.TMP_Text::SetText` | 4 | 3 | 是（S3） |
| `UnityEngine.UI.Text::set_text` | 3 | 3 | 是（S3） |
| `TMPro.TMP_InputField::set_text` | 3 | — | 否（仅统计） |
| `ScenarioForge.SetTextFromGameVariable::SetText` | 1 | — | 否（仅统计） |

> 非台词 setter（TMPro.TMP_InputField::set_text、ScenarioForge.SetTextFromGameVariable::SetText）只统计不参与打分，避免输入框/下拉框文本污染候选。

### 4.1 文本写入最集中的类型（Top 12）

> 这里统计**全部** set_text 调用点（不限于候选），用来回答"文本到底从哪个类流出"。
> 候选漏掉的主对话类常常出现在这张表里，值得对照 §2 检查是否漏报。

| 类型 | 写入点 | 方法数 | 示例方法 |
|---|---|---|---|
| `InventorySlotUIElement` | 36 | 2 | `RefreshDisplay` |
| `CharacterStatusElementCore` | 26 | 3 | `RefreshDisplay` |
| `StatusConditionUI` | 21 | 1 | `RefreshStatusList` |
| `ShopMenuUIState` | 20 | 9 | `UpdateActiveState` |
| `BattleUIState` | 15 | 6 | `UpdateMartyHPBar` |
| `SimpleItemSlot` | 12 | 3 | `SetItemData` |
| `EquipmentMenuUIState` | 9 | 4 | `SetEquipmentMenuActive` |
| `BattleResultsDisplay/<DisplayLevelUpScreen>d__67` | 7 | 1 | `MoveNext` |
| `ScenarioForge.UILeftRightSelector` | 6 | 3 | `SetCurOption` |
| `DebugLoadMenuUIState` | 5 | 3 | `RefreshDisplay` |
| `MusicPlayerUIState` | 5 | 4 | `SetTopTextBarToNonePlaying` |
| `SaveSlotUIElement` | 5 | 2 | `RefreshDisplay` |

## 5. 对话框架痕迹

| 框架 | 类别 | 类型数 | 方法数 | 样例类型 |
|---|---|---|---|---|
| ScenarioForge | 已知框架 | 446 | 2438 | `ScenarioForge.AbstractSaveManager`1`<br>`ScenarioForge.AchievementCommand`<br>`ScenarioForge.AchievementCommand/Mode` |
| 自定义 *Dialog*/*Talk* 命名 | 自定义命名 | 22 | 135 | `BattleResultsDisplay/<SetDialogueCanvasGroupsAlphaDelayed>d__72`<br>`RPGDialogueBoxManager`<br>`RPGDialogueBoxManager/<DisplayChoices>d__59` |

## 6. 候选数据目录排名

| 选择 | 数据目录 | 深度 | 分数 | 类型/方法 | 写入方法 | CJK 方法 | 框架类型 | 理由 |
|---|---|---|---|---|---|---|---|---|
| ✅ | `To the Moon_Data` | 1 | 1750 | 1318/7477 | 93 | 0 | 446 | +1000 已知对话框架类型×446；+1200 对话命名类型×474；+300 文本写入方法×93；+150 目录名与输入目录匹配；-900 启动器/更新器可疑类型×18 |
|  | `To the Moon - Sigmund Minisode 1_Data` | 1 | 1600 | 1313/7462 | 92 | 0 | 446 | +1000 已知对话框架类型×446；+1200 对话命名类型×474；+300 文本写入方法×92；-900 启动器/更新器可疑类型×18 |
|  | `To the Moon - Sigmund Minisode 2_Data` | 1 | 1600 | 1314/7464 | 92 | 0 | 446 | +1000 已知对话框架类型×446；+1200 对话命名类型×474；+300 文本写入方法×92；-900 启动器/更新器可疑类型×18 |

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
- 本程序集未检出 CJK 字面量（英文游戏或文本全在资源里），S6 维度不适用，中文台词定位能力对本作无效

### 本作不适用的打分维度
- S6（+10）：本程序集无 CJK 字面量，维度不适用

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
- [ ] 从候选 `System.Void ScenarioForge.SetTextFromGameVariable::SetText(System.String)` 下断点，确认一次对话会产生几次 set_text 调用
- [ ] 本作无 CJK 字面量：说明台词来自资源文件，静态打分只能靠框架/命名，可靠性下降
- [ ] 游戏目录已有注入痕迹（BepInEx, doorstop_config.ini, .doorstop_version），需确认是否与本工具产物冲突
- [ ] 存在 7 个语义上游候选：确认 Hook 上游（一次拿到完整句子）还是 Hook setter（拿最终渲染文本）

## 9. 备注与告警

- ℹ️ 该游戏目录已存在补丁/注入痕迹（BepInEx, doorstop_config.ini, .doorstop_version, winhttp.dll, nvdaControllerClient.dll），只做静态只读分析，未改动任何文件
- ℹ️ 未解析程序集：0；主文本写入 API：TMPro.TMP_Text::set_text×238, TMPro.TMP_Text::SetText×4, UnityEngine.UI.Text::set_text×3

## 10. 复现与产物

```pwsh
# 在 src\experiments\unity-mono-analyzer 目录下
D:\dotnet\dotnet.exe run -- --game "D:\Steam\steamapps\common\To the Moon" --out <输出目录>
```

- 机器可读产物：`analysis.json`（schemaVersion 1.0，字段见 README）
- 程序集 sha256：`5253c4a50e25a0aaf23b6152497e159aed42b507816af8fb0d7dc8f9812dc15b`
- 候选上限：71 条入选 / 29 条低于阈值未展开

