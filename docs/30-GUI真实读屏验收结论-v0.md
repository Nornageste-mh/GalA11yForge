# docs/30 GUI 真实读屏验收结论（task-43 / W2-U，2026-11-15）

- 对象：`releases/v0.2.2/gui/Forge.Gui.exe`（运行中 PID 22072）
- 读屏：NVDA 2026.2（中文界面，vocalizer_expressive2 / Ting-Ting，rate 295，volume 80）
- 方法：NVDA 朗读查看器（路线 A）+ WASAPI loopback 录音与逐进程音频归属（路线 B）+ UIA 树/源码交叉核对
- 结论级别：**部分验证（偏正面）**——软件层面“NVDA 已合成并送出声”成立；用户耳朵是否听见未由软件证明

## 一、结论（[实测]）

1. 真的出声：两轮 loopback 录音都录到与按键事件一一对应的语音段（25.02s 录到 5 段、34.07s 录到 6 段）；每段起点在对应按键/焦点事件后 0.31–2.07s，空档为数字静音。
2. 声音来自 nvda.exe：34s 内每 0.3s 采样逐进程音频会话，nvda 会话 59 个采样峰值 >0.001，最大 0.7659 与整段录音样本峰值相等；Chrome/Steam/QQ/ChatGPT/VSCode/VLC/争渡等其他会话峰值始终 0.0。
3. 控件名与帮助文本进了 NVDA 语音队列：查看器记录到「游戏目录 编辑框 要处理的 Galgame 安装目录……Alt+1 空白」「浏览游戏目录 按钮……」「工作区 编辑框……」「执行结果 编辑框……【环境自检】成功」等，且与录音时间段对得上。
4. 0.2.2 的 P1 修复有效：动态「提示/最近一次结果/执行结果」由 UIA Text/Edit 的 Name/Value 提供（不再固定 AccessibleName），查看器记录到忙碌提示、最近输出、成功结论三行 → 动态文字会被 NVDA 自动念出。
5. Alt+O 与旧任务卡不一致是任务卡笔误：源码绑定 `O`（`src/Forge.Gui.Core/ForgeShortcuts.cs:14`），按钮文字「一键(&O)」（`src/Forge.Gui/MainForm.cs:286`）；`Alt+Q` 无绑定。本轮未改产品代码。

## 二、未验证（必须如实写）

1. 没有证明声音进入用户耳朵（只证明进了默认输出设备的数字混音）。
2. NVDA+Down 连续朗读在本机没工作（“没有子对象”），625 字符执行结果未被逐行读完。
3. Alt+O / Alt+3/4/5 / Ctrl+Shift+R / Esc 未实际按键验证。
4. 「最近输出」会把 120 字符原始 JSON 念出来，读屏体验偏吵（既有设计，未改）。

## 三、证据

- `research/37-gui-nvda-audio/REPORT.md`（完整报告）
- `evidence/06-speechviewer-session2.png`（朗读查看器悬浮在 GUI 上）
- `evidence/05-speechviewer-open.png`（查看器由 NVDA 菜单打开）
- `evidence/audio/nvda-speech-loopback-2.wav`（34s 录音）+ `.analysis.json` + `segments.csv` + `waveform-timeline.png`
- `_gui-audio/`（LoopbackRecorder .NET 8、RunAudioSession.ps1、AnalyzeSession.py、MakeSegmentsCsv.py、DrawTimeline.py、SpeechCapture.ps1）

## 四、给用户的人工复核清单（发布说明/邮件用）

戴上耳机、确认系统音量，启动 GUI 后依次：
1. Tab 到「游戏目录」→ 应听到「游戏目录 编辑框 要处理的 Galgame 安装目录……Alt+1 空白」。
2. Tab 到「浏览…」→ 应听到「浏览游戏目录 按钮」。
3. 按 Alt+V（环境自检）→ 忙碌时听到「正在执行……」，完成后听到结果摘要（含「成功」）。
4. 按 Alt+D（识别）→ 听到引擎识别结论。
5. 用 Alt+1..5 在输入框间跳转 → 每次都能听到字段名与当前值。
6. 若听不到：先查耳机/音量；用 `evidence/audio/nvda-speech-loopback-2.wav` 确认机器确实有输出。

## 五、对 0.2.3 的动作

- 把本报告作为「GUI 可被 NVDA 念出」的证据写入发布说明（标注“部分验证”）。
- 修正任务卡笔误：Alt+Q→Alt+O（本轮已确认）。
- 后续（0.2.3+）：连续朗读（NVDA+Down）适配（结果区改为可逐行聚焦的列表或设置 UIA 文本模式）；「最近输出」只念摘要不念原始 JSON（低频可选项）。
- 不做：为“证明用户耳朵”去录人声/声纹（隐私），保持数字混音+查看器双证。

## 补充：给用户的人工验收清单

- 已整理成 10 分钟版清单：docs/32-GUI人工验收清单.md（含记录表与“不用当 bug 报”的已知限制）。
- 用户回来后可自行跑一遍，把“过/不过/备注”发回邮箱；阻断项按 P0 处理。