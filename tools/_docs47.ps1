$ErrorActionPreference = 'Stop'
Set-Location 'D:\DSHWorkSpace'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$p = 'docs\28-KiriKiri路线结论-v0.md'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
if ($t -notmatch 'R26 最小朗读闭环') {
    $t += @"

---

## 2026-11-15 02:25 R26 最小朗读闭环结论（task-47，《千恋＊万花》1.2.0.3）

- **能进局**：getSample.dll 代理在场时，Steam 启动 → 主菜单 → 点「新遊戲」→ CHAPTER 1-1 正常；代理用 message-only 窗口 + SetTimer(400ms) 在引擎主线程持续工作。
- **能拿到真实台词**：读取 `global.kag.historyLayer.currentInfo.plaintext`，一次进局拿到 8 行日文原文（`「お客さんってば」`、`「あっ、はい。なんですか？」`、`飛び起きた俺は、前に座っている運転手に返事をする。` 等）；第 8 行与截图画面逐字一致。
- **但只有日文原文**：`.disp`（说话人）与 `.text`（预期译文）整轮为空；画面上的中文译文目前取不到 → 这是 KKZ 适配器 v1 的头号缺口。
- **逐字中间态**：轮询会看到重复行/空行（逐字显示造成），v1 必须做稳定化（同句 ~150–250ms 不再变化再朗读）+ 去重 + 最小长度。
- **TTS 可行（外部）**：用捕获的真实台词做外部 SAPI 合成得到 153,672B WAV（约 6 秒）。进程内 SAPI 已编译并稳定加载，但未实际发声 `[未验证]`。
- **回滚干净**：plugin 59 文件 + 根 33 文件逐文件 SHA256 与实验前全等；savedata 未变；临时文件 0 残留。
- **硬约束**：TJS 侧 `new Object()` 触发 `0xC0000409`（/GS fail-fast）；`typeof(对象)` 抛异常；状态机/缓冲/落盘必须放原生侧。全屏→窗口切换后合成输入（mouse_event/PostMessage/keybd_event）全部失效，只影响自动回归，不影响产品。

**对产品的含义**：KKZ v1 只能定位为「实验性/开发者预览」，不能承诺一键；取文本设计=plaintext + 200ms 稳定化 + 原生侧状态机 + 进程外朗读（代理只落盘/IPC）；安装器必须带哈希校验与回滚。下一步两条并行：R27 定位中文译文源并补 8 行原件证据（task-48），W2-Y 代理模板工程化与安装/回滚设计（task-49）。
"@
    [IO.File]::WriteAllText($p, $t, $utf8)
}

$p = 'docs\27-0.2.3-计划.md'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
if ($t -notmatch 'task-47 完成') {
    $t += @"

## 进度补充（2026-11-15 02:25）

- task-47/R26 KKZ 最小朗读闭环**完成并验收**：进局 + 8 行真实日文台词 + 外部 TTS 验证 + 回滚干净；blocker 是中文译文源（`.text` 为空）。
- task-48/R27 中文译文源定位 + 8 行原件重抓（subagent 3242e46a）进行中。
- task-49/W2-Y KKZ 代理模板工程化 + 安装/回滚设计（subagent a9c9be11）进行中，不碰游戏目录。
- 0.2.4 发布门槛不变；KKZ 是否纳入 extras 等 R27/R28 结论。
"@
    [IO.File]::WriteAllText($p, $t, $utf8)
}

$p = 'docs\23-0.2.2-集成计划.md'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
if ($t -notmatch '02:25') {
    $t += @"

## 2026-11-15 02:25 追加

- task-47/R26 验收完成，结论写入 docs/28「R26 最小朗读闭环」。
- 启动 task-48（R27 译文源 + 原件重抓，subagent 3242e46a）与 task-49（W2-Y 适配器原型，subagent a9c9be11）。
"@
    [IO.File]::WriteAllText($p, $t, $utf8)
}

Write-Output 'docs updated ok'
