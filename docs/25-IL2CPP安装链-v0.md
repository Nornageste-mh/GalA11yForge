# IL2CPP 安装链 v0（BepInEx 6 + Il2CppInterop）

> 状态：**v0 真机打通（SiliconHeart 3012630 副本）**，2026-11-15。
> 适用范围：Unity IL2CPP x64 游戏；BepInEx **6.0.0-be.788**（固定版本）；Windows。
> 对应任务：task-42 / W2-X；研究报告：`research/35-il2cpp-install/REPORT.md`。

## 一、一句话

以前 IL2CPP 只能“分析 + 生成工程”，`forge build` / `forge install` 直接返回 `E_NOT_IMPLEMENTED`；
现在 build 会先检查 interop 是否就绪、再用真实 BepInEx 6 核心编译；install 会把 be.788 整包、
插件 DLL、语音运行期 DLL 装进游戏副本，写账本、做备份、可回滚；`forge verify` 安装/回滚两种模式
都能给出通过或明确失败，不再有“假装成功”。

## 二、之前卡在哪

| 环节 | 0.2.2.0 状态 | 证据 |
|---|---|---|
| detect / analyze / plan / generate | 可用（interop 静态分析） | `research/29-il2cpp-e2e/REPORT.md` |
| build（dotnet 编译） | `E_NOT_IMPLEMENTED`：适配器没有 IBuilder | `src/Forge.Cli/PipelineCommands.cs:513-518`（改动前） |
| install（部署/备份/回滚） | `E_NOT_IMPLEMENTED`：没有 IDeployer | 同上 `:584-593` |
| 语音 DLL 放置 | 手工复制，容易只放 `BepInEx/plugins` 导致退回 SAPI | R21 `REPORT.md` P1 |

## 三、现在的实现

### 3.1 新增/修改的文件

| 文件 | 作用 |
|---|---|
| `src/Forge.Build/BepInEx6Package.cs` | BepInEx 6 包定位 + 固定 SHA-256 校验 + 读条目；`BepInEx6Locator` 搜索 `BepInEx_win_x64_6*.zip` |
| `src/Forge.Build/Il2CppInteropProbe.cs` | interop 前置检查（`BepInEx/core/BepInEx.Unity.IL2CPP.dll` + `BepInEx/interop/*.dll` ≥ 10 个），缺失给出四步可执行指引 |
| `src/Forge.Build/Il2CppPluginDeployer.cs` | IL2CPP 部署器：计划/备份/写盘/账本/回滚/运行期清理，复用既有 `InstallManifest`、`DeploymentBaseline`、`RollbackReport` 模型 |
| `src/Forge.Build/DeploymentModels.cs` | `DeploymentOptions` 增加 `SpeechRuntimeDirectory`、`PlaceNvdaClientInGameRoot` |
| `src/Forge.Cli/BuiltInAdapters.cs` | IL2CPP 条目加 `DeployerFactory` 与 `RequiresIl2CppInterop` |
| `src/Forge.Cli/PipelineCommands.cs` | build/install/verify 的 IL2CPP 分支 + interop 闸门 + `IsUnityIl2Cpp` |
| `src/Forge.Cli/CliOptions.cs` / `CommandLineParser.cs` / `CliApplication.cs` | 新选项 `--speech-runtime <目录>` |
| `tests/Forge.Build.Tests/Il2CppDeploymentTests.cs` | 14 个单测（包校验/interop 探测/安装/回滚/路径安全） |
| `tests/Forge.Core.Tests/CliTests.cs` | +3 个 CLI 测试（`--speech-runtime`、IL2CPP 无 interop 时 build/install 明确报错） |

构建本身没有新写 builder：直接复用 `PluginBuilder`，由 CLI 传入 `-p:GameDir=<游戏目录>`，
生成的 csproj 里 `CoreDir/InteropDir` 都从 `GameDir` 推导，因此和手动 `dotnet build` 等价。

### 3.2 BepInEx 6 包固定值

| 项 | 值 |
|---|---|
| 文件 | `BepInEx_win_x64_6.0.0-be.788.zip` |
| 大小 | 34,336,405 B `[实测]` |
| SHA-256 | `F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A` `[实测]` |
| 必备文件 | `winhttp.dll`、`doorstop_config.ini`、`BepInEx/core/BepInEx.Unity.IL2CPP.dll`、`BepInEx/core/BepInEx.Core.dll` |

校验失败直接 `E_ARTIFACT_INVALID`，消息里同时给“期望/实际/官方下载页”，不会拿错包硬装。
注意：be.788 **没有** `BepInEx/core/BepInEx.dll`，所以不能复用 Mono 的 `BepInExPackage`（其 marker 是 5.x 的）。

定位顺序：`--bepinex <zip>` → 环境变量 `GALA11Y_BEPINEX6` → 工作区内递归找 `BepInEx_win_x64_6*.zip`
（优先精确 be.788、再优先名字带 `_a11y`、浅层、新修改）。找不到返回 `E_DEPENDENCY_MISSING` 并给下载页。

### 3.3 安装内容与放置规则

一次 install 会写三类文件（全部进同一份 `install-manifest.json`）：

1. **BepInEx 6 整包**（`Source="bepinex"`，约 205–230 个文件）：`winhttp.dll`、`doorstop_config.ini`、
   `.doorstop_version`、`BepInEx/core/**`、`dotnet/**`（.NET 6 运行时）。
2. **插件 DLL**（`Source="plugin"`）：本次 `forge build` 产物（顶层 `*.dll`），放进 `BepInEx/plugins/`，
   与 Mono 路径一致。
3. **语音运行期 DLL**（`Source="speech"`，可选）：`--speech-runtime <目录>` 指向用户自备的
   Tolk 运行期目录，目录下所有文件复制到 `BepInEx/plugins/`（`Tolk.dll` 等）。

**`nvdaControllerClient64.dll` 放哪？** 默认放**两份**：`BepInEx/plugins/`（跟 Tolk 在一起）+
游戏 exe 根（Tolk 用 `LoadLibrary` 按 exe 目录搜索）。原因：R21 真机实测**只放 plugins 时
Tolk 报“阅读器=SAPI”，只在 exe 根加一份才变成 NVDA**；放两份是“能工作”的最稳组合。
风险：两份可能被后续手工替换成不同版本，排查时要以 exe 根那份为准；如需严格单份，
把 `DeploymentOptions.PlaceNvdaClientInGameRoot` 设为 `false`（当前 CLI 没暴露该开关，属内部选项）。

**许可证红线**：Tolk / `nvdaControllerClient64.dll` / `SAAPI64.dll` / `ZDSRAPI_x64.dll` 都**不随产品分发**；
安装器只从用户指定目录复制，目录不存在时安装继续但写 warning + 人工待办（“语音后端未就绪”），
`verify` 的语音检查会显示未验证，不会假报通过。

### 3.4 interop 前置检查（build 之前）

IL2CPP 的 csproj 需要引用游戏首启生成的 interop 程序集；没有 interop 时 `dotnet build` 只会报一堆
`CS0246`，对用户毫无帮助。因此 build 第一步先检查：

- `BepInEx/core/BepInEx.Unity.IL2CPP.dll` 与 `BepInEx/core/BepInEx.Core.dll` 存在；
- `BepInEx/interop/` 下 `*.dll` ≥ 10 个（真机实测 94 个顶层 DLL；R21 记录 97 个文件）。

任一不满足：`E_DEPENDENCY_MISSING`（退出码 3），消息给四步指引：
① 把游戏复制到可写目录；② 解压 be.788 到游戏根；③ 启动一次，等 `BepInEx/interop` 出现约 97 个 DLL
（首启约 229 秒，Cpp2IL 约 20 秒，其余是 interop 生成 + 写盘）；④ 重跑 `forge build`。

### 3.5 账本、备份与回滚

- 账本：`<workspace>/games/<slug>/05-deploy/install-manifest.json`（与 Mono/Ren'Py 同一位置与格式）；
- 备份：`<workspace>/backup/<deploymentId>/<原相对路径>`，回滚前逐个校验备份哈希；
- 基线：`<workspace>/games/<slug>/05-deploy/baseline.json`，供 `forge verify` 的“账本外原文件未变”核对；
  **IL2CPP 会把 BepInEx 运行期文件（`BepInEx/interop|cache|config|patchers` 与 `BepInEx/*.log`）
  从基线里排除**——这些文件每跑一次游戏就会变（interop 生成、chainloader 缓存、日志重建），
  留着会把“正常运行”误判成“篡改”（见 5.1）。
- 回滚：`forge verify --rollback [--force]` 让部署器按账本逆序还原/删除，然后清理本次新增的运行期文件与空目录；
  安装前就存在的 BepInEx 文件（`install-manifest.json` 里的 `bepInExBaseline`）不会被删。
- 重复安装：同一工作区已有账本时默认拒绝（`E_ARG_INVALID`，提示先 `--rollback` 或加 `--force`）；
  `--force` 会把旧账本归档成 `install-manifest.previous-<时间>.json` 再装。

### 3.6 CLI 用法

```powershell
$forge = "D:\DSHWorkSpace\src\Forge.Cli\bin\Release\net8.0\forge.exe"
$game  = "D:\...\SiliconHeart"          # 必须是可写副本
$ws    = "D:\...\ws"

& $forge detect  --game $game --workspace $ws --json
& $forge analyze --game $game --workspace $ws --json
& $forge plan    --game $game --workspace $ws --json
& $forge generate --game $game --workspace $ws --json
& $forge build   --game $game --workspace $ws --json
& $forge install --game $game --workspace $ws --json `
    --bepinex "D:\...\BepInEx_win_x64_6.0.0-be.788.zip" `
    --speech-runtime "D:\...\speech"
& $forge verify  --game $game --workspace $ws --json
# 玩完/不该留补丁时
& $forge verify  --game $game --workspace $ws --rollback --force --json
```

`--bepinex`、`--speech-runtime` 都可省：前者会在工作区里找（找到多个时按 3.2 的排序挑一个），
后者缺省表示“不装语音 DLL”，此时插件仍会装、但朗读后端不可用，`verify` 会如实标未验证。

## 四、真机证据（SiliconHeart 3012630 副本）

> 完整数据与日志见 `research/35-il2cpp-install/`；下面只摘关键数字。

| 步骤 | 结果 |
|---|---|
| 副本 | 436 文件 / 717,359,068 B（`robocopy`，Steam 原目录只读） |
| 装 be.788 + 首启生成 interop | 97 文件 / 74,424,659 B，约 229 秒 `[实测]` |
| detect | `unity_il2cpp`，confidence 0.95，Unity 2021.3.15f1，x64 |
| analyze | 40 个文本候选（高 38 / 中 2）；未选中会崩的 `TextPrinterManager::PrintTextAsync` |
| plan | 6 个 hook 点（`UITextPrinter::set_Text`、`RevealableTextPrinterPanel::set_PrintedText`、`RevealableUIText::set_Text`、3×`set_AuthorNameText`） |
| generate | 12 个文件；`hook_count=6` |
| build | 成功，`game.il2cpp.a11y.dll` 16,896 B，6.7 秒；日志显示 `[准备] IL2CPP interop 就绪：94 个 DLL / 49,235,968 B` |
| install | 232 个文件操作（228 替换 + 4 新增），备份 228 个文件 |
| 游戏内 | `Tolk 已加载，阅读器=NVDA`；连续朗读日志 19 行（`RevealableUIText::set_Text len=… sha=…`） |
| verify（安装） | `pass_with_skips`：17 项检查 0 失败、5 项未验证 |
| 回滚 | 还原 228 / 删除 4 / 清理空目录 2 / 哈希不一致 0 |
| verify（回滚） | `pass`：10 项检查 0 失败 |
| 回滚后 | 文件数 836（安装前 836）；`GameAssembly.dll`、`SiliconHeart.exe`、`global-metadata.dat` 哈希不变；interop 97 个文件保留 |

## 五、验证时踩到的两个坑（已修/已记）

### 5.1 基线把运行期文件算进去 → verify 假失败（已修）

第一次安装后 `verify` 的 `m-07` 报：

```
BepInEx/cache/chainloader_typeloader.dat 大小变化（基线 4，实际 299）
BepInEx/LogOutput.log 大小变化（基线 6437，实际 3348）
```

这是“游戏跑过一次”的正常结果，不是篡改。修复：IL2CPP 部署器采集基线后剔除
`BepInEx/interop|cache|config|patchers/**` 与 `BepInEx/*.log`，并在部署计划的 warnings 里写明剔除了多少条。
修复后同一流程：安装 verify `pass_with_skips`、回滚 verify `pass`。

### 5.2 名字门与“设置”按钮（自动化注意）

- 第一次进剧情会弹 `//SYS_INPUT_WND/`“请设置主人公名称”，输入框已填默认名。
  程序化通过方式（本次实测）：点输入框 → 回车；或点“确定”。
- 屏幕右下角一排图标（存档/读档/设置/菜单）就在对话推进区右侧，**用鼠标点右下角空位推进剧情会误触“设置”**，
  再点一下还会弹“是否返回标题画面”。推进剧情用**回车**最稳（空格在本作不生效）。
- 本次第一次跑拿到 19 行连续朗读日志（超过 10 行目标）；第二次跑为回归只推进 4 行。

## 六、未验证 / 不覆盖

- **只测了一款游戏**（SiliconHeart），且是 Unity 2021.3.15f1c1、metadata v29、x64；其它版本/其它
  Il2CppInterop 版本未测。
- **6 个钩子只从日志看到 1 个通道在说话**（`RevealableUIText::set_Text`）。生成器有 250ms 跨通道去重，
  同一行只记第一个通道，所以“其余 5 个钩子是否命中”从日志无法区分；未单独验证 `set_AuthorNameText`
  是否真的触发（作者名可能由别的字段显示）。
- **朗读是否真的出声**：`Tolk 已加载，阅读器=NVDA` 只证明 Tolk 选中 NVDA，未做“NVDA 实际读出的文本”
  的逐条比对（日志按设计只记 len/sha，不记正文，避免把台词写进日志）。
- **焦点顺序 / 选项朗读 / 回听 / 存读档**：`verify` 明确列为未验证项；本次没有逐项人工走查。
- **Steam 完整性校验**：副本上做的，未在 Steam 原目录验证“校验是否会打回”。
- **安装包体积**：be.788 带 .NET 6 运行时（约 200 个 dotnet DLL），安装会多占约 30 MB；未做精简。

## 七、风险与约束

| 风险 | 说明 | 缓解 |
|---|---|---|
| interop 与游戏版本锁死 | interop 是“针对这一份 `GameAssembly.dll` 生成的”，游戏一更新就必须重启一次重新生成，旧 interop 可能编译失败或运行期崩 | build 前置检查 + 账本记录 `interop_assembly_hash`；`verify` 的 `b-06` 会核对日志里插件加载行 |
| BepInEx6 版本固定 | 只支持 be.788（SHA-256 pin）；be.789+ 需重新实测再改常量 | 包哈希校验拒绝未知包；常量集中在 `BepInEx6Package` |
| x64-only | 包名与校验只覆盖 `win_x64`；x86 游戏直接失败 | 检测到 x86 时明确报错（后续可加） |
| 双份 controller | plugins 与 exe 根各一份，可能被手工替换成不同版本 | 文档规定以 exe 根为准；需要单份时关 `PlaceNvdaClientInGameRoot` |
| 语音 DLL 许可证 | Tolk/各读屏器客户端 DLL 不可随产品分发 | 只从用户目录复制，缺失时 warning + 未验证，不阻断安装 |
| 名字门 | 首次运行必须设名，纯键盘自动化要“点输入框+回车” | 文档写明；`verify` 不把它当失败 |
| 回滚删空目录 | 只删本次新建且已空的目录；玩家自己建的空目录不动 | 账本 `createdDirectories` 记录来源 |

## 八、复现（最小步骤）

1. `robocopy "D:\Steam\steamapps\common\SiliconHeart" "D:\DSHWorkSpace\_il2cpp-install-e2e\game" /E`
2. 解压 `BepInEx_win_x64_6.0.0-be.788.zip` 到副本根；`Start-Process .\SiliconHeart.exe`
   （**必须重定向 stdout/stderr**，否则 GUI 进程会挂住调用方），等 `BepInEx\interop` 出现 90+ 个 DLL 后关掉游戏。
3. 按 3.6 依次跑 detect→analyze→plan→generate→build→install→verify。
4. 启动游戏，回车推进剧情；`BepInEx\LogOutput.log` 里应看到
   `[a11y] Tolk 已加载，阅读器=NVDA` 与逐行 `set_Text len=… sha=…`。
5. `forge verify --rollback --force`，再核对副本文件数与关键哈希。
