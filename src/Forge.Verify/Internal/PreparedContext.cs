using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>校验开始前已经过安全校验的上下文。</summary>
internal sealed class PreparedContext
{
    public required string GameDirectory { get; init; }

    public required ForgeWorkspace Workspace { get; init; }

    public required string Slug { get; init; }

    public string? ManifestPath { get; init; }

    public required string LogPath { get; init; }

    public string? BaselinePath { get; init; }

    public bool BaselinePathExplicit { get; init; }

    public VerifyMode Mode { get; init; }

    public VerifyInstallerKind InstallerKind { get; init; }

    public int LogTailLines { get; init; }
}
