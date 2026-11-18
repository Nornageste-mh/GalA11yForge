namespace GalA11yForge.Core.Models;

public enum ArtifactKind
{
    Source = 0,
    Project = 1,
    Config = 2,
    Script = 3,
    Binary = 4,
    Documentation = 5,
    Other = 6,
}

/// <summary>计划生成物。RelativePath 相对补丁工程根目录，禁止 ".." 或绝对路径。</summary>
public sealed class GeneratedArtifact
{
    public string Name { get; set; } = string.Empty;

    public ArtifactKind Kind { get; set; } = ArtifactKind.Other;

    public string RelativePath { get; set; } = string.Empty;

    public string? Description { get; set; }
}
