# 分析证据与报告 Schema v0（中间产物数据契约）

日期：2026-11-14　状态：草案 v0（Lead 起草，供 W2-B/W2-C/W2-D/W2-E/W2-J 与各 RE 子代理对齐）
配套：`docs/06-接口契约-v1.md` 定义 C# 接口；本文定义**落盘 JSON 的数据契约**。

## 0. 为什么需要它

RE 自动化的核心是「证据可流转」：静态分析器产出的候选点，要被生成器消费；生成器产物要被验证器检查；
所有结论都要能写进给用户看的分析报告。如果不统一格式，每个模块各造一套 JSON，产品就拼不起来。

规则：

- 所有 JSON 使用 UTF-8（无 BOM）、键名 lowerCamelCase、路径用相对路径或 `%LOCALAPPDATA%` 变量。
- 未知字段必须原样保留（前向兼容）；新增字段只能追加，不删不改语义；`schemaVersion` 用 `0.x.y`。
- 每条结论必须带 `evidence[]`，元素形如 `{"kind":"file|command|web|runtime","ref":"路径:行号|命令|URL","note":"..."}`。
- 严禁写入游戏资源、完整剧本、CG/音频；文本样例最多 3 条、每条 ≤50 字，超出必须截断并标记。

## 1. 流水线与产物文件

工作区：`%LOCALAPPDATA%\GalA11yForge\workspaces\<gameKey>\`

| 文件 | 产出者 | 消费者 | 内容 |
|---|---|---|---|
| `detect.json` | IEngineDetector | Analyzer / Planner / GUI | 引擎、版本、位数、入口、保护检测 |
| `analysis.json` | IEngineAnalyzer（各引擎/通用/Ghidra） | Planner / Generator / 报告 | 文本源、对话框架、选项、菜单、置信度 |
| `plan.json` | IPlanner | Generator / Build / GUI | 路线、要生成的文件、安装策略、验证步骤、人工待办 |
| `generated/` | ICodeGenerator | Build | 补丁工程源码、profile、构建脚本、README |
| `install-manifest.json` | IDeployer | Verify / Uninstaller | 备份清单、新增/修改文件、哈希、回滚脚本 |
| `verify.json` | IVerifier | 报告 / GUI | 自检结果、抓取统计、清理状态 |
| `report.md` | 报告器 | 用户 | 人读版分析报告（结论、证据、人工待办、风险） |
| `human-todos.md` | Planner | 用户 | 机器搞不定的步骤（可勾选） |

`gameKey` = `<appid 或目录名>-<exe 名去扩展>`，只保留 `[A-Za-z0-9._-]`，长度 ≤64。

## 2. 公共信封

所有 JSON 顶层共用一个信封：

```json
{
  "schemaVersion": "0.1.0",
  "artifact": "detect | analysis | plan | verify",
  "toolVersion": "0.1.0.0",
  "generatedAt": "2026-11-14T16:00:00+08:00",
  "target": {
    "gameKey": "3030980-Build",
    "gameDir": "D:\\Steam\\steamapps\\common\\Build",
    "exe": "Build.exe",
    "exeSha256": "…",
    "exeSize": 123456,
    "bitness": "x64",
    "engine": "unity-mono",
    "engineVersion": "6000.0.49f1",
    "protection": ["none"]
  },
  "evidence": []
}
```

`protection` 取值：`none | upx | aspack | themida | vmprotect | enigma | dotnet-reactor | confuserex | unknown`；
检测到商业壳时**只记录、不脱壳**，并在 `plan.json` 里给人工待办。

## 3. detect.json

```json
{
  "schemaVersion": "0.1.0",
  "artifact": "detect",
  "candidates": [
    {"engine": "unity-mono", "confidence": 0.97, "signals": [
      {"type": "file", "value": "Build_Data/Managed/Assembly-CSharp.dll", "weight": 0.5},
      {"type": "version", "value": "6000.0.49f1", "weight": 0.3}
    ]}
  ],
  "entry": {"exe": "Build.exe", "how": "pe-scan|process-probe|launcher-child|manual"},
  "launch": {"args": [], "workingDir": ".", "needsLauncher": false},
  "notes": [],
  "evidence": []
}
```

要求：候选按 confidence 降序；低于 0.5 的仍保留但标 `"lowConfidence": true`（给人工）。

## 4. analysis.json（核心）

```json
{
  "schemaVersion": "0.1.0",
  "artifact": "analysis",
  "textSources": [
    {
      "id": "ts-001",
      "kind": "unity-tmp|unity-ugui|unity-imgui|renpy-say|krkrz-kag|native-api|memory-scan|ocr",
      "symbol": "TMPro.TMP_Text.set_text(string)",
      "tokenOrRva": "0x06001234",
      "callers": ["DialogueUGUI.ShowLine"],
      "score": {
        "total": 86,
        "positive": {"S1": 20, "S2": 15, "S3": 10, "S4": 12, "S5": 9, "S6": 8, "S7": 6, "S8": 6},
        "negative": {"N1": 0, "N2": 0, "N3": 0, "N4": 0},
        "ruleVersion": "mono-0.1"
      },
      "confidence": 0.86,
      "sampleSafe": ["……（≤50 字）"],
      "evidence": []
    }
  ],
  "dialogFrameworks": [
    {"name": "NodeCanvas.DialogueTrees", "confidence": 0.93, "types": ["DialogueUGUI", "SayNode", "MultipleChoiceNode"], "evidence": []}
  ],
  "choices": [{"source": "ts-001", "how": "MultipleChoiceNode|renpy-menu|kag-select|unknown"}],
  "menus": [{"name": "config|save|load|history|settings", "detected": true, "how": "…"}],
  "localization": {"language": "zh-Hans|ja|en|unknown", "encoding": "utf-8|utf-16le|cp932|unknown"},
  "hookPlan": [
    {"target": "ts-001", "method": "harmony-prefix|harmony-postfix|detour|tjs-wrap|say-filter|api-monitor", "why": "…", "risk": "low|medium|high"}
  ],
  "humanTodos": [
    {"id": "h-01", "severity": "P0|P1|P2", "what": "…", "why": "…", "howToVerify": "…"}
  ],
  "coverageNote": "本分析覆盖剧情/选项；菜单未覆盖（原因见 h-02）",
  "evidence": []
}
```

分数规则：正分 S1–S8 / 负分 N1–N4 由各分析器定义并写 `ruleVersion`；总分 0–100，置信度为 0–1 的独立判断。
分析器必须输出 `coverageNote`，禁止笼统写"已支持"。

## 5. plan.json

```json
{
  "schemaVersion": "0.1.0",
  "artifact": "plan",
  "route": "adapter|generic|ghidra|ocr-fallback|manual",
  "adapter": "unity-mono|renpy|il2cpp|krkrz|none",
  "steps": [
    {"id": "p-01", "do": "generate:plugin", "inputs": ["analysis.json"], "outputs": ["generated/Plugin.cs"], "onFail": "stop|skip"}
  ],
  "install": {
    "strategy": "bepinex5-zip|bepinex6-zip|overlay-rpy|plugin-tpm|native-inject",
    "requiresBackup": true,
    "filesToModify": [],
    "rollback": "manifest-uninstall"
  },
  "verification": [
    {"id": "v-01", "check": "plugin-loaded", "expect": "log 含 Plugin loaded"},
    {"id": "v-02", "check": "text-captured", "expect": "启动后 60 秒内 ≥1 条台词"}
  ],
  "decisions": [{"choice": "bepinex5", "why": "游戏为 Unity Mono 2022.3", "alternatives": ["bepinex6"]}],
  "humanTodos": [],
  "evidence": []
}
```

## 6. verify.json

```json
{
  "schemaVersion": "0.1.0",
  "artifact": "verify",
  "checks": [
    {"id": "v-01", "name": "plugin-loaded", "status": "pass|fail|skip", "detail": "…", "evidence": []},
    {"id": "v-02", "name": "text-captured", "status": "pass", "metrics": {"lines": 37, "duplicates": 2, "firstLatencyMs": 420}}
  ],
  "cleanup": {"uninstalled": true, "restored": ["winhttp.dll 已删除", "doorstop_config.ini 已还原"], "hashMatch": true},
  "overall": "pass|partial|fail",
  "evidence": []
}
```

`skip` 必须写原因；`overall=pass` 要求 P0 检查全过。

## 7. report.md 固定结构

1. 一句话结论（这款游戏能不能自动搞定、搞定到什么程度）
2. 识别结果（引擎/版本/入口/保护）
3. 文本管线与候选点（表格：符号、置信度、命中验证状态）
4. 已生成/已安装/已回滚的内容
5. **人工待办**（P0/P1/P2 表，含验证方法）
6. 覆盖说明与已知限制（含未实测项）
7. 证据清单（文件/命令/URL）

## 8. 兼容与校验

- W2-E 验证器必须校验所有 JSON 的 `schemaVersion` 与必填字段；缺字段给 `fail` 而不是崩溃。
- W2-A Core 提供 `ArtifactJson` 读写与 schema 校验工具类（未知字段保留）。
- 任何模块新增字段先改本文档，再改代码；文档版本与 `schemaVersion` 同步递增。
