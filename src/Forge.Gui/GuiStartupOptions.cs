namespace GalA11yForge.Gui;

/// <summary>
/// 启动参数（全部可选）：
/// <c>--forge &lt;forge.exe&gt;</c>、<c>--game &lt;游戏目录&gt;</c>、<c>--workspace &lt;工作区&gt;</c>、<c>--dry-run</c>。
/// 解析失败不弹窗打断启动：未知参数记下来，在界面上提示。
/// </summary>
public sealed class GuiStartupOptions
{
    public string? ForgeExePath { get; private set; }

    public string? GamePath { get; private set; }

    public string? WorkspacePath { get; private set; }

    public bool DryRun { get; private set; }

    public IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    public static GuiStartupOptions Parse(IReadOnlyList<string>? args)
    {
        var options = new GuiStartupOptions();
        if (args is null || args.Count == 0)
        {
            return options;
        }

        var problems = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            var (name, inlineValue) = SplitOption(argument);
            switch (name.ToLowerInvariant())
            {
                case "--forge":
                case "--exe":
                    if (!TryTakeValue(args, ref i, inlineValue, out var forge, problems, name))
                    {
                        break;
                    }

                    options.ForgeExePath = forge;
                    break;

                case "--game":
                    if (!TryTakeValue(args, ref i, inlineValue, out var game, problems, name))
                    {
                        break;
                    }

                    options.GamePath = game;
                    break;

                case "--workspace":
                case "--ws":
                    if (!TryTakeValue(args, ref i, inlineValue, out var workspace, problems, name))
                    {
                        break;
                    }

                    options.WorkspacePath = workspace;
                    break;

                case "--dry-run":
                case "--dryrun":
                    options.DryRun = true;
                    break;

                default:
                    problems.Add($"未知参数：{argument}（已忽略）");
                    break;
            }
        }

        options.Problems = problems;
        return options;
    }

    private static bool TryTakeValue(
        IReadOnlyList<string> args,
        ref int index,
        string? inlineValue,
        out string value,
        List<string> problems,
        string optionName)
    {
        if (!string.IsNullOrWhiteSpace(inlineValue))
        {
            value = inlineValue.Trim();
            return true;
        }

        // 下一个参数本身是开关（--dry-run 之类）时不能当路径吞掉，否则用户少写路径
        // 会静默把开关变成文件名，问题很难看出来。
        if (index + 1 < args.Count
            && !string.IsNullOrWhiteSpace(args[index + 1])
            && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            index++;
            value = args[index].Trim();
            return true;
        }

        value = string.Empty;
        problems.Add($"参数 {optionName} 后面缺少路径（已忽略）");
        return false;
    }

    private static (string Name, string? InlineValue) SplitOption(string argument)
    {
        var equals = argument.IndexOf('=', StringComparison.Ordinal);
        return equals > 0
            ? (argument[..equals], argument[(equals + 1)..])
            : (argument, null);
    }
}
