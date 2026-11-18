# Forge.Analyzers.Ghidra

`forge analyze --advanced` 的 Ghidra 原生代码后端：调用本机（BYO）Ghidra headless，
把导入表文本 API、CJK 字符串引用和调用关系变成 `TextCandidate` 证据。

- 目标框架：`net8.0`；仅引用 `Forge.Core`。
- 不随包分发 Ghidra，也不下载 Ghidra；只调用用户自己安装的副本。
- 只写工作区 `<workspace>\work\ghidra\...`；游戏目录只读。

## 组件

| 文件 | 职责 |
|---|---|
| `GhidraLocator.cs` | 定位 Ghidra：`--option ghidra.path` > `GHIDRA_HOME` > `GHIDRA_INSTALL_DIR` > 常见目录 |
| `GhidraCommandBuilder.cs` | 构造 `analyzeHeadless` argv（纯函数，可单测）；`.bat` 经 `cmd.exe /d /s /c` 启动 |
| `GhidraRunner.cs` | 启动子进程、收集 stdout/stderr、超时/取消杀进程树、退出码映射、路径越界拒绝 |
| `GhidraResults.cs` | 脚本 JSON 的 DTO 与解析（`gala11y.ghidra.candidates/1`，snake_case） |
| `GhidraAnalyzerBackend.cs` | `IAnalyzerBackend` 实现 + 富结果 `AnalyzeDetailedAsync`（状态/耗时/证据/风险/未验证） |
| `GhidraArtifact.cs` | `01-analyze/analysis-ghidra.json` 的落盘结构（脚本原始 JSON 原样嵌入） |
| `GhidraBackendOptions.cs` | `ghidra.*` 选项解析与范围校验（坏值进 warnings，不抛） |
| `GhidraScriptResources.cs` | 释放嵌入的 `CandidateHookDump.java` 并校验 SHA-256 |

## 许可证与脚本来源

- **Ghidra** 本体：Apache-2.0（https://github.com/NationalSecurityAgency/ghidra ）。
  本项目不重分发 Ghidra，也不修改其安装；运行时通过 `analyzeHeadless.bat` 调用。
- **`Assets/CandidateHookDump.java`**：来自本项目 `research/10-ghidra-re`（Ghidra 高级分析研究），
  由本项目自写，**不含第三方代码**；随本仓库许可证发布。
  - 文件大小：41,955 字节
  - SHA-256：`8aad1bbcf14fc81d09c1278193e589237dcc45c2c366e1cc9b03befce84bfc28`
  - 脚本 schema：`gala11y.ghidra.candidates/1`
  - 行为：分析当前程序，找文本 API 导入/调用、CJK 字符串引用与调用者，按固定规则给 0–100 分，
    写 `<outDir>/candidates.json`，并在 stdout 打印 `WROTE <path> bytes=… candidates=… cjk_strings=… packed=…`。
- **其余 C# 代码**：本项目原创，随仓库许可证发布。

脚本以嵌入资源方式打包（`Forge.Analyzers.Ghidra.csproj` 的 `EmbeddedResource`），
运行时释放到本次分析的 `work/ghidra/<id>/scripts/`，内容哈希不一致时拒绝使用。

## 使用

通常不直接引用本项目，而是通过 CLI：

```powershell
forge analyze --game D:\Games\SomeGame --advanced
forge analyze --game D:\Games\SomeGame --advanced --option ghidra.path=D:\ghidra_12.1.4_PUBLIC
```

选项：`ghidra.path`、`ghidra.enabled`、`ghidra.timeoutSeconds`（默认 600）、
`ghidra.analysisTimeoutSeconds`（默认 900）、`ghidra.maxCpu`（默认 4）、
`ghidra.maxMem`、`ghidra.keepProject`。详见 `docs/21-Ghidra高级分析接入.md`。

## 行为约定

- 没装 Ghidra：返回 `NotAvailable`，`summary` 固定为「未启用高级分析（未找到 Ghidra）。」，
  `unverified` 写明“不能据本次结果判断没有文本钩子”；`AnalyzeAsync` 返回成功结果而不是异常。
- 超时/取消：`Kill(entireProcessTree:true)`，删除临时工程目录，保留 `stdout.log`/`stderr.log`。
- 脚本产物路径必须在本次 work 目录内，否则按 `E_PATH_UNSAFE` 拒绝。
- 脚本 JSON 的 schema 前缀不符、产物缺失、自报候选数与产物不一致，都会映射到明确错误或 unverified。

## 测试

```powershell
D:\dotnet\dotnet.exe test tests\Forge.Analyzers.Ghidra.Tests\Forge.Analyzers.Ghidra.Tests.csproj -c Release
```

默认 55 个离线测试；真实 headless 冒烟需 `GHIDRA_REAL=1`（可用 `GHIDRA_REAL_SAMPLE` 指定样本）。

## 已知限制

静态分析不运行游戏；加壳/自保护、运行时解析 API、非 GDI 自绘文本、.NET 托管程序都会导致
0 候选或低置信度。详见 docs/21 §7。
