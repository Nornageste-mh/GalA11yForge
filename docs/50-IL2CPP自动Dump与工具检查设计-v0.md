# IL2CPP 自动 Dump 与工具检查：设计草案 v0

- 状态：草案（2026-11-15 10:0x），待 task-56（工具获取）与 task-57（字段兜底）报告校正
- 背景：0.2.10 的 IL2CPP 路线要求用户已经具备两样之一——BepInEx6 生成的 `BepInEx/interop` 程序集，或手工准备的 `dump.cs`。
  用户 2026-11-15 邮件明确要求“直接集成 Il2CppDumper”。本草案把“缺输入”变成“自动补输入”。

## 1. 要解决的具体问题

现在 `forge analyze` 在 SiliconHeart、nonand2 上返回 `E_DEPENDENCY_MISSING`，提示用户自己找 dump.cs。
对甲方/测试这类用户来说，下面三点都做不到：去哪下载 Il2CppDumper、命令行怎么给参数、dump 文件放哪。
所以产品应当：**能自己找到工具 → 自己跑 → 自己接上后面的 analyze/plan**；找不到就给出可执行的下一步，而不是一句错误码。

## 2. 工具发现的顺序（确定性，便于排查）

对每个外部工具（Il2CppDumper、pyghidra、Frida、dnSpyEx），按同一顺序找：

1. 显式参数：`--option il2cpp.dumperPath=<exe 或目录>`（临时覆盖，不落盘）
2. 工作区旁：`<workspace>/tools/<tool>/...`
3. 程序旁：`<forge.exe 所在目录>/tools/<tool>/...`（与 bepinex 随包同一思路）
4. 用户配置（后续版本）：`%LOCALAPPDATA%\GalA11yForge\config.json`
5. 系统 PATH（例如 `frida-ps.exe`、`python.exe`）

找到后记录“来源=哪一步”，输出到 analyze 报告的 `tools` 段与 `forge doctor`，方便用户截图给我们排查。

## 3. 自动 Dump 流程（analyze 内部，读游戏目录只读）

前置：检测结果为 unity_il2cpp，且 interop 目录与 `dump.cs` 都不存在时，进入“自动 dump”分支。

1. 定位输入：`<game>/GameAssembly.dll` 与 `<game>/<Game>_Data/il2cpp_data/Metadata/global-metadata.dat` 必须同时存在；否则 E_DEPENDENCY_MISSING（details 写清缺哪一个）。
2. 定位输出：`<workspace>/work/<game>/03-generate` 同级新建 `02-dump/`（不写游戏目录）。
3. 执行：`Il2CppDumper.exe <GameAssembly.dll> <global-metadata.dat> <输出目录>`，超时 10 分钟，流式收集 stdout 到日志。
   - 启动时设环境变量 `DOTNET_ROLL_FORWARD=LatestMajor`（Il2CppDumper 6.7.46 目标 .NET 6，本机只有 .NET 8；不设会起不来）。
   - **成功判定看产物，不看退出码**：实测该版本结尾 `Console.ReadKey` 在重定向 stdin 下会抛异常，退出码是 -532462766，但 dump.cs/script.json 完整。
4. 产物校验：`dump.cs` 存在且大小 > 100KB；否则 E_PARTIAL，details 带 dumper 退出码与日志尾部。
5. 继续走已有的“仅 dump.cs”路线：解析类型/方法/字段 → 打分 → Plan；报告里标注 `dumpSource=auto`。
6. 失败降级：把 interop 路线作为推荐（“装 BepInEx6 启动一次”），两条路都写清楚，不假装成功。

## 4. `forge tools` 子命令（本版只做 check，不做自动下载）

- `forge tools check [--json]`：逐项输出工具名、是否找到、路径、版本（能读则读）、许可证（来自内置静态表，不联网）、来源步骤。
- `forge tools guide [--tool <name>]`：打印“去哪下载、放哪个目录、注意许可证”，默认不下载。
- 为什么不在产品里自动下载：dnSpyEx 是 GPLv3、Frida 系列体积/注入风险、Il2CppDumper 版本更新快；
  产品自动下载会把“许可证与版本责任”背到自己身上。**下载动作由用户/自动化脚本完成，产品只做发现与校验。**
- `forge doctor` 增加可选段 `re-tools`：四项状态；任何一项缺失都不影响 `ok`（可选能力，不是硬依赖）。

## 5. 与 0.2.11 的版本边界

- 0.2.11 只做：Il2CppDumper 自动 dump（步骤 3）+ `tools check/guide` + 报告里的 tools 段。
- pyghidra/Frida 在 0.2.11 只做“发现 + 指引”，不接分析链（等它们在本机兼容性/稳定性有实测结论）。
- 不在发行包里放任何 GPL/动态注入工具；发行包只带“工具清单与下载指引”。

## 6. 测试计划

- 单测（不依赖真实 dumper）：工具路径解析顺序、参数拼装、缺文件分支、`dump.cs` 校验、错误 details 字段。
- 假 dumper 集成测试：用一个测试内生成的小脚本冒充 Il2CppDumper，返回固定 dump.cs，验证 analyze 能接着走；只在测试临时目录，不进仓库产物。
- 真机：SiliconHeart 或 nonand2 只读副本上跑一次完整 analyze；记录耗时、dump.cs 行数、候选数。
- 回归：manosaba（已有 interop）不应触发自动 dump，行为不变。

## 7. 风险与未确认

- Il2CppDumper 对 Unity 2021+/2022+ metadata 版本的兼容性（task-56 会给实测版本）。
- dump.cs 可能几十万行、几百 MB；要做体积上限与清理策略（默认保留，报告里提示可删）。
- 许可证（2026-11-15 task-56 实测）：Il2CppDumper **MIT**、pyghidra **Apache-2.0**（可随包，但当前产品不随包分发）；
  Frida/frida-tools **wxWindows 3.1**、frida-il2cpp-bridge **MIT**、dnSpyEx **GPLv3**——这三类不随发行包。
- 自动 dump 是“本地派生数据”，不随发行包分发；报告与证据只收字段片段，不整包入库。

## 6. 真机验证记录（0.2.11.0）

- 命令：forge analyze --game D:\Steam\steamapps\common\SiliconHeart --workspace _il2cpp-autodump-e2e --option re.il2cppdumper.path=D:\DSHWorkSpace\_re-tools\il2cppdumper\v6.7.46\Il2CppDumper.exe --json。
- 结果：ok=true，自动 dump 20.0s 生成 17.4MB dump.cs，分析出 40 个候选；游戏目录 0 改动。
- 产物目录：<工作区>/games/siliconheart/il2cpp-dump/（dump.cs、script.json、stringliteral.json、il2cpp.h、DummyDll/）。
- 失败路径单测：Analyze_WithoutInteropOrDump_TriesAutoDumpThenFailsHonestly、Analyze_WithAutoDumpDisabled_KeepsInteropAndDumpInstructions。

## 7. 真机验证记录（0.2.12.0，字段兜底）

- 入口：Lilith（已有 interop）→ analyze/plan/generate/build 全链路，产物 18,944 B。
- 运行：`[Speech:fields]` 16 条；`ShowNode` 读 `_dialogueText`（4 字＝「周末快乐」）；`_diag` 噪声 0 条；
  过滤前同场景 1,010 条（含 `not started`/`started hwnd=…`）。
- 证据：`research/50-il2cpp-field-fallback/evidence/runtime-fieldhook-LogOutput.log`（255,179 B）、
  `runtime-fieldhook-filtered-LogOutput.log`（8,251 B）。
- 已知边界：界面装饰文本仍可能被读（`GiftCellView::SetRootVisible` 读出饰品名）；Tolk 缺失时只写日志。

## 8. 复验补充（0.2.13.0，manosaba）

- 目的：验证字段兜底不会劫持本来有字符串形参的游戏。
- 发现：0.2.12 把 `ChatMessage::set_messageText(System.String|)`（形参无名）误当字段补丁，build 报 CS0012（缺 UnityEngine.UI）。
- 修复：计划器只在 `fieldOnly`（真正没有 string 形参）时走字段分支；生成器为字段补丁补 UnityEngine.UI 引用。
- 复验：manosaba 6 个带名 string 钩子，generate/build 成功；Lilith 字段兜底路线回归构建成功。
- 证据：`research/50-il2cpp-field-fallback/evidence/manosaba-0.2.13-build.txt`。

## 9. 占位文本过滤真机 A/B（0.2.14.0，manosaba）

- 问题：0.2.13 真机日志里 24 条 `Naninovel.UI.GameStateSlot::SetTitleText：…Empty` 被当台词输出（标题画面的占位槽位）。
- 修复：IL2CPP 运行时（`A11y/A11yRuntime.cs` 模板）与 Unity Mono 的 `SpeechGate` 都加占位判定——去掉 `[12] ` 前缀后等于 `Empty`/`None`/`null`/`N/A`/`未设置`/`无内容` 的文本直接丢弃；`Shutdown()` 统计新增“占位 N 次”。
- 真机 A/B（同一台机器、同一 manosaba、同一操作：New Game → 连点推进 6 次）：
  - 0.2.13：`[a11y-speech]` 共 31 条 = 24 条 Empty 噪声 + 7 条真台词。
  - 0.2.14：**Empty 0 条**；7 条真台词按序（`——这里是囚牢。`、`樱羽艾玛只想捂紧耳朵…`、`可是，她被无形的锁链束缚着…`、`有时被锁在厕所隔间里。`、`有时课本被撕毁丢弃。`、`有时室内鞋里被塞满了虫。`、`有时被集体暴打施以私刑。`）。
- 证据：`research/50-il2cpp-field-fallback/evidence/manosaba-0.2.14-placeholder-LogOutput.log`（16,292 B）、`manosaba-0.2.14-placeholder-lines.txt`（7 行）、截图 `_il2cpp-manosaba-e2e/runtime/shot-0214-dialogue.png`。
- 顺带记录的操作坑：manosaba（Unity 6 新 Input System）只认“真实光标 + 鼠标消息”，且必须在同一进程内先 `SetForegroundWindow` 再 `SetCursorPos + mouse_event`；`PostMessage` 单发、`SendKeys`、标题按钮 y=985 位置都可能无效，点 `New Game` 用 `(415, 905)` 才能进游戏。游戏目录收尾已还原（删除临时 DLL、恢复 `LogOutput.log`、插件目录只剩用户原有两文件）。



## 10. UI 装饰文本噪声抑制（0.2.15.0）

- 问题：0.2.12 真机里 Lilith 的字段兜底会读 `GiftCellView._text`（饰品名）和 `LilithFolderView`（界面字）；0.2.15 复跑还发现 `MemoFullView::RefreshAll/RefreshChrome/RefreshEditor` 这类界面工具类会占满 6 个补丁点名额，把真正的 `DialogueBubbleUI::ShowNode` 挤掉。
- 修复（三层）：
  1. 分析阶段排除“装饰性容器”宿主类型：`Il2CppPatterns.FieldHostExcludeRx = (cell|slot|item|folder|grid|list|tab|scroll|button|toggle|badge|icon|avatar|thumbnail|photo|sprite)`，在 `Il2CppAnalyzer.IsFieldFallbackCandidate` 里对 `type.Name` 直接跳过。
  2. 排序加权：`FieldHostPriorityRx = (dialogue|dialog|chat|bubble|subtitle|caption|speech|story|say)` 命中的宿主补到 56 分；其中方法名再命中 Show/Display/Print 这类展示动词的补到 60 分，保证 `ShowNode`/`ShowOptions` 在同宿主里排最前。
  3. 名额上限：`Il2CppPlanner.MaxFieldFallbackHooks = 2`——每款游戏最多自动放 2 个字段兜底入口，其余进人工待办（标题“字段兜底名额已满（人工确认）”，不假装自动成功）。
- 单测：`Analyze_DecorativeHostType_IsNotFieldFallbackCandidate`（GiftCellView 不进候选）、`Plan_CapsFieldFallbackHooksAtTwo`（4 个字段候选只出 2 个补丁点 + 1 条名额已满人工待办）；Il2Cpp 测试 52 → 54。
- 真机（Lilith，0.2.15 开发版构建，手工放 DLL 进 `BepInEx\plugins`，不经 forge install）：
  - 计划从“6 个钩子（含 2 条 DOTween setter、MemoFullView×3）”变为 **2 个钩子**：`DialogueBubbleUI::ShowNode`、`DialogueBubbleUI::ShowOptions`（均 60 分）。
  - 运行日志出现 `[Speech:fields] hook=DialogueBubbleUI::ShowNode field=_dialogueText len=4` 与 `[a11y-speech] DialogueBubbleUI::ShowNode：周末快乐`。
  - 噪声计数：`GiftCellView` 0、`LilithFolderView` 0、`MemoFullView` 0（对比 0.2.12 的 GiftCellView/LilithFolderView 命中）。
  - 诚实边界：本机只触发到标题画面的一句（DSH GUI 窗口遮挡导致无法稳定推进多句），未验证连续多句与选项朗读；游戏目录已还原（删临时 DLL、恢复 `LogOutput.log`、插件目录只剩用户原有 4 个文件）。
- 证据：`research/50-il2cpp-field-fallback/evidence/lilith-0.2.15-two-hooks-runtime-LogOutput.log`、`lilith-0.2.15-two-hooks-lines.txt`、截图 `_il2cpp-field-e2e2/runtime/shot-0215-play.png`。

## 11. SiliconHeart interop 渠道复验（2026-11-15 17:35，0.2.18 版）

- 背景：0.2.12 初次对 SiliconHeart 分析时该游戏没有 `BepInEx/interop`，自动 dump 只能产出 dump.cs 骨架；
  0.2.17 修了 dump 复用，但“有 interop 时的字符串通道”一直没在这款游戏上验证。
- 用户 17:29 第 2 条“允许”后执行（本次只做 interop 安装 + 工程验证，不改游戏资源）：
  1. 手动把 BepInEx 6.0.0-be.788 解压进游戏根目录，首次启动 100 秒生成 interop **94 个 DLL**
     （日志 `Il2CppInteropGen Done in 00:00:13`）；
  2. `forge analyze`：ok=true，40 个候选；前三 `Naninovel.UITextPrinter::set_Text(String|value)` 100、
     `RevealableTextPrinterPanel::set_PrintedText` 95、`RevealableUIText::set_Text` 90；
  3. `forge plan`：route=automaticwithconfirmation，**6 个补丁点全是 Naninovel 字符串形参钩子**，12 个生成物；
  4. `forge generate` + `forge build`：成功，DLL 17,408 B，SHA256 `A34C41E7A4B9C16217DFCD69742E35B7A248B20D0A7D785EB7165EF2D47F74F7`；
  5. `forge install --bepinex BepInEx_win_x64_6.0.0-be.788.zip`：229 个文件操作，deploymentId `dep-20261115-093830-ad94`；
  6. 启动日志：`[Info :siliconheart 无障碍补丁] [a11y] 朗读运行时就绪；speech=True，tolk=False`（插件加载、运行时初始化成功；本机无 Tolk 走日志兜底）。
- 未取证/边界：真实台词行未捕获（用户前台窗口遮挡，无法安全推进对话）；未人工听辨；interop 生成耗时约 100 秒，属一次性成本。
- 回滚：`forge verify --rollback --json` → overall=pass、10 项检查、失败 0、未验证 2；随后手动移除 BepInEx6 安装文件，
  游戏目录恢复为原始的 6 项 + `SiliconHeart_Data`。
- 证据：`research/50-il2cpp-field-fallback/evidence/siliconheart-interop-{analyze,plan,LogOutput}.log/json`、
  `siliconheart-interop-0.2.18-brief.txt`、`siliconheart-interop-verify-rollback.md`。
