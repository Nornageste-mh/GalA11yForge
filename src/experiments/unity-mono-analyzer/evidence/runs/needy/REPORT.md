# Unity Mono 文本钩子静态分析报告

> 工具：unity-mono-analyzer 0.1.0.0（Mono.Cecil 0.11.6） · schema 1.0
> 生成时间（UTC）：2026-11-14T09:20:00.0867183Z
> 复现命令：`unity-mono-analyzer --game "D:\Steam\steamapps\common\NEEDY GIRL OVERDOSE" --out D:\DSHWorkSpace\src\experiments\unity-mono-analyzer\evidence\runs\needy`

## 1. 结论摘要

- 游戏：NEEDY GIRL OVERDOSE
- 游戏目录：`D:\Steam\steamapps\common\NEEDY GIRL OVERDOSE`
- 数据目录：`Windose_Data`（`D:\Steam\steamapps\common\NEEDY GIRL OVERDOSE\Windose_Data`）
- 启动入口：`Windose.exe`（与数据目录 Windose_Data 同名）
- Unity 版本：2022.3.22f1（来源 globalgamemanagers）
- 程序集：`Assembly-CSharp.dll` 2540.5 KB（sha256 `2aead7caf7aa…`）
- 规模：2,508 类型 / 10,962 方法（10,556 有方法体）/ 17,773 条 ldstr
- 文本写入点：562 处，来自 223 个方法（TMPro.TMP_Text::set_text×402、UnityEngine.UI.Text::set_text×160）
- 框架痕迹：自定义 *Dialog*/*Talk* 命名（71 类型）
- 候选分层：A 6 / B 22 / C 162（入选 190 条，低于阈值 33 条；共打分 223 条）
- CJK 字面量：64 个方法命中
- 分析耗时：5632 ms（发现 38 / 候选扫描 3230 / 全量扫描 2227 / 打分 57）
- **建议优先确认**：`System.Void ngov3.ControllerGuide_Switch::SetText(ngov3.ControllerGuide/TabType)`（B 级 60 分，dialogue）
- 退出码：0（0 成功 / 4 不是 Unity Mono / 5 部分成功）

## 2. 候选钩子点（Top 20）

| # | 级别 | 分数 | 置信 | 类型 | 方法 | 写入点 | 标记 | 证据摘要 |
|---|---|---|---|---|---|---|---|---|
| 1 | A | 85 | high | dialogue | `System.Void ngov3.sayonalastDialog::OnLanguageChanged()` | 3 | i18n-refresh,multi-write | S1+S3+S4+S5+S6+S7+S8；自定义 *Dialog*/*Talk*；CJK×1；来源 mixed |
| 2 | A | 75 | medium | dialogue | `System.Void ngov3.HakkyoDialog::OnLanguageChanged()` | 3 | i18n-refresh,multi-write | S1+S3+S4+S5+S7+S8；自定义 *Dialog*/*Talk*；来源 mixed |
| 3 | A | 75 | medium | dialogue | `System.Void ngov3.RebootDialog::OnLanguageChanged()` | 3 | system-ui,i18n-refresh,multi-write | S1+S3+S4+S5+S7+S8；自定义 *Dialog*/*Talk*；来源 call |
| 4 | A | 75 | medium | dialogue | `System.Void ngov3.ShutDownDialog::OnLanguageChanged()` | 3 | system-ui,i18n-refresh,multi-write | S1+S3+S4+S5+S7+S8；自定义 *Dialog*/*Talk*；来源 call |
| 5 | A | 75 | medium | dialogue | `System.Void ngov3.TimePassDialog::OnLanguageChanged()` | 3 | system-ui,i18n-refresh,multi-write | S1+S3+S4+S5+S7+S8；自定义 *Dialog*/*Talk*；来源 call |
| 6 | A | 75 | medium | dialogue | `System.Void ngov3.adieuDialog::OnLanguageChanged()` | 3 | i18n-refresh,multi-write | S1+S3+S4+S5+S7+S8；自定义 *Dialog*/*Talk*；来源 mixed |
| 7 | B | 60 | medium | dialogue | `System.Void ngov3.ControllerGuide_Switch::SetText(ngov3.ControllerGuide/TabType)` | 41 | multi-write | S2+S3+S4+S7；来源 call |
| 8 | B | 60 | medium | dialogue | `System.Void ngov3.ControllerGuide_PlayStation::SetText(ngov3.ControllerGuide/TabType)` | 40 | multi-write | S2+S3+S4+S7；来源 call |
| 9 | B | 60 | medium | dialogue | `System.Void ngov3.EndingDialog::OnLanguageChanged()` | 2 | i18n-refresh,multi-write | S1+S3+S4+S5+S7+S8+N2；自定义 *Dialog*/*Talk*；来源 mixed |
| 10 | B | 60 | medium | dialogue | `System.Void ngov3.StatusTooltip2D::SetBonusLine(System.Boolean,ngov3.LanguageType)` | 1 | system-ui | S2+S3+S4+S7；来源 call |
| 11 | B | 60 | medium | dialogue | `System.Void ngov3.StatusTooltip::SetBonusLine(ngov3.LanguageType)` | 1 | system-ui | S2+S3+S4+S7；来源 call |
| 12 | B | 55 | low | unknown | `System.Void ngov3.Live::UpdateDetail()` | 2 | multi-write | S3+S4+S6+S7+S8；CJK×2；来源 call |
| 13 | B | 55 | low | unknown | `System.Void ngov3.LiveComment::highlighted()` | 2 | multi-write | S3+S4+S6+S7+S8；CJK×1；来源 mixed |
| 14 | B | 55 | low | unknown | `System.Void ngov3.Live_gen_old::setSpeed()` | 1 |  | S3+S4+S6+S7+S8；CJK×3；来源 field |
| 15 | B | 50 | low | unknown | `System.Boolean ngov3.KusoRepView::SetBody(ngov3.LanguageType)` | 11 | multi-write | S3+S4+S5+S7+S8；来源 field |
| 16 | B | 50 | low | unknown | `System.Boolean ngov3.KusoRepViewStatic::SetBody()` | 11 | multi-write | S3+S4+S5+S7+S8；来源 field |
| 17 | B | 50 | low | menu | `System.Void ngov3.StartMenuView::OnLanguageUpdated()` | 6 | i18n-refresh,multi-write | S3+S4+S5+S7+S8；来源 call |
| 18 | B | 50 | low | unknown | `System.Void Rewired.Demos.GamepadTemplateUI.ControllerUIElement::ClearLabels()` | 3 | sample-code,multi-write | S3+S4+S5+S7+S8；来源 field |
| 19 | B | 50 | low | unknown | `System.Boolean ngov3.EgosaRepView2D::SetBody(ngov3.LanguageType)` | 1 |  | S3+S4+S5+S7+S8；来源 call |
| 20 | B | 50 | low | unknown | `System.Boolean ngov3.KusoRepView2D::SetBody(ngov3.LanguageType)` | 1 |  | S3+S4+S5+S7+S8；来源 field |

> 级别：A≥70 且 S1>0（可生成 Hook 草案 + 运行期回放）；B≥50（降级，人工勾选）；C≥30（只记录观察）。
> 分数是静态启发式，不等于可 Hook 性；A 级同样必须过 §8 的运行期确认。

### 2.1 建议优先复核（已排除系统 UI / 示例代码 / 本地化刷新）

| # | 级别 | 分数 | 方法 | 标记 | 为什么被挑出来 |
|---|---|---|---|---|---|
| 1 | B | 60 | `System.Void ngov3.ControllerGuide_Switch::SetText(ngov3.ControllerGuide/TabType)` | multi-write | 同一方法多次写文本（需去重） |
| 2 | B | 60 | `System.Void ngov3.ControllerGuide_PlayStation::SetText(ngov3.ControllerGuide/TabType)` | multi-write | 同一方法多次写文本（需去重） |
| 3 | B | 55 | `System.Void ngov3.Live::UpdateDetail()` | multi-write | 同一方法多次写文本（需去重） |

> 标记含义：system-ui=确认框/设置/存档/暂停等系统 UI；i18n-refresh=换语言/初始化时整窗重写；sample-code=框架自带 Examples/Samples；framework-internal=框架内部实现（游戏侧入口通常在其上游）；multi-write=一处方法多次写文本；hardcoded-text=全是硬编码字面量；upstream-only=只在调用链上游包了一层。标记只做证据标注，**不参与** R4 打分。

## 3. 候选详情（前 8 条）

### #1 `sayonalastDialog.OnLanguageChanged` · A 级 · 85 分 · dialogue · 置信 high

- 方法全名：`System.Void ngov3.sayonalastDialog::OnLanguageChanged()`
- 签名：`OnLanguageChanged()`（3 处文本写入，来源 mixed）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S6 +10 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 3 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 sayonalastDialog 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S6 +10 方法内有 1 条 CJK 字面量
  - S7 +10 文本写入有 2/3 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`ngov3.sayonalastDialog::Start`、`ngov3.sayonalastDialog::<Start>b__4_0`
- 所属类型持有文本字段：1 个

### #2 `HakkyoDialog.OnLanguageChanged` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void ngov3.HakkyoDialog::OnLanguageChanged()`
- 签名：`OnLanguageChanged()`（3 处文本写入，来源 mixed）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 3 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 HakkyoDialog 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 2/3 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`ngov3.HakkyoDialog::Start`、`ngov3.HakkyoDialog::<Start>b__6_0`
- 所属类型持有文本字段：1 个

### #3 `RebootDialog.OnLanguageChanged` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void ngov3.RebootDialog::OnLanguageChanged()`
- 签名：`OnLanguageChanged()`（3 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 3 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 RebootDialog 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 3/3 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`ngov3.RebootDialog::Start`、`ngov3.RebootDialog::<Start>b__4_0`
- 所属类型持有文本字段：1 个

### #4 `ShutDownDialog.OnLanguageChanged` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void ngov3.ShutDownDialog::OnLanguageChanged()`
- 签名：`OnLanguageChanged()`（3 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 3 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 ShutDownDialog 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 3/3 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`ngov3.ShutDownDialog::Start`、`ngov3.ShutDownDialog::<Start>b__4_0`
- 所属类型持有文本字段：1 个

### #5 `TimePassDialog.OnLanguageChanged` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void ngov3.TimePassDialog::OnLanguageChanged()`
- 签名：`OnLanguageChanged()`（3 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 3 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 TimePassDialog 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 3/3 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`ngov3.TimePassDialog::Start`、`ngov3.TimePassDialog::<Start>b__6_0`
- 所属类型持有文本字段：1 个

### #6 `adieuDialog.OnLanguageChanged` · A 级 · 75 分 · dialogue · 置信 medium

- 方法全名：`System.Void ngov3.adieuDialog::OnLanguageChanged()`
- 签名：`OnLanguageChanged()`（3 处文本写入，来源 mixed）
- 发现途径：direct-text-setter；框架：自定义 *Dialog*/*Talk*
- 规则得分：S1 +25 / S3 +20 / S4 +10 / S5 +5 / S7 +10 / S8 +5
  - S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）
  - S3 +20 直接调用 TMPro.TMP_Text::set_text 共 3 处
  - S4 +10 所属类型持有 1 个 Text/TMP 字段
  - S5 +5 类名 adieuDialog 含 Window/UI/Dialog/Box/Panel 等 UI 语义
  - S7 +10 文本写入有 3/3 处来自参数/字段/调用（非硬编码）
  - S8 +5 被 2 个不同调用者调用
- 调用者（2）：`ngov3.adieuDialog::Start`、`ngov3.adieuDialog::<Start>b__6_0`
- 所属类型持有文本字段：1 个

### #7 `ControllerGuide_Switch.SetText` · B 级 · 60 分 · dialogue · 置信 medium

- 方法全名：`System.Void ngov3.ControllerGuide_Switch::SetText(ngov3.ControllerGuide/TabType)`
- 签名：`SetText(TabType)`（41 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S2 +20 / S3 +20 / S4 +10 / S7 +10
  - S2 +20 方法名 SetText 命中对话语义关键词
  - S3 +20 直接调用 UnityEngine.UI.Text::set_text 共 41 处
  - S4 +10 所属类型持有 41 个 Text/TMP 字段
  - S7 +10 文本写入有 41/41 处来自参数/字段/调用（非硬编码）
- 调用者（0）：无（入口方法或事件回调）
- 所属类型持有文本字段：41 个

### #8 `ControllerGuide_PlayStation.SetText` · B 级 · 60 分 · dialogue · 置信 medium

- 方法全名：`System.Void ngov3.ControllerGuide_PlayStation::SetText(ngov3.ControllerGuide/TabType)`
- 签名：`SetText(TabType)`（40 处文本写入，来源 call）
- 发现途径：direct-text-setter；框架：无
- 规则得分：S2 +20 / S3 +20 / S4 +10 / S7 +10
  - S2 +20 方法名 SetText 命中对话语义关键词
  - S3 +20 直接调用 UnityEngine.UI.Text::set_text 共 40 处
  - S4 +10 所属类型持有 40 个 Text/TMP 字段
  - S7 +10 文本写入有 40/40 处来自参数/字段/调用（非硬编码）
- 调用者（0）：无（入口方法或事件回调）
- 所属类型持有文本字段：40 个

## 4. 识别到的文本写入 API

| API | 调用次数 | 调用者方法数 | 是否参与打分 |
|---|---|---|---|
| `TMPro.TMP_Text::set_text` | 402 | 173 | 是（S3） |
| `UnityEngine.UI.Text::set_text` | 160 | 50 | 是（S3） |
| `TMPro.TMP_InputField::set_text` | 7 | — | 否（仅统计） |
| `Rewired.UI.ControlMapper.ControlMapper/GUILabel::set_text` | 2 | — | 否（仅统计） |
| `TMPro.TMP_Dropdown/OptionData::set_text` | 2 | — | 否（仅统计） |
| `ngov3.ControllerGuide::SetText` | 1 | — | 否（仅统计） |
| `ngov3.TextInputItem::SetText` | 1 | — | 否（仅统计） |

> 非台词 setter（TMPro.TMP_InputField::set_text、Rewired.UI.ControlMapper.ControlMapper/GUILabel::set_text、TMPro.TMP_Dropdown/OptionData::set_text、ngov3.ControllerGuide::SetText、ngov3.TextInputItem::SetText）只统计不参与打分，避免输入框/下拉框文本污染候选。

### 4.1 文本写入最集中的类型（Top 12）

> 这里统计**全部** set_text 调用点（不限于候选），用来回答"文本到底从哪个类流出"。
> 候选漏掉的主对话类常常出现在这张表里，值得对照 §2 检查是否漏报。

| 类型 | 写入点 | 方法数 | 示例方法 |
|---|---|---|---|
| `ngov3.StatusTooltip` | 49 | 5 | `SetActionName` |
| `ngov3.ControllerGuide_Switch` | 41 | 1 | `SetText` |
| `ngov3.ControllerGuide_PlayStation` | 40 | 1 | `SetText` |
| `ngov3.PoketterCell` | 22 | 7 | `SetData` |
| `ngov3.JineCell` | 19 | 5 | `changeDay` |
| `ngov3.StatusTooltip2D` | 19 | 5 | `SetActionName` |
| `ngov3.PoketterCellStatic` | 17 | 5 | `SetDataStatic` |
| `ngov3.EndingUraUraTweets` | 12 | 1 | `OnLanguageUpdated` |
| `ngov3.KusoRepView` | 12 | 2 | `SetUserName` |
| `ngov3.KusoRepViewStatic` | 12 | 2 | `SetUserName` |
| `Rewired.UI.ControlMapper.CalibrationWindow` | 9 | 2 | `Initialize` |
| `ngov3.Boot` | 9 | 3 | `SetCaution` |

## 5. 对话框架痕迹

| 框架 | 类别 | 类型数 | 方法数 | 样例类型 |
|---|---|---|---|---|
| 自定义 *Dialog*/*Talk* 命名 | 自定义命名 | 71 | 299 | `NGO.AmeTalkType`<br>`Rewired.Demos.ControlRemappingDemo1/DialogHelper`<br>`Rewired.Demos.ControlRemappingDemo1/DialogHelper/DialogType` |

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
- 未发现「语义上游」候选（没有方法名带 Say/Line/Dialogue 等关键词的上层包装器），可能存在非语义命名的包装器

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
- [ ] 从候选 `System.Void ngov3.sayonalastDialog::OnLanguageChanged()` 下断点，确认一次对话会产生几次 set_text 调用
- [ ] 确认 CJK 字面量所在的候选是否就是台词路径（有些只是界面按钮文案）

## 8. 备注与告警

- ℹ️ 未解析程序集：0；主文本写入 API：TMPro.TMP_Text::set_text×402, UnityEngine.UI.Text::set_text×160

## 9. 复现与产物

```pwsh
# 在 src\experiments\unity-mono-analyzer 目录下
D:\dotnet\dotnet.exe run -- --game "D:\Steam\steamapps\common\NEEDY GIRL OVERDOSE" --out <输出目录>
```

- 机器可读产物：`analysis.json`（schemaVersion 1.0，字段见 README）
- 程序集 sha256：`2aead7caf7aa003f7c3fe68988fc7befd989b29e1ba74f65305bcbe65456901d`
- 候选上限：190 条入选 / 33 条低于阈值未展开

