namespace GalA11yForge.Verify.Models;

/// <summary>
/// 部署前基线中的一个文件。只记录路径、大小、哈希，不保存文件内容。
/// <see cref="Exists"/> 为 false 时表示采集时该文件尚不存在（安装目标），用于回滚后核对“仍然不存在”。
/// </summary>
public sealed class BaselineFileEntry
{
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>采集时文件是否存在。旧版基线没有该字段，反序列化缺省为 true。</summary>
    public bool Exists { get; set; } = true;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// 部署前游戏原文件基线。用于回答“账本之外，游戏原文件有没有被改过”。
/// 安装基线额外记录本次部署会触碰的目标（含“原本不存在”），回滚后可逐文件核对。
/// </summary>
public sealed class GameBaseline
{
    /// <summary>docs/09 公共信封中的产物类型。</summary>
    public const string ArtifactName = "baseline";

    public int SchemaVersion { get; set; } = 1;

    /// <summary>产物类型；与 docs/09 的 artifact 字段对齐。旧版基线缺省为 baseline。</summary>
    public string Artifact { get; set; } = ArtifactName;

    public string GamePath { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public List<BaselineFileEntry> Files { get; set; } = new();
}
