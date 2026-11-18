# Forge.Verify 模块报告（W2-E）

版本：0.1.0.0　日期：2026-11-14　状态：离线自检与测试已完成，未接入真实游戏运行期

## 结论摘要

1. 已实现 `src/Forge.Verify/**`：安装后自检、回滚后检查、账本/备份/日志/基线核对、语音后端只读探测、`verify.json` + `verify.md` 输出，以及 Core `IVerifier` 适配器。安装前基线由 `Forge.Build` 的 `install` 自动生成并按本模块 schema 写到 `games/<slug>/05-deploy/baseline.json`。
2. 已实现 `tests/Forge.Verify.Tests/**` 共 64 个 xunit 测试，Debug 与 Release 均全绿（实际命令与输出见下文）。
3. 所有测试使用临时假游戏目录与假账本，**没有启动任何游戏，也没有调用 Tolk/NVDA/争渡/SAPI 发声**。本模块只能证明“离线文件与账本一致”，不能证明“游戏内能读屏玩”。
4. `overall` 不把跳过写成通过：无失败但有跳过时为 `pass_with_skips`，只有全部检查通过才是 `pass`，P0 失败一律 `fail`。
5. 有两处与 `docs/09` 现有样例不完全一致，已在“风险/待确认”列出，需要 Lead 决定：`schemaVersion` 类型，以及 `pass_with_skips` 是否正式入 schema。

## 用法

```csharp
var options = new VerifyOptions
{
    GameDirectory = @"D:\Games\Sample",
    WorkspaceRoot = @"C:\Users\me\AppData\Local\GalA11yForge",
    Slug = "sample",
    Mode = VerifyMode.Install,          // 或 VerifyMode.Rollback
    ProbeEnvironment = true,            // 只读探测，不加载、不发声
};

var service = new VerificationService();
var result = await service.VerifyAsync(options);
if (result.Ok)
{
    var report = result.Value!;
    // report.Overall: pass | pass_with_skips | partial | fail
}
```

写出报告（拒绝写入游戏目录）：

```csharp
var written = await VerifyReportWriter.VerifyAndWriteAsync(
    options,
    outputDirectory: @"C:\Users\me\AppData\Local\GalA11yForge\games\sample\06-verify");
// written.Value.JsonPath / written.Value.MarkdownPath
```

接入 `IVerifier`：

```csharp
var verifier = new Verifier(); // 内部使用 WindowsEnvironmentProbe
var coreResult = await verifier.VerifyAsync(gameContext, deployResult);
// 只有 overall == pass 时 coreResult.Value.Passed 才为 true；
// skip 项映射为 Passed=false，并在 Detail 前加 "skip:"。
```

## 检查项

`priority` 只影响整体结论：任一 P0 失败 → `fail`；只有 P1/P2 失败 → `partial`。跳过项始终不当作通过。

| ID | 优先级 | 检查内容 | 主要限制 |
|---|---|---|---|
| m-01 | P0 | 账本能从默认位置或显式路径读取 | 只读，不修复账本 |
| m-02 | P0 | `schemaVersion=1`、必填字段、游戏目录匹配、files 非空、动作枚举合法 | 不校验账本之外的文件 |
| m-03 | P0 | 相对路径安全、无重复记账 | 只检查账本声明 |
| m-04 | P0 | Add/Replace 文件存在；Remove 文件不存在 | 不判断文件内容（见 m-05） |
| m-05 | P0 | 安装后 SHA-256 与账本一致 | 账本未记录哈希时 skip |
| m-06 | P0 | 有哈希的安装文件不是 0 字节 | 账本没有 size 字段，不能核对精确字节数（已写入未验证项） |
| m-07 | P0 | 基线逐文件核对：安装模式核对“账本之外”的原文件；回滚模式核对全部基线文件（含本次部署目标），要求“当前状态 == 安装前状态” | 只核对基线列出的文件；不把基线外新增文件判为篡改 |
| k-01 | P0 | 每个 `ExistedBefore` 条目有备份，且备份文件存在 | 只验证账本声明的备份 |
| k-02 | P0 | `Sha256Before` 与备份文件一致 | 无 `Sha256Before` 时 skip |
| k-03 | P0 | 备份路径是绝对路径，位于账本 `BackupRoot` 与工作区 `backup/` 下 | 防止备份逃逸 |
| b-01 | P1 | BepInEx/ 目录存在 | 非 BepInEx 安装只报 skip |
| b-02 | P0 | 找到 BepInEx 核心 DLL 候选 | 不验证能否加载 |
| b-03 | P0 | 账本声明的所有 BepInEx 插件 DLL 在位 | 不验证插件被加载 |
| b-04 | P1 | 找到 winhttp/version/doorstop 之一 | 自定义注入可能误报 skip（已写入未验证项） |
| b-05 | P1 | BepInEx 日志存在且非空 | 只看最近一次运行留下的日志 |
| b-06 | P0 | 日志包含 BepInEx 头和至少一个已声明插件的 Loading/Loaded 行（DLL 名不匹配时按生成器约定的「无障碍补丁」显示名兜底，并在 detail 注明弱匹配） | 有界读取头+尾；不能证明本次运行加载成功 |
| r-01 | P0 | 回滚后原有文件存在，优先与 `Sha256Before` 比对，缺失时回退到备份哈希 | 无任何可用哈希时记为无哈希，不当作通过 |
| r-02 | P0 | 回滚后新增文件已删除 | 只按账本检查 |
| r-03 | P0 | `cleanup.uninstalled/restored/hashMatch` 与磁盘事实一致 | 不执行卸载动作 |
| e-01 | P1 | Tolk/NVDA/争渡/SAPI 组件是否存在 | 只报“组件存在”，不验证出声 |

安装态不运行 r-01..r-03，`cleanup.required=false`，并明确写“未执行卸载回滚”。回滚态不运行 m-04/m-05/m-06、b-*、e-*。

## 安装前基线与 P1-3（2026-11-14）

`forge install`（`Forge.Build.PluginDeployer`）现在会在**第一次写游戏目录之前**只读采集安装前基线，
写到工作区 `games/<slug>/05-deploy/baseline.json`；`forge verify --rollback` 会自动读取它并逐文件核对
“当前状态 == 安装前状态”。

基线内容与格式：

- `artifact: "baseline"`、`schemaVersion: 1`、`gamePath`、`createdAt`、`files[]`；
- 每个文件记录 `relativePath`、`exists`、`size`、`sha256`（不存在时 `exists=false`、`size=0`、`sha256=""`）；
- 覆盖范围：本次部署计划触碰的游戏文件 + `BepInEx/cache|config|patchers/**` + `BepInEx/*.log`。

m-07 的两种模式：

- **Install**：仍只核对“账本之外”的原文件（部署目标在安装后本就应变化，避免自己跟自己比）；
- **Rollback**：核对基线中的**全部**文件（包括本次部署目标），任何大小/哈希/存在性与安装前不一致都判 fail，
  并在 detail 列出具体路径与期望/实际。这样能证明“安装前就存在的文件在回滚后没有被改动”，
  而不只是“账本内文件被删/还原”。

安全与失败语义：

- 采集只读游戏目录，不跟随 reparse point；baseline 只能写工作区，目标/父链含符号链接或联接点时拒绝；
- `GameBaselineStore.Save/SaveAsync` 原子写（同目录临时文件 → overwrite move，UTF-8 无 BOM），不会留下半截 JSON；
- **基线缺失**（旧安装、被手动删除）时 m-07 仍为 skip + 未验证项，`overall` 最多 `pass_with_skips`，绝不冒充 pass；
- 显式传入的 `BaselinePath` 若在工作区外、等于工作区根或是 reparse point，直接返回 `E_PATH_UNSAFE`；
- 基线内的非法相对路径（`../`、绝对路径、盘符）逐条判 fail 并写“基线路径非法”，不会越界读取或崩溃。

## overall 语义

| overall | 含义 |
|---|---|
| `pass` | 所有已执行的检查均为 pass，且没有 skip |
| `pass_with_skips` | 没有 fail，但至少一项 skip；**不等于全部验证通过** |
| `partial` | 存在非 P0 的 fail（没有 P0 fail） |
| `fail` | 存在 P0 fail，或没有任何检查项 |

`VerifyReport.ComputeOverall` 是唯一计算入口，测试覆盖空检查、P0 fail、P1 fail、仅 skip、全 pass、小写优先级等分支。

## 环境探测（只读、不发声）

`WindowsEnvironmentProbe` 只做四类只读事实检查：

- Tolk：在游戏目录、程序目录、当前目录、`_tolk_probe`、工作区 `adapters/`、`GALA11Y_TOLK_DIR` 中找 `Tolk.dll`；
- NVDA：`nvda` 进程 + `nvdaControllerClient64.dll`/`nvdaControllerClient.dll`（含 `Program Files\NVDA`）；
- 争渡：`ZDSRMain/ZDSRMain_x64/ZDSRDaemon/ZDSRService/ZDSRMultiEngine` 进程 + `ZDSRAPI_x64.dll`/`ZDSRAPI.dll`；
- SAPI5：只读注册表 `Speech\Voices\Tokens` 与 `Speech_OneCore\Voices\Tokens` 计数。

它不加载 Tolk、不调用任何朗读 API、不启动游戏。`Available=true` 只表示“进程/文件/注册表事实齐全”，不表示“朗读真的出声”。缺少后端时 `e-01` 为 skip，并在未验证项写明原因。

## 输出

`VerifyReportWriter.WriteAsync` 在指定目录写：

- `verify.json`：UTF-8 无 BOM，camelCase，枚举 snake_case；含 `checks`、`metrics`、`cleanup.restored/hashMatch`、`overall`、`unverified`、`environment`、`evidence`；
- `verify.md`：固定包含 `## 检查结果`、`## 覆盖声明`、`## 未验证项`、`## 清理与还原`、`## 环境事实`、`## 指标`、`## 证据`；空未验证项写“（无显式未验证项；这不等于全部已验证）”，结尾再次声明 skip 不是通过。

输出目录若位于游戏目录内，返回 `E_GAME_WRITE_DENIED`，不创建目录。

## 测试命令与结果 [实测]

```pwsh
D:\dotnet\dotnet.exe test tests\Forge.Verify.Tests\Forge.Verify.Tests.csproj --nologo -v minimal
D:\dotnet\dotnet.exe test tests\Forge.Verify.Tests\Forge.Verify.Tests.csproj -c Release --nologo -v minimal
```

本机结果（2026-11-14）：

```text
已通过! - 失败: 0，通过: 64，已跳过: 0，总计: 64
```

测试覆盖：标准安装全通过、哈希不一致、文件缺失、0 字节、账本缺失/非法 JSON/版本不支持/必填字段缺失/游戏路径不匹配/路径逃逸/重复记账、备份缺失/内容不符/位置逃逸、基线篡改/默认缺失/显式缺失/非法 JSON/游戏路径不符、**恶意基线相对路径（`../`、绝对路径、盘符）/`exists=false` 条目/显式基线越界拒绝**、日志缺失/空/无插件加载行、无 BepInEx 布局、环境可用/不可用/探测异常、回滚干净/残留/哈希不符/文件缺失/**回滚态 m-07 全量核对**、安装态误跑回滚模式、报告写出/固定 Markdown 节/拒绝游戏目录/空未验证文案、`IVerifier` 映射、Windows 探测四后端。

## 覆盖声明

**本模块离线验证的：**

- 安装账本与磁盘的存在、哈希、路径、重复记账；
- 账本外原文件的基线比对（安装模式）与安装前基线全量回滚核对（回滚模式，含本次部署目标）；
- 备份文件与备份位置；
- BepInEx 目录/核心 DLL/插件 DLL/加载器/日志关键行；
- 回滚后磁盘状态与 `cleanup` 字段；
- 语音后端组件存在性（只读事实）。

**本模块未验证的：**

- 游戏内实际朗读、焦点顺序、选项、回听、存档等运行期行为；
- 语音后端是否真的出声；
- BepInEx 日志对应的是否为本次运行；
- 卸载动作本身（模块只做回滚后检查，不执行删除/还原）；
- Steam 完整性校验与原版可启动性；
- 基线之外新增文件的合法性（运行期日志、缓存等）；
- 账本未记录哈希的安装文件的完整性与精确大小。

## 风险 / 待确认

1. **schemaVersion 不一致**：`docs/09` 的 verify.json 样例是字符串 `"0.1.0"`，本模块沿用 Core 现有惯例写整数 `1`。若下游按 docs/09 校验，会不兼容。需要 Lead 统一约定；未擅自改 Core。
2. **`pass_with_skips` 是扩展值**：docs/09 只列 `pass|partial|fail`。本模块为了让“没测”不冒充 `pass` 使用了扩展值。若 schema 严格要求三值，需要 Lead 决定是改文档还是改成 `partial` + 未验证项表达。
3. **账本没有 size 字段**：m-06 只能检查“非空”，不能核对精确大小；已在未验证项注明。若需要精确核对，需 W2-A 在 `DeployFileEntry` 增加 size（会改 Core 契约）。
4. **基线只覆盖列出的文件**：不检测基线外新增文件，避免把 BepInEx 运行期日志/缓存误判为篡改；回滚模式虽已核对基线全部条目（含部署目标），仍不是“整目录未被改动”的证明。安装前基线由 `install` 自动生成，但只覆盖部署目标与 BepInEx 白名单，游戏自己新建的存档/设置文件不在内。
5. **日志可能来自上一次运行**：模块只读日志，不启动游戏，b-06 通过仅表示“最近一次日志里出现过插件加载行”。
6. **回滚模式的 CLI 参数解析不在本模块**：本模块提供 `VerifyMode.Rollback`、基线自动加载与核对；`forge verify --rollback` 的解析/接线由 CLI 负责，本模块只保证传入正确 mode 时行为正确。
7. **未做真实游戏验证**：测试全部基于临时假目录；`WindowsEnvironmentProbe` 虽在本机跑过，但没有调用朗读 API，不能对外说“已实测读屏可用”。

## 建议下一步

1. Lead 先定 `schemaVersion` 与 `pass_with_skips` 的契约，再让 GUI/CLI 消费 `verify.json`。
2. 把 `VerificationService` 接进 CLI 的 `verify` 子命令，并提供 `--rollback` 参数；本模块不擅自改 Cli。
3. 用一台装有目标游戏的机器跑一次“安装→verify→卸载→verify --rollback”，把真实 `verify.json` 作为集成证据，而不是用假游戏结论对外。
4. 若要求“精确大小”，先改 `DeployFileEntry`（Core）再扩展 m-06；当前不伪造该能力。
