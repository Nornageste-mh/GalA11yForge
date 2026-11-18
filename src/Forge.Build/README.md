# Forge.Build

部署与回滚模块：把生成的 BepInEx 插件（Unity）或文件覆盖层（Ren'Py）安全地装进游戏目录，并保证 `forge verify --rollback` 能把游戏目录恢复到安装前。

## 模块职责

| 文件 | 职责 |
|---|---|
| `PluginBuilder.cs` | 调用 `dotnet publish` 构建插件工程，产出 `A11yPlugin.dll` 与构建日志。 |
| `PluginDeployer.cs` | 计划、安装、预检、回滚；所有写盘都经过 `PathGuard` 校验。 |
| `OverlayDeployer.cs` | Ren'Py 文件覆盖层：从 `03-generate` 的 `a11y_manifest.json`/`deploy_plan.json` 出计划，逐文件备份、原子写、记账本、逆序回滚（含 `.rpyc` 与空目录清理）。 |
| `DeploymentModels.cs` | `DeploymentPlan`、`InstallManifest`（部署账本）、BepInEx 基线快照。 |
| `DeploymentBaseline.cs` | 安装前基线：采集「本次部署目标 + BepInEx 运行期白名单」并写到 `05-deploy/baseline.json`。 |
| `InstallManifestStore.cs` | `install-manifest.json` 的原子读写与结构校验。 |
| `InstallManifestValidation.cs` | 账本校验：路径不得逃逸、备份必须存在、条目字段自洽。 |
| `RollbackModels.cs` | `RollbackOptions`、`RollbackAction`、`RollbackReport`。 |
| `BepInExPackage.cs` / `BepInExLocator.cs` | 识别并解压 BepInEx 5.4.23.5 缓存包。 |
| `PathGuard.cs` | 拒绝游戏目录之外的路径、盘符、`..` 与 reparse point。 |
| `FileHashes.cs` | SHA-256 计算与比较。 |

## 安装

`PluginDeployer.DeployWithPlan` 先做目标状态预检，再在**第一次写游戏目录之前**调用
`CaptureBepInExBaseline` 记录白名单区域的已有文件/目录，然后备份被覆盖的文件、写盘、复检、保存账本。

`InstallManifest` 中的 `BepInExBaseline` 是新增字段：

- 对象存在：表示部署前确实扫描过；`Files`/`Directories` 为空表示当时什么都没有。
- `null`：旧版账本（v0.2 及更早），没有基线，回滚退回按 `CreatedDirectories` 推断。
- `Complete = false`：部署前枚举失败（权限/IO），快照不完整；回滚只在能证明“整棵 BepInEx 都是本次新建”时才清理，否则跳过并写警告。

### 安装前基线（P1-3）

`DeployPlanCore` 在**第一次写游戏目录之前**还会调用 `DeploymentBaseline.Capture`，只读采集：

- 本次部署计划会触碰的全部游戏文件（`plan.Operations`，含 `Add` 目标）；
- `BepInEx/cache|config|patchers/**` 下安装前已有的文件；
- `BepInEx/` 根目录一层的 `*.log`。

每条记录包含 `relativePath`、`exists`、`size`、`sha256`（不存在时 `size=0`、`sha256=""`），
写到工作区 `games/<slug>/05-deploy/baseline.json`，顶层带 `"artifact": "baseline"` 与
`schemaVersion: 1`。采集只读；路径逐条经过 `ValidateRelative` → `ResolveUnder` → 拒绝 reparse point，
不会跟随符号链接读出游戏目录。

写盘时机与失败语义：

- 基线在部署写盘及 `VerifyAll` 成功、`install-manifest.json` 保存成功之后才原子写入
  （同目录临时文件 → `File.Move(overwrite: true)`，UTF-8 无 BOM），不会出现半截 JSON。
- 基线保存失败会触发自动回滚；若本次是首次安装（部署前没有账本），同时删除刚写的账本，
  避免留下“账本在、基线缺”的误导状态。
- 部署在写盘阶段失败时不会创建 `baseline.json`，旧基线也不会被改动。

回滚后由 `forge verify --rollback` 自动读取该文件，逐文件核对“当前状态 == 安装前状态”；
缺失时 m-07 仍为 skip/未验证，不会冒充通过。详见 `src/Forge.Verify/README.md` 的 m-07 说明。

## Ren'Py 覆盖层（OverlayDeployer）

- 来源：`games/<slug>/03-generate`。`a11y_manifest.json`（`gala11y.renpy.overlay/1`，9 个内容文件的 `path`/`bytes`/`sha256`）必填；`deploy_plan.json` 可选，但其 `copy` 步骤的目标集合必须与清单一致。
- 目标路径相对游戏目录，逐条经 `PathGuard.ResolveGameRelative`；目标已存在 → `Replace`（先备份并校验 `sha256Before`），不存在 → `Add`。
- 账本默认 `05-deploy/install-manifest.json`（`InstallManifest`，schemaVersion=1）。已有账本时必须 `--force`，旧账本归档到 `backup/<deploymentId>/previous-install-manifest.json`。
- 回滚按账本逆序：先还原 `Replace`/`Remove`，再删除 `Add`（连同同名 `.rpyc`），最后按 `CreatedDirectories` 清理空目录；目标被改动且无 `--force` 时拒绝。
- 真机：DDLC 6.99 副本 install 9 文件、rollback 删除 9 文件 + 清理 1 目录，哈希不一致 0，详见 `research/28-renpy-overlay/REPORT.md`。

## 回滚

回滚顺序：

1. 按账本恢复 `Replace`/`Remove` 的备份文件，并校验 `sha256Before`。
2. 删除账本里 `Add` 的文件。
3. **清理 BepInEx 运行期生成物（E1）**（`CleanBepInExRuntimeArtifacts`，默认 `true`）。
4. 清理本次部署新建且已变空的目录（`CleanupEmptyDirectories`，默认 `true`）。
5. 返回 `RollbackReport`。

任一步出现错误都会提前返回 `PartialRollback`，不会带着半截状态继续清理。

### 白名单（只清这些）

- `BepInEx/cache/**`
- `BepInEx/config/**`
- `BepInEx/patchers/**`
- `BepInEx/LogOutput.log` 以及 `BepInEx/*.log`（根目录一层）

其余路径一律不碰，尤其是 `BepInEx/core/**`、`BepInEx/plugins/**` 和游戏本体文件。

### 判定“本次安装新建”

对每个白名单候选文件：

- 账本 `Files` 里已记录的文件不在这里处理（走正常的恢复/删除流程）。
- 有基线：文件不在 `BepInExBaseline.Files` 中才允许删除。
- 旧账本（无基线）：文件父目录（或更上层）必须出现在 `CreatedDirectories` 中。
- 整个 `BepInEx` 在安装前不存在时（有基线看 `Directories` 是否含 `BepInEx`，旧账本看 `CreatedDirectories` 是否含 `BepInEx`），视为整棵树都是本次新建，可直接清白名单文件。

删除后按深度优先清理白名单内的空目录；只有能证明“本次新建”的目录才会删。
安装前没有 `BepInEx` 的场景，白名单产物清完、`core`/`plugins` 等空目录也清完后，
`BepInEx` 目录本身会被删除，不留空壳。

安装前已经存在的用户文件（例如用户自己的 `BepInEx/config/BepInEx.cfg`、旧日志）不会被删除，
即使运行期被 BepInEx 覆盖过，也原样保留。

### 如何看清理结果

`RollbackReport` 新增两个列表（都会写进 `rollback.json`）：

- `cleanedRuntimeArtifacts`：本次实际删除的运行期产物（相对游戏目录、`/` 分隔）。
- `skippedRuntimeArtifacts`：在白名单里、但无法证明“安装前不存在”而保留的文件；原因同时写入 `warnings`。

空目录仍复用 `cleanedDirectories`，所以“删了哪些文件”和“删了哪些目录”都能从 JSON 里逐一核对。
示例：

```json
{
  "ok": true,
  "cleanedRuntimeArtifacts": ["BepInEx/LogOutput.log", "BepInEx/cache/chainloader_typeloader.dat"],
  "skippedRuntimeArtifacts": ["BepInEx/config/BepInEx.cfg"],
  "cleanedDirectories": ["BepInEx/cache", "BepInEx/patchers", "BepInEx"]
}
```

CLI 控制台现有汇总行只统计 `restored/deleted/cleanedDirectories/hashMismatches`，
完整清单以 `rollback.json` 为准（CLI 不在本模块写盘边界内，未改）。

### 安全边界与已知限制

- 所有删除目标都经过 `PathGuard.ResolveGameRelative`，游戏目录之外、含 reparse point 的路径直接拒绝。
- 基线不完整且无法证明整棵树新建时，宁可不删：记录 `warnings`，不猜。
- `RollbackAsync(DeployManifest)` 旧接口拿不到完整账本（`DeployManifest` 不含基线/`CreatedDirectories`），
  因此不会自动清理运行期产物；CLI 的 `forge verify --rollback` 走 `RollbackFromFileAsync`，不受影响。

## 测试

```powershell
D:\dotnet\dotnet.exe test tests\Forge.Build.Tests\Forge.Build.Tests.csproj
D:\dotnet\dotnet.exe test src\GalA11yForge.sln -c Release
```

当前 `Forge.Build.Tests` 共 58 个测试，覆盖安装/回滚、哈希校验、冲突保护、E1 运行期清理与安装前基线：

- 安装前无 `BepInEx` → 回滚后目录不残留；
- 安装前已有同名 `config`/日志 → 保留，新增产物删除；
- 正常回滚后恢复文件哈希与安装前一致；
- 旧账本无基线时仍能按 `CreatedDirectories` 清理整棵新建的 `BepInEx`；
- 安装成功自动写 `05-deploy/baseline.json`（含部署目标、白名单文件、`exists=false` 条目、无 BOM、无临时文件残留）；
- 回滚后 `Verify(Rollback)` 对未篡改状态判 m-07 pass；
- 安装后篡改安装前已有的 `BepInEx/config/user.cfg`，回滚不会替用户“修好”它，verify 判 m-07 fail 并列出该路径；
- 删除基线后 verify 保持 m-07 skip，整体不是 pass；
- 写入阶段失败的部署不留下 `baseline.json` 或半截账本；
- `OverlayDeployerTests`（9 个）：Ren'Py 清单/计划映射、越界路径与生成物篡改拒绝、备份/账本、`--force` 归档旧账本、逆序回滚（含 `.rpyc` 与空目录）。
