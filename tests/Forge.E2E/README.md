# Forge.E2E —— GalA11y Forge 端到端测试

## 这是什么

- 黑盒集成测试：用 `Process` 调用 `forge` CLI，不引用 `src/**` 程序集。
- 覆盖 6 条链路：detect（假游戏 / 空目录）、analyze、plan、generate、install→rollback。
- 夹具是**合成样本**（`tests/fixtures/fake-game-unity-mono`），不是真实游戏；禁止运行、注入或分发。
- 本目录先不加入 `GalA11yForge.sln`，由 Lead 集成时添加。

## 默认全部跳过

所有测试使用自定义 `[E2EFact]`；未设置 `FORGE_E2E=1` 时显示 SKIP，不影响日常测试。
启用方式（PowerShell）：

```powershell
# 1. 构建 CLI（测试也会自动尝试构建一次）
D:\dotnet\dotnet.exe build src\Forge.Cli -c Debug

# 2. 重建假游戏夹具（首次必须；可重复运行）
pwsh -File tests\fixtures\make-fixture.ps1

# 3. 启用 E2E
$env:FORGE_E2E = '1'
D:\dotnet\dotnet.exe test tests\Forge.E2E\Forge.E2E.csproj
```

只想跑一个测试：`--filter FullyQualifiedName~DetectTests`。

## 当前接线状态（重要）

- CLI 目前只有 `detect` 已接线；`analyze/plan/generate/build/install/verify/report`
  仍返回 `E_NOT_IMPLEMENTED`、退出码 3。
- 遇到未实现步骤时，测试会**明确失败**并输出「接线未完成：…这不是夹具或测试代码的问题」，
  不会假装通过；接线完成后重跑即可。
- install 测试依赖 build 产物与本地 BepInEx 包；缺包会明确报 `E_DEPENDENCY_MISSING`。

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `FORGE_E2E` | 未设置 | 必须为 `1` 才执行测试 |
| `FORGE_DOTNET` | `D:\dotnet\dotnet.exe` | dotnet 路径，找不到则回退 PATH |
| `FORGE_CLI_DLL` | 自动构建 | 指定已有 `forge.dll`，跳过构建 |
| `FORGE_EXE` | — | 指定 `forge.exe`（与 DLL 二选一） |
| `FORGE_SKIP_BUILD` | 未设置 | 为 `1` 且 DLL 已存在时不再 build |
| `FORGE_REPO_ROOT` | 自动向上查找 `src/GalA11yForge.sln` | 仓库根 |
| `FORGE_FIXTURE_DIR` | `tests/fixtures/fake-game-unity-mono` | 夹具目录 |
| `FORGE_E2E_TIMEOUT_SEC` | `600` | 单次 CLI 超时（30–3600 秒） |

## 失败怎么定位

1. 失败消息里有 `提示：sandbox：<路径>`；打开该目录看 `workspace/`、`out/` 与 CLI stdout JSON。
2. 命令、退出码、stdout/stderr 尾部会直接打印在 xunit 失败消息里。
3. 沙箱位于 `%TEMP%\forge-e2e\`，**不会自动删除**；确认后可整体删除。
4. 夹具坏了就重跑 `pwsh -File tests\fixtures\make-fixture.ps1`（幂等）。

## 已知缺口

- CLI 契约暂无 `rollback` 命令；install 测试会先试 `forge rollback`，再试生成的
  `uninstall*.ps1`。两者都不可用时，测试会报告 A8 无法自动验收，而不是假通过。
- 安装测试**只对假游戏的临时副本**执行，绝不写 `D:\Steam` 或真实游戏目录。
