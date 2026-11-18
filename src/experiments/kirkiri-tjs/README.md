# kirkiri-tjs：KiriKiri/KAG3 TPM 最小实验代码

这是 task-41 的研究实验，不是产品代码。只验证三件事：

1. `plugin\*.tpm` 是否在 `startup.tjs` 之前自动加载；
2. TPM 里执行 TJS，能否包装 KAG3 `MessageLayer` 实例拿到显示文本；
3. TPM 创建的原生方法对象能否被 TJS 调用。

## 构建

需要 VS2022 的 x86 工具链（`vcvars32.bat`）。默认路径 `D:\VS2022\VC\Auxiliary\Build\vcvars32.bat`，可用环境变量 `VCVARS` 覆盖。

```cmd
cd src\experiments\kirkiri-tjs
build.cmd
```

产物是 `gal_a11y.dll`。KiriKiri 只自动加载扩展名 `.tpm` 的文件，因此安装时要改名：

```powershell
Copy-Item gal_a11y.dll <game>\plugin\gal_a11y.tpm
```

64 位引擎需要 x64 构建并放 `plugin64\`；本次**没有**验证 x64。

## 依赖

- `tjsCommHead.h`、`tjsInterface.h` 来自 krkrz 1.4.0r2 的 tjs2 头（实验目录 `_kkr-lab\engine-src\tjs2`）。
- 不链接 `tjs2.lib`，所有 TJS ABI 用 POD 手写（原因见 `gal_a11y.cpp` 注释）。
- 只使用 Windows 自带库（`user32.lib` 等）。

## 运行

把官方 krkrz 1.4.0.8 解压到实验目录，放 KAG3 的 `data\`，再放 `plugin\gal_a11y.tpm`：

```powershell
$p = [System.Diagnostics.Process]::Start('<lab>\tvpwin32.exe')
Start-Sleep -Seconds 9
if(!$p.HasExited){ $p.Kill() }
Get-Content <lab>\gal_a11y.log
```

预期关键行：

```
BOOT: V2Link TJS ready, typeof(global.kag)=undefined
HOOK_INSTALLED:4 of 4
CLEAR_FLUSH:...
```

完整复现与证据见 `research/34-kirkiri-design/evidence/README.md`，设计结论见 `research/34-kirkiri-design/REPORT.md`。

## 边界

- 实验只写日志，不调用 Tolk/NVDA/SAPI，不证明能出声。
- 只测 1.4.0.8 + KAG3 3.32 + x86；没有测 1.2.0.3、64 位、商业作。
- 日志会包含测试台词；产品化时不得默认把台词写盘。
