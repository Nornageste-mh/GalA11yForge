# Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo）

> ⚠️ **Vibe Coding 产物：代码不保证稳定可用。** 仅供学习、研究与试验，请勿用于生产环境或作为可靠工具依赖；问题以工作区日志和 `docs/` 为准。

目标：把 Galgame 安装目录变成"能读屏玩"的 Demo。流程为：识别引擎 → 分析文本管线 → 规划补丁点 → 生成补丁工程 Demo → 构建 → 安装 → 自检 → 回滚。机器无法处理的部分明确输出"人工待办"，不假装成功。

## 当前版本

- **0.2.86.0（2026-11-19）**：改名 GalA11y Demo，统一"生成工程 Demo"文案，README 增加 Vibe Coding 警告；行为不变，**752 测试**。
- 近期版本：0.2.85 校验钩子挂载对账；0.2.84 verify 新增 s-06；0.2.83 同名重载钩子修复；0.2.82 `--speech-log` 相对路径修复；0.2.80 发布包全文件校验清单。
- 历史版本与发布包见 `releases/`。版本统一按 `0.2.x` 叠加，不设 1.0。

## 能力现状（如实）

| 引擎 | 识别 | 分析/生成 | 真机朗读 |
|---|---|---|---|
| Unity Mono | 已覆盖 | 有（BepInEx5，自动接线朗读；选项+数字键） | **已验证**（《五天一夜》23 句、选项、回滚干净） |
| Ren'Py | 已覆盖 | 有（覆盖层）；install/rollback 已接 CLI | 可启动、同步朗读已修；DDLC 副本安装/回滚通过；未做人耳验收 |
| Unity IL2CPP | 已覆盖 | 有（interop/dump.cs，BepInEx6） | **已验证到 19 行**（《硅心》副本）；未逐条比对 |
| KiriKiri Z | 已覆盖 | 研究完成；本版不提供自动安装 | 未验证，只识别+人工待办 |
| RPG Maker / 其他 | 已识别 | 未做 | 未做 |

加载器兼容性预检会拦住"运行库被裁剪、BepInEx 必崩"的游戏，build/install 提前拒绝。

## 仓库地图

| 位置 | 内容 |
|---|---|
| `docs/` | 方案、决策、架构、合规、使用指南、发布说明、阶段证据 |
| `src/` | Core、CLI、Detection、Build、Verify、Generators、GUI、Ghidra、Renpy/UnityMono/Il2Cpp 适配器、ExternalReader |
| `tests/` | 10 个测试项目，752 个自动化测试全绿；E2E 夹具在 `tests/fixtures/` |
| `research/` | 引擎、语音、OCR、RE、Ghidra、加壳、合规、加载器、真机 E2E 等研究 |
| `releases/` | 发布包与校验和（含历史版本、extras 外部读屏器） |
| `Framework/`、作品目录 | 前期验证框架与作品，只读参考，不随产品分发 |
| `tools/mail/mail163.py` | 用户联络邮箱脚本；授权码从环境变量或本地文件读取，不入库 |

## 快速开始

详见 `docs/18-使用指南-v0.md` 或发布包内 README。最短路径：

```powershell
$forge = '.\releases\<版本>\app\forge.exe'
$game  = 'D:\Steam\steamapps\common\<游戏目录>'
$ws    = "$env:LOCALAPPDATA\GalA11yForge"

& $forge detect  --game $game
& $forge analyze --game $game --workspace $ws
& $forge plan    --game $game --workspace $ws
& $forge generate --game $game --workspace $ws
& $forge build   --game $game --workspace $ws --bepinex '<BepInEx.zip>'
& $forge install --game $game --workspace $ws --bepinex '<BepInEx.zip>'
# 启动游戏、听声音后：
& $forge verify  --game $game --workspace $ws
# 回滚：
& $forge verify  --game $game --workspace $ws --rollback --force
```

## 安全与合规

- 只对自己拥有的单机游戏使用；不碰在线/反作弊、不绕 DRM、不下载盗版。
- 安装前备份、写清单、可一键回滚；发布包不含游戏资源或台词。
- 详见 `docs/10-合规与保护边界-v0.md`、`docs/34-发布说明-v0.2.4.md`。

## 开发

```powershell
D:\dotnet\dotnet.exe test src\GalA11yForge.sln -c Release   # 752 个测试
D:\dotnet\dotnet.exe build src\Forge.Cli\Forge.Cli.csproj -c Release
```

协作约定见 `AGENTS.md`；研究结论须带证据：`[实测]` / `[文件]` / `[推断]`。
