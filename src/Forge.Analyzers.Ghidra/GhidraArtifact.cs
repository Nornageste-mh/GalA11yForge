using System.Text.Json;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>
/// 01-analyze/analysis-ghidra.json 的落盘结构。脚本原始 JSON 原样嵌在 script 字段里，
/// 后面即使脚本 schema 演进，也能追溯当时到底看到了什么。
/// </summary>
public sealed class GhidraArtifact
{
    public int SchemaVersion { get; init; } = 1;
    public string Backend { get; init; } = GhidraAnalyzerBackend.BackendIdValue;
    public string Status { get; init; } = "unknown";
    public bool Available { get; init; }
    public string? EngineFamily { get; init; }
    public string? ExecutablePath { get; init; }
    public List<string> ExecutableCandidates { get; init; } = new();
    public string? ExecutableSha256 { get; init; }
    public long ExecutableSizeBytes { get; init; }

    /// <summary>按可执行文件体积粗估的耗时（秒），仅用于提示，不参与判定。</summary>
    public int? EstimatedSeconds { get; init; }

    /// <summary>超时后建议的 timeoutSeconds（秒），仅用于给出可复制建议。</summary>
    public int? SuggestedTimeoutSeconds { get; init; }

    public string? GhidraVersion { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? CommandLine { get; init; }
    public string? CandidatesJsonPath { get; init; }
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
    public JsonElement? Script { get; init; }
    public List<TextCandidate> Candidates { get; init; } = new();
    public CoverageReport Coverage { get; init; } = new();
    public List<string> Unverified { get; init; } = new();
    public List<RiskItem> Risks { get; init; } = new();
    public List<string> Evidence { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Suggestion { get; init; }

    public static GhidraArtifact FromOutcome(GhidraAnalysisOutcome outcome, string? engineFamily = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        JsonElement? script = null;
        if (!string.IsNullOrWhiteSpace(outcome.RawScriptJson))
        {
            using var document = JsonDocument.Parse(outcome.RawScriptJson);
            script = document.RootElement.Clone();
        }

        return new GhidraArtifact
        {
            SchemaVersion = 1,
            Backend = outcome.BackendId,
            Status = StatusText(outcome.Status),
            Available = outcome.Available,
            EngineFamily = engineFamily,
            ExecutablePath = outcome.ExecutablePath,
            ExecutableCandidates = outcome.ExecutableCandidates,
            ExecutableSha256 = outcome.ExecutableSha256,
            ExecutableSizeBytes = outcome.ExecutableSizeBytes,
            EstimatedSeconds = outcome.EstimatedSeconds,
            SuggestedTimeoutSeconds = outcome.SuggestedTimeoutSeconds,
            GhidraVersion = outcome.ToolVersion ?? outcome.Location?.Version,
            ElapsedSeconds = Math.Round(outcome.Elapsed.TotalSeconds, 1, MidpointRounding.AwayFromZero),
            CommandLine = outcome.CommandLine,
            CandidatesJsonPath = outcome.CandidatesJsonPath,
            StdoutPath = outcome.StandardOutputPath,
            StderrPath = outcome.StandardErrorPath,
            Script = script,
            Candidates = outcome.Candidates,
            Coverage = outcome.Coverage,
            Unverified = outcome.Unverified,
            Risks = outcome.Risks,
            Evidence = outcome.Evidence,
            Notes = outcome.Notes,
            ErrorCode = outcome.ErrorCode,
            ErrorMessage = outcome.ErrorMessage,
            Suggestion = outcome.Suggestion,
        };
    }

    public static string StatusText(GhidraAnalysisStatus status) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}
