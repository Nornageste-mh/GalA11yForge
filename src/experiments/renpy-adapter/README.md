# renpy-adapter（W2-G 原型）

把一款 Ren'Py 游戏的安装目录变成"能读屏玩"的**零归档覆盖层**：
只往 `game/` 里叠松散 `.rpy`，不打开、不改写、不重打包任何 `.rpa`。

## 快速开始

```powershell
# 1) 只读盘点（不写游戏目录）
D:\python\python.exe renpy_a11y_gen.py --game "<游戏根目录>" --inspect out.json --json

# 2) 生成覆盖层到 build\<slug>（9 个文件）
D:\python\python.exe renpy_a11y_gen.py --game "<游戏根目录>" --out build\ddlc --slug ddlc

# 3) 静态校验 21 条
D:\python\python.exe lint_overlay.py build\ddlc

# 4) 假引擎冒烟 16 条（本机没有 Ren'Py SDK 时证明 init 代码能跑、钩子生效）
D:\python\python.exe smoke_harness.py build\ddlc

# 5) 部署到**副本**（Steam 目录会被拒绝）
D:\python\python.exe renpy_a11y_gen.py --game "<副本根>" --out build\x --apply

# 6) 用户真跑过一次后，检查八条启动证据
D:\python\python.exe check_boot.py "<副本根>" --json boot.json
```

## 产物形状

```
build\<slug>\
  game\a11y_platform\00_contract.rpy   # 状态/日志/文本清洗/兼容层  (init -1850)
                     01_speech.rpy     # 语音后端链              (init -1800)
                     02_dialogue.rpy   # say filter → 朗读台词    (init -1700)
                     03_menu.rpy       # 选项/主菜单整体播报       (init -1600)
                     04_focus.rpy      # 焦点轮询 + 心跳          (init -1500)
                     05_keys.rpy       # F5/F8/F9/F10/F11      (init  1200)
  a11y_say.vbs                          # 无 NVDA 时的 SAPI 兜底
  README-A11Y.txt  uninstall.ps1  a11y_manifest.json  deploy_plan.json
```

## 按键

| 键 | 作用 |
|---|---|
| F5 | 重读上一句 |
| F8 | 开/关朗读（也可在游戏根放 `a11y_off.flag`） |
| F9 | 朗读当前屏幕 |
| F10 | 切换语音后端（NVDA / SAPI / 日志） |
| F11 | 写一条 `[A11y快照]` 到日志 |

## 红线

- 不修改 `.rpa`、不删游戏文件、不改游戏行为（say filter 原样返回）。
- `--apply` 拒绝 `steamapps` 路径与已存在 `a11y_*` 目录的游戏。
- 覆盖层只含代码/配置，不含任何游戏文本、图片、音频。
