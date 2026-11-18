# Forge.Generators 测试与交付报告（W2-C 补丁生成器）

- 日期：2026-11-14
- 环境：Windows 10；`D:\dotnet\dotnet.exe`（SDK 8.0.404）；工作目录 `D:\DSHWorkSpace`
- 结论：**49 个测试全部通过**（含 golden 逐字节对比、幂等、路径边界、中文路径、内容安全）；生成样例已落盘。生成物尚未用真实 BepInEx/Unity 引用编译过（本机没有对应游戏与 BepInEx），见「未验证与风险」。

## 1. 交付物

| 路径 | 内容 |
|---|---|
| `src/Forge.Generators/` | 新项目 `Forge.Generators`（net8.0 类库，引用 `Forge.Core`，未改 Core/Cli/sln） |
| `src/Forge.Generators/UnityMonoGenerator.cs` | `ICodeGenerator` 实现：Unity Mono（BepInEx 5 / netstandard2.1）生成阶段 |
| `src/Forge.Generators/Templates/` | Plugin / csproj / Hook / build.ps1 / README / 安装 / 卸载 / NOTICE 模板 |
| `src/Forge.Generators/Internal/` | 占位符渲染、文本校验、Hook 目标解析、确定性 ID、清单、路径与错误码封装 |
| `src/Forge.Generators/FrameworkVoiceBridgeSource.cs` | 从 `Framework/tools/l1-files.txt` 读取 L1 + `contract/A11yHost.cs`，校验清单并算内容指纹 |
| `tests/Forge.Generators.Tests/` | xunit 测试工程（49 个测试）+ 测试支撑 |
| `tests/Forge.Generators.Tests/golden/` | 13 个文件的逐字节 golden 快照（用最小 stub 语音桥，换机器可复现） |
| `tests/Forge.Generators.Tests/evidence/forge-sample/` | 21 个文件的真实生成样例（真实 Framework 语音桥，约 310 KB） |

生成物清单（每次生成固定 11 类）：`Plugin.cs`、`<命名空间末段>.A11y.csproj`、`Hooks/Hook_*.cs`（每个 Hook 补丁点一个）、`Platform/*.cs`（9 个 L1 + A11yHost）、`profile.json`、`build.ps1`、`README.md`、`安装说明.md`、`卸载说明.md`、`NOTICE.md`、`forge.manifest.json`。

## 2. 测试命令与结果 [实测]

```powershell
# 1) 编译生成器
D:\dotnet\dotnet.exe build src\Forge.Generators\Forge.Generators.csproj -c Release --nologo -v q
# 结果：已成功生成。0 个警告，0 个错误（约 6 秒）

# 2) 正常跑测试（不写快照）
D:\dotnet\dotnet.exe test tests\Forge.Generators.Tests\Forge.Generators.Tests.csproj -c Release --nologo -v q
# 结果：已通过! - 失败: 0，通过: 49，已跳过: 0，总计: 49，持续时间: 2 s

# 3) 重建 golden 与 evidence（仅生成器有意变更时执行）
$env:FORGE_UPDATE_GOLDEN = "1"; $env:FORGE_WRITE_EVIDENCE = "1"
D:\dotnet\dotnet.exe test tests\Forge.Generators.Tests\Forge.Generators.Tests.csproj -c Release --nologo -v q
# 结果：已通过! - 失败: 0，通过: 49，总计: 49
```

第 3 步之后必须再跑一次第 2 步（已跑）：证明 `golden/` 与 `evidence/` 和当前生成器逐字节一致，而不是「写完就算」。

## 3. 覆盖矩阵（任务要求 → 测试）

| 要求 | 测试（文件 / 用例） |
|---|---|
| 空 plan、缺必填 → 明确错误码 | `ValidationTests`：Null plan / Null context / 空 PatchPoints / 缺 slug / 非法 slug / 空输出目录 → `E_ARG_INVALID` |
| 引擎或路线不支持 | `ValidationTests`：Unsupported route、非 `unity_mono` 引擎 → `E_ENGINE_UNSUPPORTED` |
| 禁止编造游戏类名 | `ValidationTests`：目标含台词、空目标、`.ctor`、重复 id → `E_ARG_INVALID`；`HookTargetParser` 只接受标识符与白名单参数类型 |
| 语音桥缺失/被改坏 | `ValidationTests`：缺 A11yHost、无 `namespace A11yFramework`、残留 `{{NS}}`、文件不在 `Platform/`、Framework 目录找不到 |
| 路径越界 / 游戏目录 | `PathOutputTests`：`..\escape`、工作区外绝对路径 → `E_PATH_UNSAFE`；游戏目录内 → `E_GAME_WRITE_DENIED`（游戏目录哨兵逐字节不变）；工作区根、非空无清单、目标是文件 → `E_ARG_INVALID` |
| DryRun 不写盘 | `PathOutputTests.Dry_run_validates_but_writes_nothing`（`Metadata["dryRun"]="true"`） |
| golden 对比 | `GoldenAndEvidenceTests.Golden_matches_repository_snapshot`（13 个文件集合 + 逐字节） |
| 同输入输出确定 | `DeterminismTests.Same_input_in_two_workspaces_is_byte_identical`；同 slug 跨工作区 GUID/命名空间相同；不同 slug 变化 |
| 重复生成幂等 | `DeterminismTests.Regeneration_is_idempotent`；减少 Hook 后只删旧清单里的陈旧文件 |
| 清单可信 | `Manifest_lists_every_file_except_itself_with_matching_sha256`（逐文件 SHA-256 对照，不含自身）；外部/损坏清单 → `E_ARTIFACT_INVALID`；换 slug 拒绝覆盖 |
| 占位符未替换检测 | `ContentSafetyTests.No_placeholder_or_route_tokens_remain`；`ArtifactTextValidator` 同时拦截 `namespace A11yFramework` 与 `@@ROUTE` |
| 中文路径 | `ContentSafetyTests.Chinese_workspace_and_game_directory_are_supported`（中文工作区 + 中文游戏名「某游戏 中文」，游戏目录只读） |
| 生成物不含游戏内容 | `ContentSafetyTests.Plan_free_text_never_leaks_into_generated_project`（台词/待办/风险文本全不出现）；`Generated_files_use_allowlisted_extensions_and_contain_no_media`（白名单 `.cs/.csproj/.json/.ps1/.md`）；`Hook_source_does_not_copy_plan_description` |
| 编码与内嵌桥 | `Encodings_are_stable`（`.ps1` 带 BOM、其余无 BOM、全部 CRLF、profile 中文原文不转义）；`Bridge_namespace_is_replaced_everywhere`（无 `A11yFramework` 残留） |
| 不引用游戏程序集 | `Csproj_does_not_reference_game_assembly`（无 `Assembly-CSharp`，含 BepInEx/0Harmony/Unity.TextMeshPro/Deterministic） |

## 4. 生成样例（evidence）

位置：`tests/Forge.Generators.Tests/evidence/forge-sample/`（21 个文件，317,639 字节）

- 输入：`SamplePlans.Create()`（虚构命名空间 `GameNS`，两条 Hook + 一条非 Hook 补丁点；游戏路径固定为 `D:\Games\GalgameSample` 以便换机器复现）。
- 语音桥：真实 `Framework/tools/l1-files.txt` 列出的 9 个 L1 文件 + `contract/A11yHost.cs`，命名空间替换为 `GalA11y.ForgeGolden`，内容指纹 `4429b469…c090`。
- 可核对的点：`Plugin.cs` 填满 A11yHost 必填槽位、`Speech.Init`、`=` 重读；两个 Hook 的 `Target` 原样来自 plan（`GameNS.ADV.Dialogue:SetText`、`GameNS.ADV.Advance:OnClick(string)`）；`profile.json` 标 `status: skeleton_unwired`；非 Hook 点标 `not_implemented_by_generator`；`forge.manifest.json` 记录每个文件 SHA-256。
- 样例不含游戏台词、图片、音频、字体或游戏原始文件；`NOTICE.md` 记录 Framework MIT 许可与 BepInEx/HarmonyX/Tolk/NVDA/ZDSR 的许可边界。

## 5. 未验证与风险（如实说明）

1. **未用真实 BepInEx/Unity 引用编译生成工程**：本机没有目标游戏与 BepInEx，无法验证生成的 `Platform/*.cs` + `Plugin.cs` 在真实引用下能编译。当前只做了模板/占位符/结构/内容校验；「可编译」尚属推断。建议下一步在有游戏与 BepInEx 的机器上跑一次 `build.ps1`。
2. **生成工程依赖本机路径**：csproj 与 build.ps1 默认写死生成时的游戏路径，换机器要 `-p:GameDir=...` 覆盖；这是刻意的不魔法设计，但需要文档说清（README/安装说明已写）。
3. **只生成钩子骨架**：不接线朗读，`profile.json` 与 README 都标 `skeleton_unwired`；不夸大。
4. **plan 自由文本不复制**：人工待办/风险的原文不进入生成物（防台词泄漏），用户需回看 `plan.json`。
5. **Core 的 ForgeJson 会把中文转义**：为让 `profile.json` 可读，生成器用本地 `GeneratedJson`（`UnsafeRelaxedJsonEscaping`），未改 Core；清单仍可被 Core 的 `ForgeJson.Deserialize` 读取。
6. **未改硬边界**：`src/Forge.Core`、`src/Forge.Cli`、`src/GalA11yForge.sln`、`src/experiments/**`、`Framework/` 均未改动；测试与生成只写 `src/Forge.Generators/**`、`tests/Forge.Generators.Tests/**` 与 `%TEMP%`。

## 6. 复现

```powershell
D:\dotnet\dotnet.exe test tests\Forge.Generators.Tests\Forge.Generators.Tests.csproj -c Release --nologo -v q
# 想重建快照：先设 $env:FORGE_UPDATE_GOLDEN="1"; $env:FORGE_WRITE_EVIDENCE="1" 再跑，然后不设变量再跑一次
```

生成器入口：

```csharp
ICodeGenerator generator = new UnityMonoGenerator();               // 自动找仓库 Framework
// 或 new UnityMonoGenerator(null, new GeneratorOptions { FrameworkDirectory = "..." })
var result = await generator.GenerateAsync(context, plan, outputDirectory, cancellationToken);
// result.Ok / result.Error.Code / result.Value.Artifacts / result.Value.Metadata
```
