using GalA11yForge.Core.Models;

namespace GalA11yForge.Generators.Internal;

/// <summary>一个待写入的生成物：相对路径、类别、说明与最终字节。</summary>
internal sealed class GeneratedFile
{
    public GeneratedFile(string relativePath, ArtifactKind kind, string description, byte[] content)
    {
        RelativePath = relativePath;
        Kind = kind;
        Description = description;
        Content = content;
    }

    public string RelativePath { get; }

    public ArtifactKind Kind { get; }

    public string Description { get; }

    public byte[] Content { get; }

    public GeneratedArtifact ToArtifact() => new()
    {
        Name = Path.GetFileName(RelativePath),
        Kind = Kind,
        RelativePath = RelativePath,
        Description = Description,
    };
}
