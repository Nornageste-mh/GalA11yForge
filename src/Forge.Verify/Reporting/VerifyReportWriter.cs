using System.Globalization;
using System.Text;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Reporting;

public sealed class VerifyReportOutput
{
    public string JsonPath { get; init; } = string.Empty;

    public string MarkdownPath { get; init; } = string.Empty;
}

/// <summary>
/// 把 VerifyReport 写成 verify.json + verify.md。UTF-8 无 BOM，拒绝写进游戏目录。
/// Markdown 固定包含“覆盖声明”和“未验证项”，并明确 skip 不是通过。
/// </summary>
public sealed class VerifyReportWriter
{
    private const string NoUnverified = "（无显式未验证项；这不等于全部已验证）";

    private readonly string? _gameDirectory;

    public VerifyReportWriter(string? gameDirectory = null)
    {
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory) ? null : gameDirectory;
    }

    public async Task<ForgeResult<VerifyReportOutput>> WriteAsync(
        VerifyReport report,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return ForgeResult<VerifyReportOutput>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "报告输出目录不能为空。",
                "指定工作区内的 reports/ 目录。"));
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(outputDirectory);
            if (_gameDirectory is not null && PathSafety.IsUnder(_gameDirectory, directory))
            {
                return ForgeResult<VerifyReportOutput>.Failure(ForgeError.Create(
                    ForgeErrorCodes.GameWriteDenied,
                    $"拒绝把验证报告写进游戏目录：{directory}",
                    "报告属于工作区产物，请写到 06-verify/ 或 reports/。"));
            }

            Directory.CreateDirectory(directory);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<VerifyReportOutput>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ForgeResult<VerifyReportOutput>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"创建报告目录失败：{ex.Message}",
                "检查目录权限与磁盘空间。"));
        }

        var jsonPath = Path.Combine(directory, "verify.json");
        var markdownPath = Path.Combine(directory, "verify.md");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            await File.WriteAllTextAsync(
                jsonPath,
                ForgeJson.Serialize(report),
                encoding,
                cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                markdownPath,
                BuildMarkdown(report),
                encoding,
                cancellationToken).ConfigureAwait(false);

            return ForgeResult<VerifyReportOutput>.Success(new VerifyReportOutput
            {
                JsonPath = jsonPath,
                MarkdownPath = markdownPath,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ForgeResult<VerifyReportOutput>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"写入验证报告失败：{ex.Message}",
                "检查目录权限与磁盘空间。"));
        }
    }

    public static async Task<ForgeResult<VerifyReportOutput>> VerifyAndWriteAsync(
        VerifyOptions options,
        string outputDirectory,
        VerificationService? service = null,
        CancellationToken cancellationToken = default)
    {
        var verify = await (service ?? new VerificationService())
            .VerifyAsync(options, cancellationToken)
            .ConfigureAwait(false);
        if (!verify.Ok)
        {
            return ForgeResult<VerifyReportOutput>.Failure(verify.Error!);
        }

        var writer = new VerifyReportWriter(options.GameDirectory);
        return await writer.WriteAsync(verify.Value!, outputDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildMarkdown(VerifyReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 安装后验证报告");
        builder.AppendLine();
        builder.AppendLine($"- 游戏：`{report.GamePath ?? "（未记录）"}`");
        builder.AppendLine($"- 游戏 slug：`{report.Slug ?? "（未记录）"}`");
        builder.AppendLine($"- 账本：`{report.ManifestPath ?? "（未记录）"}`");
        builder.AppendLine($"- 部署 ID：`{report.DeploymentId ?? "（未记录）"}`");
        builder.AppendLine($"- 验证模式：{ModeText(report.Mode)}");
        builder.AppendLine($"- 生成时间：{report.GeneratedAt}");
        builder.AppendLine($"- 工具版本：{report.ToolVersion}");
        builder.AppendLine($"- 结论：**{OverallText(report.Overall)}**");
        builder.AppendLine($"- 检查统计：通过 {report.PassCount} / 失败 {report.FailCount} / 跳过 {report.SkipCount}");
        builder.AppendLine();

        builder.AppendLine("## 检查结果");
        builder.AppendLine();
        if (report.Checks.Count == 0)
        {
            builder.AppendLine("（无检查项；没有检查项本身即为不通过。）");
        }
        else
        {
            builder.AppendLine("| ID | 检查项 | 优先级 | 状态 | 说明 |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (var check in report.Checks)
            {
                builder.AppendLine(
                    $"| {Cell(check.Id)} | {Cell(check.Name)} | {Cell(check.Priority)} | {Cell(StatusText(check.Status))} | {Cell(check.Detail)} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 覆盖声明");
        builder.AppendLine();
        builder.AppendLine(
            "本次验证为离线静态检查，覆盖：安装账本与磁盘一致性、BepInEx 插件就位、BepInEx 日志关键行、备份完整性、账本之外原文件的基线比对、语音后端组件是否存在。"
            + "不覆盖：游戏内实际朗读、焦点顺序、选项/回听/存档等运行期行为；也不验证语音是否真的出声。");
        if (report.Mode == VerifyMode.Install)
        {
            builder.AppendLine("未执行卸载回滚；补丁可还原性需要在卸载后以回滚模式再次运行验证。");
        }
        else
        {
            builder.AppendLine("回滚后原版能否启动、Steam 完整性校验是否通过，未覆盖。");
        }

        builder.AppendLine();
        builder.AppendLine("## 未验证项");
        builder.AppendLine();
        if (report.Unverified.Count == 0)
        {
            builder.AppendLine(NoUnverified);
        }
        else
        {
            foreach (var item in report.Unverified)
            {
                builder.AppendLine($"- {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 清理与还原");
        builder.AppendLine();
        builder.AppendLine($"- 是否要求清理：{(report.Cleanup.Required ? "是" : "否")}");
        builder.AppendLine($"- 是否已卸载/还原：{(report.Cleanup.Required ? (report.Cleanup.Uninstalled ? "是" : "否") : "不适用")}");
        builder.AppendLine($"- 哈希一致性：{HashMatchText(report.Cleanup.HashMatch)}");
        if (!string.IsNullOrWhiteSpace(report.Cleanup.Detail))
        {
            builder.AppendLine($"- 说明：{report.Cleanup.Detail}");
        }

        builder.AppendLine("- 明细：");
        if (report.Cleanup.Restored.Count == 0)
        {
            builder.AppendLine("  - （无）");
        }
        else
        {
            foreach (var item in report.Cleanup.Restored)
            {
                builder.AppendLine($"  - {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 环境事实");
        builder.AppendLine();
        if (report.Environment is null)
        {
            builder.AppendLine("（本次未探测语音后端；这不代表语音后端不可用。）");
        }
        else
        {
            builder.AppendLine("| 后端 | 可用 | 事实 |");
            builder.AppendLine("|---|---|---|");
            foreach (var backend in report.Environment.Backends)
            {
                builder.AppendLine(
                    $"| {Cell(backend.DisplayName)} | {Cell(backend.Available switch { true => "可用", false => "不可用", null => "未确认" })} | {Cell(backend.Fact)} |");
            }

            if (report.Environment.Notes.Count > 0)
            {
                builder.AppendLine();
                foreach (var note in report.Environment.Notes)
                {
                    builder.AppendLine($"- {note}");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 指标");
        builder.AppendLine();
        if (report.Metrics.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var pair in report.Metrics.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 证据");
        builder.AppendLine();
        if (report.Evidence.Count == 0)
        {
            builder.AppendLine("（无）");
        }
        else
        {
            foreach (var item in report.Evidence)
            {
                builder.AppendLine($"- {item}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("> 说明：skip 表示该项本次未验证，不是通过。overall=pass 仅表示本报告列出的离线检查全部通过，不代表游戏可正常朗读。");
        return builder.ToString();
    }

    private static string OverallText(string? overall) => overall switch
    {
        VerifyOverall.Pass => "通过（离线检查全部通过）",
        VerifyOverall.PassWithSkips => "通过但有跳过项（见未验证项）",
        VerifyOverall.Partial => "部分通过（存在非 P0 失败）",
        VerifyOverall.Fail => "未通过",
        _ => $"未知（{overall}）",
    };

    private static string ModeText(VerifyMode mode)
        => mode == VerifyMode.Rollback ? "回滚后检查" : "安装后检查";

    private static string StatusText(VerifyCheckStatus status) => status switch
    {
        VerifyCheckStatus.Pass => "通过",
        VerifyCheckStatus.Fail => "失败",
        VerifyCheckStatus.Skip => "跳过",
        _ => status.ToString(),
    };

    private static string HashMatchText(bool? hashMatch) => hashMatch switch
    {
        true => "通过（已比对哈希全部一致）",
        false => "未通过（存在哈希不一致）",
        null => "未比对（没有可用的安装前哈希）",
    };

    private static string Cell(string? value)
        => (value ?? string.Empty)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
}
