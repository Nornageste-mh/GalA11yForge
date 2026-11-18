# signature-match —— 游戏签名探针与匹配原型（R19 / task-22）

把「一个 Galgame 安装目录」变成「命中哪些框架签名、分数多少、为什么」的只读原型。
签名库在 `research/21-signature-db/evidence/signatures.json`，验证结果在
`research/21-signature-db/evidence/match-results.csv`。

## 文件

| 文件 | 作用 |
|---|---|
| `sigprobe.py` | 只读探针：目录布局、DLL 名单、exe VersionInfo、Unity 版本、IL2CPP metadata/interop token 计数、Ren'Py 版本、KiriKiri/RPG Maker 特征 → `galforge-probe-v1` |
| `match.py` | 匹配器：probe + analysis（unity-mono-analyzer v1）+ 可选 facts → `galforge-match-v1`（排序、分数、命中/缺失证据、解释）；支持批量 manifest → CSV |
| `README.md` | 本文件 |

## 快速开始

```powershell
# 1) 单款：目录 → 探针 JSON（只读）
D:\python\python.exe src\experiments\signature-match\sigprobe.py `
  --game "D:\Steam\steamapps\common\SiliconHeart" `
  --signatures research\21-signature-db\evidence\signatures.json `
  --out research\21-signature-db\evidence\probes\siliconheart.json --name 硅心

# 2) 匹配（probe + 已有 Cecil 分析；二者都可单独使用）
#    IL2CPP/未装补丁：只给 --probe 也能判框架；Unity Mono 想拿到类型/方法证据请加 --analysis
D:\python\python.exe src\experiments\signature-match\match.py `
  --probe research\21-signature-db\evidence\probes\siliconheart.json `
  --out research\21-signature-db\evidence\matches\siliconheart.json
#    有分析时：
#    --analysis research\21-signature-db\evidence\analysis\5omeday.json

# 3) 批量验证（会重跑探针，写 probes/、matches/、CSV）
D:\python\python.exe src\experiments\signature-match\match.py `
  --batch research\21-signature-db\evidence\manifest.json `
  --out-dir research\21-signature-db\evidence `
  --csv research\21-signature-db\evidence\match-results.csv
```

`--analysis` 接受 `src/experiments/unity-mono-analyzer` 的 `analysis.json`
（schemaVersion 1.0）。`--facts` 可补充分析器不输出的字段/方法/版本证据，格式：

```json
{"types": ["A.B"], "methods": [{"type": "A.B", "name": "Say", "signature": "(string)"}],
 "fields": [{"type": "A.B", "name": "lines"}], "versions": [{"path": "x.exe", "productname": "..."}]}
```

## 探针输出要点（galforge-probe-v1）

- `layout.paths`：相对路径（最多 30000 条，只列名不读内容）；`managed_dlls` / `interop_dlls`。
- `exe_version[]`：PE StringFileInfo（ProductName/FileVersion/OriginalFilename…）。
- `unity.flavor`：`mono` / `il2cpp` / `null`；`versionGuess`。
- `binary_tokens{}`：按来源分组（metadata / gameassembly / managed / interop）的 ASCII token 次数；
  `type_names_sample[]`：从 `global-metadata.dat` 字符串堆抽的「点分类型名」样本（启发式，非完整解析）。
- `engine_hint`、`renpy_version`。
- 只读、不导出资源、不落盘任何台词文本；IL2CPP 只读 metadata 与托管/interop DLL 的字节串计数。

## 签名库 schema（galforge-signatures-v1）

```json
{
  "schema": "galforge-signatures-v1",
  "probe_tokens": ["Naninovel", "..."],
  "match_policy": {"tiers": {"A": 70, "B": 50, "C": 30}},
  "signatures": [{
    "id": "naninovel", "name": "…", "family": "naninovel", "version": "1.0",
    "engines": ["unity-mono", "unity-il2cpp"],
    "tests": [
      {"id": "type.namespace", "evidence": "type", "mode": "regex",
       "pattern": "^Naninovel\\.", "weight": 35, "identity": true, "note": "…"}
    ],
    "sources": [{"path": "noexistence_a11y/mod/src/NoExistenceA11y/Plugin.cs", "line": 698}],
    "notes": "…"
  }]
}
```

证据类型：`file` `dir` `dll` `type` `method` `field` `string` `version`
`text_sink` `framework_trace` `engine`；匹配模式：`exact` `glob` `substring` `regex` `prefix`。
`method`/`field` 可用 `scope_type` 限定宿主类型；`string` 用 `min_count`；`text_sink` 用 `min_calls`。
证据源不可用（如 v1 分析无字段名）时该 test 跳过，不计入分母、不算缺失。

## 打分

- 每条命中累加权重，**上限 100**；负权重可用于反证。
- `identity: true` 的证据至少命中 1 条，否则总分封顶 30（防通用证据误报）。
- 分档：A ≥70 / B ≥50 / C ≥30 / D <30；置信度 A 0.85 起（每多 1 条 identity +0.04）、B 0.6、C 0.4。
- 排名按 (score, identity_hits, id)；没有一条证据命中的签名不进排名。

## 验证（2026-11-14，本机 16 款）

15 款已知游戏 top1 全中（平均 85 分，全部 A 档），Electron 负对照判 `unknown`。
P0：五天一夜 80 A（自定义 UIText）、硅心 70 A（未装补丁 IL2CPP）。
详见 `research/21-signature-db/REPORT.md`。

## 已知限制

- 静态证据只到「类型/方法候选」，台词/选项/菜单仍需运行时确认。
- IL2CPP 未装补丁时只有 metadata token + 目录证据，没有方法级钩子；完整解析要接 Il2CppDumper。
- `token` 是字节串计数，可能被字符串常量夸大；阈值需随样本校准。
- 非 Unity 引擎目前只有 Ren'Py 目录与 KiriKiri Z 目录/版本签名。
