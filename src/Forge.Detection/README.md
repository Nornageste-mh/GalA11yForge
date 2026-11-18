# Forge.Detection 引擎识别层

把「一个 Galgame 安装目录」识别成引擎 id + 版本 + 位数 + 置信度 + 证据。
本层**只读**文件系统：不启动游戏、不运行可执行文件、不写入目标目录。

## 输入输出

输入：游戏根目录路径（`string`）。
输出：`DetectionResult`

- `Primary`：契约类型 `GalA11yForge.Core.Models.EngineDetection`（family/version/bitness/confidence/evidence）。
- `Candidates`：全部命中规则，按分数降序；含 `Engine/RuleId/Score/Confidence/Evidence/SuppressedBy`。
- 另有 `Verdict`（无/低/中/高）、`IsKnown`、`IsSuspected`、`FilesScanned`、`Truncated`、`Elapsed`。
- `ToJson()` 用 Core 的 `ForgeJson` 序列化 `Primary`；`ToTuples()` 便于 CLI 直接打印候选。

空目录或未知布局 → `family="unknown"`、`confidence=0`，证据列明扫描统计与顶层信号。
目录不存在 → 抛 `DirectoryNotFoundException`（CLI 侧建议映射为 `E_ARG_INVALID`）。

## 用法

```csharp
var detector = new EngineDetector();                 // 加载内置指纹库
DetectionResult result = detector.Detect(gameDir);
Console.WriteLine(result.Family + " " + result.Confidence);
```

## 指纹库

`Fingerprints/engine-fingerprints.json` 由 research 的 `engine-fingerprints.json` 原样复制
（26 引擎 / 26 规则 / 5 组消歧 / 7 种条件原语），同时以嵌入资源 + 输出目录副本两种方式分发。
`FingerprintLoader.LoadDefault()` 优先读磁盘 `Fingerprints/`，否则读嵌入资源。
`Load(path)` / `LoadFromJson(json, label)` 供测试与外部指纹库使用。

校验（失败抛 `FingerprintFormatException`，消息含全部问题最多 20 条，绝不静默降级）：
schema 必须为 `gala11y.engine-fingerprints/1`；引擎 id 唯一、必填字段非空、difficulty 在图例内；
规则 id 唯一、engine 必须存在、score 0..100、`all`/`any` 互斥且非空；每种原语按类型校验参数
（如 magic 的 hex 必须偶数字节）；消歧的 resolve 必须属于 when。未知字段用
`JsonExtensionData` 保留，便于后续版本向前兼容。

## 检测流程

1. `GameDirectoryIndex` 递归索引（确定性排序；跳过 reparse point 防 junction 环；上限 20 万文件）。
2. 逐条规则短路求值 7 种原语：`file_glob`/`file_any`/`dir_exists`/`count`/`magic`/`pe_string`/`text`。
3. 命中按 score 降序；5 组消歧把同组非 resolve 引擎标记 `SuppressedBy`；主结果取首个未抑制引擎。
4. 版本/位数能读就读：Unity 读 `*_Data/globalgamemanagers`（offset 48，前缀正则），
   回退 `UnityPlayer.dll` 的 ProductVersion；Ren'Py 读 `renpy/__init__.py` 的 `version_tuple`
   与 `renpy/vc_version.py`；位数读根目录最大 `*.exe` 的 PE Machine（0x14c=32、0x8664=64）。
   读不到写 `null`/`0`，并在证据中写 `unverified:...`，**不编造**。

证据前缀：`file:` `dir:` `measured:` `rule:` `candidate:` `suppressed:` `scan:` `unverified:` `miss:` `warn:`。

## 与参考实现 detect.py 的差异

- 以 JSON 为准：`condition_types` 有 **7** 种（REPORT 曾写 6 种），7 种全部实现。
- 文件/目录枚举改为确定性排序（参考实现依赖 OS 目录顺序），便于复现与测试。
- 跳过 reparse point（参考实现会把 junction 当普通目录）。
- 目录不存在时抛异常（参考实现静默返回空结果）。
- 保留参考实现的常量上限与短路语义：PE 12MB/8 个、magic 20 个、text 5 个/2MB；
  `pe_string` 同时搜 ASCII 与 UTF-16LE（SenrenBanka.exe 只有 UTF-16LE，实测确认）。

## 已知局限 / 集成风险

- 8 个闭源引擎（如 advhd、siglusengine、alicesoft）目前只有纸面规则，样本未验证。
- KiriKiri Z 依赖 `*.xp3.sig` 存在；去掉 .sig 的发行版会退化为 `kirikiri_2` 或识别失败。
- 本层输出 research 原始 id（`kirikiri_z`、`rm_mv_mz`），与契约示例的 `kirikiriz`、
  `rpgmaker_mv_mz` 不同；可用 `EngineAliasMap.ToContractId/ToResearchId` 转换，最终取舍交 Lead。
- 静态扫描只看布局与文件头，不判断游戏能否运行；分数 < 50 的结果只是「疑似」。
- `unity_mono` 与 `unity_il2cpp` 按 `Managed/` 与 `il2cpp_data/` 区分；两者都不存在的
  Unity 变体（如纯数据包）只会命中 R26（BepInEx）或不命中。

## 测试

```pwsh
D:\dotnet\dotnet.exe test tests\Forge.Detection.Tests\Forge.Detection.Tests.csproj
```

夹具在临时目录构造；3 个本机真实目录用例在目录不存在时跳过（Skip），不会失败。
