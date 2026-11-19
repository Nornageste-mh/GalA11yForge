namespace GalA11yForge.Generators.Templates;

/// <summary>NOTICE：第三方来源与许可，如实写明「谁在内嵌、谁不随附」。</summary>
internal static class NoticeTemplate
{
    internal const string Text = """
# NOTICE — {{DISPLAY}}

本工程由 **Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo）** 生成，生成器版本 {{GENERATOR_VERSION}}。

## 内容边界

本工程只包含代码、配置与接口说明，**不包含**游戏原始资源、台词文本、图片、音频、字体，
也不包含任何从游戏中提取的数据。运行时读取的是用户本机已安装的游戏。

## 内嵌的第三方代码

| 组件 | 来源 | 许可 | 位置 |
|---|---|---|---|
| 语音桥平台层（Speech/Nvda/Sapi/Zdsr/GlobalKeys/Hotkey/KeyEdge/Repeat；按作定制的 UiNav 不随生成物分发）与 A11yHost 契约层 | GalA11y Demo Framework | MIT（Copyright (c) 2026 Nornageste-mh） | `Platform/*.cs` |

内嵌内容的来源指纹（按来源 id 排序后取 SHA-256）：`{{FRAMEWORK_FINGERPRINT}}`。

## 运行时依赖（不随本工程分发）

| 组件 | 许可 | 说明 |
|---|---|---|
| BepInEx 5.4.x | MIT | 用户自行下载安装；本工程只引用其 DLL 编译 |
| HarmonyX | MIT | 随 BepInEx 5 提供 |
| NVDA Controller Client | LGPL-2.1 | 不随附；装了 NVDA 时由系统提供 |
| Tolk | LGPL-3.0 | 不随附；需要时由用户自行安装 |
| ZDSRAPI（争渡） | 专有 | 不随附；由用户自备并遵守其许可 |
| .NET SDK 8 | MIT | 仅构建期需要 |

## 游戏权利

游戏本体、角色、剧本、美术、音频等权利归原厂商所有。本工程不授予、也不主张任何游戏内容的权利；
请合法拥有游戏后再使用本补丁。

## 免责

本工程是辅助工具，按现状提供，不保证对所有文本、所有版本生效。因游戏更新、第三方组件变更
或使用方式导致的任何问题，由使用者自行承担；发现问题请附日志反馈，不要自行修改生成的代码。
""";
}
