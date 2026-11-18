namespace GalA11yForge.Gui.Core;

/// <summary>
/// forge 退出码（与 src/Forge.Cli/CliOptions.cs 的 ExitCodes 保持一致）。
/// 改 CLI 退出码表时必须同步这里与 tests/Forge.Gui.Tests/ExitCodeTests。
/// </summary>
public static class ForgeExitCodes
{
    public const int Success = 0;
    public const int InternalError = 1;
    public const int ArgumentError = 2;
    public const int DependencyMissing = 3;
    public const int EngineUnsupported = 4;
    public const int PartialSuccess = 5;

    /// <summary>退出码的中文含义，用于结果区与状态栏。</summary>
    public static string Describe(int exitCode) => exitCode switch
    {
        Success => "成功",
        InternalError => "内部错误；工具缺陷，不是游戏问题",
        ArgumentError => "参数错误；路径或选项不合法",
        DependencyMissing => "依赖或能力缺失；未实现的命令也返回这个码",
        EngineUnsupported => "引擎不支持",
        PartialSuccess => "部分成功；有未完成项",
        _ => $"未知退出码 {exitCode}",
    };

    public static string Text(int exitCode) => $"{exitCode}（{Describe(exitCode)}）";
}

public enum ForgeRunStatus
{
    Success = 0,
    PartialSuccess = 1,
    Failed = 2,
    Canceled = 3,
    LaunchFailed = 4,
}

public static class ForgeRunStatusText
{
    public static string Of(ForgeRunStatus status) => status switch
    {
        ForgeRunStatus.Success => "成功",
        ForgeRunStatus.PartialSuccess => "部分成功",
        ForgeRunStatus.Failed => "失败",
        ForgeRunStatus.Canceled => "已取消",
        ForgeRunStatus.LaunchFailed => "无法启动",
        _ => "未知",
    };

    public static bool IsGood(ForgeRunStatus status)
        => status is ForgeRunStatus.Success or ForgeRunStatus.PartialSuccess;
}

/// <summary>
/// 一次 CLI 调用的最终结果：退出码 + 两路输出 + 解析出的信封 + 耗时。
/// 状态推导规则集中在这里，GUI 与测试共用同一套。
/// </summary>
public sealed class ForgeRunOutcome
{
    public ForgeRunStatus Status { get; private init; }

    public int ExitCode { get; private init; }

    public string CommandLine { get; private init; } = string.Empty;

    public string Stdout { get; private init; } = string.Empty;

    public string Stderr { get; private init; } = string.Empty;

    public ForgeEnvelope? Envelope { get; private init; }

    /// <summary>信封解析失败的原因（成功时为空）。</summary>
    public string? EnvelopeError { get; private init; }

    public TimeSpan Elapsed { get; private init; }

    /// <summary>启动阶段就失败（找不到 exe、没有权限）时的人话原因。</summary>
    public string? LaunchError { get; private init; }

    public string? Code => Envelope?.Code;

    public string StatusText => ForgeRunStatusText.Of(Status);

    public bool IsGood => ForgeRunStatusText.IsGood(Status);

    /// <summary>优先用信封 message；没有信封时退到 stdout 第一行非空文本。</summary>
    public string? Message
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Envelope?.Message))
            {
                return Envelope!.Message;
            }

            if (LaunchError is not null)
            {
                return LaunchError;
            }

            var firstLine = Stdout
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('{'));
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
        }
    }

    public string? Suggestion => Envelope?.Suggestion;

    /// <summary>结构化补充信息（CLI details），GUI 结果区逐条显示。</summary>
    public IReadOnlyDictionary<string, string>? Details => Envelope?.Details;

    public bool HasStderr => !string.IsNullOrWhiteSpace(Stderr);

    /// <summary>
    /// 从进程退出码 + 输出推导状态：
    /// 0 且信封 ok=true → 成功；0 且信封 ok=false → 失败（退出码与结果矛盾，如实标失败）；
    /// 5 → 部分成功；其它非 0 → 失败。绝不把非 0 当成功。
    /// </summary>
    public static ForgeRunOutcome Completed(
        int exitCode,
        string commandLine,
        string? stdout,
        string? stderr,
        TimeSpan elapsed)
    {
        var parsed = ForgeEnvelopeParser.Parse(stdout);
        var status = exitCode switch
        {
            ForgeExitCodes.Success => parsed.Envelope is { Ok: false } ? ForgeRunStatus.Failed : ForgeRunStatus.Success,
            ForgeExitCodes.PartialSuccess => ForgeRunStatus.PartialSuccess,
            _ => ForgeRunStatus.Failed,
        };

        return new ForgeRunOutcome
        {
            Status = status,
            ExitCode = exitCode,
            CommandLine = commandLine,
            Stdout = stdout ?? string.Empty,
            Stderr = stderr ?? string.Empty,
            Envelope = parsed.Envelope,
            EnvelopeError = parsed.Envelope is null ? parsed.Error : null,
            Elapsed = elapsed,
        };
    }

    public static ForgeRunOutcome Canceled(string commandLine, string? stdout, string? stderr, TimeSpan elapsed)
    {
        var parsed = ForgeEnvelopeParser.Parse(stdout);
        return new ForgeRunOutcome
        {
            Status = ForgeRunStatus.Canceled,
            ExitCode = -1,
            CommandLine = commandLine,
            Stdout = stdout ?? string.Empty,
            Stderr = stderr ?? string.Empty,
            Envelope = parsed.Envelope,
            EnvelopeError = parsed.Envelope is null ? parsed.Error : null,
            Elapsed = elapsed,
        };
    }

    public static ForgeRunOutcome LaunchFailed(string commandLine, string error, TimeSpan elapsed) => new()
    {
        Status = ForgeRunStatus.LaunchFailed,
        ExitCode = -1,
        CommandLine = commandLine,
        LaunchError = error,
        Elapsed = elapsed,
    };
}
