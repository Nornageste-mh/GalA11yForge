using System.Text;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Logging;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Core.Reports;

/// <summary>
/// 报告写出器：同一份 ReportDocument 生成 report.json 与 report.md。
/// Markdown 固定包含“覆盖声明”和“未验证项”两节；即使为空也写“（无）”，
/// 避免读者把“没写”误读成“全部覆盖/全部验证”。
/// </summary>
public sealed class JsonMarkdownReportWriter : IReportWriter
{
    private readonly IFlowLogger _logger;
    private readonly string? _gameDirectory;

    public JsonMarkdownReportWriter(IFlowLogger? logger = null, string? gameDirectory = null)
    {
        _logger = logger ?? NullFlowLogger.Instance;
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory) ? null : Path.GetFullPath(gameDirectory);
    }

    public async Task<ForgeResult<ReportOutput>> WriteAsync(
        ReportDocument report,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return ForgeResult<ReportOutput>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "报告输出目录不能为空。",
                "用 --out 指定一个工作区内的目录。"));
        }

        var directory = Path.GetFullPath(outputDirectory);
        if (_gameDirectory is not null && PathSafety.IsUnder(_gameDirectory, directory))
        {
            return ForgeResult<ReportOutput>.Failure(ForgeError.Create(
                ForgeErrorCodes.GameWriteDenied,
                $"拒绝把报告写进游戏目录：{directory}。",
                "报告写到工作区 reports/ 或 --out 指定的独立目录。"));
        }

        try
        {
            Directory.CreateDirectory(directory);
            report.GeneratedAt ??= DateTimeOffset.UtcNow.ToString("O");
            report.ToolVersion = string.IsNullOrWhiteSpace(report.ToolVersion) ? ForgeInfo.Version : report.ToolVersion;

            var jsonPath = Path.Combine(directory, "report.json");
            var markdownPath = Path.Combine(directory, "report.md");
            await File.WriteAllTextAsync(jsonPath, ForgeJson.Serialize(report), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(markdownPath, ToMarkdown(report), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            _logger.Log(FlowLogLevel.Info, "report", "报告已写出", new Dictionary<string, string>
            {
                ["json"] = jsonPath,
                ["markdown"] = markdownPath,
            });

            return ForgeResult<ReportOutput>.Success(new ReportOutput { JsonPath = jsonPath, MarkdownPath = markdownPath });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<ReportOutput>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"写报告失败：{directory}：{ex.Message}",
                "检查目录权限与磁盘空间后重试。"));
        }
    }

    public static string ToMarkdown(ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(report.GameSlug) ? "未命名游戏" : report.GameSlug;
        builder.AppendLine($"# 无障碍补丁报告：{title}");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{report.GeneratedAt ?? "未记录"}");
        builder.AppendLine($"- 工具版本：{report.ToolVersion}");
        if (!string.IsNullOrWhiteSpace(report.GamePath))
        {
            builder.AppendLine($"- 游戏目录：`{report.GamePath}`");
        }

        builder.AppendLine();
        builder.AppendLine("## 结论摘要");
        var detection = report.Detection ?? report.Analysis?.Detection;
        builder.AppendLine($"- 引擎：{detection?.Family ?? "未知"}{(string.IsNullOrWhiteSpace(detection?.Version) ? string.Empty : $" {detection.Version}")}");
        builder.AppendLine($"- 识别置信度：{(detection is null ? "未识别" : detection.Confidence.ToString("0.00"))}");
        builder.AppendLine($"- 路线：{RouteText(report.Plan?.Route)}");
        builder.AppendLine($"- 文本候选：{report.Analysis?.Candidates.Count ?? 0} 条；补丁点：{report.Plan?.PatchPoints.Count ?? 0} 个");
        builder.AppendLine($"- 覆盖：已覆盖 {report.Coverage.Covered.Count} 项，未覆盖 {report.Coverage.NotCovered.Count} 项，人工待办 {report.Coverage.ManualTodo.Count} 项");
        builder.AppendLine();

        builder.AppendLine("## 覆盖声明");
        AppendList(builder, "已覆盖", report.Coverage.Covered);
        AppendList(builder, "未覆盖", report.Coverage.NotCovered);
        AppendList(builder, "人工待办", report.Coverage.ManualTodo);
        builder.AppendLine();

        builder.AppendLine("## 未验证项");
        AppendUnverified(builder, report.Unverified);
        builder.AppendLine();

        builder.AppendLine("## 风险");
        if (report.Plan?.Risks is { Count: > 0 } risks)
        {
            foreach (var risk in risks)
            {
                builder.AppendLine($"- [{risk.Severity.ToString().ToLowerInvariant()}] {OneLine(risk.Description)}（{risk.Id}）");
            }
        }
        else
        {
            builder.AppendLine("- （无）");
        }

        builder.AppendLine();
        builder.AppendLine("## 补丁点");
        if (report.Plan?.PatchPoints is { Count: > 0 } points)
        {
            foreach (var point in points)
            {
                builder.AppendLine($"- {point.Id}（{point.Kind.ToString().ToLowerInvariant()}）：{OneLine(point.Description)}；目标 `{point.Target}`；置信度 {point.Confidence:0.00}");
            }
        }
        else
        {
            builder.AppendLine("- （无）");
        }

        builder.AppendLine();
        builder.AppendLine("## 人工待办与说明");
        if (report.Plan?.ManualTodo is { Count: > 0 } todos)
        {
            foreach (var todo in todos)
            {
                var blocking = todo.Blocking ? "【阻塞】" : string.Empty;
                builder.AppendLine($"- {blocking}{todo.Id}：{OneLine(todo.Title)}{(string.IsNullOrWhiteSpace(todo.Detail) ? string.Empty : $"——{OneLine(todo.Detail)}")}");
            }
        }
        else
        {
            builder.AppendLine("- （无）");
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine("本报告由 Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo） 自动生成。未实测项不作为通过依据；覆盖声明不等于全部可用。");
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string heading, IReadOnlyCollection<string> items)
    {
        builder.AppendLine();
        builder.AppendLine($"### {heading}");
        if (items.Count == 0)
        {
            builder.AppendLine("- （无）");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- {OneLine(item)}");
        }
    }

    private static void AppendUnverified(StringBuilder builder, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
        {
            builder.AppendLine("- （无显式未验证项；这不等于全部已验证）");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- 未验证：{OneLine(item)}");
        }
    }

    private static string RouteText(PlanRoute? route) => route switch
    {
        PlanRoute.Automatic => "A 自动",
        PlanRoute.AutomaticWithConfirmation => "B 自动 + 人工确认",
        PlanRoute.SemiAutomatic => "C 半自动",
        PlanRoute.Unsupported => "D 不支持",
        _ => "未规划",
    };

    private static string OneLine(string? text)
        => (text ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
