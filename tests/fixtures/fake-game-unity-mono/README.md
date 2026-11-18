# fake-game-unity-mono —— 合成「Unity Mono Galgame」夹具

这是 **GalA11y Forge 测试用合成样本，不是真实游戏**。它只模拟真实 Unity Mono
游戏在磁盘上的关键文件形态，供「引擎识别 / 静态分析 / 生成器 / E2E」离线测试使用。

- 不含任何商业游戏资源、台词、图片、音频；
- 不可运行、不可注入、不可安装；**不要**把它当游戏启动，也不要指向真实游戏目录；
- 所有二进制由 [`make-fixture.ps1`](../make-fixture.ps1) 在本机用 dotnet 编译生成，
  默认不提交（见本目录 `.gitignore`）。

## 目录内容

| 路径 | 作用 |
|---|---|
| `5omeday.exe` | net8.0 空 Main 控制台程序；`5omeday_Data` 与它同名，供入口选择器配对 |
| `5omeday_Data/Managed/Assembly-CSharp.dll` | netstandard2.1 类库改名；含 `FakeDialogueManager`、`UnityEngine.UI.Text`、`TMPro.TMP_Text` 存根 |
| `5omeday_Data/globalgamemanagers` | 合成二进制，前 512 字节含 `2021.3.16f1`，供版本识别正则命中 |
| `UnityPlayer.dll` | 有效 PE 占位（复用宿主 exe 字节），不是真实 Unity 运行库 |
| `MonoBleedingEdge/EmbedRuntime/` | 目录占位，模拟 Mono 运行时布局 |
| `fixture-manifest.json` | 生成物清单（大小 + SHA256 + 生成方式），便于核对夹具是否被改动 |

`Assembly-CSharp.dll` 里的两个存根类型只用来制造 **`set_text` 调用点**：
`FakeDialogueManager.ShowText/ShowChoice` 会写入 `_text.text` / `_tmp.text`。
C# 属性赋值编译为 `callvirt set_text`，正好命中 Unity Mono 静态分析器的汇点规则。
这两个类型没有真实 Unity 实现，**不能**加载进 Unity，也不能拿来注入。

`FakeDialogueScript` 里的两句文本是自造哨兵（`FORGE_FIXTURE_LINE_DO_NOT_SHIP_5OMEDAY`
和一句中文），用于 E2E 断言「生成的补丁工程不得包含游戏原文」。它们不是任何游戏的台词。

## 重建

```powershell
# 在仓库根目录执行；可重复运行
pwsh -NoProfile -File tests/fixtures/make-fixture.ps1

# 指定 dotnet / 输出目录 / 保留编译临时目录排查问题
pwsh -NoProfile -File tests/fixtures/make-fixture.ps1 `
  -DotnetPath D:\dotnet\dotnet.exe `
  -OutDir D:\temp\fake-game `
  -KeepWork
```

脚本会优先单文件发布 `5omeday.exe`；若本机 SDK 不支持则回退普通 apphost 构建，
此时 `5omeday.exe` 缺少同目录的 `5omeday.dll`，只保证「文件存在」，不保证能启动。
两种模式都满足检测/分析测试需要；`fixture-manifest.json` 的 `publishMode` 会注明。

## 已知限制

- `globalgamemanagers` 不是真实的 Unity 序列化文件，只保证版本字符串在前 512 字节内；
- `UnityPlayer.dll` 是 PE 占位，没有版本资源，不应用于版本回退测试；
- 夹具版本固定为 `2021.3.16f1`，改版本需改脚本（并同步 `fixture-manifest.json`）；
- 本夹具只覆盖 Unity Mono 识别/静态分析的「最小形态」，不覆盖 IL2CPP、真实资源解析。

## 谁在用

- `tests/Forge.E2E`：默认 Skip，`FORGE_E2E=1` 时以本夹具跑 detect/analyze/plan/generate/install。
- 修改检测或分析规则后，请先重建夹具，再跑对应测试；夹具改动应同步更新本文档。
