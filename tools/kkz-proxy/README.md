# kkz-proxy —— KiriKiri 插件代理安装器（原型 v1）

把游戏 `plugin\` 下的一个插件 DLL 换成代理 DLL（原 DLL 改名为 `<name>_orig.dll`），
并把原件备份到**游戏目录之外**，带逐文件 SHA256 快照、manifest 与一键回滚。

> 状态：**原型**。已在沙箱目录实测 10 项（T1–T10，见 `research/39-kirkiri-proxy2/REPORT.md` §6），
> **尚未在真实游戏目录运行过**。仅供自用/研究，不随游戏分发。

## 用法

```powershell
# 1) 预演：只打印计划，不碰任何文件
.\install.ps1 -GameDir 'D:\Steam\steamapps\common\SenrenBanka' `
              -TargetDll 'plugin\menu.dll' `
              -ProxyDll  'D:\...\kkr_proxy2.dll' -DryRun

# 2) 安装（会备份原件、改名、放代理、写 manifest）
.\install.ps1 -GameDir 'D:\Steam\steamapps\common\SenrenBanka' `
              -TargetDll 'plugin\menu.dll' `
              -ProxyDll  'D:\...\kkr_proxy2.dll' -ProxyVersion 0.1.0.0

# 3) 安装后等 marker（可选，会阻塞最多 N 秒，需你手动启动游戏）
.\install.ps1 ... -MarkerTimeoutSec 90 -MarkerName 'kkr_proxy2_probe.txt'

# 4) 回滚（默认从 %LOCALAPPDATA%\kkz-proxy-backup 找 manifest）
.\uninstall.ps1 -GameDir 'D:\Steam\steamapps\common\SenrenBanka' -TargetDll 'plugin\menu.dll'

# 5) 回滚并清理备份
.\uninstall.ps1 ... -PurgeBackup
```

## 参数

| 参数 | 说明 |
|---|---|
| `-GameDir` | 游戏根目录（必须含 `-ExeName` 指定的 exe） |
| `-TargetDll` | 要替换的 DLL，相对 `-GameDir` 或绝对路径 |
| `-ProxyDll` | 代理 DLL（安装模式必填） |
| `-BackupDir` | 备份根目录，默认 `%LOCALAPPDATA%\kkz-proxy-backup` |
| `-DryRun` | 只打印计划 |
| `-Rollback` | 回滚模式（也可用 `uninstall.ps1`） |
| `-ProxyVersion` | 写进 manifest 的代理版本号 |
| `-MarkerTimeoutSec` | >0 时轮询 marker 文件，出现即打印前 12 行 |
| `-ExeName` | 进程名/主 exe，默认 `SenrenBanka.exe` |

## 安全校验（不满足就 `KKZ_INSTALL_FAIL`，不落盘）

1. `GameDir` 下存在主 exe；
2. 目标 DLL 存在；
3. 目标位于 `plugin\` 目录 **或** 被 `Plugins.link` 引用；
4. 同名 `<stem>_orig.dll` 不存在（防重复安装）；
5. 游戏进程未运行；
6. 备份后复核 SHA256；
7. 回滚前复核"当前目标哈希 == manifest 里记录的代理哈希"（防止 Steam 更新覆盖后把别人的文件当代理删掉）；
8. 回滚后复核"还原文件哈希 == manifest 里的原件哈希"。

## 产物

```
<BackupDir>\<stem>-<gameDir>\manifest.json          # schemaVersion=1
<BackupDir>\<stem>-<gameDir>\snapshot-before.json   # 逐文件 name/size/sha256（<20MB 算哈希）
<BackupDir>\<stem>-<gameDir>\<name>.<hash16>.bak    # 原件备份
```

manifest 字段：`schemaVersion / installedAtUtc / tool / gameDir / target{path,relative,sha256Before,sizeBefore} /
original{name,sha256,size,backupPath} / renamedInGame{name,path} / proxy{sourcePath,name,sha256,version} /
snapshot{path,fileCount,sha256OfList} / validation{inPluginDir,inPluginsLink,exeName}`。

## 边界与已知限制（务必读）

- **不做 Steam DRM 绕过**：只替换游戏自己会加载的插件；启动仍走 `steam.exe -applaunch`。
- **Steam 更新会覆盖**：游戏更新可能重写 `plugin\*.dll`。更新后先 `-Rollback`（大概率失败，因为哈希已变），
  再手动确认；manifest 里的备份路径仍可用于人工还原。
- **杀软/EDR**：代理 DLL 是未签名二进制，可能被拦截；替换后先看引擎是否加载（marker）、再看模块表。
- **一次只动一个 DLL**：多插件替换请逐个安装/回滚，避免 manifest 互相覆盖。
- **不校验代理 ABI**：安装器不知道代理导出什么；请先 `dumpbin /exports` 确认导出名与原件一致
  （KiriKiri 一般只有 `V2Link` / `V2Unlink`，个别有 `GetModuleThreadModel`）。
- **不碰存档/data.xp3**：只动 `-TargetDll` 一个文件。
- **仅自用**：不要把你改过的游戏目录打包分发；代理源码/二进制是否分发按各作品许可决定。
- **务必保留备份**：`%LOCALAPPDATA%\kkz-proxy-backup` 不要随手清；`-PurgeBackup` 只在确认游戏正常后使用。
- 依赖 Windows PowerShell 5.1 即可（无第三方模块）；脚本必须保存为 **UTF-8 with BOM**，否则 5.1 会把中文按 ANSI 解析导致语法错误。`Get-RelPath` 对 5.1 做了 Uri 回退。
