using System.Text;
using System.Text.Json;
using GalA11yForge.Analyzers.Ghidra;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Cli;

internal sealed class GhidraStepResult
{
    public required AnalysisReport Analysis { get; init; }
    public required Dictionary<string, object?> Section { get; init; }
    public required string HumanLine { get; init; }
    public bool HardFailure { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Suggestion { get; init; }
    public string? ArtifactPath { get; init; }
}

/// <summary>
/// analyze --advanced 的 Ghidra 接线层：跑后端、落 analysis-ghidra.json、把候选/未验证/风险合并进 analysis.json。
/// 只在这里调用 headless（plan/generate 等命令不重复付 2–4 分钟的分析成本）。
/// </summary>
internal static class GhidraAdvanced
{
    public const string ArtifactFileName = "analysis-ghidra.json";

    public static async Task<GhidraStepResult> RunAsync(
        string gameDirectory,
        string workspaceRoot,
        string stageDirectory,
        string engineFamily,
        AnalysisReport analysis,
        IReadOnlyDictionary<string, JsonElement>? profileOptions,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var options = GhidraBackendOptions.FromProfile(profileOptions, workspaceRoot);
        var optionWarnings = options.Warnings.ToList();

        if (!options.Enabled)
        {
            return BuildWithoutRun(
                analysis,
                status: "disabled",
                available: false,
                humanLine: "已禁用高级分析（ghidra.enabled=false）。",
                unverified: new[] { "已按配置禁用 Ghidra 高级分析，本次没有原生代码证据。" },
                optionWarnings: optionWarnings);
        }

        if (dryRun)
        {
            var probe = GhidraLocator.Locate(new GhidraLocationRequest { ExplicitPath = options.GhidraPath });
            var line = probe.Available
                ? $"试运行：已找到 Ghidra {probe.Version ?? "(版本未知)"}，跳过实际分析（headless 冷跑约 2–4 分钟）。"
                : "试运行：未找到 Ghidra；实际运行会跳过高级分析并写入 unverified。";
            var dryRunSection = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["available"] = probe.Available,
                ["status"] = "dry_run",
                ["ghidraVersion"] = probe.Version,
                ["elapsedSeconds"] = 0.0,
                ["candidates"] = 0,
                ["unverified"] = Array.Empty<string>(),
                ["manualTodo"] = Array.Empty<string>(),
                ["artifact"] = null,
                ["optionWarnings"] = optionWarnings,
            };
            return new GhidraStepResult
            {
                Analysis = analysis,
                Section = dryRunSection,
                HumanLine = line,
            };
        }

        var backend = new GhidraAnalyzerBackend(options);
        var outcome = await backend.AnalyzeDetailedAsync(
            new AnalyzerBackendRequest
            {
                GameDirectory = gameDirectory,
                ExecutablePath = options.ExecutablePath,
                TimeoutSeconds = options.TimeoutSeconds,
                Advanced = true,
            },
            cancellationToken).ConfigureAwait(false);

        Merge(analysis, outcome);

        var artifactPath = Path.Combine(stageDirectory, ArtifactFileName);
        Directory.CreateDirectory(stageDirectory);
        var artifact = GhidraArtifact.FromOutcome(outcome, engineFamily);
        await File.WriteAllTextAsync(
            artifactPath,
            ForgeJson.Serialize(artifact),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        var section = BuildSection(outcome, artifactPath, optionWarnings);
        var human = BuildHumanLine(outcome, artifactPath);
        if (outcome.ExecutableCandidates.Count > 1)
        {
            human += $" 另有 {outcome.ExecutableCandidates.Count - 1} 个候选可执行文件，可用 --option ghidra.executablePath=<路径> 指定。";
        }

        if (outcome.EstimatedSeconds is int estimatedSeconds &&
            outcome.Status is GhidraAnalysisStatus.Timeout or GhidraAnalysisStatus.Canceled or GhidraAnalysisStatus.NonZeroExit)
        {
            var humanEstimate = estimatedSeconds >= 600
                ? $"{estimatedSeconds / 60} 分钟"
                : $"{estimatedSeconds} 秒";
            var timeoutHint = outcome.SuggestedTimeoutSeconds is int suggested
                ? $"可调 --option ghidra.timeoutSeconds={suggested}"
                : "可调 --option ghidra.timeoutSeconds=<更大值>";
            human += $" 按文件大小粗估，分析这枚 exe 大约需要 {humanEstimate}；超时不够时{timeoutHint}。";
        }

        return new GhidraStepResult
        {
            Analysis = analysis,
            Section = section,
            HumanLine = human,
            HardFailure = outcome.IsHardFailure,
            ErrorCode = outcome.ErrorCode,
            ErrorMessage = outcome.ErrorMessage,
            Suggestion = outcome.Suggestion,
            ArtifactPath = artifactPath,
        };
    }

    private static GhidraStepResult BuildWithoutRun(
        AnalysisReport analysis,
        string status,
        bool available,
        string humanLine,
        IEnumerable<string> unverified,
        List<string> optionWarnings)
    {
        var unverifiedList = unverified.ToList();
        foreach (var item in unverifiedList)
        {
            if (!analysis.Unverified.Contains(item))
            {
                analysis.Unverified.Add(item);
            }
        }

        return new GhidraStepResult
        {
            Analysis = analysis,
            Section = new Dictionary<string, object?>
            {
                ["enabled"] = false,
                ["available"] = available,
                ["status"] = status,
                ["ghidraVersion"] = null,
                ["elapsedSeconds"] = 0.0,
                ["candidates"] = 0,
                ["unverified"] = unverifiedList,
                ["manualTodo"] = Array.Empty<string>(),
                ["artifact"] = null,
                ["optionWarnings"] = optionWarnings,
            },
            HumanLine = humanLine,
        };
    }

    private static Dictionary<string, object?> BuildSection(
        GhidraAnalysisOutcome outcome,
        string artifactPath,
        List<string> optionWarnings)
    {
        var section = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["available"] = outcome.Available,
            ["status"] = GhidraArtifact.StatusText(outcome.Status),
            ["ghidraVersion"] = outcome.ToolVersion ?? outcome.Location?.Version,
            ["elapsedSeconds"] = Math.Round(outcome.Elapsed.TotalSeconds, 1, MidpointRounding.AwayFromZero),
            ["executable"] = outcome.ExecutablePath,
            ["executableSizeBytes"] = outcome.ExecutableSizeBytes,
            ["estimatedSeconds"] = outcome.EstimatedSeconds,
            ["suggestedTimeoutSeconds"] = outcome.SuggestedTimeoutSeconds,
            ["executableCandidates"] = outcome.ExecutableCandidates,
            ["candidates"] = outcome.Candidates.Count,
            ["unverified"] = outcome.Unverified,
            ["manualTodo"] = outcome.Coverage.ManualTodo,
            ["covered"] = outcome.Coverage.Covered,
            ["notCovered"] = outcome.Coverage.NotCovered,
            ["risks"] = outcome.Risks,
            ["notes"] = outcome.Notes,
            ["artifact"] = artifactPath,
            ["optionWarnings"] = optionWarnings,
        };

        if (outcome.ErrorCode is not null)
        {
            section["error"] = new Dictionary<string, object?>
            {
                ["code"] = outcome.ErrorCode,
                ["message"] = outcome.ErrorMessage,
                ["suggestion"] = outcome.Suggestion,
            };
        }
        else
        {
            section["error"] = null;
        }

        if (outcome.Location is not null)
        {
            section["location"] = new Dictionary<string, object?>
            {
                ["available"] = outcome.Location.Available,
                ["home"] = outcome.Location.HomeDirectory,
                ["source"] = outcome.Location.Source,
                ["probes"] = outcome.Location.Probes,
            };
        }

        return section;
    }

    private static string BuildHumanLine(GhidraAnalysisOutcome outcome, string artifactPath)
    {
        var seconds = outcome.Elapsed.TotalSeconds;
        return outcome.Status switch
        {
            GhidraAnalysisStatus.Completed =>
                $"Ghidra {outcome.ToolVersion ?? outcome.Location?.Version ?? "(版本未知)"}：{outcome.Candidates.Count} 个候选，耗时 {seconds:F0}s；产物 {ArtifactFileName}。",
            GhidraAnalysisStatus.NotAvailable =>
                "未启用高级分析（未找到 Ghidra）：安装 Ghidra 12.x 后用 --option ghidra.path=<目录> 指定，或设置 GHIDRA_HOME。",
            GhidraAnalysisStatus.Disabled =>
                "已禁用高级分析（ghidra.enabled=false）。",
            GhidraAnalysisStatus.NoTarget =>
                "未找到可分析的可执行文件（游戏根目录没有 .exe），跳过高级分析。",
            GhidraAnalysisStatus.Timeout =>
                $"Ghidra 分析超时（{seconds:F0}s），已终止进程树；结果不完整，日志见 work 目录。",
            GhidraAnalysisStatus.Canceled =>
                "Ghidra 分析已取消。",
            _ =>
                $"Ghidra 分析失败（{GhidraArtifact.StatusText(outcome.Status)}）：{outcome.ErrorMessage}",
        };
    }

    /// <summary>把高级分析结果并入适配器结果；失败也保留 unverified/coverage，诚实优先。</summary>
    private static void Merge(AnalysisReport analysis, GhidraAnalysisOutcome outcome)
    {
        analysis.Candidates.AddRange(outcome.Candidates);
        analysis.Candidates.Sort((left, right) => right.Score.CompareTo(left.Score));

        AppendDistinct(analysis.Coverage.Covered, outcome.Coverage.Covered);
        AppendDistinct(analysis.Coverage.NotCovered, outcome.Coverage.NotCovered);
        AppendDistinct(analysis.Coverage.ManualTodo, outcome.Coverage.ManualTodo);
        AppendDistinct(analysis.Unverified, outcome.Unverified);

        var existingRiskIds = analysis.Risks.Select(risk => risk.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var risk in outcome.Risks)
        {
            if (existingRiskIds.Add(risk.Id))
            {
                analysis.Risks.Add(risk);
            }
        }
    }

    private static void AppendDistinct(List<string> target, IEnumerable<string> items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item) && !target.Contains(item))
            {
                target.Add(item);
            }
        }
    }
}
