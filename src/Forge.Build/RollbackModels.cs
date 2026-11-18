using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Build;

/// <summary>回滚选项。Force 只允许覆盖“部署后又被玩家改动”的冲突，不允许拿损坏的备份硬恢复。</summary>
public sealed class RollbackOptions
{
    /// <summary>目标当前哈希与账本 sha256After 不一致时，是否仍继续回滚。</summary>
    public bool Force { get; set; }

    /// <summary>回滚后删除本次部署新建且已经变空的目录。默认 true。</summary>
    public bool CleanupEmptyDirectories { get; set; } = true;

    /// <summary>
    /// 回滚后清理本次安装创建、由 BepInEx 运行期生成的白名单产物
    /// （BepInEx/cache/**、BepInEx/config/**、BepInEx/patchers/**、BepInEx 根目录 *.log）。
    /// 安装前已存在的文件/目录绝不删除。默认 true。
    /// </summary>
    public bool CleanBepInExRuntimeArtifacts { get; set; } = true;
}

/// <summary>回滚计划/回滚结果中的一步，便于 CLI 输出和测试断言。</summary>
public sealed class RollbackAction
{
    public int Order { get; set; }

    public DeployRollbackAction Action { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    public string? BackupPath { get; set; }

    /// <summary>planned / restored / deleted / skipped / failed。</summary>
    public string Outcome { get; set; } = "planned";

    public string? Sha256Expected { get; set; }

    public string? Sha256Actual { get; set; }

    public string? Error { get; set; }
}

/// <summary>一次回滚的完整记录；JSON 可直接交给报告或测试。</summary>
public sealed class RollbackReport
{
    public string DeploymentId { get; set; } = string.Empty;

    public string GameDirectory { get; set; } = string.Empty;

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string? ManifestPath { get; set; }

    public bool DryRun { get; set; }

    public bool AlreadyClean { get; set; }

    public bool Ok { get; set; }

    public List<RollbackAction> Actions { get; set; } = new();

    public List<string> RestoredFiles { get; set; } = new();

    public List<string> DeletedFiles { get; set; } = new();

    public List<string> SkippedFiles { get; set; } = new();

    public List<string> CleanedDirectories { get; set; } = new();

    /// <summary>本次回滚实际删除的 BepInEx 运行期产物（相对游戏目录、'/' 分隔）。</summary>
    public List<string> CleanedRuntimeArtifacts { get; set; } = new();

    /// <summary>白名单内但无法证明“安装前不存在”而保留的产物；原因同时写进 Warnings。</summary>
    public List<string> SkippedRuntimeArtifacts { get; set; } = new();

    public List<string> HashMismatches { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public List<string> Errors { get; set; } = new();

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(this, indented ? ForgeJson.Options : ForgeJson.Compact);
}
