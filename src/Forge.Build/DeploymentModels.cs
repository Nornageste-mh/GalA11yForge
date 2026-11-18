using System.Text.Json;
using System.Text.Json.Serialization;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Build;

/// <summary>部署选项。默认只装 BepInEx + 本次插件，不主动删除用户已有插件。</summary>
public sealed class DeploymentOptions
{
    /// <summary>部署批次 id；为空时自动生成 dep-yyyyMMdd-HHmmss-xxxx。</summary>
    public string? DeploymentId { get; set; }

    /// <summary>显式 BepInEx zip；为空时在工作区搜索缓存。</summary>
    public string? BepInExPackagePath { get; set; }

    public string BepInExVersion { get; set; } = "5.4.23.5";

    /// <summary>官方 5.4.23.5 win x64 的 SHA-256；设为 null 可跳过校验，但不建议。</summary>
    public string? BepInExSha256 { get; set; } = BepInExPackage.PinnedSha256;

    public bool VerifyPackageHash { get; set; } = true;

    /// <summary>已存在部署账本时是否允许再次部署（会先归档旧账本）。默认拒绝，避免悄悄叠加两层备份。</summary>
    public bool AllowRedeploy { get; set; }

    /// <summary>插件版本，写进 manifest；为空时从 BuildResult.Metadata["version"] 或 DLL 文件版本读取。</summary>
    public string? PluginVersion { get; set; }

    public string? PluginName { get; set; }

    /// <summary>额外要删除的游戏内相对路径（例如旧补丁 DLL）。会先备份再删，并记账。</summary>
    public List<string> AdditionalRemovals { get; set; } = new();

    /// <summary>自定义 manifest 路径；必须在工作区内，默认 05-deploy/install-manifest.json。</summary>
    public string? ManifestPath { get; set; }

    /// <summary>
    /// 语音运行期目录（Tolk.dll / SAAPI64.dll / byctrl-x64.dll / nvdaControllerClient64.dll 等）。
    /// 只对 IL2CPP 部署器生效：目录里的文件会复制到 BepInEx/plugins；目录不存在时安装继续但明确警告，
    /// 不把“插件能加载”当成“能朗读”。产品不分发这些第三方二进制（见 AGENTS.md §5）。
    /// </summary>
    public string? SpeechRuntimeDirectory { get; set; }

    /// <summary>
    /// IL2CPP 专有：是否把 nvdaControllerClient64.dll 也复制到游戏 exe 根。
    /// 默认 true —— R21 实测只有 exe 根那份能被 Tolk 按默认 DLL 搜索路径找到（plugins 里那份不生效）。
    /// 关掉等于退回 plugins-only，已知 NVDA 不生效，仅用于排障对照。
    /// </summary>
    public bool PlaceNvdaClientInGameRoot { get; set; } = true;

    public DeploymentOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(BepInExVersion))
        {
            throw new ArgumentException("BepInEx 版本不能为空。", nameof(BepInExVersion));
        }

        if (BepInExSha256 is not null && !FileHashes.IsSha256(BepInExSha256))
        {
            throw new ArgumentException("BepInExSha256 必须是 64 位十六进制。", nameof(BepInExSha256));
        }

        return this;
    }
}

/// <summary>部署计划中的一步。dry-run 只输出这个对象，不写盘。</summary>
public sealed class DeploymentOperation
{
    public string RelativePath { get; set; } = string.Empty;

    public DeployFileAction Action { get; set; } = DeployFileAction.Add;

    public bool ExistedBefore { get; set; }

    public string? Sha256Before { get; set; }

    public string? Sha256After { get; set; }

    public long SizeBefore { get; set; }

    public long SizeAfter { get; set; }

    /// <summary>bepinex / plugin / remove。</summary>
    public string Source { get; set; } = string.Empty;

    public string? Version { get; set; }

    /// <summary>相对 BackupRoot 的备份路径，统一用 '/'。</summary>
    public string? BackupRelativePath { get; set; }

    /// <summary>dry-run 且构建未实际执行时为 true，此时没有源文件可哈希。</summary>
    public bool Planned { get; set; }
}

/// <summary>完整部署计划；--dry-run 的 JSON 就是它的序列化结果。</summary>
public sealed class DeploymentPlan
{
    public int SchemaVersion { get; set; } = 1;

    public string DeploymentId { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string GameKey { get; set; } = string.Empty;

    public string GameDirectory { get; set; } = string.Empty;

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string BackupDirectory { get; set; } = string.Empty;

    public string ManifestPath { get; set; } = string.Empty;

    public string? BepInExVersion { get; set; }

    public string? BepInExPackagePath { get; set; }

    public string? BepInExPackageSha256 { get; set; }

    public string? PluginName { get; set; }

    public string? PluginVersion { get; set; }

    public bool DryRun { get; set; }

    public bool AllowRedeploy { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public List<DeploymentOperation> Operations { get; set; } = new();

    public List<string> CreatedDirectories { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(this, indented ? ForgeJson.Options : ForgeJson.Compact);

    /// <summary>把计划转成待写账本。真正部署时 BackupPath 已由事务写盘前填好。</summary>
    public InstallManifest ToManifest()
    {
        var manifest = new InstallManifest
        {
            SchemaVersion = 1,
            DeploymentId = DeploymentId,
            GameKey = GameKey,
            GamePath = GameDirectory,
            WorkspacePath = WorkspaceRoot,
            BackupRoot = BackupDirectory,
            CreatedAt = CreatedAt,
            DryRun = DryRun,
            BepInExVersion = BepInExVersion,
            BepInExPackage = BepInExPackagePath,
            BepInExPackageSha256 = BepInExPackageSha256,
            PluginName = PluginName,
            PluginVersion = PluginVersion,
            ManifestPath = ManifestPath,
            RollbackHint = "按此账本逆序回滚：先恢复 replace/remove，再删除 add。",
            CreatedDirectories = CreatedDirectories.ToList(),
            Files = Operations.Select(operation => new InstallFileEntry
            {
                RelativePath = operation.RelativePath,
                Action = operation.Action,
                ExistedBefore = operation.ExistedBefore,
                BackupPath = operation.BackupRelativePath,
                Sha256Before = operation.Sha256Before,
                Sha256After = operation.Sha256After,
                SizeBefore = operation.SizeBefore,
                SizeAfter = operation.SizeAfter,
                Source = operation.Source,
                Version = operation.Version,
                DeployedAt = CreatedAt,
                Planned = operation.Planned,
            }).ToList(),
        };

        return manifest;
    }
}

/// <summary>富结果：除 Core 的 DeployResult 外，还带计划 JSON 与告警，便于 CLI/报告直接展示。</summary>
public sealed class DeploymentOutcome
{
    public InstallManifest Manifest { get; set; } = new();

    public string ManifestPath { get; set; } = string.Empty;

    public string BackupDirectory { get; set; } = string.Empty;

    public DeploymentPlan Plan { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public string PlanJson => Plan.ToJson();
}

/// <summary>账本中的单文件记录。比 Core 模型多大小/时间/来源，回滚时仍可无损转成 Core 模型。</summary>
public sealed class InstallFileEntry
{
    public string RelativePath { get; set; } = string.Empty;

    public DeployFileAction Action { get; set; } = DeployFileAction.Add;

    public bool ExistedBefore { get; set; }

    /// <summary>相对 BackupRoot 的路径，统一 '/'；Add 为 null。</summary>
    public string? BackupPath { get; set; }

    public string? Sha256Before { get; set; }

    public string? Sha256After { get; set; }

    public long SizeBefore { get; set; }

    public long SizeAfter { get; set; }

    /// <summary>bepinex / plugin / remove。</summary>
    public string? Source { get; set; }

    public string? Version { get; set; }

    public string? DeployedAt { get; set; }

    public bool Planned { get; set; }

    /// <summary>保留未来字段，读旧账本再写回时不丢信息。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// install-manifest.json 的磁盘模型。字段比 Core DeployManifest 多，但保持可逆转换，
/// 供 IDeployer/Verify/Uninstaller 共用。
/// </summary>
public sealed class InstallManifest
{
    public int SchemaVersion { get; set; } = 1;

    public string DeploymentId { get; set; } = string.Empty;

    /// <summary>游戏标识：slug（本机唯一）。</summary>
    public string GameKey { get; set; } = string.Empty;

    public string GamePath { get; set; } = string.Empty;

    public string WorkspacePath { get; set; } = string.Empty;

    public string BackupRoot { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public string? BepInExVersion { get; set; }

    public string? BepInExPackage { get; set; }

    public string? BepInExPackageSha256 { get; set; }

    public string? PluginName { get; set; }

    public string? PluginVersion { get; set; }

    public bool DryRun { get; set; }

    public string? ManifestPath { get; set; }

    public string? RollbackHint { get; set; }

    /// <summary>
    /// 回滚成功后把账本标记为“已回滚”：账本文件仍保留（追溯用），但下一次安装不再要求 --force。
    /// 0.2.46 及更早生成的账本没有该字段，反序列化为 false，行为与旧版一致。
    /// </summary>
    public bool RolledBack { get; set; }

    /// <summary>回滚标记时间（ISO 8601）。</summary>
    public string? RolledBackAt { get; set; }

    public List<InstallFileEntry> Files { get; set; } = new();

    public List<string> CreatedDirectories { get; set; } = new();

    /// <summary>
    /// 部署前对 BepInEx 运行期白名单区域（cache/config/patchers 与根目录 *.log）的基线快照。
    /// 旧版账本没有该字段（null），回滚时退回按 CreatedDirectories 推断，避免误删安装前已有的文件。
    /// </summary>
    public BepInExInstallBaseline? BepInExBaseline { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public DeployManifest ToDeployManifest()
    {
        var files = Files.Select(file => new DeployFileEntry
        {
            RelativePath = file.RelativePath,
            Action = file.Action,
            ExistedBefore = file.ExistedBefore,
            BackupPath = string.IsNullOrWhiteSpace(file.BackupPath)
                ? null
                : Path.GetFullPath(Path.Combine(BackupRoot, file.BackupPath.Replace('/', Path.DirectorySeparatorChar))),
            Sha256Before = file.Sha256Before,
            Sha256After = file.Sha256After,
        }).ToList();

        return new DeployManifest
        {
            SchemaVersion = 1,
            DeploymentId = DeploymentId,
            GamePath = GamePath,
            BackupRoot = BackupRoot,
            CreatedAt = CreatedAt,
            Files = files,
            RollbackHint = RollbackHint,
        };
    }

    public static InstallManifest FromDeployManifest(DeployManifest manifest, ForgeWorkspaceInfo? workspace = null)
    {
        var backupRoot = string.IsNullOrWhiteSpace(manifest.BackupRoot)
            ? string.Empty
            : Path.GetFullPath(manifest.BackupRoot);

        var files = (manifest.Files ?? new List<DeployFileEntry>()).Select(file =>
        {
            string? relativeBackup = null;
            if (!string.IsNullOrWhiteSpace(file.BackupPath))
            {
                var fullBackup = Path.GetFullPath(file.BackupPath);
                if (!string.IsNullOrWhiteSpace(backupRoot) && PathSafetyIsUnder(backupRoot, fullBackup))
                {
                    relativeBackup = Path.GetRelativePath(backupRoot, fullBackup).Replace('\\', '/');
                }
                else
                {
                    // 保留绝对路径，后续 Validate 会明确拒绝，而不是悄悄用错备份。
                    relativeBackup = fullBackup;
                }
            }

            return new InstallFileEntry
            {
                RelativePath = file.RelativePath,
                Action = file.Action,
                ExistedBefore = file.ExistedBefore,
                BackupPath = relativeBackup,
                Sha256Before = file.Sha256Before,
                Sha256After = file.Sha256After,
                DeployedAt = manifest.CreatedAt,
                Source = "legacy",
            };
        }).ToList();

        return new InstallManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            DeploymentId = manifest.DeploymentId,
            GameKey = workspace?.Slug ?? string.Empty,
            GamePath = manifest.GamePath,
            WorkspacePath = workspace?.RootPath ?? string.Empty,
            BackupRoot = backupRoot,
            CreatedAt = manifest.CreatedAt,
            Files = files,
            RollbackHint = manifest.RollbackHint,
        };
    }

    private static bool PathSafetyIsUnder(string root, string path)
        => GalA11yForge.Core.Workspaces.PathSafety.IsUnder(root, path);
}

/// <summary>
/// 部署前 BepInEx 白名单区域的基线快照。回滚时只有“不在这里”的运行期产物才允许自动删除；
/// 值为空列表表示部署前确实什么都没有（区别于旧账本的 null）。
/// </summary>
public sealed class BepInExInstallBaseline
{
    /// <summary>安装前已存在的白名单文件，相对游戏目录、'/' 分隔。</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>安装前已存在的白名单目录（含 BepInEx 本身），相对游戏目录、'/' 分隔。</summary>
    public List<string> Directories { get; set; } = new();

    /// <summary>
    /// 快照是否完整。枚举时有目录读不到会置 false；此时回滚只清理“整棵 BepInEx 都是本次新建”的场景，
    /// 其余一律保留，避免误删安装前已有的用户文件。
    /// </summary>
    public bool Complete { get; set; } = true;
}

/// <summary>避免 FromDeployManifest 依赖整个 ForgeWorkspace 的小载体。</summary>
public sealed class ForgeWorkspaceInfo
{
    public string RootPath { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;
}
