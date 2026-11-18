namespace GalA11yForge.Core.Models;

/// <summary>
/// 导出报告聚合体：JSON 与 Markdown 使用同一份数据。
/// Coverage 与 Unverified 永远序列化（即使为空），避免读者误以为“全部覆盖/全部验证”。
/// </summary>
public sealed class ReportDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string? GameSlug { get; set; }

    public string? GamePath { get; set; }

    /// <summary>ISO 8601 时间；由调用方提供，便于测试与复现。</summary>
    public string? GeneratedAt { get; set; }

    public string ToolVersion { get; set; } = ForgeInfo.Version;

    public EngineDetection? Detection { get; set; }

    public AnalysisReport? Analysis { get; set; }

    public PatchPlan? Plan { get; set; }

    public CoverageReport Coverage { get; set; } = new();

    public List<string> Unverified { get; set; } = new();

    /// <summary>从分析结果构造报告，自动带上 coverage/unverified。</summary>
    public static ReportDocument FromAnalysis(AnalysisReport analysis, PatchPlan? plan = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return new ReportDocument
        {
            GameSlug = plan?.GameSlug,
            GamePath = analysis.GamePath,
            Detection = analysis.Detection,
            Analysis = analysis,
            Plan = plan,
            Coverage = analysis.Coverage,
            Unverified = analysis.Unverified.ToList(),
        };
    }
}

/// <summary>报告写出的两个路径。</summary>
public sealed class ReportOutput
{
    public string JsonPath { get; set; } = string.Empty;

    public string MarkdownPath { get; set; } = string.Empty;
}
