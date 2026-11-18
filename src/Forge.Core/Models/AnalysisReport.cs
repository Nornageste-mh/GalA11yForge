namespace GalA11yForge.Core.Models;

/// <summary>分析结果（01-analyze/analysis.json）。必须带 coverage 与 unverified，报告侧据此生成覆盖声明。</summary>
public sealed class AnalysisReport
{
    public int SchemaVersion { get; set; } = 1;

    public string? GamePath { get; set; }

    public EngineDetection Detection { get; set; } = new();

    public List<TextCandidate> Candidates { get; set; } = new();

    public CoverageReport Coverage { get; set; } = new();

    public List<RiskItem> Risks { get; set; } = new();

    /// <summary>未验证项：没有实测证据、不能当作已通过的结论。</summary>
    public List<string> Unverified { get; set; } = new();
}
