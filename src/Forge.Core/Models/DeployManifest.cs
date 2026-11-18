namespace GalA11yForge.Core.Models;

/// <summary>部署账本里的单文件动作。</summary>
public enum DeployFileAction
{
    Add = 0,
    Replace = 1,
    Remove = 2,
}

/// <summary>部署账本中的单文件记录。BackupPath 必须位于 manifest.BackupRoot 之下。</summary>
public sealed class DeployFileEntry
{
    /// <summary>相对游戏目录的路径；禁止绝对路径与 ".."。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public DeployFileAction Action { get; set; } = DeployFileAction.Add;

    public bool ExistedBefore { get; set; }

    public string? BackupPath { get; set; }

    public string? Sha256Before { get; set; }

    public string? Sha256After { get; set; }
}

/// <summary>
/// 部署账本（05-deploy/deploy.json）。写前备份、写后记账；回滚按 Files 的逆序恢复。
/// </summary>
public sealed class DeployManifest
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>部署批次 id（slug 风格），例如 dep-20261114-001。</summary>
    public string DeploymentId { get; set; } = string.Empty;

    public string GamePath { get; set; } = string.Empty;

    /// <summary>本批次备份根目录，必须位于工作区 backup/ 下。</summary>
    public string BackupRoot { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public List<DeployFileEntry> Files { get; set; } = new();

    public string? RollbackHint { get; set; }
}

public enum DeployRollbackAction
{
    Restore = 0,
    Delete = 1,
}

/// <summary>一条回滚指令：Restore 从 BackupPath 还原；Delete 删除本次新增的文件。</summary>
public sealed class DeployRollbackStep
{
    public int Order { get; set; }

    public DeployRollbackAction Action { get; set; }

    public string TargetRelativePath { get; set; } = string.Empty;

    public string? BackupPath { get; set; }
}
