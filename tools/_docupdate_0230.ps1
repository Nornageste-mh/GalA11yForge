$ErrorActionPreference = 'Stop'
Set-Location 'D:\DSHWorkSpace'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Edit([string]$path, [scriptblock]$mutate) {
    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $new = & $mutate $text
    if ($new -eq $text) { Write-Output "NOCHANGE $path" } else { [IO.File]::WriteAllText($path, $new, $utf8); Write-Output "EDITED $path" }
}

Edit 'docs\05-决策确认与实施计划-v2.md' { param($t)
    $old = '加载器预检并入；**590 测试全绿**。'
    $new = $old + "`n- v0.2.3.0：已发布（releases/v0.2.3）——IL2CPP 构建/安装链（BepInEx6+Il2CppInterop，真机连续 19 行命中、回滚哈希不变）+ Mono 基线假失败修复（interop/cache/patchers/日志不进基线）+ GUI 真实读屏部分验证（loopback 证明 NVDA 发声）+ KKZ 路线结论（1.2.0.3 不自动加载 tpm，不提供自动安装）；**607 测试全绿**。"
    $t.Replace($old, $new)
}

Edit 'docs\07-验收清单.md' { param($t)
    $t = $t.Replace('## 5. 当前状态（2026-11-14 23:50，0.2.2.0 发布后）', '## 5. 当前状态（2026-11-15 02:05，0.2.3.0 发布后）')
    $t = $t.Replace("**590 通过 0 失败**（Core 77、Verify 64、Detection 30、Generators 55、Ren'Py 34、UnityMono 31、Build 58、Gui 138、Il2Cpp 48、Ghidra 55）", "**607 通过 0 失败**（Core 80、Verify 64、Detection 30、Generators 55、Ren'Py 34、UnityMono 31、Build 72、Gui 138、Il2Cpp 48、Ghidra 55）")
    $t.Replace('IL2CPP 真机 interop/朗读、GUI 的 NVDA 播报验收、3 选项/多语言选项覆盖', 'IL2CPP 已接安装/回滚并真机 19 行（朗读逐条未比对）、GUI 已做 loopback 部分验证、KKZ 最小朗读闭环（task-47）、3 选项/多语言选项覆盖')
}

Edit 'docs\12-集成计划-v0.1.md' { param($t)
    $t = $t.Replace("**590 通过 0 失败 0 跳过**（Core 77 + Verify 64 + Detection 30 + Generators 55 + Ren'Py 34 + UnityMono 31 + Build 58 + Gui 138 + Il2Cpp 48 + Ghidra 55）", "**607 通过 0 失败 0 跳过**（Core 80 + Verify 64 + Detection 30 + Generators 55 + Ren'Py 34 + UnityMono 31 + Build 72 + Gui 138 + Il2Cpp 48 + Ghidra 55）")
    $t.Replace('全量基线见上（590）', '全量基线见上（607）')
}

Edit 'docs\18-使用指南-v0.md' { param($t)
    $t = $t.Replace('# GalA11y Forge 使用指南（v0.2.2 开发版，2026-11-14）', '# GalA11y Forge 使用指南（v0.2.3 开发版，2026-11-15）')
    $t = $t.Replace('当前是 0.2.2.0，不设 1.0。', '当前是 0.2.3.0，不设 1.0。')
    $t = $t.Replace('**Unity IL2CPP** 能识别/分析/生成工程（安装与朗读未接）', '**Unity IL2CPP** 已接安装/回滚（《硅心》真机连续 19 行命中，朗读未逐条比对）')
    $t = $t.Replace('发布版在 `releases/v0.2.2/app/forge.exe`', '发布版在 `releases/v0.2.3/app/forge.exe`')
    $t = $t.Replace('## 7. 已知限制（0.2.2）', '## 7. 已知限制（0.2.3）')
    $t = $t.Replace('Ren''Py 覆盖层安装已接 CLI（0.2.2）；GUI 还没做 NVDA 正式“听到声音”验收；Ghidra 需自装（BYO）；外部读屏器仍是实验原型。', 'IL2CPP 已接安装/回滚（0.2.3），但只验证 1 款游戏、1 个 BepInEx 版本；GUI 只做了 loopback 部分验证（未人耳验收）；Ghidra 需自装（BYO）；外部读屏器是实验原型，KKZ 不提供自动安装。')
    $t = $t.Replace('- 0.2.2.0：已发布——Ren''Py 覆盖层安装/回滚接 CLI、GUI 读屏 P1 修复、`--option`、Ghidra 高级分析（`--advanced`）、IL2CPP P0/P2 修复、加载器预检并入；590 测试全绿。', "- 0.2.2.0：已发布——Ren'Py 覆盖层安装/回滚接 CLI、GUI 读屏 P1 修复、`--option`、Ghidra 高级分析（`--advanced`）、IL2CPP P0/P2 修复、加载器预检并入；590 测试全绿。`n- 0.2.3.0：已发布——IL2CPP 构建/安装链、Mono 基线假失败修复、GUI loopback 部分验证、KKZ 路线结论；607 测试全绿。")
}

Edit 'docs\19-第二款游戏失败分析与修复.md' { param($t)
    $t.Replace('（当时快照；0.2.2.0 发布时全量为 590）', '（当时快照；0.2.2.0 发布时全量为 590，0.2.3.0 为 607）')
}

Edit 'docs\27-0.2.3-计划.md' { param($t)
    $t = $t.Replace('- 前置状态：0.2.2.0 已发布（releases/v0.2.2；590 测试全绿）', '- 前置状态：0.2.2.0 已发布（releases/v0.2.2；590 测试全绿）；本版已发布 0.2.3.0（releases/v0.2.3；607 测试全绿）')
    $t.Replace('当前 590，本版预计 >600', '当前 607')
}
