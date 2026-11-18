# KKZ 实验性适配器 v1（task-49 / W2-Y）

> **实验性 / 开发者预览。** 这是一个"插件 DLL 代理"模板，用来在 **不支持 `plugin\*.tpm` 自动加载**的
> 旧版吉里吉里 Z（KiriKiri Z，下称 KKZ）上，以只读方式取出当前台词并落盘（可选朗读）。
> 只面向已经在场的人做研究用，**不是给普通玩家的成品**，也不会被主 CLI 自动调用。

- 目标：KKZ **1.2.0.3**（SenrenBanka 那代）——1.4.0.8 有 `.tpm` 自动加载，不需要本适配器。
- 依据：`research/40-kkz-mvp/REPORT.md`（task-47 实测拿到 8 行真实台词）、`docs/35-KKZ适配器设计-v0.md`（设计/安装/回滚）。
- 红线：不改 `data.xp3`、不重分发任何游戏文件、不做 DRM 相关操作、不默认指向 Steam 目录。

## 目录

```
src/experiments/kkz-adapter/
  src/kkz_adapter.cpp        代理模板（导出 V2Link/V2Unlink/GetModuleThreadModel）
  src/kkz_adapter.def        导出表（只有三符号；必须保持纯 ASCII）
  build.ps1                  构建 + PE 校验 + 出 dist/SHA256SUMS.txt
  build.cmd                  薄封装（优先 pwsh，退回 powershell）
  tools/install.ps1          安装（默认 dry-run，-Apply 才写盘）
  tools/uninstall.ps1        体检 / 回滚（默认 dry-run）
  tools/kkz-adapter.example.ini  运行期配置样板
  build/                     中间产物（build.log、dumpbin.txt、kkz-adapter.map、生成的配置头）
  dist/                      发布副本 + SHA256SUMS.txt
```

## 一、构建（x86 必选）

```powershell
cd src\experiments\kkz-adapter
pwsh -File .\build.ps1 -Clean                 # 或用 build.cmd
pwsh -File .\build.ps1 -EnableTts             # 带进程内 SAPI 朗读
pwsh -File .\build.ps1 -PollMs 150 -RequireExeVersion '1.2.*'
```

`build.ps1` 会：生成 `build\kkz_build_config.h`（编译期默认值）→ 调 `cl`/`link` →
用 `dumpbin` 与 `pefile` **双重**校验"machine=x86 + 三个导出齐全" → 打印 SHA256 →
复制到 `dist\kkz-adapter-<版本>-x86.dll` 并写 `dist\SHA256SUMS.txt`。

**本机实测（2026-11-15，MSVC 14.44 / VS2022 BuildTools）**

```
命令:  pwsh -File .\build.ps1 -Clean
产物:  dist\kkz-adapter-0.1.0.0-x86.dll   102,400 B
SHA256: 31C2661BB0112E35EEBBEE090CBD0213E1502942FE4904FF99CC828E91208225
校验:  machine=0x14c (x86)  exports=GetModuleThreadModel,V2Link,V2Unlink
       cl /EHa /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8  →  link /DLL /MACHINE:X86 /Brepro /DEF
可复现: link 带 /Brepro（否则 MSVC 会写入时间戳，每次哈希都不同）；同源码连跑两次哈希一致 [实测]
参考:  同源码 -EnableTts 也能构建（SAPI 链接通过），产物 SHA256 B65A433AF70A2239E5C76768333D5B16125FFA28259866211E9AC78284C47A90
```

工具链说明：

| 工具链 | 状态 | 备注 |
|---|---|---|
| MSVC（`vcvarsall.bat x86`） | **[实测] 可用**，首选 | 与 task-47 验证过的 `/EHa`+`/MT` 一致 |
| w64devkit `g++ -m32` | **[实测] 本机不可用** | 本机 w64devkit 是纯 64 位发行版（无 i686 库/`lib32`），`-m32` 能编译但链接报 `cannot find -lkernel32`；`build.ps1` 会直接给出这条提示 |

> `/EHa` 不能省：task-46 实测，TJS 抛出的 C++ 异常若没有 `/EHa`，会直接终止游戏进程。

## 二、编译期参数（`build.ps1` 开关 → `KKZ_CFG_*`）

| 开关 | 默认 | 含义 |
|---|---|---|
| `-TargetDll` | `getSample.dll` | 被代理的插件文件名，必须等于代理 DLL 自己的文件名 |
| `-OrigSuffix` | `_orig` | 原 DLL 改名后缀 |
| `-PollMs` | `200` | 只读轮询间隔（50–5000，运行期可覆盖） |
| `-StabilizeMs` | `200` | 同一文本连续不变的稳定期（滤逐字动画中间态） |
| `-MinChars` | `2` | 小于该长度的行丢弃 |
| `-MaxRows` | `0` | 落盘行数上限，0=不限 |
| `-OutputFile` | `%TEMP%\kkz_adapter_capture.txt` | 台词落盘路径 |
| `-LogFile` | `%TEMP%\kkz_adapter_probe.txt` | 健康检查日志 |
| `-EnableTts` | 关 | 编译进进程内 SAPI 朗读（运行期 `EnableTts=1` 才真的读） |
| `-RequireExe` / `-RequireExeVersion` | 空 | 宿主 exe 名 / 版本前缀门（如 `1.2.*`） |
| `-RequireOrigSha256` | 空 | 原 DLL 哈希门 |
| `-FailMode` | `degraded` | 安全门不过时：只转发原插件 / 直接失败 |
| `-Version` | `0.1.0.0` | 写进日志与 dist 文件名 |

> **为什么用生成的 `build\kkz_build_config.h` 而不是 `/D`？** 实测 `cl` 拒绝含 `%` 的 `/D` 值
> （`error D8038`），而默认输出路径正是 `%TEMP%\...`。走头文件可原样保留 `%`、空格、反斜杠、中文。

## 三、运行期配置（`kkz-adapter.ini`，与代理 DLL 同目录；`KKZ_ADAPTER_INI` 可覆盖）

键名与默认值见 `tools/kkz-adapter.example.ini`。要点：

- `[adapter]`：`TargetDll` / `OrigSuffix` / `PollMs` / `StabilizeMs` / `MinChars` / `MaxRows` /
  `OutputFile` / `LogFile` / `EnableTts` / `RequireExe` / `RequireExeVersion` / `RequireOrigSha256` / `FailMode`
- `[text]`：`ExprName` / `ExprText` / `ExprPlain`（默认三条 `global.kag.historyLayer.currentInfo.*`）
- **含中文的值（如游戏路径）必须把整个 ini 存成 UTF-16LE**，否则 `GetPrivateProfileStringW` 读成乱码；
  `install.ps1` 直接用 `-Encoding Unicode` 写。

## 四、产物格式

`kkz_adapter_capture.txt`（UTF-8，一行一条，Tab 分隔）：

```
<序号>  stable  <name>  <text>  <plaintext>
1	stable	（画面上有、但 1.2.0.3 上通常为空）	（同上）	真实台词
```

`kkz_adapter_probe.txt`（UTF-8，`key=value`）：先是一段启动信息（`version`/`self`/`target_dll`/
`host_exe`/`host_exe_version`/`orig_sha256`），然后是安全门结果 `gate_module_name_ok`、
`gate_exe_name_ok`、`gate_exe_version_ok`、`gate_orig_exists`、`gate_orig_hash_ok`、`gate_all_ok`，
注入结果 `orig_loaded`/`forwarded`/`forward_ret`/`inject`，运行期 `Q_total`/`tjs_ready`/`pump=fail err`/
`ticks`/`rows`，以及每落一行的 `row=N name_len= text_len= plain_len=`；启用 TTS 时还有 `tts_voice`/
`tts_speak_ok_no`/`tts_speak_fail_hr`。

**判断"真的工作了"只需三个值**：`gate_all_ok=1`、`inject=1`、`rows` 递增。

## 五、安全门与失败行为

不满足任一条 → **不注入**（`inject=0`），但仍 `LoadLibrary` 原 DLL 并转发，游戏照常运行：

1. 本模块文件名 == `TargetDll`（防误改名安装）
2. 宿主 exe 名/版本匹配 `RequireExe`/`RequireExeVersion`（版本支持 `1.2.*` 前缀）
3. 原 DLL 存在；`RequireOrigSha256` 非空时必须一致（CryptoAPI SHA-256）

`FailMode=strict` 时改为直接返回失败（让引擎认为插件加载失败）。默认 `degraded`。

## 六、安装 / 回滚

见 `docs/35-KKZ适配器设计-v0.md` 第五节。简述：

```powershell
.\tools\install.ps1 -GameDir 'D:\Games\某游戏' -GameExe '某游戏.exe'            # 默认 dry-run
.\tools\install.ps1 -GameDir 'D:\Games\某游戏' -GameExe '某游戏.exe' -Apply     # 真装
.\tools\uninstall.ps1 -GameDir 'D:\Games\某游戏'                               # 体检
.\tools\uninstall.ps1 -GameDir 'D:\Games\某游戏' -Rollback -Apply              # 回滚
```

脚本**没有任何默认游戏路径**，`-GameDir` 与 `-GameExe` 必须显式给，写盘必须显式 `-Apply`。
清单（含全部哈希）落在 `%LOCALAPPDATA%\kkz-adapter\<指纹>.json`。

## 七、坑位清单（都是实测踩出来的，改代码前先读）

1. **TJS 侧禁止 `new Object()` / `new Array()`**：task-47 直接 `0xC0000409`（`___report_gsfailure`）。
   本模板**完全不往 TJS 注入脚本/对象**，只对属性做只读求值；状态机/缓冲/去重/落盘/朗读全在原生侧。
2. **TJS 侧禁止 `typeof(对象)`**：会抛异常（`typeof(1)` 正常）。存在性判定一律 `x == void`。
3. **`wsprintfW(buf, L"前缀=", 值)` 会静默丢值**（格式串里没有转换符）。本文件不用 `wsprintf` 拼日志，
   全部走 `Append*()` 手工拼接。
4. **V2Link 里立刻求值太早**（KAG 未加载）。V2Link 只取函数指针 + `CreateWindowExW(HWND_MESSAGE)` + `SetTimer`，
   所有求值都在第一个 `WM_TIMER` 之后（此时 KAG 已就绪，实测 `wtid == cur_tid`）。
5. **逐字显示有中间态**（空行/重复行/半句）→ 稳定化 + 最小长度 + 连续去重，三件套都在落盘前做。
6. **`.def` 不能含非 ASCII**：中文注释会让 `link` 报 `warning LNK4017: V2Link 语句不支持目标平台；已忽略`
   并**静默丢弃全部导出**——产物"构建成功"但游戏加载后什么都没发生。`build.ps1` 会检测这条警告并提示。
7. **`.ps1` 必须存成 UTF-8 with BOM**：Windows PowerShell 5.1 按 ANSI 读无 BOM 的 UTF-8，中文注释会
   把字符串截断成语法错误（`The string is missing the terminator`）。本目录所有 `.ps1` 已带 BOM；
   `build.cmd` 优先调用 `pwsh`，没有才退回 `powershell`。
   **注意**：改完 `.ps1` 要确认 BOM 还在——有的编辑器/工具会悄悄把它去掉（本目录就中过一次）。
   另外 5.1 不认 `New-Object System.Collections.Generic.List[string]` 这种裸泛型写法，
   脚本里用 `System.Collections.ArrayList` 或字符串拼接代替。
8. **`cl` 的 `/D` 值不能含 `%`**（`D8038`）→ 配置走生成头文件。
9. **MSVC 默认不可复现**：不写 `/Brepro` 时 `link` 会把时间戳写进 PE 头，同源码两次构建 SHA256 不同
   （本目录第一版就踩了：`BA9D…` 与重建后的值不等）。`build.ps1` 已加 `/Brepro`，同源码两次构建哈希一致。

## 八、未验证 / 已知限制

- 真实游戏目录**一次都没跑过**（task-49 明确不启动、不改任何游戏目录）；安装/回滚脚本只在
  沙箱假目录（`_scratch\kkz-adapter-sandbox`）走过全流程：装前哈希不符拒绝、重复安装拒绝、
  回滚后哈希回到原值、卸载后目录只剩原文件。
- 进程内 SAPI 只验证了"能编译、能链接、能被调用"，**没有验证真的发出声音**（task-47 同样未验证）。
- 只覆盖"纯 V2 插件"（`getSample.dll` 这类）。`menu.dll` 等 COM 插件需要单独评估，本模板不导出
  `DllGetClassObject`，不要拿它去替换。
- 文本只有日文原文（`plaintext`）；中文译文取不到（1.2.0.3 上 `.disp`/`.text` 整轮为空）。
