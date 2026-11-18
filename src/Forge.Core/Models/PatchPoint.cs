namespace GalA11yForge.Core.Models;

public enum PatchPointKind
{
    Hook = 0,
    Config = 1,
    Script = 2,
    Asset = 3,
    Injection = 4,
    Other = 5,
}

/// <summary>补丁点。Target 是具体钩子/配置键/脚本位置；CandidateId 可指向 TextCandidate.Id。</summary>
public sealed class PatchPoint
{
    public string Id { get; set; } = string.Empty;

    public PatchPointKind Kind { get; set; } = PatchPointKind.Other;

    public string Target { get; set; } = string.Empty;

    public string? CandidateId { get; set; }

    /// <summary>0..1 置信度。</summary>
    public double Confidence { get; set; }

    public string Description { get; set; } = string.Empty;
}
