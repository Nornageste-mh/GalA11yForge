# unity-mono-analyzer（W2-B 独立原型 0.1.0.0）

Unity **Mono** 游戏的**静态**文本钩子点分析器：输入游戏目录/启动 exe，用 Mono.Cecil 只读分析
`*_Data\Managed\Assembly-CSharp.dll`，输出**候选文本写入点**（JSON + Markdown 报告），供后续生成
Hook 草案、也供人先判断"这游戏能不能自动做无障碍补丁"。

- **只读**：不修改游戏目录、不安装任何东西、不联网。
- **不产出游戏文本**：只统计字面量数量与类型/方法名，报告里不出现任何台词原文（版权红线）。
- **不做运行期验证**：结论是"静态候选"，`coverage.runtimeConfirmations` 里列了 8 项必须人工/运行期确认的事。
- 独立 csproj，**不进主 sln**，与 `forge-core` 无依赖。

## 1. 快速开始

```powershell
# 在 src\experiments\unity-mono-analyzer 下
& D:\dotnet\dotnet.exe run -- --game "D:\Steam\steamapps\common\GalMaster" --out .\out\galmaster
& D:\dotnet\dotnet.exe run -- --game "D:\Steam\steamapps\common\GalMaster" --json | ConvertFrom-Json   # 只吐 JSON
```

| 参数 | 说明 |
|---|---|
| `--game, -g <path>` | 游戏根目录 / `*_Data` 目录 / 启动 exe / `Assembly-CSharp.dll` 四选一 |
| `--exe, -e <path>` | 显式指定启动 exe（先用它反查 `<exe名>_Data`，覆盖自动探测） |
| `--out, -o <dir>` | 报告输出目录，默认 `.\out\<数据目录名>` |
| `--top, -t <N>` | 报告里列出的候选条数，默认 20 |
| `--json` | 只输出完整 JSON（不写报告） |
| `--help, -h` | 帮助 |

退出码：`0` 成功 · `2` 参数错 · `3` 内部异常 · `4` 不是 Unity Mono（含 IL2CPP）· `5` 部分成功（有告警，例如版本未知、多候选并列、无候选）。

## 2. 输出物

每次运行写两个文件（`--out` 目录下）：

- `REPORT.md`：中文报告。§1 结论摘要 · §2 候选表（Top N）· **§2.1 建议优先复核** · §3 候选详情 ·
  §4 文本写入 API + **§4.1 写入点最集中的类型** · §5 框架痕迹 · §6 候选数据目录排名 · §7 覆盖声明 ·
  §8 人工确认清单（8 项 + 本作专项）· §9 告警 · §10 复现。
- `analysis.json`：固定 schema（`schemaVersion=1.0`，camelCase，中文不转义）。顶层键：
  `schemaVersion / run / game / discovery / unity / assembly / stats / textSinks / textSinkHotspots /
  frameworkTraces / candidates / coverage / notes / warnings / errors / timing / exitCode`。

`candidates[]` 每条含：`rank / kind / score / tier / confidence / methodFullName / typeFullName / signature /
discoveredBy / ruleScores(S1..S8,N1..N4) / evidence / textSinkCalls / textSinkApis / valueSource /
cjkLiteralCount / callerCount / typeHoldsTextFields / framework / frameworkCategory / flags`。

## 3. 分析流程

1. **发现**：递归最多 3 层找 `*_Data\Managed\Assembly-CSharp.dll`（跳过 BepInEx、dotnet、Burst 等工具目录）；
   多个候选时先做"快速扫描"（不建调用图）再排名。
2. **入口**：排除 `UnityCrashHandler*` / `*_BurstDebug*` / `UnityPlayer` / `*Crash*`；优先取与 `<X>_Data` 同名的 exe，
   其次与游戏目录同名的 exe，再看版本资源 `ProductName`，最后兜底体积最大者（每次判决都写进 `game.exeReason`）。
3. **版本**：读 `globalgamemanagers` 前 512 字节的版本串（支持 `2019.4.40c1`、`6000.0.59f2` 这类 Unity 6 / 中国版后缀），
   兜底 `UnityPlayer.dll` 文件版本，来源记入 `unity.versionSource`。
4. **扫描（两遍 IL）**：第一遍统计字面量（只记数量/长度）、收集 `set_text` 写入点、按"类型::方法名"聚合；
   第二遍只为"写入者 ∪ 语义命名方法"收集调用边，反推调用者/被调用者（不建全量调用图，避免爆炸）。
5. **打分**：见下；**排序**：分数 ↓ → 直接写入优先 → 写入点数量 ↓ → 方法名。
6. **报告**：Markdown + JSON；有告警时退出码 5。

## 4. 打分规则（research/04-engines §11 的 R4）

| 正分 | | 负分 | |
|---|---|---|---|
| S1 已知对话框架（或调用链上游） | +25 | N1 调试/日志命名 | −20 |
| S2 方法名语义（Say/Line/Dialogue/Talk/Choice/Select…） | +20 | N2 非台词字面量占比 ≥50% 且无 CJK | −15 |
| S3 直接调用 `TMP_Text/UI.Text::set_text` | +20 | N3 只被 Awake/Start/OnEnable/构造调用 | −10 |
| S4 类型持有 Text/TMP 字段 | +10 | N4 全是长度 <2 的固定串 | −10 |
| S5 类名含 Window/UI/Dialog/Box/Panel | +5 | | |
| S6 方法内有 CJK 字面量 | +10 | | |
| S7 写入值来自参数/字段/调用（非硬编码） | +10 | | |
| S8 调用者 >1 或上游有 Choice/Select | +5 | | |

分层：**A** = 总分 ≥70 **且 S1>0**（可生成 Hook 草案）· **B** ≥50（人工勾选）· **C** ≥30（只记录）· 其余排除。
`flags` 是**人工复核标记**（`system-ui / i18n-refresh / sample-code / framework-internal / multi-write /
hardcoded-text / upstream-only`），**不参与打分**；§2.1 的"建议优先复核"会把前三类噪声剔掉——这是防止
"照着 Top1 下 Hook"的关键。

## 5. 4 款校准游戏实测（[实测]，2026-11-14，Release，本机）

| 游戏 | Unity | 类型/方法 | 文本写入点（TMP / UI.Text） | A/B/C | 静态 Top1 | 耗时 |
|---|---|---|---|---|---|---|
| 我与你共度的七日 | 2021.3.31f1 | 325 / 1602 | 26（20 / 6） | 3/2/10 | `NodeCanvas…Examples.DialogueUGUI::OnMultipleChoiceRequest` 90 A | 2.9 s |
| NEEDY GIRL OVERDOSE | 2022.3.22f1 | 2508 / 10962 | 562（402 / 160） | 6/22/162 | `sayonalastDialog::OnLanguageChanged` 85 A | 6.3 s |
| GalMaster | 2022.3.62f3 | 235 / 2108 | 31（30 / 1） | 4/6/6 | `DialogueHandle::ShowDialogue(Int32,Boolean)` 105 A | 1.9 s |
| To the Moon（顶层输入） | 2022.3.28f1 | 1318 / 7477 | 245（238 + SetText 4 / 3） | 4/24/43 | `ScenarioForge.SetTextFromGameVariable::SetText` 85 A | 6.6 s |

三条实测结论：

1. **入口这组判对了**：七日输入根目录（真游戏在 `game\` 子目录）选到真游戏而不是启动器；NEEDY 选到
   `Windose.exe` 而不是 `UnityCrashHandler64.exe`；To the Moon 顶层无 exe、嵌套 3 个 Unity 作，按目录名匹配选中主作。
2. **"写入点多 = 是对话"不成立**：七日启动器 37 处 > 真游戏 26 处；NEEDY 的 `ngov3.Window*` 根本不直接写 TMP，
   真正的写入集中在 `StatusTooltip`（49）、`ControllerGuide_*`（41/40）、`PoketterCell`（22）、`JineCell`（19）。
   → 必须用"框架痕迹 + 对话命名 + CJK + 启动器惩罚"复合排序，并看 §4.1 热点表。
3. **A 级仍可能不是目标**：七日的 A 级头名是 NodeCanvas 自带 Examples（标记 `sample-code`），真游戏体文本在
   `SpeecController::GetStringLine`；To the Moon 的 A 级全是 ScenarioForge 框架内部实现（`framework-internal`）。

## 6. 覆盖声明

**能**：Mono 程序集内的 `TMP_Text/UI.Text::set_text` 直接调用者、语义命名的一层上游包装、类型持有的 Text/TMP 字段、
框架名痕迹统计、CJK 字面量方法计数、Unity 版本/入口/多候选排名、覆盖与人工清单。

**不能**：IL2CPP（直接判 4）；运行期才能确定的可见性/打字机重复写入/说话人顺序（见 §8 清单）；
非 `set_text` 的文本管线（TextMesh 老 API、自定义 shader 字形、委托/事件/资源文件里的文本）；
反射/字符串拼接构造的文本；`Resources`/Addressables 里的台词资源；跨程序集（只扫 `Assembly-CSharp.dll`）。

## 7. 已知误报 / 漏报

- 误报：`InputField::set_text`、TMP 内部实现、DOTween 的 `DOText`、i18n 整窗刷新都会被算作写入点（S3 只认基类为
  `TMP_Text`/`UI.Text` 的 setter）。C 级里这类噪声最多，靠 `flags` 过滤。
- 漏报：只被委托/事件调用的文本方法（没有静态调用边）、异步状态机方法（`MoveNext` 会单独成候选）、
  纯数据驱动且写入点不在 `Assembly-CSharp` 的管线。
- 无 CJK 的英文游戏 S6 恒为 0，A 级只能靠 S1/S2/S3 堆出来（To the Moon 即如此）。

## 8. 目录结构与复跑

```
src\experiments\unity-mono-analyzer\
├─ UnityMonoAnalyzer.csproj      # 独立项目，不进主 sln
├─ src\
│  ├─ Models.cs                  # 输出 schema 的 POCO
│  ├─ TextPatterns.cs            # CJK / 命名语义 / 框架表 / 版本正则
│  ├─ Discovery\                 # EntrySelector(+UnityVersionDetector) / GameDiscovery
│  ├─ Analysis\                  # AssemblyScanner / IlStackAnalyzer / Scoring / CandidateRanker / ReviewFlags / Analyzer
│  ├─ Reporting\                 # JsonReport / MarkdownReport / Coverage
│  └─ Cli\                       # Program / CliOptions
├─ tests\UnityMonoAnalyzer.Tests\ # xunit：107 项（含 7 项真游戏集成，游戏缺失自动 Skip）
├─ tools\run-evidence.ps1         # 复跑 4 款游戏 → evidence\
└─ evidence\                      # 4games-summary.csv / run-log.md / runs\<slug>\{REPORT.md,analysis.json,console.txt}
```

```powershell
# 测试（107 项，含对本机 4 款游戏的只读集成测试；未安装的游戏自动 Skip）
& D:\dotnet\dotnet.exe test tests\UnityMonoAnalyzer.Tests

# 复跑实测证据（只写 evidence\）
pwsh -NoProfile -File tools\run-evidence.ps1
```

## 9. 许可与数据说明

代码为本项目自有（未引入第三方源码）；依赖仅 **Mono.Cecil 0.11.6（MIT）**。分析对象是本机已安装游戏的程序集，
产物只含类型名/方法名/计数，**不含任何游戏台词、图片、音频**；游戏目录全程只读。
