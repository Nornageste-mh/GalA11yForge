namespace GalA11yForge.Generators.Templates;

/// <summary>卸载说明：只删自己装的东西，并提供「游戏是否还原」的检查方法。</summary>
internal static class UninstallGuideTemplate
{
    internal const string Text = """
# 卸载说明 — {{DISPLAY}}

卸载只删本插件新增的文件，不碰游戏本体。

## 1. 删除插件

删除整个目录：

```
游戏目录\BepInEx\plugins\{{ASSEMBLY}}\
```

## 2. 删除配置（可选）

```
游戏目录\BepInEx\config\{{GUID}}.cfg
```

## 3. 确认游戏恢复

1. 启动游戏，确认能正常进入、正常游玩。
2. 如果游戏反而起不来：先确认 `winhttp.dll`、`BepInEx\core` 等 BepInEx 文件是否被其它插件依赖；
   本插件只依赖 BepInEx 5 本体。
3. 需要彻底回退时：用 Steam「验证游戏文件完整性」，或按安装前的备份还原 `BepInEx\plugins`。

## 4. 不要做的事

- 不要为了卸载而删除游戏目录里的 `data.xp3`、`Assembly-CSharp.dll` 等原始文件。
- 不要把 `BepInEx` 整个删掉（如果你还有别的插件在用）。
- 不需要改注册表，不需要重启系统。

## 5. 残留检查

卸载后，游戏目录里与本补丁相关的残留应只剩：

- 构建产物（在补丁工程目录的 `bin/`，不在游戏目录）
- `BepInEx\LogOutput.log` 里的历史日志（可留可删）

补丁工程目录本身可以整个删除；它只是源码，不影响游戏。
""";
}
