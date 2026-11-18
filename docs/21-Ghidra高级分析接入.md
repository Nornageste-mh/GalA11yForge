# 21 Ghidra 高级分析接入（`analyze --advanced`）

> 任务卡：task-39 / W2-V。状态：已接线并通过真实 headless 冒烟（2026-11-14）。
> 本文所有数字均来自本机实测；`[实测]` 表示命令输出，`[文件:行]` 表示源码位置，`[推断]` 表示未直接验证。

## 1. 结论摘要

1. `forge analyze --advanced` 现在会真正调用本机 Ghidra headless（`support\analyzeHeadless.bat`），
   产出 `01-analyze/analysis-ghidra.json`，并把原生候选合并进 `analysis.json` 与 JSON 信封的 `ghidra` 段。
2. 没装 Ghidra 时**不报假成功**：`ok:true`、退出 0，但 `data.ghidra.available=false`、
   `status:"not_available"`，人类输出明确写「未启用高级分析（未找到 Ghidra）」，`unverified` 必须出现。
3. 真实冒烟（KiriKiri Z《千恋＊万花》exe 副本）：墙钟 153.8s、Ghidra 148.4s、318 个函数、
   **0 个文本 API 候选**、`packed_suspected=true`；这不是“没有文本”，而是静态分析对加壳/自保护引擎
   基本无效——产品按要求如实报告并给人工路线。[实测]
4. 只有在 `analyze` 且 `--advanced` 时才调用 Ghidra；`plan`/`generate`/`build` 等命令不会重复跑，
   避免每条命令多花 2–4 分钟。
5. 新项目 `src/Forge.Analyzers.Ghidra` 与测试项目 `tests/Forge.Analyzers.Ghidra.Tests` 已由 Lead
   于 2026-11-14 23:28 加入 `src/GalA11yForge.sln`；全量 sln 测试已包含这 55 个新测试，
   实测 **590 通过 0 失败**（2026-11-14 23:41，见 docs/12 §6）。[实测]

## 2. 定位：补“原生/未知引擎兜底”，不替代适配器

| 引擎 | 常规路径 | `--advanced` 的作用 |
|---|---|---|
| Unity Mono | Cecil 适配器 | 追加原生候选（一般用不到） |
| IL2CPP | dump.cs/interop 适配器 | 追加原生候选 |
| Ren'Py | 脚本适配器 | 追加原生候选（一般用不到） |
| 已知但无适配器（如 KiriKiri Z） | 以前 `E_ENGINE_UNSUPPORTED` | 单独产出一份诚实的原生分析结果 |
| `unknown`（仅有 exe） | `E_ENGINE_UNSUPPORTED` | 允许单独产出（需游戏目录根层有 `.exe`） |

Ghidra 后端只做**静态**分析：找导入的文本 API（`TextOutW`、`ExtTextOutW`、`DrawTextW`、
`GetGlyphOutlineW` 等）、CJK 字符串引用和调用关系，给出 0–100 的置信度与挂钩建议。
它不运行游戏，也不生成可直接用的补丁。

## 3. 前置条件与 BYO

- Ghidra **不随包**（BYO）：需要用户自己安装 Ghidra 12.x（实测 12.1.4）与 JDK 21。
- 定位优先级（`src/Forge.Analyzers.Ghidra/GhidraLocator.cs`）：
  1. `--option ghidra.path=<目录>`（权威：显式给了但无效就直接报不可用，不回退）；
  2. 环境变量 `GHIDRA_HOME`，再 `GHIDRA_INSTALL_DIR`；
  3. 常见目录探测（含 `D:\ghidra_12.1.4_PUBLIC`、`ghidra*` 子目录按版本倒序）。
- 可用判据：目录下存在 `support\analyzeHeadless.bat`。Ghidra 版本读
  `<home>\Ghidra\application.properties` 的 `application.version=`。

`doctor` 增加了一个**可选**检查项 `ghidra`：找到显示「通过」，没找到显示「未检出」，
不影响 `doctor` 成功退出。[实测]

```powershell
forge doctor --workspace D:\work\forge-ws --json
# ghidra: { "passed": true, "optional": true,
#           "detail": "可选：已找到 Ghidra 12.1.4（D:\ghidra_12.1.4_PUBLIC，来源 env:GHIDRA_INSTALL_DIR）。" }
```

## 4. 用法

```powershell
# 最简：自动探测 Ghidra
forge analyze --game "D:\Games\SomeGame" --advanced

# 指定 Ghidra 位置（不落盘，只本次生效）
forge analyze --game "D:\Games\SomeGame" --advanced --option ghidra.path=D:\ghidra_12.1.4_PUBLIC

# 调大超时 / 内存 / 保留工程目录
forge analyze --game "D:\Games\SomeGame" --advanced `
  --option ghidra.timeoutSeconds=900 `
  --option ghidra.maxMem=4G `
  --option ghidra.keepProject=true

# 只看探测结果，不跑 Ghidra
forge analyze --game "D:\Games\SomeGame" --advanced --dry-run
```

选项（全部走 `profile.options`，`--option` 可覆盖，见 docs/06 §6.1）：

| 键 | 默认 | 说明 |
|---|---:|---|
| `ghidra.path` | 自动探测 | Ghidra 安装目录（含 `support\analyzeHeadless.bat`） |
| `ghidra.enabled` | `true` | `false` 时跳过，结果写 unverified |
| `ghidra.timeoutSeconds` | `600` | 子进程墙钟超时；超时杀整棵进程树并清理临时工程 |
| `ghidra.analysisTimeoutSeconds` | `900` | 传给 `-analysisTimeoutPerFile` |
| `ghidra.maxCpu` | `4` | 传给 `-max-cpu` |
| `ghidra.maxMem` | 空 | 设置 `GHIDRA_HEADLESS_MAXMEM`（如 `4G`） |
| `ghidra.keepProject` | `false` | `true` 时保留工程目录用于排查 |

## 5. 产物与工作目录

一次成功的 `analyze --advanced` 会写：

```
<workspace>\games\<slug>\01-analyze\
  analysis.json           # 适配器结果 + 合并后的 Ghidra 候选
  analysis-ghidra.json    # Ghidra 专属产物（含脚本原始 JSON）
<workspace>\work\ghidra\ghidra-yyyyMMdd-HHmmss-xxxxxxxx\
  project\                # Ghidra 工程（默认 -deleteProject，成功/失败都会清理）
  scripts\CandidateHookDump.java
  out\candidates.json     # 脚本产物
  stdout.log / stderr.log # 移出工程目录，失败也能留证据
```

JSON 信封（`--json`）新增 `data.ghidra` 段：

```json
{
  "enabled": true, "available": true, "status": "completed",
  "ghidraVersion": "12.1.4", "elapsedSeconds": 148.4,
  "executable": "…\\SenrenBanka.exe",
  "candidates": 0,
  "unverified": ["Ghidra 候选是静态推断，未在游戏中运行验证（未确认文本确实流经这些函数）。", "…"],
  "manualTodo": ["…"], "covered": ["…"], "notCovered": ["…"],
  "risks": [{ "id": "ghidra-packed", "severity": "high", "…": "…" }],
  "artifact": "…\\01-analyze\\analysis-ghidra.json",
  "location": { "available": true, "home": "D:\\ghidra_12.1.4_PUBLIC", "source": "env:GHIDRA_INSTALL_DIR" }
}
```

`analysis-ghidra.json` 的字段与 `status` 取值见 docs/06 §3.6；脚本原始 JSON 原样嵌在 `script` 字段。

## 6. 真实冒烟证据（2026-11-14）

样本：从 Steam 只读目录复制到工作区的《千恋＊万花》主程序（**未写 Steam 目录**）：

- 路径：`research\32-ghidra-integration\sample\SenrenBanka\SenrenBanka.exe`
- 大小：4,583,168 字节；SHA-256：`601b10faac9fbf2a4a7d54bc53a4c6790eea99254eff1acb103ec964d9fa1945`
- 引擎：目录里只有 exe，检测为 `unknown`（KiriKiri Z 规则需要 `*.xp3`），因此走 Ghidra 单独产出。

命令（Release 内置 CLI）：

```powershell
src\Forge.Cli\bin\Release\net8.0\forge.exe analyze `
  --game D:\DSHWorkSpace\research\32-ghidra-integration\sample\SenrenBanka `
  --advanced --workspace D:\DSHWorkSpace\research\32-ghidra-integration\ws-smoke-final --json
```

结果 [实测]（完整 stdout：`research\32-ghidra-integration\smoke-final.out.txt`）：

| 指标 | 值 |
|---|---:|
| CLI 退出码 | 0 |
| 墙钟总耗时 | 153.8 s |
| Ghidra 子进程耗时 | 148.4 s |
| 分析函数数 | 318 |
| 文本 API 候选 | 0 |
| `packed_suspected` | true |
| 导入表文本 API 命中 | 3（`SetWindowTextW`、`GetTextExtentPoint32W`、`GetGlyphOutlineW`） |
| CJK 字符串引用 | 339（严格 UTF-16 扫描 0） |
| `dynamic_api_resolution_suspected` | true |
| risks | 2（`ghidra-packed` high、`ghidra-dynamic-api` medium） |
| unverified | 4（静态未运行 / packed / 运行时解析 / 0 候选不等于没有文本） |

人类输出：

```
[完成] 分析完成：0 个文本候选（高 0 / 中 0 / 低 0）。
  引擎：unknown（置信度 …）
  [高级分析] Ghidra 12.1.4：0 个候选，耗时 148s；产物 analysis-ghidra.json。
  覆盖：3 条 / 未覆盖 5 条 / 人工待办 3 条 / 未验证 5 条
```

对这张 exe 的结论：`.text` 熵 8.00、入口在 `.bind`、导入表只有 3 个间接文本 API、字符串引用集中在
非代码段——与 research/11 的“KiriKiri Z 自带保护，静态基本无效”一致。**0 候选不代表没有文本**，
`analysis-ghidra.json` 的 `unverified` 已明确写出这一点。

对照：同一 exe 在 Steam 原目录直接跑（默认超时当时还是 240s）→ 退出 5、墙钟 252s、
`status:"timeout"`，Ghidra 日志停在 `ANALYZING all memory and code`。这次实测促成了默认超时改为 600s，
且失败时清理工程目录、日志移出工程目录。[实测] 证据：`research\32-ghidra-integration\smoke-senrenbanka.out.txt`。

## 7. 限制（必须如实告诉用户）

1. **静态分析**：不运行游戏，候选是“文本可能流经这里”的推断，需要 x64dbg/运行时确认参数。
2. **加壳/自保护**：Ghidra 的 `packed_suspected` 是“可执行段 ≥64KB 且熵 ≥7.2”的粗启发式；
   KiriKiri Z 这类会误判/命中，静态候选通常为 0。只有 UPX 可安全自动脱壳，其余转人工。
3. **运行时解析的 API**（`GetProcAddress` 等）：导入表里看不到，会漏。
4. **非 GDI/DirectWrite 自绘文本**（D3D/OpenGL 自己画字）：没有可挂钩的文本 API，0 候选。
5. **.NET/托管程序**：Ghidra 看不到 IL，只能看到 native stub，效果差（research/10 里 TolkProbe 72.9s / 0 候选）。
6. **耗时**：冷跑 2–4 分钟/文件，首次打开大文件更慢；Ghidra 工程不能跨机器复用。
7. **Windows 专用实现**：当前命令构造与 `.bat` 启动针对 Windows；macOS/Linux 未验证。

## 8. 失败语义（不假成功）

| 情况 | `ok` | 退出码 | `data.ghidra.status` / 说明 |
|---|---|---:|---|
| 未装 Ghidra | true | 0 | `not_available`；人类输出「未启用高级分析（未找到 Ghidra）」；写入 unverified |
| `ghidra.enabled=false` | true | 0 | `disabled`；写入 unverified |
| 无适配器 + 超时/崩溃/产物非法 | false | 5 | `timeout`/`non_zero_exit`/`invalid_output`，`code=E_PARTIAL`，message/suggestion 可操作 |
| 有适配器 + 超时/崩溃 | true | 0 | 适配器结果保留，stderr 警告「原生候选缺失」 |
| 完成但 0 候选 / 疑似加壳 | true | 0 | `completed`；unverified 写明“0 候选不等于没有文本” |
| 游戏目录没有 `.exe` | false | 5 | `no_target`，`E_ARTIFACT_MISSING` |

超时会 `Kill(entireProcessTree:true)`，并删除 `<work>\project`；`stdout.log`/`stderr.log` 保留在
`<work>` 根下，便于把日志附给用户。

## 9. 测试

```powershell
# 单元测试（不需要 Ghidra，55 个）
D:\dotnet\dotnet.exe test tests\Forge.Analyzers.Ghidra.Tests\Forge.Analyzers.Ghidra.Tests.csproj -c Release

# 真实 headless 冒烟（会跑 2–3 分钟；默认跳过）
$env:GHIDRA_REAL=1
D:\dotnet\dotnet.exe test tests\Forge.Analyzers.Ghidra.Tests\Forge.Analyzers.Ghidra.Tests.csproj -c Release `
  --filter FullyQualifiedName~GhidraRealTests
```

覆盖：定位器优先级、命令构造/引用、脚本 JSON 解析（snake_case 多词字段）、超时/取消/进程树、
路径越界拒绝、错误映射、产物合并、packed/dynamic 风险与 unverified 文案。
真实测试用 `GHIDRA_REAL=1` 开启，样本可用 `GHIDRA_REAL_SAMPLE` 指定；未设置时用例直接返回
（xunit v2 没有动态 Skip，CI 上按通过计——这一点必须如实说明，见 docs/12 §6）。

已实际开启 `GHIDRA_REAL=1` 跑过一次（2026-11-14 23:29–23:34）：1/1 通过、退出 0，
该次命令墙钟约 260s（冷态，含测试宿主与 JVM 启动）。注意 vstest 对异步用例打印的
「持续时间」是 `< 1 ms`，**不可作为耗时依据**；耗时以 §6 的 CLI 实测为准。

## 10. 许可证与来源

- Ghidra 本体的许可证是 **Apache-2.0**；本项目**不重分发** Ghidra，只调用用户自己安装的副本。
- `src/Forge.Analyzers.Ghidra/Assets/CandidateHookDump.java` 来自本项目 research/10-ghidra-re
  的研究脚本（自写，无第三方代码），SHA-256
  `8aad1bbcf14fc81d09c1278193e589237dcc45c2c366e1cc9b03befce84bfc28`；
  通过嵌入资源释放到工作区脚本目录，运行前不做修改。
- 其余 C# 代码为本项目原创，随仓库许可证发布。

## 11. 未验证 / 下一步

- 未在 Unity/IL2CPP 真机游戏上跑 `--advanced`（适配器路径已接，但未做耗时与候选对比）。
- 未验证 macOS/Linux；未验证 Ghidra 13.x 的兼容性。
- 未做“Ghidra 候选 → 自动生成挂钩代码”的闭环；本轮只到证据与人工待办。
- 建议下一步：挑一个非加壳的 KiriKiri/自研引擎小游戏验证“有候选”的路径，
  并把候选数与 x64dbg 抽样核对结果写进 research/32（sln 已含 Ghidra 项目，无需再改）。

