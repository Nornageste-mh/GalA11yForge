# Forge.Adapters.Renpy

Ren'Py 适配器（C#，net8.0）。实现 Core 的 detect/analyze/plan/generate/verify，产物是零归档覆盖层：
只把松散 `.rpy` 放到 `game/a11y_platform/`，不解析、不改写 `.rpa`，不写游戏目录。

状态：0.1.0.0；`dotnet test` 28/28 通过；**未真机启动**，运行时行为仍需人工验收。

## 1. 接口实现

| Core 接口 | 实现类 | 行为 |
|---|---|---|
| IEngineDetector | RenpyDetector | 读 `renpy/vc_version.py` 与 `renpy/__init__.py`，兼容 6.99（`version_tuple`）与 8.x（`version=...`）；识别 Python 位数、`.rpa` 数量/体积、已装 `a11y_*`；置信度 0–1，证据带相对路径与行号 |
| IEngineAnalyzer | RenpyAnalyzer | 只读盘点；4 个 TextCandidate（`config.say_menu_text_filter` 为对话/选项主钩子，`focus_list` 为菜单，`tts_function` 为旁白兜底），source 形如 `engine:hook:...`；coverage/notCovered/manualTodo/unverified 全量填写 |
| IPlanner | RenpyPlanner | `route=automatic`；8 个 patchPoints（6 overlay + vbs + 安装清单）与 11 个 artifacts；已有 `a11y_*` 时追加 pp-009 并要求先卸载 |
| ICodeGenerator | RenpyGenerator | 把 9 个模板渲染到工作区目录，加上清单共 11 个文件；模板缺失 `E_ARTIFACT_MISSING`；拒绝写游戏目录 |
| IVerifier | RenpyVerifier | 离线静态检查，不启动游戏、不写游戏目录；生成目录缺失 `E_ARTIFACT_MISSING` |

`RenpyAdapter` 聚合以上五步，公开 `EngineId="renpy"`、`AdapterName="renpy"`、`AdapterVersion="0.1.0.0"`；
另有 `VerifyDirectoryAsync(context, outputDirectory, ct)` 供 CLI 校验自定义 `--out` 目录。

## 2. 生成策略

- 输出目录必须在 `WorkspaceRoot` 内、且不在游戏目录内，否则 `E_GAME_WRITE_DENIED` / `E_PATH_UNSAFE`。
- 9 个内容文件：不存在则新增；字节相同则跳过；不同但与上次 `a11y_manifest.json` 记录的哈希一致则覆盖
  （重复 generate 幂等）；用户改动过的文件拒绝覆盖，返回 `E_ARTIFACT_INVALID`。
- `a11y_manifest.json` 与 `deploy_plan.json` 每次重写，哈希从磁盘实测。
- Profile 可选项：`renpy.patchVersion`（默认 0.2.0.1，四段数字）、`renpy.tts`（auto/nvda/sapi/log）、
  `renpy.sapiRate`（-10..10）；兼容无前缀键。

## 3. 11 个生成物（与 Python 原型同名同路径）

- `game/a11y_platform/00_contract.rpy`、`01_speech.rpy`、`02_dialogue.rpy`、`03_menu.rpy`、`04_focus.rpy`、`05_keys.rpy`：
  契约/语音/对话/菜单/焦点/键位六个覆盖层文件。
- 根目录：`a11y_say.vbs`（SAPI 桥）、`README-A11Y.txt`（玩家说明）、`uninstall.ps1`（按哈希卸载）。
- 清单：`a11y_manifest.json`（文件 + SHA256，`gala11y.renpy.overlay/1`）、`deploy_plan.json`（部署步骤与验收提示）。

模板源文件在 `Templates/` 便于审查，同时以 `EmbeddedResource` 打进程序集；运行时从资源加载。

## 4. 与 Python 原型的差异

1. 模板嵌入程序集（LogicalName `Templates/platform/*`、`Templates/root/*`），不再依赖外部模板目录。
2. verify 用纯 C# 结构自检（制表符、括号/引号/三引号配平），**不等同 CPython compile**；
   A1/A2/B1–B12/C1–C6 沿用 `lint_overlay.py` 语义，另加 D1–D7；A3 比原型弱。
3. C2 只扫 6 个 `.rpy`；README 里“磁盘优先于 .rpa”是说明文字，不算写盘。
4. 新增旧清单哈希白名单，允许覆盖本工具上次生成且未改动的文件；原型直接写盘。
5. 不执行 `--apply`、不部署、不运行卸载脚本；生成物只写工作区。
6. 单独的根目录 `a11y_say.vbs` 不再判为冲突（DDLC 原版自带同名脚本）；只看 `game/a11y*` 目录或根清单。
7. 清单 `generator` 为 `gala11y-forge-renpy`，`deploy_plan.verify` 文案改为 `forge verify` + 人工真机检查。

## 5. 已知限制

- **未真机启动**：没有验证 Ren'Py 运行时真的加载覆盖层、朗读顺序与实际延迟。
- `.rpa` 内文本与图片不在范围：不解析归档，不替换任何资源；CG 无替代文本。
- OBE（永恒与星辰与日常）已装 `game/a11y_*`，需先按旧清单卸载；plan 会标冲突并阻止直接混用。
- Steam `steamapps` 目录只读：生成物需复制到可写副本或按 README 手工安装。
- 纯语音、存读档界面、1–9 直选、说话人反解仍可能不完整。
- Ren'Py 6.99 与 8.x 的 `get_screen`/`_tts_all`/键位差异只做签名回退，未逐版本实测。
- 当前 CLI 尚未接入适配器（W2-A 骨架对 detect 等仍返回 `E_NOT_IMPLEMENTED`）。

## 6. 如何被 CLI 调用（契约形状）

CLI 通过 `adapters/adapter.json` 注册，建议项：

```json
{
  "schemaVersion": 1,
  "adapters": [
    {
      "name": "renpy",
      "engine": "renpy",
      "priority": 100,
      "assembly": "Forge.Adapters.Renpy.dll",
      "type": "GalA11yForge.Adapters.Renpy.RenpyAdapter",
      "capabilities": ["detect", "analyze", "plan", "generate", "verify"]
    }
  ]
}
```

对应命令（`--workspace` 省略时用默认工作区）：

```text
forge detect   --game <游戏根目录>
forge analyze  --game <游戏根目录> --workspace <工作区>
forge plan     --game <游戏根目录> --workspace <工作区>
forge generate --game <游戏根目录> --workspace <工作区> --out <工作区>/games/<slug>/generated
forge verify   --game <游戏根目录> --workspace <工作区>
```

## 7. 测试与参考

`tests/Forge.Adapters.Renpy.Tests`：28 个 xunit 用例，覆盖合成 6.99/8.5 夹具（假 RPA、已装 `a11y_*`）、
各步错误码、生成幂等/拒绝覆盖、篡改校验失败，以及 DDLC / mei / 永恒与星辰与日常 的只读真实目录用例
（不存在自动 Skip；用例前后对比文件清单与大小，不启动游戏）。

```powershell
D:\dotnet\dotnet.exe test tests\Forge.Adapters.Renpy.Tests\Forge.Adapters.Renpy.Tests.csproj
```

研究依据：`research/14-renpy/REPORT.md`；原型与 21 条断言：`src/experiments/renpy-adapter/`；模板源自本项目原型，不含游戏资源/台词，许可证按仓库 `AGENTS.md`。
