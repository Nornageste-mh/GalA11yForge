using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Generators.Internal;

/// <summary>
/// <c>forge.manifest.json</c>：生成器的写盘账本。
///
/// <para>
/// 两个用途：1) 重复生成时只清理「上次是本生成器写的、这次不再生成」的文件，绝不碰用户的文件；
/// 2) 输出目录里已有 manifest 才允许覆盖，否则视为用户目录，直接拒绝（E_ARG_INVALID）。
/// </para>
/// </summary>
internal sealed class GeneratorManifest
{
    public const string FileName = "forge.manifest.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Generator { get; set; } = "unity_mono";

    public string GeneratorVersion { get; set; } = string.Empty;

    public string FrameworkFingerprint { get; set; } = string.Empty;

    public string GameSlug { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public string PluginGuid { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;

    public string Engine { get; set; } = "unity_mono";

    /// <summary>不含 manifest 自身，按 path 序数排序。</summary>
    public List<ManifestFileEntry> Files { get; set; } = new();

    /// <summary>语音桥来源及其内容哈希，按 id 序数排序。</summary>
    public List<ManifestSourceEntry> Sources { get; set; } = new();

    public static GeneratorManifest? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return ForgeJson.Deserialize<GeneratorManifest>(json);
    }
}

internal sealed class ManifestFileEntry
{
    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public ArtifactKind Kind { get; set; } = ArtifactKind.Other;
}

internal sealed class ManifestSourceEntry
{
    public string Id { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}
