namespace GalA11yForge.Gui.Core;

/// <summary>按钮「为什么不能按」的原因分类；GUI 用它决定提示措辞。</summary>
public enum BlockingKind
{
    None = 0,
    Busy = 1,
    MissingExecutable = 2,
    MissingGamePath = 3,
    GamePathNotFound = 4,
}

public sealed record CommandAvailability(bool Enabled, BlockingKind BlockingKind, string? Reason)
{
    public static CommandAvailability Ok() => new(true, BlockingKind.None, null);

    public static CommandAvailability Blocked(BlockingKind kind, string reason) => new(false, kind, reason);
}

/// <summary>
/// 界面输入的不可变快照。路径只是字符串；「目录/文件是否存在」由调用方探测后填进来，
/// 这样状态机可以在没有文件系统的单元测试里跑。
/// </summary>
public sealed class GuiInputs
{
    public string? GamePath { get; init; }

    public string? WorkspacePath { get; init; }

    public string? BepInExPath { get; init; }

    /// <summary>可选：verify 用的运行日志，用于自动检查补丁有没有读出台词。</summary>
    public string? SpeechLogPath { get; init; }

    public string? ForgeExePath { get; init; }

    public bool DryRun { get; init; }

    /// <summary>高级分析：分析文本时传给 CLI 的 --advanced（调用 Ghidra，只读但较慢）。</summary>
    public bool Advanced { get; init; }

    /// <summary>可选：高级分析时显式指定要分析的 exe；留空则由 Ghidra 自动选择。</summary>
    public string? GhidraExecutablePath { get; init; }

    /// <summary>可选：显式指定的分析程序大小（字节），仅用于就绪提示里的耗时粗估。</summary>
    public long? GhidraExecutableSizeBytes { get; init; }

    /// <summary>允许强制：映射到 CLI 的 --force（仅对支持 force 的命令生效）。默认 false。</summary>
    public bool Force { get; init; }

    public bool IsBusy { get; init; }

    public ForgeCommandKind? RunningCommand { get; init; }

    public bool GameDirectoryExists { get; init; }

    public bool ForgeExeExists { get; init; }

    public bool HasGamePath => !string.IsNullOrWhiteSpace(GamePath);

    public bool HasValidGame => HasGamePath && GameDirectoryExists;

    public static GuiInputs Create(
        string? gamePath,
        string? workspacePath,
        string? bepInExPath,
        string? forgeExePath,
        bool dryRun,
        bool isBusy,
        ForgeCommandKind? runningCommand,
        Func<string?, bool> directoryExists,
        Func<string?, bool> fileExists,
        string? speechLogPath = null,
        bool advanced = false,
        string? ghidraExecutablePath = null,
        long? ghidraExecutableSizeBytes = null,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);

        var game = Trim(gamePath);
        var exe = Trim(forgeExePath);
        return new GuiInputs
        {
            GamePath = game,
            WorkspacePath = Trim(workspacePath),
            BepInExPath = Trim(bepInExPath),
            SpeechLogPath = Trim(speechLogPath),
            Advanced = advanced,
            GhidraExecutablePath = Trim(ghidraExecutablePath),
            GhidraExecutableSizeBytes = ghidraExecutableSizeBytes is > 0 ? ghidraExecutableSizeBytes : null,
            Force = force,
            ForgeExePath = exe,
            DryRun = dryRun,
            IsBusy = isBusy,
            RunningCommand = runningCommand,
            GameDirectoryExists = game is not null && directoryExists(game),
            ForgeExeExists = exe is not null && fileExists(exe),
        };
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 界面状态机：给定输入，算出每个操作按钮是否可用、为什么不可用。
/// 纯函数、无 WinForms 依赖，可在 net8.0 测试项目里直接断言。
/// </summary>
public static class GuiStateMachine
{
    public static CommandAvailability Evaluate(ForgeCommandKind kind, GuiInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.IsBusy)
        {
            var running = inputs.RunningCommand is { } current
                ? $"「{ForgeCommands.Of(current).Title}」"
                : "另一项操作";
            return CommandAvailability.Blocked(BlockingKind.Busy, $"正在执行{running}，完成或点「停止」后再试。");
        }

        if (!inputs.ForgeExeExists)
        {
            return CommandAvailability.Blocked(
                BlockingKind.MissingExecutable,
                "没有找到 forge.exe；请在「forge.exe 路径」里指定 CLI 可执行文件。");
        }

        var descriptor = ForgeCommands.Of(kind);
        if (!descriptor.RequiresGame)
        {
            return CommandAvailability.Ok();
        }

        if (!inputs.HasGamePath)
        {
            return CommandAvailability.Blocked(BlockingKind.MissingGamePath, "请先选择「游戏目录」（Alt+1 跳到输入框）。");
        }

        if (!inputs.GameDirectoryExists)
        {
            return CommandAvailability.Blocked(
                BlockingKind.GamePathNotFound,
                $"游戏目录不存在：{inputs.GamePath}。请重新选择。");
        }

        return CommandAvailability.Ok();
    }

    /// <summary>「一键」= 识别 → 分析 → 生成 → 构建，需要游戏目录；不会安装。</summary>
    public static CommandAvailability EvaluateOneClick(GuiInputs inputs)
        => Evaluate(ForgeCommandKind.Detect, inputs);

    public static CommandAvailability EvaluateStop(GuiInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return inputs.IsBusy
            ? CommandAvailability.Ok()
            : CommandAvailability.Blocked(BlockingKind.None, "当前没有正在执行的操作。");
    }

    public static IReadOnlyDictionary<ForgeCommandKind, CommandAvailability> EvaluateAll(GuiInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var map = new Dictionary<ForgeCommandKind, CommandAvailability>();
        foreach (var descriptor in ForgeCommands.AllOrdered)
        {
            map[descriptor.Kind] = Evaluate(descriptor.Kind, inputs);
        }

        return map;
    }

    public static bool AnyEnabled(GuiInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return ForgeCommands.AllOrdered.Any(descriptor => Evaluate(descriptor.Kind, inputs).Enabled);
    }

    /// <summary>按钮下方那行提示：告诉用户现在能做什么、缺什么。空字符串表示一切就绪。</summary>
    public static string HintText(GuiInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.IsBusy)
        {
            var running = inputs.RunningCommand is { } current
                ? ForgeCommands.Of(current).Title
                : "操作";
            return $"正在执行「{running}」……按 Esc 或 Alt+X 可停止。";
        }

        if (!inputs.ForgeExeExists)
        {
            return "找不到 forge.exe：点「浏览」选 src\\Forge.Cli\\bin\\Release\\net8.0\\forge.exe。";
        }

        if (!inputs.HasGamePath)
        {
            return "请选择游戏目录后再执行操作；「环境自检」不需要游戏目录。";
        }

        if (!inputs.GameDirectoryExists)
        {
            return $"游戏目录不存在：{inputs.GamePath}";
        }

        var ready = inputs.DryRun
            ? "就绪（演练模式：安装与回滚不会真正写盘）。"
            : "就绪。安装或回滚会改写游戏目录，执行前会有确认框。补丁装好后，可在游戏的 BepInEx 配置里调「字段补读最长等待秒」（默认 4，0=关闭）；自检时可在「语音日志」里填游戏运行日志，自动检查补丁有没有读出台词。";
        if (inputs.Advanced)
        {
            ready += "已勾选「高级分析」：分析文本会调用 Ghidra 做原生静态分析，只读但较慢。";
            if (!string.IsNullOrWhiteSpace(inputs.GhidraExecutablePath))
            {
                ready += $"将分析你指定的程序：{Path.GetFileName(inputs.GhidraExecutablePath)}。";
                if (inputs.GhidraExecutableSizeBytes is { } size && size > 0)
                {
                    ready += $"按文件大小粗估，分析这个程序大约需要{AnalysisDurationHint.Describe(size)}。";
                }
            }
        }

        if (inputs.Force)
        {
            ready += "已勾选「允许强制」：安装会加 --force，可能覆盖已有补丁或越过「没有正文入口」的安全拦阻。";
        }

        return ready;
    }

    /// <summary>执行前的最后一道本地校验（UI 点按钮时调用），返回 null 表示可以跑。</summary>
    public static string? ValidateBeforeRun(ForgeCommandKind kind, GuiInputs inputs)
    {
        var availability = Evaluate(kind, inputs);
        return availability.Enabled ? null : availability.Reason;
    }
}
