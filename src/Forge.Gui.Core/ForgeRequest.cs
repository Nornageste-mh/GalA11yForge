namespace GalA11yForge.Gui.Core;

/// <summary>
/// 一次 CLI 调用的输入。所有路径原样保留（不做规范化），由 CLI 负责校验；
/// GUI 只负责「把用户填的字段翻译成合法参数」，非法输入在 <see cref="GuiStateMachine"/> 里提前拦住。
/// </summary>
public sealed class ForgeRequest
{
    public ForgeCommandKind Command { get; init; }

    public string? GamePath { get; init; }

    public string? WorkspacePath { get; init; }

    public string? BepInExPath { get; init; }

    /// <summary>可选：verify 时的运行日志路径，传给 CLI 的 --speech-log，用于自动确认“补丁有没有读出台词”。</summary>
    public string? SpeechLogPath { get; init; }

    /// <summary>演练模式：传给 CLI 的 --dry-run，安装/回滚时最安全。</summary>
    public bool DryRun { get; init; }

    /// <summary>高级分析：分析文本时传给 CLI 的 --advanced，允许调用 Ghidra 做原生静态分析（只读、较慢）。</summary>
    public bool Advanced { get; init; }

    /// <summary>可选：高级分析时显式指定要分析的 exe，传给 CLI 的 --option ghidra.executablePath=…；留空则自动选择。</summary>
    public string? GhidraExecutablePath { get; init; }

    /// <summary>安装 --force（允许覆盖已存在部署）/ 回滚 --force。</summary>
    public bool Force { get; init; }

    public bool Verbose { get; init; }

    /// <summary>默认 true：GUI 依赖 JSON 信封，永远带 --json。</summary>
    public bool IncludeJson { get; init; } = true;

    /// <summary>只读：该命令是否需要 --game。</summary>
    public bool RequiresGame => ForgeCommands.Of(Command).RequiresGame;
}

/// <summary>
/// 把 <see cref="ForgeRequest"/> 变成 argv。零依赖、可单测；CLI 的参数表变化时先改这里再改测试。
/// </summary>
public static class ForgeArgumentBuilder
{
    /// <summary>
    /// 生成 argv（不含 exe 本身）。顺序固定：命令 → 路径 → 语义开关 → --json。
    /// 顺序固定便于测试断言，也让「查看命令」输出稳定。
    /// </summary>
    public static IReadOnlyList<string> Build(ForgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = ForgeCommands.Of(request.Command);
        var args = new List<string> { descriptor.CliName };

        var game = Clean(request.GamePath);
        if (game is not null)
        {
            args.Add("--game");
            args.Add(game);
        }

        var workspace = Clean(request.WorkspacePath);
        if (workspace is not null)
        {
            args.Add("--workspace");
            args.Add(workspace);
        }

        var bepInEx = Clean(request.BepInExPath);
        if (bepInEx is not null)
        {
            args.Add("--bepinex");
            args.Add(bepInEx);
        }

        // 只有 verify 接受 --speech-log；其它命令带上会被 CLI 当成未知参数。
        var speechLog = Clean(request.SpeechLogPath);
        if (speechLog is not null && request.Command == ForgeCommandKind.Verify)
        {
            args.Add("--speech-log");
            args.Add(speechLog);
        }

        foreach (var fixedArgument in descriptor.FixedArguments)
        {
            args.Add(fixedArgument);
        }

        // 只有 analyze 用得到 --advanced（CLI 的其它命令不接受该语义）；避免 GUI 里误加。
        if (request.Advanced && request.Command == ForgeCommandKind.Analyze)
        {
            args.Add("--advanced");
        }

        // 只有 analyze 用得到 ghidra.executablePath；显式指定后 Ghidra 不再自动挑 exe。
        var ghidraExe = Clean(request.GhidraExecutablePath);
        if (ghidraExe is not null && request.Command == ForgeCommandKind.Analyze)
        {
            args.Add("--option");
            args.Add("ghidra.executablePath=" + ghidraExe);
        }

        if (request.DryRun)
        {
            args.Add("--dry-run");
        }

        if (request.Force && descriptor.SupportsForce)
        {
            args.Add("--force");
        }

        if (request.Verbose)
        {
            args.Add("--verbose");
        }

        if (request.IncludeJson)
        {
            args.Add("--json");
        }

        return args;
    }

    /// <summary>人类可读的命令行（用于结果区与日志）。含空格/引号的参数加双引号。</summary>
    public static string Describe(IReadOnlyList<string> arguments, string executable = "forge")
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return executable + " " + string.Join(' ', arguments.Select(Quote));
    }

    /// <summary>
    /// 用于 Windows Process 的单个命令行字符串（GUI 实际用 ArgumentList，这里只给日志与外部复现用）。
    /// </summary>
    public static string ToCommandLine(IReadOnlyList<string> arguments, string executable = "forge")
        => Describe(arguments, executable);

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (value.Any(c => c is ' ' or '\t' or '"'))
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
