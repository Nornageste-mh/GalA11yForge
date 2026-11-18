namespace GalA11yForge.Core.Models;

/// <summary>流水线上下文。适配器拿到的全部输入都从这里来，不直接读全局状态。</summary>
public sealed class GameContext
{
    public string GameDirectory { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string WorkspaceRoot { get; set; } = string.Empty;

    public ProfileDocument? Profile { get; set; }

    public bool Advanced { get; set; }

    public bool DryRun { get; set; }

    public static GameContext Create(string gameDirectory, string workspaceRoot, string? slug = null)
        => new()
        {
            GameDirectory = gameDirectory,
            WorkspaceRoot = workspaceRoot,
            Slug = slug ?? string.Empty,
            Advanced = false,
            DryRun = false,
        };
}
