using GalA11yForge.Core.Results;

namespace GalA11yForge.Cli;

/// <summary>
/// 手写参数解析（零外部依赖）。支持 `--name value` 与 `--name=value`；
/// 未知参数、缺失值、多余位置参数都返回 E_ARG_INVALID。
/// </summary>
public static class CommandLineParser
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--game", "--out", "--workspace", "--bepinex", "--speech-runtime", "--speech-log", "--option",
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--json", "--dry-run", "--verbose", "--advanced", "--help", "-h", "--version", "-V", "--rollback", "--force",
    };

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        if (args is null || args.Count == 0)
        {
            return Error("缺少命令。", "运行 forge --help 查看可用命令。");
        }

        string? game = null;
        string? output = null;
        string? workspace = null;
        string? bepInEx = null;
        string? speechRuntime = null;
        string? speechLog = null;
        var optionOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var json = false;
        var dryRun = false;
        var verbose = false;
        var advanced = false;
        var rollback = false;
        var force = false;
        var help = false;
        var version = false;
        var positionals = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!token.StartsWith('-'))
            {
                positionals.Add(token);
                continue;
            }

            var name = token;
            string? inlineValue = null;
            var equals = token.IndexOf('=');
            if (equals > 0)
            {
                name = token[..equals];
                inlineValue = token[(equals + 1)..];
            }

            if (FlagOptions.Contains(name))
            {
                if (inlineValue is not null)
                {
                    return Error($"参数 {name} 不接受值。", "去掉 = 后面的内容。");
                }

                switch (name.ToLowerInvariant())
                {
                    case "--json":
                        json = true;
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--verbose":
                        verbose = true;
                        break;
                    case "--advanced":
                        advanced = true;
                        break;
                    case "--rollback":
                        rollback = true;
                        break;
                    case "--force":
                        force = true;
                        break;
                    case "--help":
                    case "-h":
                        help = true;
                        break;
                    case "--version":
                    case "-v":
                        version = true;
                        break;
                }

                continue;
            }

            if (!ValueOptions.Contains(name))
            {
                return Error($"未知参数：{name}。", "可用参数：--game --out --json --dry-run --verbose --advanced --workspace --bepinex --speech-runtime --speech-log --option --rollback --force --help --version。");
            }

            string value;
            if (inlineValue is not null)
            {
                value = inlineValue;
            }
            else
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--"))
                {
                    return Error($"参数 {name} 缺少值。", $"例如 {name} <路径>。");
                }

                value = args[++i];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return Error($"参数 {name} 的值不能为空。", $"例如 {name} <路径>。");
            }

            switch (name.ToLowerInvariant())
            {
                case "--game":
                    game = value;
                    break;
                case "--out":
                    output = value;
                    break;
                case "--workspace":
                    workspace = value;
                    break;
                case "--bepinex":
                    bepInEx = value;
                    break;
                case "--speech-runtime":
                    speechRuntime = value;
                    break;
                case "--speech-log":
                    speechLog = value;
                    break;
                case "--option":
                    var eq = value.IndexOf('=');
                    if (eq <= 0)
                    {
                        return Error($"参数 --option 的值应为 key=value：{value}。", "例如 --option il2cpp.dumpPath=D:\\dump.cs。");
                    }

                    var optionKey = value[..eq].Trim();
                    var optionValue = value[(eq + 1)..].Trim();
                    if (optionKey.Length == 0)
                    {
                        return Error("参数 --option 的键不能为空。", "例如 --option il2cpp.dumpPath=D:\\dump.cs。");
                    }

                    optionOverrides[optionKey] = optionValue;
                    break;
            }
        }

        if (positionals.Count == 0)
        {
            if (help)
            {
                return new CliParseResult { HelpRequested = true, Options = new CliOptions { Command = CliCommand.Help, Json = json } };
            }

            if (version)
            {
                return new CliParseResult { VersionRequested = true, Options = new CliOptions { Command = CliCommand.Version, Json = json } };
            }

            return Error("缺少命令。", "运行 forge --help 查看可用命令。");
        }

        if (positionals.Count > 1)
        {
            return Error($"多余的位置参数：{string.Join(' ', positionals.Skip(1))}。", "一条命令只能有一个命令名。");
        }

        var commandText = positionals[0];
        var parsedCommand = ParseCommand(commandText);
        if (parsedCommand == CliCommand.None)
        {
            return Error($"未知命令：{commandText}。", "可用命令：detect analyze plan generate build install verify report doctor tools。");
        }

        if (help && parsedCommand is not (CliCommand.Help or CliCommand.Version))
        {
            return new CliParseResult
            {
                Options = new CliOptions { Command = parsedCommand, Json = json },
            };
        }

        return new CliParseResult
        {
            Options = new CliOptions
            {
                Command = parsedCommand,
                GamePath = game,
                OutputPath = output,
                WorkspacePath = workspace,
                Json = json,
                DryRun = dryRun,
                Verbose = verbose,
                Advanced = advanced,
                BepInExPath = bepInEx,
                SpeechRuntimePath = speechRuntime,
                SpeechLogPath = speechLog,
                Rollback = rollback,
                Force = force,
                OptionOverrides = optionOverrides,
            },
            HelpRequested = help,
            VersionRequested = version,
        };
    }

    public static CliCommand ParseCommand(string text) => text.ToLowerInvariant() switch
    {
        "detect" => CliCommand.Detect,
        "analyze" => CliCommand.Analyze,
        "plan" => CliCommand.Plan,
        "generate" => CliCommand.Generate,
        "build" => CliCommand.Build,
        "install" => CliCommand.Install,
        "verify" => CliCommand.Verify,
        "report" => CliCommand.Report,
        "doctor" => CliCommand.Doctor,
        "tools" => CliCommand.Tools,
        "help" => CliCommand.Help,
        "version" => CliCommand.Version,
        _ => CliCommand.None,
    };

    private static CliParseResult Error(string message, string suggestion)
        => new() { Error = ForgeError.Create(ForgeErrorCodes.ArgumentInvalid, message, suggestion) };
}
