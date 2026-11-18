using System.Text;
using System.Text.Json;

namespace GalA11yForge.Gui.Core;

public sealed class ForgeTextOptions
{
    /// <summary>是否在结果区附带原始 JSON 信封（默认关，读屏时太长）。</summary>
    public bool IncludeRawJson { get; init; }

    /// <summary>是否回显非 JSON 的 stdout 行（正常 --json 调用一般没有）。</summary>
    public bool IncludeStdoutEcho { get; init; } = true;

    public int MaxStderrLines { get; init; } = 200;
}

/// <summary>
/// 把 <see cref="ForgeRunOutcome"/> 排成一块可朗读的纯文本：
/// 第一行是结论（读屏用户最想先知道的部分），然后是命令、退出码、消息、建议，
/// 最后才是数据与原始输出。所有字段都在一处拼装，方便单测。
/// </summary>
public static class ForgeResultTextBuilder
{
    public static string Build(ForgeRunOutcome outcome, ForgeTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        options ??= new ForgeTextOptions();

        var commandTitle = TitleOf(outcome.CommandLine);
        var builder = new StringBuilder();
        builder.Append("【").Append(commandTitle).Append("】")
            .AppendLine(outcome.StatusText);

        if (!string.IsNullOrWhiteSpace(outcome.CommandLine))
        {
            builder.Append("命令：").AppendLine(outcome.CommandLine);
        }

        if (outcome.Status != ForgeRunStatus.LaunchFailed)
        {
            builder.Append("退出码：")
                .AppendLine(outcome.ExitCode >= 0 ? ForgeExitCodes.Text(outcome.ExitCode) : "未产生（进程没有正常结束）");
        }

        builder.Append("耗时：").AppendLine(FormatElapsed(outcome.Elapsed));

        if (!string.IsNullOrWhiteSpace(outcome.Code))
        {
            builder.Append("错误码：").AppendLine(outcome.Code);
        }

        if (!string.IsNullOrWhiteSpace(outcome.Message))
        {
            builder.Append("消息：").AppendLine(outcome.Message);
        }

        if (!string.IsNullOrWhiteSpace(outcome.Suggestion))
        {
            builder.Append("建议：").AppendLine(outcome.Suggestion);
        }

        if (outcome.Details is { Count: > 0 } details)
        {
            builder.AppendLine("详情：");
            foreach (var pair in details)
            {
                builder.Append("  ").Append(pair.Key).Append('：').AppendLine(pair.Value);
            }
        }

        if (outcome.Status == ForgeRunStatus.LaunchFailed)
        {
            builder.AppendLine("建议：在「forge.exe 路径」里指定 CLI 可执行文件；默认会找 src\\Forge.Cli\\bin\\Release\\net8.0\\forge.exe。");
        }

        if (outcome.Status == ForgeRunStatus.Canceled)
        {
            builder.AppendLine("说明：本次执行被用户中止，状态未知；游戏目录可能已被部分改动，请用「安装后自检」确认。");
        }

        if (!string.IsNullOrWhiteSpace(outcome.EnvelopeError) && outcome.Status != ForgeRunStatus.LaunchFailed)
        {
            builder.Append("信封解析：").AppendLine(outcome.EnvelopeError);
        }

        AppendData(builder, outcome);

        if (options.IncludeStdoutEcho)
        {
            AppendStdoutEcho(builder, outcome);
        }

        AppendStderr(builder, outcome, options.MaxStderrLines);

        if (options.IncludeRawJson)
        {
            builder.AppendLine();
            builder.AppendLine("原始输出（stdout）：");
            builder.AppendLine(string.IsNullOrWhiteSpace(outcome.Stdout) ? "（空）" : outcome.Stdout.TrimEnd());
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>状态栏用的一行摘要，例如「识别引擎：成功（退出码 0）——识别为 renpy」。</summary>
    public static string BuildHeadline(ForgeRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var title = TitleOf(outcome.CommandLine);
        var exit = outcome.ExitCode >= 0 ? $"退出码 {outcome.ExitCode}" : "未产生退出码";
        var message = outcome.Message;
        var tail = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : "：" + message!.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return $"{title}：{outcome.StatusText}（{exit}）{tail}";
    }

    /// <summary>从命令行里取命令名并翻成中文标题；取不到就返回「forge」。</summary>
    public static string TitleOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return "forge";
        }

        var tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            foreach (var descriptor in ForgeCommands.AllOrdered)
            {
                if (string.Equals(token, descriptor.CliName, StringComparison.OrdinalIgnoreCase))
                {
                    // 回滚复用 verify，而 Verify 在枚举里排在 Rollback 前面；
                    // 只有看参数里有没有 --rollback 才能把两者区分开。
                    return IsRollback(tokens) && descriptor.Kind == ForgeCommandKind.Verify
                        ? ForgeCommands.Rollback.Title
                        : descriptor.Title;
                }
            }
        }

        return "forge";
    }

    private static bool IsRollback(IReadOnlyList<string> tokens)
        => tokens.Any(token => string.Equals(token, "--rollback", StringComparison.OrdinalIgnoreCase));

    private static void AppendData(StringBuilder builder, ForgeRunOutcome outcome)
    {
        if (outcome.Envelope is not { HasData: true })
        {
            return;
        }

        var lines = ForgeDataNarrator.Narrate(outcome.Envelope.Data);
        if (lines.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("数据：");
        foreach (var line in lines)
        {
            builder.Append("  ").AppendLine(line);
        }
    }

    private static void AppendStdoutEcho(StringBuilder builder, ForgeRunOutcome outcome)
    {
        var extra = outcome.Stdout
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('{'))
            .ToList();

        if (extra.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("标准输出（非 JSON 行）：");
        foreach (var line in extra)
        {
            builder.Append("  ").AppendLine(line);
        }
    }

    private static void AppendStderr(StringBuilder builder, ForgeRunOutcome outcome, int maxLines)
    {
        if (!outcome.HasStderr)
        {
            return;
        }

        var lines = outcome.Stderr
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            return;
        }

        var limit = Math.Max(1, maxLines);
        var shown = lines.Count > limit ? lines.Take(limit).ToList() : lines;
        builder.AppendLine();
        builder.AppendLine("标准错误：");
        foreach (var line in shown)
        {
            builder.Append("  ").AppendLine(line);
        }

        if (lines.Count > limit)
        {
            builder.Append("  ……还有 ").Append(lines.Count - limit).AppendLine(" 行未显示。");
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds >= 60
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{elapsed.TotalSeconds:F1} 秒";

    /// <summary>把信封的 data 原样序列化（供「显示原始 JSON」用）。</summary>
    public static string SerializeData(ForgeEnvelope? envelope)
    {
        if (envelope is not { HasData: true })
        {
            return string.Empty;
        }

        return JsonSerializer.Serialize(envelope.Data, new JsonSerializerOptions { WriteIndented = true });
    }
}
