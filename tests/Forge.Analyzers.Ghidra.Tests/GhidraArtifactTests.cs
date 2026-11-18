using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Analyzers.Ghidra.Tests;

public sealed class GhidraArtifactTests
{
    [Fact]
    public void FromOutcome_Completed_EmbedsScriptAndConvertsStatus()
    {
        var outcome = new GhidraAnalysisOutcome
        {
            Status = GhidraAnalysisStatus.Completed,
            Available = true,
            ToolVersion = "12.1.4",
            ExecutablePath = @"C:\samples\SenrenBanka.exe",
            ExecutableSha256 = new string('a', 64),
            ExecutableSizeBytes = 4_583_168,
            Elapsed = TimeSpan.FromSeconds(12.34),
            RawScriptJson = Fixtures.CandidatesJson(),
            Candidates = new List<TextCandidate>
            {
                new()
                {
                    Id = "ghidra-01-0x401000",
                    Kind = TextCandidateKind.NativeApi,
                    Source = "native-api:ghidra!0x401000 FUN_00401000",
                    Score = 47,
                },
            },
            Summary = "Ghidra 分析完成：1 个候选（最高 47/100），耗时 12s。",
        };

        var artifact = GhidraArtifact.FromOutcome(outcome, "kirikiriz");

        Assert.Equal(1, artifact.SchemaVersion);
        Assert.Equal("ghidra", artifact.Backend);
        Assert.Equal("completed", artifact.Status);
        Assert.True(artifact.Available);
        Assert.Equal("kirikiriz", artifact.EngineFamily);
        Assert.Equal("12.1.4", artifact.GhidraVersion);
        Assert.Equal(12.3, artifact.ElapsedSeconds);
        Assert.True(artifact.Script.HasValue);
        Assert.Equal(JsonValueKind.Object, artifact.Script!.Value.ValueKind);
        var candidate = Assert.Single(artifact.Candidates);
        Assert.Equal(TextCandidateKind.NativeApi, candidate.Kind);

        var json = ForgeJson.Serialize(artifact);
        Assert.Contains("\"status\": \"completed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"engineFamily\": \"kirikiriz\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schema\": \"gala11y.ghidra.candidates/1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"candidates\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"native_api\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FromOutcome_Unavailable_KeepsHonestStatusAndError()
    {
        var outcome = new GhidraAnalysisOutcome
        {
            Status = GhidraAnalysisStatus.NotAvailable,
            Available = false,
            ErrorCode = "E_DEPENDENCY_MISSING",
            ErrorMessage = "未启用高级分析（未找到 Ghidra）。",
            Suggestion = "安装 Ghidra 或用 --option ghidra.path=... 指定。",
            Elapsed = TimeSpan.FromMilliseconds(15),
        };

        var artifact = GhidraArtifact.FromOutcome(outcome, "unknown");

        Assert.Equal("not_available", artifact.Status);
        Assert.False(artifact.Available);
        Assert.Equal("E_DEPENDENCY_MISSING", artifact.ErrorCode);
        Assert.Null(artifact.Script);
        Assert.Empty(artifact.Candidates);

        var json = ForgeJson.Serialize(artifact);
        Assert.Contains("\"status\": \"not_available\"", json, StringComparison.Ordinal);
        Assert.Contains("\"errorCode\": \"E_DEPENDENCY_MISSING\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"script\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusText_UsesSnakeCaseForAllStatuses()
    {
        Assert.Equal("completed", GhidraArtifact.StatusText(GhidraAnalysisStatus.Completed));
        Assert.Equal("not_available", GhidraArtifact.StatusText(GhidraAnalysisStatus.NotAvailable));
        Assert.Equal("invalid_output", GhidraArtifact.StatusText(GhidraAnalysisStatus.InvalidOutput));
        Assert.Equal("non_zero_exit", GhidraArtifact.StatusText(GhidraAnalysisStatus.NonZeroExit));
    }

    [Fact]
    public void FromOutcome_Timeout_KeepsSizeAndEstimateForHints()
    {
        var outcome = new GhidraAnalysisOutcome
        {
            Status = GhidraAnalysisStatus.Timeout,
            Available = true,
            ExecutablePath = @"D:\games\To the Moon\To the Moon.exe",
            ExecutableSizeBytes = 8L * 1024 * 1024,
            EstimatedSeconds = 710,
            SuggestedTimeoutSeconds = 770,
            Elapsed = TimeSpan.FromSeconds(600.1),
            ErrorCode = "E_PARTIAL",
            ErrorMessage = "Ghidra 分析超时（600s），已终止进程树。",
            Suggestion = "提高 ghidra.timeoutSeconds 后重跑。",
        };

        var artifact = GhidraArtifact.FromOutcome(outcome, "rm_xp_vx_ace");

        Assert.Equal("timeout", artifact.Status);
        Assert.Equal(8L * 1024 * 1024, artifact.ExecutableSizeBytes);
        Assert.Equal(710, artifact.EstimatedSeconds);
        Assert.Equal(770, artifact.SuggestedTimeoutSeconds);

        var json = ForgeJson.Serialize(artifact);
        Assert.Contains("\"estimatedSeconds\": 710", json, StringComparison.Ordinal);
        Assert.Contains("\"executableSizeBytes\": " + (8L * 1024 * 1024), json, StringComparison.Ordinal);
    }

    [Fact]
    public void FromOutcome_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => GhidraArtifact.FromOutcome(null!, "unknown"));
    }
}
