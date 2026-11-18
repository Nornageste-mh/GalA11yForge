namespace GalA11yForge.Gui.Core;

/// <summary>
/// GUI 暴露的命令。与 CLI 一一对应：<see cref="ForgeCommandKind.Rollback"/> 复用 verify 并追加 --rollback。
/// </summary>
public enum ForgeCommandKind
{
    Detect = 0,
    Analyze = 1,
    Generate = 2,
    Build = 3,
    Install = 4,
    Verify = 5,
    Doctor = 6,
    Rollback = 7,
    Report = 8,
}

/// <summary>一条命令的可读描述与安全属性。WritesToGame 决定是否需要二次确认。</summary>
public sealed record ForgeCommandDescriptor(
    ForgeCommandKind Kind,
    string CliName,
    string Title,
    bool RequiresGame,
    bool WritesToGame,
    bool RequiresConfirmation,
    bool SupportsForce,
    string Description,
    string AccessibleDescription)
{
    /// <summary>命令 + 固定追加参数，用于构造命令行。</summary>
    public IReadOnlyList<string> FixedArguments => Kind == ForgeCommandKind.Rollback
        ? new[] { "--rollback" }
        : Array.Empty<string>();
}

public static class ForgeCommands
{
    public static readonly ForgeCommandDescriptor Detect = new(
        ForgeCommandKind.Detect,
        "detect",
        "识别引擎",
        RequiresGame: true,
        WritesToGame: false,
        RequiresConfirmation: false,
        SupportsForce: false,
        Description: "只读扫描游戏目录，识别 Ren'Py / Unity 等引擎与版本。",
        AccessibleDescription: "识别引擎：只读扫描游戏目录，不会写入任何文件。Alt 加 D。");

    public static readonly ForgeCommandDescriptor Analyze = new(
        ForgeCommandKind.Analyze,
        "analyze",
        "分析文本",
        RequiresGame: true,
        WritesToGame: false,
        RequiresConfirmation: false,
        SupportsForce: false,
        Description: "分析文本管线，产出文本候选与覆盖声明；只读游戏目录，结果写工作区。",
        AccessibleDescription: "分析文本：只读游戏目录，结果写入工作区。Alt 加 A。");

    public static readonly ForgeCommandDescriptor Generate = new(
        ForgeCommandKind.Generate,
        "generate",
        "生成工程",
        RequiresGame: true,
        WritesToGame: false,
        RequiresConfirmation: false,
        SupportsForce: false,
        Description: "把分析结果生成补丁工程，写入工作区。",
        AccessibleDescription: "生成补丁工程：只写工作区。Alt 加 G。");

    public static readonly ForgeCommandDescriptor Build = new(
        ForgeCommandKind.Build,
        "build",
        "构建补丁",
        RequiresGame: true,
        WritesToGame: false,
        RequiresConfirmation: false,
        SupportsForce: false,
        Description: "生成工程并 dotnet build，产物留在工作区。",
        AccessibleDescription: "构建补丁：只写工作区，需要本机 .NET SDK。Alt 加 B。");

    public static readonly ForgeCommandDescriptor Install = new(
        ForgeCommandKind.Install,
        "install",
        "安装补丁",
        RequiresGame: true,
        WritesToGame: true,
        RequiresConfirmation: true,
        SupportsForce: true,
        Description: "把补丁部署进游戏目录；部署前逐文件备份，可回滚。",
        AccessibleDescription: "安装补丁：会写入游戏目录，执行前需要确认。Alt 加 I。");

    public static readonly ForgeCommandDescriptor Report = new(
        ForgeCommandKind.Report,
        "report",
        "分析报告",
        RequiresGame: true,
        WritesToGame: false,
        RequiresConfirmation: false,
        SupportsForce: false,
        Description: "把分析结果（包括未知引擎的 Ghidra 高级分析）导出成 Markdown/JSON 报告与人工待办清单；只写工作区。",
        AccessibleDescription: "分析报告：导出报告与人工待办清单，只写工作区，不改游戏目录。Alt 加 P。");

    public static readonly ForgeCommandDescriptor Verify = new(
        ForgeCommandKind.Verify,
        "verify",
        "安装后自检",
        RequiresGame: true,
        WritesToGame: false,
        RequiresConfirmation: false,
        SupportsForce: true,
        Description: "安装后的初检：检查部署清单、备份与插件文件是否就位。",
        AccessibleDescription: "安装后自检：检查部署结果，不修改游戏目录。Alt 加 Y。");

    public static readonly ForgeCommandDescriptor Doctor = new(
        ForgeCommandKind.Doctor,
        "doctor",
        "环境自检",
        RequiresGame: false,
        WritesToGame: false,
        RequiresConfirmation: false,
        SupportsForce: false,
        Description: "检查运行时、工作区可写性与适配器注册表；不需要游戏目录。",
        AccessibleDescription: "环境自检：检查 .NET 运行时、工作区与适配器，不需要游戏目录。Alt 加 V。");

    public static readonly ForgeCommandDescriptor Rollback = new(
        ForgeCommandKind.Rollback,
        "verify",
        "回滚补丁",
        RequiresGame: true,
        WritesToGame: true,
        RequiresConfirmation: true,
        SupportsForce: true,
        Description: "按安装清单从游戏目录移除补丁并还原备份。",
        AccessibleDescription: "回滚补丁：会改写游戏目录，执行前需要确认。Alt 加 R。");

    /// <summary>界面按钮顺序（与 Tab 顺序一致）。</summary>
    public static IReadOnlyList<ForgeCommandDescriptor> AllOrdered { get; } = new[]
    {
        Detect, Analyze, Generate, Build, Report, Install, Verify, Doctor, Rollback,
    };

    /// <summary>「一键」只跑不写游戏目录的四步，避免误装。</summary>
    public static IReadOnlyList<ForgeCommandKind> OneClickSequence { get; } = new[]
    {
        ForgeCommandKind.Detect,
        ForgeCommandKind.Analyze,
        ForgeCommandKind.Generate,
        ForgeCommandKind.Build,
    };

    public static ForgeCommandDescriptor Of(ForgeCommandKind kind) => kind switch
    {
        ForgeCommandKind.Detect => Detect,
        ForgeCommandKind.Analyze => Analyze,
        ForgeCommandKind.Generate => Generate,
        ForgeCommandKind.Build => Build,
        ForgeCommandKind.Report => Report,
        ForgeCommandKind.Install => Install,
        ForgeCommandKind.Verify => Verify,
        ForgeCommandKind.Doctor => Doctor,
        ForgeCommandKind.Rollback => Rollback,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知命令。"),
    };
}
