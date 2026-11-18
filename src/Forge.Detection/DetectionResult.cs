using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Detection;

/// <summary>
/// 一条命中规则对应的候选引擎。
/// 同一个引擎可能被多条规则命中（例如 Unity Mono 的 R01 与 R26），此时会有多个候选。
/// </summary>
public sealed class DetectionCandidate
{
    /// <summary>引擎 id（research 原始 id，如 unity_mono / kirikiri_z）。</summary>
    public string Engine { get; init; } = "unknown";

    /// <summary>引擎显示名（来自指纹库 engines[].name）。</summary>
    public string EngineName { get; init; } = string.Empty;

    /// <summary>引擎大类（指纹库 engines[].family，如 Unity / KiriKiri）。注意与 <see cref="EngineDetection.Family"/>（引擎 id）区分。</summary>
    public string EngineFamily { get; init; } = string.Empty;

    /// <summary>研究难度分级 A/B/B+/C/C-/D（指纹库 difficulty）。</summary>
    public string Difficulty { get; init; } = string.Empty;

    /// <summary>命中的规则 id（如 R01）。</summary>
    public string RuleId { get; init; } = string.Empty;

    /// <summary>规则分值 0..100（来自指纹库 rules[].score）。</summary>
    public int Score { get; init; }

    /// <summary>置信度 = Score / 100.0，取值范围 0..1。</summary>
    public double Confidence => Score / 100.0;

    /// <summary>命中条件的证据行（file:/measured:/dir: 前缀），非空。</summary>
    public List<string> Evidence { get; init; } = new();

    /// <summary>被消歧抑制的原因；非 null 表示该候选已被更强的引擎取代。</summary>
    public string? SuppressedBy { get; internal set; }

    /// <summary>是否被消歧规则抑制（例如 KKZ 存在 .sig 时 KiriKiri 2 被压制）。</summary>
    public bool IsSuppressed => SuppressedBy is not null;
}

/// <summary>
/// 一次检测的完整结果：主 <see cref="EngineDetection"/>（可直接序列化进 ForgeResult）
/// + 全部候选（含被抑制的）+ 扫描统计。
/// </summary>
public sealed class DetectionResult
{
    /// <summary>主检测结论；未识别时 Family="unknown"、Confidence=0。</summary>
    public EngineDetection Primary { get; init; } = new();

    /// <summary>按分值降序排列的全部命中候选（包含被消歧抑制的项）。</summary>
    public IReadOnlyList<DetectionCandidate> Candidates { get; init; } = Array.Empty<DetectionCandidate>();

    /// <summary>被检测目录的绝对路径。</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>扫描到的文件数。</summary>
    public int FilesScanned { get; init; }

    /// <summary>扫描到的目录数（不含根目录本身）。</summary>
    public int DirectoriesScanned { get; init; }

    /// <summary>超出本层上限被截断时为 true（结果可能漏检，证据里有 warn:）。</summary>
    public bool Truncated { get; init; }

    /// <summary>检测耗时（含扫描）。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>引擎 id；unknown 表示未识别。</summary>
    public string Family => Primary.Family;

    /// <summary>主结论置信度 0..1。</summary>
    public double Confidence => Primary.Confidence;

    /// <summary>是否识别出已知引擎。</summary>
    public bool IsKnown => !string.Equals(Primary.Family, "unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>报告用语：高（≥85 分）/中（≥60 分）/低（&lt;60 分）/无（未识别）。</summary>
    public string Verdict =>
        !IsKnown ? "无"
        : Primary.Confidence >= 0.85 ? "高"
        : Primary.Confidence >= 0.60 ? "中"
        : "低";

    /// <summary>指纹库 scoring 约定：score &lt; 50 只报「疑似」，不自动下结论。</summary>
    public bool IsSuspected => IsKnown && Primary.Confidence < 0.5;

    /// <summary>未被抑制的最高分候选（无命中时为 null）。</summary>
    public DetectionCandidate? BestCandidate => Candidates.FirstOrDefault(c => !c.IsSuppressed);

    /// <summary>按契约 §3.1 的 camelCase 序列化主结论（null 字段不写）。</summary>
    public string ToJson() => ForgeJson.Serialize(Primary);

    /// <summary>任务卡要求的 (engine, confidence, evidence) 元组视图。</summary>
    public List<(string Engine, double Confidence, IReadOnlyList<string> Evidence)> ToTuples() =>
        Candidates
            .Select(c => (c.Engine, c.Confidence, (IReadOnlyList<string>)c.Evidence))
            .ToList();
}
