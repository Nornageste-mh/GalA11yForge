# 4 款校准游戏实测记录

> 生成时间（含时区）：2026-11-14 17:20:14 +08:00
> 机器：Windows 10 · dotnet 8.0.404 · 工具 1.0.0.0
> 全部输入来自本机已安装游戏；游戏目录只读，产物只写在 evidence/ 下。
> 复跑：`pwsh -File tools\run-evidence.ps1`

## 汇总表

| 游戏 | Unity | 类型/方法 | 文本写入点（TMP / UI.Text） | 候选 A/B/C | Top1 | 耗时 | 退出码 |
|---|---|---|---|---|---|---|---|
| 我与你共度的七日 | 2021.3.31f1 | 325/1602 | 26（20 / 6） | 3/2/10 | `System.Void NodeCanvas.DialogueTrees.UI.Examples.DialogueUGUI::OnMultipleChoiceRequest(NodeCanvas.DialogueTrees.MultipleChoiceRequestInfo)` 90 A | 2862 ms | 0 |
| NEEDY GIRL OVERDOSE | 2022.3.22f1 | 2508/10962 | 562（402 / 160） | 6/22/162 | `System.Void ngov3.sayonalastDialog::OnLanguageChanged()` 85 A | 6275 ms | 0 |
| GalMaster | 2022.3.62f3 | 235/2108 | 31（30 / 1） | 4/6/6 | `System.Void DialogueHandle::ShowDialogue(System.Int32,System.Boolean)` 105 A | 1918 ms | 0 |
| To the Moon | 2022.3.28f1 | 1318/7477 | 245（238 / 3） | 4/24/43 | `System.Void ScenarioForge.SetTextFromGameVariable::SetText(System.String)` 85 A | 6600 ms | 0 |

## 每款游戏的输入与入口判定

### 我与你共度的七日

- 输入：`D:\Steam\steamapps\common\我与你共度的七日`（根目录是启动器，真游戏在 game\ 子目录）
- 数据目录：`The seven days i spent with you_Data`；启动入口：`The seven days i spent with you.exe`
- Unity：2021.3.31f1（来源 globalgamemanagers）
- ldstr 373 条；写入点 26 处 / 17 个方法；框架痕迹：NodeCanvas DialogueTrees×20；自定义 *Dialog*/*Talk* 命名×8
- 产物：`runs/seven-days/REPORT.md`、`runs/seven-days/analysis.json`、`runs/seven-days/console.txt`

### NEEDY GIRL OVERDOSE

- 输入：`D:\Steam\steamapps\common\NEEDY GIRL OVERDOSE`（exe 名 Windose.exe 与数据目录同名，旁边有 UnityCrashHandler64.exe）
- 数据目录：`Windose_Data`；启动入口：`Windose.exe`
- Unity：2022.3.22f1（来源 globalgamemanagers）
- ldstr 17773 条；写入点 562 处 / 223 个方法；框架痕迹：自定义 *Dialog*/*Talk* 命名×71
- 产物：`runs/needy/REPORT.md`、`runs/needy/analysis.json`、`runs/needy/console.txt`

### GalMaster

- 输入：`D:\Steam\steamapps\common\GalMaster`（目录名与 exe 名都带空格）
- 数据目录：`GAL PRO MASTER_Data`；启动入口：`GAL PRO MASTER.exe`
- Unity：2022.3.62f3（来源 globalgamemanagers）
- ldstr 709 条；写入点 31 处 / 15 个方法；框架痕迹：自定义 *Dialog*/*Talk* 命名×77
- 产物：`runs/galmaster/REPORT.md`、`runs/galmaster/analysis.json`、`runs/galmaster/console.txt`

### To the Moon

- 输入：`D:\Steam\steamapps\common\To the Moon`（顶层无 exe，嵌套 3 个 Unity Mono 作）
- 数据目录：`To the Moon_Data`；启动入口：`To the Moon.exe`
- Unity：2022.3.28f1（来源 globalgamemanagers）
- ldstr 2494 条；写入点 245 处 / 93 个方法；框架痕迹：ScenarioForge×446；自定义 *Dialog*/*Talk* 命名×22
- 产物：`runs/to-the-moon/REPORT.md`、`runs/to-the-moon/analysis.json`、`runs/to-the-moon/console.txt`

## 总耗时

4 款游戏串行跑完共 18.3 s（含进程启动、写盘；不含构建）。

