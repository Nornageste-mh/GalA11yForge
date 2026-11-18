namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>Ren'Py 游戏目录的只读盘点结果。所有字段都来自文件系统，不解析 .rpa 内容。</summary>
internal sealed class RenpyGameFacts
{
    public string Root { get; init; } = string.Empty;

    public bool HasRenpyDirectory { get; init; }

    public bool HasGameDirectory { get; init; }

    public bool HasRenpyInit { get; init; }

    public string? ExecutableName { get; init; }

    public string? EngineVersion { get; init; }

    public string? VersionSource { get; init; }

    public string? VersionName { get; init; }

    public string? Branch { get; init; }

    public bool? Nightly { get; init; }

    public bool? Official { get; init; }

    public List<int> VersionTuple { get; init; } = new();

    /// <summary>2 / 3；0 表示未知。</summary>
    public int PythonMajor { get; init; }

    /// <summary>32 / 64；0 表示未知。</summary>
    public int Bitness { get; init; }

    public List<string> PythonLibs { get; init; } = new();

    public List<RenpyRpaInfo> RpaFiles { get; init; } = new();

    public long RpaBytes { get; init; }

    /// <summary>相对 game/ 的松散 .rpy（'/' 分隔），已排除 cache 等运行时目录。</summary>
    public List<string> LooseRpy { get; init; } = new();

    public int LooseRpycCount { get; init; }

    /// <summary>game/ 下疑似 a11y 覆盖层目录（相对 game/）。</summary>
    public List<string> OverlayDirectories { get; init; } = new();

    public string? AccessibilityScript { get; init; }

    public long AccessibilityBytes { get; init; }

    public List<RenpyNvdaClientInfo> NvdaClients { get; init; } = new();

    /// <summary>游戏根目录里的 .vbs（相对 root）。</summary>
    public List<string> RootVbs { get; init; } = new();

    public bool HasRootSayVbs { get; init; }

    public bool HasRootManifest { get; init; }

    public bool IsSteamPath { get; init; }

    public bool HasSteamAppId { get; init; }

    public bool IsSteam => IsSteamPath || HasSteamAppId;

    /// <summary>
    /// 检测到 game/a11y* 目录或根 a11y_manifest.json 才视为已有补丁痕迹。
    /// 单独的根目录 a11y_say.vbs 证据不足（原版 DDLC 自带同名脚本），不据此判冲突。
    /// </summary>
    public bool HasExistingOverlay => OverlayDirectories.Count > 0 || HasRootManifest;

    public RenpyHookFacts Hooks { get; init; } = new();

    public bool IsRenpy => HasRenpyDirectory && HasGameDirectory;

    public string GameDirectory => Path.Combine(Root, "game");
}

internal sealed class RenpyRpaInfo
{
    public string Name { get; init; } = string.Empty;

    public long Bytes { get; init; }

    /// <summary>归档头，例如 RPA-3.0；读不到时为 null。</summary>
    public string? Header { get; init; }
}

internal sealed class RenpyNvdaClientInfo
{
    public string RelativePath { get; init; } = string.Empty;

    public int Bitness { get; init; }

    public long Bytes { get; init; }
}

/// <summary>引擎级钩子在当前游戏 renpy/ 源码里的实际出现位置；找不到就留空，由分析步骤写“未验证”。</summary>
internal sealed class RenpyHookFacts
{
    /// <summary>renpy/ast.py 中 say_menu_text_filter 的行号。</summary>
    public List<int> AstSayFilterLines { get; init; } = new();

    public int? ConfigSayFilterLine { get; init; }

    public int? ConfigTtsFunctionLine { get; init; }

    public int? ConfigPeriodicCallbacksLine { get; init; }

    public int? FocusListLine { get; init; }

    public bool? HasGetScreenExport { get; init; }
}
