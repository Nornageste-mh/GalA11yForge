using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>Ren'Py 覆盖层安装选项。覆盖层不需要编译，因此没有 BepInEx 相关字段。</summary>
public sealed class OverlayDeployOptions
{
    /// <summary>已有部署账本时是否允许再次部署（会先归档旧账本）。默认拒绝，避免悄悄叠第二层覆盖层。</summary>
    public bool AllowRedeploy { get; set; }

    /// <summary>部署批次 id；为空时自动生成 renpy-yyyyMMdd-HHmmss-xxxxxxxx。</summary>
    public string? DeploymentId { get; set; }

    /// <summary>生成物目录（含 a11y_manifest.json）；为空时用 BuildResult.OutputDirectory 或 03-generate。</summary>
    public string? GenerationDirectory { get; set; }

    /// <summary>自定义账本路径；必须在工作区内，默认 05-deploy/install-manifest.json。</summary>
    public string? ManifestPath { get; set; }
}

/// <summary>覆盖层部署计划中的一步；dry-run 只输出这个对象，不写盘。</summary>
public sealed class OverlayOperation
{
    /// <summary>相对游戏根目录的部署路径，统一 '/'。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public DeployFileAction Action { get; set; } = DeployFileAction.Add;

    public bool ExistedBefore { get; set; }

    public string? Sha256Before { get; set; }

    public string Sha256After { get; set; } = string.Empty;

    public long SizeBefore { get; set; }

    public long SizeAfter { get; set; }

    /// <summary>相对生成物目录的源路径（与 RelativePath 相同）。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>相对 BackupRoot 的备份路径，统一 '/'；Add 为 null。</summary>
    public string? BackupRelativePath { get; set; }

    public bool Planned { get; set; }
}

/// <summary>覆盖层部署计划；对应 install --dry-run 的可读输出。</summary>
public sealed class OverlayDeploymentPlan
{
    public int SchemaVersion { get; set; } = 1;

    public string Kind { get; set; } = "renpy-overlay";

    public string DeploymentId { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string GameKey { get; set; } = string.Empty;

    public string GameDirectory { get; set; } = string.Empty;

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string GenerationDirectory { get; set; } = string.Empty;

    public string BackupDirectory { get; set; } = string.Empty;

    public string ManifestPath { get; set; } = string.Empty;

    public bool DryRun { get; set; }

    public bool AllowRedeploy { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string? PatchVersion { get; set; }

    public bool ConflictOverlay { get; set; }

    public List<OverlayOperation> Operations { get; set; } = new();

    public List<string> CreatedDirectories { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(this, indented ? ForgeJson.Options : ForgeJson.Compact);

    /// <summary>仅供 DeploymentBaseline.Capture 使用（它只读 Operations.RelativePath）。</summary>
    public DeploymentPlan ToDeploymentPlan()
        => new()
        {
            SchemaVersion = 1,
            DeploymentId = DeploymentId,
            Slug = Slug,
            GameKey = GameKey,
            GameDirectory = GameDirectory,
            WorkspaceRoot = WorkspaceRoot,
            BackupDirectory = BackupDirectory,
            ManifestPath = ManifestPath,
            DryRun = DryRun,
            AllowRedeploy = AllowRedeploy,
            CreatedAt = CreatedAt,
            Operations = Operations.Select(operation => new DeploymentOperation
            {
                RelativePath = operation.RelativePath,
                Action = operation.Action,
                ExistedBefore = operation.ExistedBefore,
                Sha256Before = operation.Sha256Before,
                Sha256After = operation.Sha256After,
                SizeBefore = operation.SizeBefore,
                SizeAfter = operation.SizeAfter,
                Source = "renpy-overlay",
                BackupRelativePath = operation.BackupRelativePath,
                Planned = operation.Planned,
            }).ToList(),
            CreatedDirectories = CreatedDirectories.ToList(),
            Warnings = Warnings.ToList(),
        };

    /// <summary>把计划转成待写账本；真正部署时 BackupRelativePath 已在写盘前填好。</summary>
    public InstallManifest ToManifest()
        => new()
        {
            SchemaVersion = 1,
            DeploymentId = DeploymentId,
            GameKey = GameKey,
            GamePath = GameDirectory,
            WorkspacePath = WorkspaceRoot,
            BackupRoot = BackupDirectory,
            CreatedAt = CreatedAt,
            PluginName = "renpy-overlay",
            PluginVersion = PatchVersion,
            DryRun = DryRun,
            ManifestPath = ManifestPath,
            RollbackHint = "按此账本逆序回滚：先恢复 replace，再删除 add（含 Ren'Py 编译缓存 .rpyc）。",
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
                Source = "renpy-overlay",
                Version = PatchVersion,
                DeployedAt = CreatedAt,
                Planned = operation.Planned,
            }).ToList(),
        };
}

/// <summary>
/// Ren'Py 文件覆盖层部署器。
///
/// <para>
/// 来源=工作区 <c>games/&lt;slug&gt;/03-generate</c> 里的 a11y_manifest.json 与 9 个内容文件；
/// 目标路径由清单（可选用 deploy_plan.json 交叉校验）给出，全部经 PathGuard 限制在游戏目录内。
/// 安装前逐文件备份到 <c>backup/&lt;deploymentId&gt;/</c>，写 <c>05-deploy/install-manifest.json</c>；
/// 回滚按账本逆序恢复/删除并清理空目录与 .rpyc 编译缓存。dry-run 只输出计划，不写盘。
/// </para>
/// </summary>
public sealed class OverlayDeployer : IDeployer
{
    public const string OverlayManifestFileName = "a11y_manifest.json";

    public const string DeployPlanFileName = "deploy_plan.json";

    public const string OverlaySchema = "gala11y.renpy.overlay/1";

    public const string DeployPlanSchema = "gala11y.renpy.deploy_plan/1";

    private readonly ForgeWorkspace _workspace;

    private readonly OverlayDeployOptions _options;

    public OverlayDeployer(ForgeWorkspace workspace, OverlayDeployOptions? options = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? new OverlayDeployOptions();
        _options.DeploymentId = string.IsNullOrWhiteSpace(_options.DeploymentId) ? null : _options.DeploymentId.Trim();
    }

    /// <summary>生成部署计划；只读游戏目录与生成物，不写任何文件。</summary>
    public ForgeResult<OverlayDeploymentPlan> Plan(GameContext context, BuildResult? build = null)
    {
        var gameResult = ResolveGameDirectory(context);
        if (!gameResult.Ok)
        {
            return ForgeResult<OverlayDeploymentPlan>.Failure(gameResult.Error!);
        }

        var game = gameResult.Value!;
        var slug = ResolveSlug(context, game);

        string generationDirectory;
        try
        {
            generationDirectory = ResolveGenerationDirectory(slug, build);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<OverlayDeploymentPlan>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure<OverlayDeploymentPlan>(
                ForgeErrorCodes.ArgumentInvalid,
                $"生成物目录不是合法路径：{ex.Message}",
                "用 --out 指定工作区内的生成目录后重新 generate。");
        }

        var manifestResults = LoadOverlayManifest(generationDirectory, game);
        if (!manifestResults.Ok)
        {
            return ForgeResult<OverlayDeploymentPlan>.Failure(manifestResults.Error!);
        }

        var info = manifestResults.Value!;

        var deploymentId = string.IsNullOrWhiteSpace(_options.DeploymentId)
            ? NewDeploymentId()
            : _options.DeploymentId!;
        if (!PathSafety.IsValidSlug(deploymentId))
        {
            return Failure<OverlayDeploymentPlan>(
                ForgeErrorCodes.ArgumentInvalid,
                $"部署批次 id 不合法：{deploymentId}",
                "只用小写字母、数字、点、下划线、连字符，最长 64。");
        }

        string manifestPath;
        string backupDirectory;
        try
        {
            manifestPath = InstallManifestStore.ResolvePath(_workspace, _options.ManifestPath, slug);
            backupDirectory = _workspace.BackupDirectory(deploymentId);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<OverlayDeploymentPlan>.Failure(ex.Error);
        }

        if (Directory.Exists(backupDirectory)
            && Directory.EnumerateFileSystemEntries(backupDirectory).Any())
        {
            return Failure<OverlayDeploymentPlan>(
                ForgeErrorCodes.ArtifactInvalid,
                $"备份目录已有内容：{backupDirectory}",
                "换一个 --deployment-id，或清空该目录后重试。");
        }

        var ledgerExists = File.Exists(manifestPath);
        // 0.2.47：上一批部署已经成功回滚（账本标记 rolledBack）时，补丁其实已经卸载，
        // 不该再要求 --force；旧账本会在部署阶段归档到备份目录。
        var previousRolledBack = ledgerExists && InstallManifestStore.IsRolledBack(_workspace, manifestPath);
        if (ledgerExists && !_options.AllowRedeploy && !previousRolledBack)
        {
            return Failure<OverlayDeploymentPlan>(
                ForgeErrorCodes.ArtifactInvalid,
                $"工作区已有部署账本：{manifestPath}",
                "先 verify --rollback 卸载旧覆盖层；确认要叠加时加 --force（旧账本会归档到备份目录）。");
        }

        var warnings = new List<string>();
        if (ledgerExists)
        {
            warnings.Add(previousRolledBack
                ? "上一批部署已回滚（账本已标记 rolledBack）；旧账本会在部署时归档，然后重新安装。"
                : "工作区已有部署账本；--force 会归档旧账本并重新部署。");
        }

        if (info.ConflictOverlay)
        {
            if (!_options.AllowRedeploy)
            {
                var legacyDirectories = EnumerateOverlayDirectories(game);
                var details = new Dictionary<string, string>
                {
                    ["legacyOverlayDirectories"] = legacyDirectories.Count == 0
                        ? "(未列出)"
                        : string.Join(", ", legacyDirectories),
                    ["note"] = "两套覆盖层共用 game/a11y_platform；先卸载旧补丁，或加 --force 叠加（不推荐，可能双读）。",
                };
                return Failure<OverlayDeploymentPlan>(
                    ForgeErrorCodes.EngineUnsupported,
                    "游戏目录里已有一套旧的无障碍覆盖层（game/a11y_*）。两套覆盖层共用 game/a11y_platform，混装会出现 A11yHost 契约不一致、启动即崩。",
                    "先运行旧补丁的 uninstall.ps1（或删除 game/a11y_game、game/a11y_platform 等旧目录），重新 analyze/plan/generate 后再安装；确认要叠加时加 --force。",
                    details);
            }

            warnings.Add("游戏里已有另一套 a11y 覆盖层，按 --force 继续叠加；两套补丁可能双读，建议先卸载旧补丁。");
        }

        if (!info.PlanPresent)
        {
            warnings.Add($"生成物里没有 {DeployPlanFileName}，按 {OverlayManifestFileName} 的 files 映射目标路径。");
        }

        var operations = new List<OverlayOperation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in info.Files)
        {
            string relative;
            string target;
            string source;
            try
            {
                relative = PathSafety.ValidateRelative(file.Path);
                if (!seen.Add(relative))
                {
                    return Failure<OverlayDeploymentPlan>(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"清单里出现重复路径：{relative}",
                        "重新 generate，或检查生成物是否被手工改过。");
                }

                target = PathGuard.ResolveGameRelative(game, relative);
                source = SourcePath(generationDirectory, relative);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<OverlayDeploymentPlan>.Failure(ex.Error);
            }

            if (Directory.Exists(target))
            {
                return Failure<OverlayDeploymentPlan>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"目标位置被目录占用：{relative}",
                    "先移走该目录，或换一个游戏副本再装。");
            }

            if (!File.Exists(source))
            {
                return Failure<OverlayDeploymentPlan>(
                    ForgeErrorCodes.ArtifactMissing,
                    $"生成物缺少文件：{source}",
                    "重新运行 forge generate，确认 03-generate 下 11 个文件齐全。");
            }

            var sourceBytes = new FileInfo(source).Length;
            if (file.Bytes >= 0 && file.Bytes != sourceBytes)
            {
                return Failure<OverlayDeploymentPlan>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"生成物大小与清单不一致：{relative}（清单 {file.Bytes}，实际 {sourceBytes}）",
                    "生成物被改动过；重新 generate 后再安装。");
            }

            var actualHash = FileHashes.Sha256File(source);
            if (!FileHashes.EqualsHash(actualHash, file.Sha256))
            {
                return Failure<OverlayDeploymentPlan>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"生成物哈希与清单不一致：{relative}",
                    "生成物被改动过；重新 generate 后再安装。",
                    new Dictionary<string, string>
                    {
                        ["relativePath"] = relative,
                        ["expectedSha256"] = file.Sha256,
                        ["actualSha256"] = actualHash,
                    });
            }

            var existed = File.Exists(target);
            string? hashBefore = null;
            var sizeBefore = 0L;
            if (existed)
            {
                hashBefore = FileHashes.Sha256File(target);
                sizeBefore = new FileInfo(target).Length;
            }

            operations.Add(new OverlayOperation
            {
                RelativePath = relative,
                Action = existed ? DeployFileAction.Replace : DeployFileAction.Add,
                ExistedBefore = existed,
                Sha256Before = hashBefore,
                Sha256After = actualHash,
                SizeBefore = sizeBefore,
                SizeAfter = sourceBytes,
                Source = relative,
                Planned = context.DryRun,
            });
        }

        var overwriteCount = operations.Count(operation => operation.Action == DeployFileAction.Replace);
        if (overwriteCount > 0)
        {
            warnings.Add($"将覆盖 {overwriteCount} 个已存在的文件（安装前逐文件备份）。");
        }

        var createdDirectories = CollectCreatedDirectories(game, operations);

        var plan = new OverlayDeploymentPlan
        {
            SchemaVersion = 1,
            Kind = "renpy-overlay",
            DeploymentId = deploymentId,
            Slug = slug,
            GameKey = slug,
            GameDirectory = game,
            WorkspaceRoot = _workspace.RootPath,
            GenerationDirectory = generationDirectory,
            BackupDirectory = backupDirectory,
            ManifestPath = manifestPath,
            DryRun = context.DryRun,
            AllowRedeploy = _options.AllowRedeploy,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            PatchVersion = info.PatchVersion,
            ConflictOverlay = info.ConflictOverlay,
            Operations = operations,
            CreatedDirectories = createdDirectories,
            Warnings = warnings,
        };

        return ForgeResult<OverlayDeploymentPlan>.Success(plan);
    }

    public Task<ForgeResult<OverlayDeploymentPlan>> PlanAsync(
        GameContext context,
        BuildResult? build = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Plan(context, build));
    }

    /// <summary>按计划安装；计划与磁盘状态不一致时拒绝写盘。</summary>
    public ForgeResult<DeployResult> DeployWithPlan(GameContext context, OverlayDeploymentPlan plan)
    {
        if (plan is null)
        {
            return Failure<DeployResult>(ForgeErrorCodes.ArgumentInvalid, "部署计划为空。", "先运行 install 生成计划。");
        }

        var gameResult = ResolveGameDirectory(context);
        if (!gameResult.Ok)
        {
            return ForgeResult<DeployResult>.Failure(gameResult.Error!);
        }

        var game = gameResult.Value!;
        try
        {
            if (!PathGuard.SamePath(plan.GameDirectory, game))
            {
                return Failure<DeployResult>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "部署计划与当前游戏目录不一致。",
                    "重新运行 install（计划可能来自另一个游戏副本）。");
            }

            if (!PathGuard.SamePath(plan.WorkspaceRoot, _workspace.RootPath))
            {
                return Failure<DeployResult>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "部署计划与当前工作区不一致。",
                    "用同一个 --workspace 重新运行 install。");
            }
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<DeployResult>.Failure(ex.Error);
        }

        if (context.DryRun || plan.DryRun)
        {
            return ForgeResult<DeployResult>.Success(new DeployResult
            {
                Manifest = plan.ToManifest().ToDeployManifest(),
                BackupDirectory = plan.BackupDirectory,
            });
        }

        string manifestPath;
        string backupDirectory;
        try
        {
            manifestPath = InstallManifestStore.ResolvePath(_workspace, plan.ManifestPath, plan.Slug);
            backupDirectory = _workspace.BackupDirectory(plan.DeploymentId);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<DeployResult>.Failure(ex.Error);
        }

        // 0.2.47：计划阶段已确认“上一批已回滚”的账本可以重装（旧账本随后归档），这里同样放行。
        if (File.Exists(manifestPath)
            && !plan.AllowRedeploy
            && !InstallManifestStore.IsRolledBack(_workspace, manifestPath))
        {
            return Failure<DeployResult>(
                ForgeErrorCodes.ArtifactInvalid,
                $"部署账本已存在：{manifestPath}",
                "先 verify --rollback，或加 --force 重新部署。");
        }

        // 写盘前复核计划里的每一项，避免“计划后目录被改过”导致半装。
        foreach (var operation in plan.Operations)
        {
            string target;
            string source;
            try
            {
                target = PathGuard.ResolveGameRelative(game, operation.RelativePath);
                source = SourcePath(plan.GenerationDirectory, operation.RelativePath);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<DeployResult>.Failure(ex.Error);
            }

            if (Directory.Exists(target))
            {
                return Failure<DeployResult>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"目标位置被目录占用：{operation.RelativePath}",
                    "移走该目录后重试。");
            }

            if (File.Exists(target) != operation.ExistedBefore)
            {
                return Failure<DeployResult>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"目标文件状态在计划后发生变化：{operation.RelativePath}",
                    "重新运行 install 生成新计划。");
            }

            if (operation.ExistedBefore
                && !FileHashes.MatchesFile(target, operation.Sha256Before!))
            {
                return Failure<DeployResult>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"目标文件在计划后被改动：{operation.RelativePath}",
                    "确认改动来源；重新运行 install 生成新计划。");
            }

            if (!File.Exists(source) || !FileHashes.MatchesFile(source, operation.Sha256After))
            {
                return Failure<DeployResult>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"生成物在计划后发生变化：{operation.RelativePath}",
                    "重新运行 forge generate 与 install。");
            }
        }

        if (Directory.Exists(backupDirectory)
            && Directory.EnumerateFileSystemEntries(backupDirectory).Any())
        {
            return Failure<DeployResult>(
                ForgeErrorCodes.ArtifactInvalid,
                $"备份目录已有内容：{backupDirectory}",
                "换一个 --deployment-id，或清空该目录后重试。");
        }

        var baselinePath = DeploymentBaseline.ResolvePath(_workspace, plan.Slug);
        byte[]? previousBaseline = null;
        try
        {
            if (File.Exists(baselinePath))
            {
                previousBaseline = File.ReadAllBytes(baselinePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<DeployResult>(
                ForgeErrorCodes.Io,
                $"读取旧基线失败：{ex.Message}",
                "检查工作区权限后重试。");
        }

        var baselineCapture = DeploymentBaseline.Capture(game, plan.ToDeploymentPlan());
        if (!baselineCapture.Ok)
        {
            return ForgeResult<DeployResult>.Failure(baselineCapture.Error!);
        }

        var applied = new List<OverlayOperation>();
        var warnings = new List<string>(plan.Warnings);
        try
        {
            Directory.CreateDirectory(backupDirectory);
            if (File.Exists(manifestPath))
            {
                var archived = Path.Combine(backupDirectory, "previous-install-manifest.json");
                File.Copy(manifestPath, archived, overwrite: false);
                warnings.Add($"旧账本已归档：{archived}");
            }

            foreach (var operation in plan.Operations)
            {
                var target = PathGuard.ResolveGameRelative(game, operation.RelativePath);
                if (operation.Action == DeployFileAction.Replace)
                {
                    var backupRelative = PathSafety.ValidateRelative(operation.RelativePath);
                    var backupFull = Path.Combine(
                        backupDirectory,
                        backupRelative.Replace('/', Path.DirectorySeparatorChar));
                    _workspace.EnsureBackupPath(backupFull);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFull)!);
                    File.Copy(target, backupFull, overwrite: false);
                    if (!FileHashes.MatchesFile(backupFull, operation.Sha256Before!))
                    {
                        throw new IOException($"备份后哈希不匹配：{operation.RelativePath}");
                    }

                    operation.BackupRelativePath = backupRelative;
                }

                applied.Add(operation);
                var source = SourcePath(plan.GenerationDirectory, operation.RelativePath);
                WriteAtomic(target, File.ReadAllBytes(source));
                if (!FileHashes.MatchesFile(target, operation.Sha256After)
                    || new FileInfo(target).Length != operation.SizeAfter)
                {
                    throw new IOException($"写入后校验失败：{operation.RelativePath}");
                }
            }

            var baselineSave = DeploymentBaseline.Save(_workspace, plan.Slug, baselineCapture.Value!);
            if (!baselineSave.Ok)
            {
                AutoRollback(game, backupDirectory, plan, applied, warnings);
                RestoreBaseline(baselinePath, previousBaseline);
                return ForgeResult<DeployResult>.Failure(baselineSave.Error!);
            }

            var manifest = plan.ToManifest();
            manifest.CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var file in manifest.Files)
            {
                file.DeployedAt = manifest.CreatedAt;
            }

            var save = InstallManifestStore.Save(_workspace, manifest, manifestPath);
            if (!save.Ok)
            {
                AutoRollback(game, backupDirectory, plan, applied, warnings);
                RestoreBaseline(baselinePath, previousBaseline);
                return ForgeResult<DeployResult>.Failure(save.Error!);
            }

            return ForgeResult<DeployResult>.Success(new DeployResult
            {
                Manifest = manifest.ToDeployManifest(),
                BackupDirectory = backupDirectory,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
        {
            AutoRollback(game, backupDirectory, plan, applied, warnings);
            RestoreBaseline(baselinePath, previousBaseline);
            return Failure<DeployResult>(
                ForgeErrorCodes.Io,
                $"覆盖层写入失败：{ex.Message}",
                "已自动回滚本次已写文件；检查磁盘空间/权限后重试。",
                new Dictionary<string, string>
                {
                    ["gameDirectory"] = game,
                    ["backupDirectory"] = backupDirectory,
                });
        }
    }

    /// <summary>一次完成“探测生成物 → 计划 → 安装”；失败时游戏目录保持原状。</summary>
    public Task<ForgeResult<DeployResult>> DeployAsync(
        GameContext context,
        BuildResult build,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = Plan(context, build);
        return Task.FromResult(plan.Ok
            ? DeployWithPlan(context, plan.Value!)
            : ForgeResult<DeployResult>.Failure(plan.Error!));
    }

    /// <summary>从 install-manifest.json 回滚。</summary>
    public ForgeResult<RollbackReport> RollbackFromFile(
        GameContext context,
        string manifestPath,
        RollbackOptions? options = null)
    {
        var loaded = InstallManifestStore.Load(_workspace, manifestPath);
        if (!loaded.Ok)
        {
            return ForgeResult<RollbackReport>.Failure(loaded.Error!);
        }

        var manifest = loaded.Value!;
        manifest.ManifestPath ??= Path.GetFullPath(manifestPath);
        var result = RollbackWithManifest(context, manifest, options);
        if (result.Ok && result.Value is { Ok: true, DryRun: false } report)
        {
            // 0.2.47：回滚成功后把账本标记为“已回滚”。补丁已经不在了，再装一次不该还要 --force；
            // 账本本身保留（追溯用），重新部署时按原有逻辑归档到 backup/<新批次>/previous-install-manifest.json。
            var marked = InstallManifestStore.MarkRolledBack(_workspace, manifest, manifest.ManifestPath);
            if (!marked.Ok)
            {
                report.Warnings.Add(
                    $"回滚成功，但账本未能标记“已回滚”：{marked.Error?.Message}；下次安装可能仍要求 --force。");
            }
        }

        return result;
    }

    public Task<ForgeResult<RollbackReport>> RollbackFromFileAsync(
        GameContext context,
        string manifestPath,
        RollbackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RollbackFromFile(context, manifestPath, options));
    }

    /// <summary>按账本逆序回滚：先恢复 replace/remove，再删除 add；校验哈希并清理空目录。</summary>
    public ForgeResult<RollbackReport> RollbackWithManifest(
        GameContext context,
        InstallManifest manifest,
        RollbackOptions? options = null)
    {
        options ??= new RollbackOptions();
        var gameResult = ResolveGameDirectory(context);
        if (!gameResult.Ok)
        {
            return ForgeResult<RollbackReport>.Failure(gameResult.Error!);
        }

        var game = gameResult.Value!;
        if (manifest.DryRun)
        {
            return Failure<RollbackReport>(
                ForgeErrorCodes.ArtifactInvalid,
                "这是 dry-run 账本，没有实际部署，不能回滚。",
                "使用真实部署生成的 install-manifest.json。");
        }

        try
        {
            if (!PathGuard.SamePath(manifest.GamePath, game))
            {
                return Failure<RollbackReport>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "账本记录的游戏目录与当前游戏目录不一致。",
                    "确认 --game 指向安装补丁的那个游戏；不要拿别的副本回滚。");
            }

            if (!string.IsNullOrWhiteSpace(manifest.WorkspacePath)
                && !PathGuard.SamePath(manifest.WorkspacePath, _workspace.RootPath))
            {
                return Failure<RollbackReport>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "账本记录的工作区与当前工作区不一致。",
                    "用安装时相同的 --workspace 运行 verify --rollback。");
            }
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<RollbackReport>.Failure(ex.Error!);
        }

        var validation = InstallManifestValidation.Validate(
            _workspace,
            manifest,
            requireBackupFiles: true,
            requireHashes: true);
        if (!validation.Ok)
        {
            return ForgeResult<RollbackReport>.Failure(validation.Error!);
        }

        var report = new RollbackReport
        {
            DeploymentId = manifest.DeploymentId,
            GameDirectory = game,
            WorkspaceRoot = _workspace.RootPath,
            ManifestPath = manifest.ManifestPath,
            DryRun = context.DryRun,
            Warnings = new List<string>(),
        };

        var restores = new List<RollbackItem>();
        var deletes = new List<RollbackItem>();
        for (var index = manifest.Files.Count - 1; index >= 0; index--)
        {
            var entry = manifest.Files[index];
            string relative;
            string target;
            try
            {
                relative = PathSafety.ValidateRelative(entry.RelativePath);
                target = PathGuard.ResolveGameRelative(game, relative);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<RollbackReport>.Failure(ex.Error!);
            }

            if (Directory.Exists(target))
            {
                return Failure<RollbackReport>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"回滚目标是目录，不是文件：{relative}",
                    "人工确认后再回滚。");
            }

            if (entry.ExistedBefore)
            {
                if (string.IsNullOrWhiteSpace(entry.BackupPath))
                {
                    return Failure<RollbackReport>(
                        ForgeErrorCodes.ArtifactMissing,
                        $"回滚缺少备份路径：{relative}",
                        "账本被手工改过；不要继续回滚。");
                }

                string backup;
                try
                {
                    backup = _workspace.EnsureBackupPath(Path.Combine(
                        manifest.BackupRoot,
                        entry.BackupPath.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<RollbackReport>.Failure(ex.Error!);
                }

                if (!File.Exists(backup))
                {
                    return Failure<RollbackReport>(
                        ForgeErrorCodes.ArtifactMissing,
                        $"回滚缺少备份文件：{relative}",
                        "备份被移动或删除，无法安全回滚。");
                }

                if (!string.IsNullOrWhiteSpace(entry.Sha256Before)
                    && !FileHashes.MatchesFile(backup, entry.Sha256Before))
                {
                    return Failure<RollbackReport>(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"备份文件哈希不匹配，拒绝用损坏的备份回滚：{relative}",
                        "从其他备份恢复，或重新部署；不要使用被改动的备份。");
                }

                if (File.Exists(target) && !string.IsNullOrWhiteSpace(entry.Sha256After))
                {
                    var actual = FileHashes.Sha256File(target);
                    if (!FileHashes.EqualsHash(actual, entry.Sha256After))
                    {
                        if (!options.Force)
                        {
                            return Failure<RollbackReport>(
                                ForgeErrorCodes.ArtifactInvalid,
                                $"目标文件在部署后被改动，拒绝覆盖：{relative}",
                                "确认改动来源；如确认要放弃改动，使用 --force 回滚。",
                                new Dictionary<string, string>
                                {
                                    ["relativePath"] = relative,
                                    ["expectedSha256"] = entry.Sha256After,
                                    ["actualSha256"] = actual,
                                });
                        }

                        report.Warnings.Add($"强制回滚已改动的文件：{relative}");
                    }
                }

                restores.Add(new RollbackItem(entry, relative, target, backup));
            }
            else
            {
                if (File.Exists(target) && !string.IsNullOrWhiteSpace(entry.Sha256After))
                {
                    var actual = FileHashes.Sha256File(target);
                    if (!FileHashes.EqualsHash(actual, entry.Sha256After))
                    {
                        if (!options.Force)
                        {
                            return Failure<RollbackReport>(
                                ForgeErrorCodes.ArtifactInvalid,
                                $"待删除文件在部署后被改动，拒绝删除：{relative}",
                                "确认改动来源；如确认要删除，使用 --force 回滚。",
                                new Dictionary<string, string>
                                {
                                    ["relativePath"] = relative,
                                    ["expectedSha256"] = entry.Sha256After,
                                    ["actualSha256"] = actual,
                                });
                        }

                        report.Warnings.Add($"强制删除已改动的文件：{relative}");
                    }
                }

                deletes.Add(new RollbackItem(entry, relative, target, null));
            }
        }

        var order = 0;
        foreach (var item in restores)
        {
            report.Actions.Add(new RollbackAction
            {
                Order = ++order,
                Action = DeployRollbackAction.Restore,
                RelativePath = item.Relative,
                BackupPath = item.Backup,
                Sha256Expected = item.Entry.Sha256Before,
            });
        }

        foreach (var item in deletes)
        {
            report.Actions.Add(new RollbackAction
            {
                Order = ++order,
                Action = DeployRollbackAction.Delete,
                RelativePath = item.Relative,
                Sha256Expected = item.Entry.Sha256After,
                Outcome = File.Exists(item.Target) ? "planned" : "skipped",
            });
        }

        report.AlreadyClean = restores.Count == 0 && deletes.All(item => !File.Exists(item.Target));

        if (context.DryRun)
        {
            report.Ok = true;
            return ForgeResult<RollbackReport>.Success(report);
        }

        foreach (var item in restores)
        {
            var action = report.Actions.First(candidate =>
                candidate.Action == DeployRollbackAction.Restore
                && candidate.RelativePath == item.Relative);
            try
            {
                WriteAtomic(item.Target, File.ReadAllBytes(item.Backup!));
                if (!string.IsNullOrWhiteSpace(item.Entry.Sha256Before)
                    && !FileHashes.MatchesFile(item.Target, item.Entry.Sha256Before))
                {
                    action.Outcome = "failed";
                    action.Error = "恢复后哈希不匹配";
                    report.HashMismatches.Add(item.Relative);
                    report.Errors.Add($"{item.Relative}: 恢复后哈希不匹配");
                    continue;
                }

                action.Outcome = "restored";
                report.RestoredFiles.Add(item.Relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                action.Outcome = "failed";
                action.Error = ex.Message;
                report.Errors.Add($"{item.Relative}: {ex.Message}");
            }
        }

        if (report.Errors.Count > 0)
        {
            report.Warnings.Add("恢复阶段有失败，已停止删除 Add 文件，避免把游戏弄成更不完整的状态。");
            return PartialRollback(report);
        }

        foreach (var item in deletes)
        {
            var action = report.Actions.First(candidate =>
                candidate.Action == DeployRollbackAction.Delete
                && candidate.RelativePath == item.Relative);
            if (action.Outcome == "skipped")
            {
                report.SkippedFiles.Add(item.Relative);
                continue;
            }

            try
            {
                if (File.Exists(item.Target))
                {
                    File.Delete(item.Target);
                }

                if (File.Exists(item.Target))
                {
                    action.Outcome = "failed";
                    action.Error = "删除后文件仍然存在";
                    report.Errors.Add($"{item.Relative}: 删除后文件仍然存在");
                    continue;
                }

                action.Outcome = "deleted";
                report.DeletedFiles.Add(item.Relative);
                TryDeleteCompiledRpy(item.Target, item.Relative, report.Warnings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                action.Outcome = "failed";
                action.Error = ex.Message;
                report.Errors.Add($"{item.Relative}: {ex.Message}");
            }
        }

        if (report.Errors.Count > 0)
        {
            return PartialRollback(report);
        }

        if (options.CleanupEmptyDirectories)
        {
            CleanupEmptyDirectories(game, manifest.CreatedDirectories, report);
        }

        report.Ok = report.Errors.Count == 0 && report.HashMismatches.Count == 0;
        return report.Ok
            ? ForgeResult<RollbackReport>.Success(report)
            : PartialRollback(report);
    }

    public Task<ForgeResult<DeployRollbackResult>> RollbackAsync(
        GameContext context,
        DeployManifest manifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (manifest is null)
        {
            return Task.FromResult(ForgeResult<DeployRollbackResult>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "回滚账本为空。",
                "使用 install 生成的 install-manifest.json。")));
        }

        var slug = ResolveSlug(context, context?.GameDirectory ?? string.Empty);
        var installManifest = InstallManifest.FromDeployManifest(
            manifest,
            new ForgeWorkspaceInfo { RootPath = _workspace.RootPath, Slug = slug });
        var rollback = RollbackWithManifest(context!, installManifest);
        if (!rollback.Ok || rollback.Value is null)
        {
            return Task.FromResult(ForgeResult<DeployRollbackResult>.Failure(rollback.Error!));
        }

        var report = rollback.Value;
        return Task.FromResult(ForgeResult<DeployRollbackResult>.Success(new DeployRollbackResult
        {
            RestoredFiles = report.RestoredFiles,
            DeletedFiles = report.DeletedFiles,
            AlreadyClean = report.AlreadyClean,
        }));
    }

    private string ResolveGenerationDirectory(string slug, BuildResult? build)
    {
        var requested = !string.IsNullOrWhiteSpace(_options.GenerationDirectory)
            ? _options.GenerationDirectory
            : build is not null && !string.IsNullOrWhiteSpace(build.OutputDirectory)
                ? build.OutputDirectory
                : _workspace.StageDirectory(slug, PipelineStage.Generate);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        if (!PathSafety.IsUnder(_workspace.RootPath, full)
            || PathGuard.SamePath(full, _workspace.RootPath))
        {
            throw new ForgePathException(ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"生成物目录必须在工作区内：{full}",
                "重新 generate 到工作区（默认 games/<slug>/03-generate）。"));
        }

        if (PathSafety.IsUnder(full, _workspace.GameDirectory ?? string.Empty)
            && !string.IsNullOrWhiteSpace(_workspace.GameDirectory))
        {
            throw new ForgePathException(ForgeError.Create(
                ForgeErrorCodes.GameWriteDenied,
                $"生成物目录不能位于游戏目录内：{full}",
                "游戏目录只读；请把生成物放在工作区。"));
        }

        return full;
    }

    private ForgeResult<OverlayManifestData> LoadOverlayManifest(string generationDirectory, string game)
    {
        var manifestPath = Path.Combine(generationDirectory, OverlayManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return Failure<OverlayManifestData>(
                ForgeErrorCodes.ArtifactMissing,
                $"找不到覆盖层清单：{manifestPath}",
                "先运行 forge generate，确认 03-generate 下有 11 个文件。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        }
        catch (JsonException ex)
        {
            return Failure<OverlayManifestData>(
                ForgeErrorCodes.ArtifactInvalid,
                $"覆盖层清单不是有效 JSON：{ex.Message}",
                "重新 generate；不要手工编辑清单。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<OverlayManifestData>(
                ForgeErrorCodes.Io,
                $"读取覆盖层清单失败：{ex.Message}",
                "检查文件权限。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure<OverlayManifestData>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "覆盖层清单结构无效：根节点不是对象。",
                    "重新 generate。");
            }

            if (!string.Equals(GetString(root, "schema"), OverlaySchema, StringComparison.Ordinal))
            {
                return Failure<OverlayManifestData>(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"覆盖层清单 schema 不匹配：{GetString(root, "schema") ?? "<null>"}",
                    $"需要 {OverlaySchema}；用与生成器匹配的版本重新 generate。");
            }

            var gameRoot = root.TryGetProperty("game", out var gameElement)
                ? GetString(gameElement, "root")
                : null;
            if (!string.IsNullOrWhiteSpace(gameRoot))
            {
                try
                {
                    if (!PathGuard.SamePath(gameRoot!, game))
                    {
                        return Failure<OverlayManifestData>(
                            ForgeErrorCodes.ArtifactInvalid,
                            "生成物属于另一个游戏目录。",
                            "在目标游戏副本上重新运行 generate。");
                    }
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<OverlayManifestData>.Failure(ex.Error!);
                }
            }

            var files = new List<OverlayFileSpec>();
            if (!root.TryGetProperty("files", out var filesElement)
                || filesElement.ValueKind != JsonValueKind.Array)
            {
                return Failure<OverlayManifestData>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "覆盖层清单结构无效：缺少 files 数组。",
                    "重新 generate。");
            }

            foreach (var element in filesElement.EnumerateArray())
            {
                var path = GetString(element, "path");
                var sha = GetString(element, "sha256");
                var bytes = GetLong(element, "bytes");
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha)
                    || !FileHashes.IsSha256(sha))
                {
                    return Failure<OverlayManifestData>(
                        ForgeErrorCodes.ArtifactInvalid,
                        "覆盖层清单里有文件缺少 path 或合法 sha256。",
                        "重新 generate；不要手工编辑清单。");
                }

                string normalized;
                try
                {
                    normalized = PathSafety.ValidateRelative(path);
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<OverlayManifestData>.Failure(ex.Error!);
                }

                files.Add(new OverlayFileSpec(normalized, sha!, bytes));
            }

            if (files.Count == 0)
            {
                return Failure<OverlayManifestData>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "覆盖层清单里没有文件。",
                    "重新 generate。");
            }

            var planPresent = false;
            var conflictOverlay = false;
            var planTargets = new List<string>();
            var planPath = Path.Combine(generationDirectory, DeployPlanFileName);
            if (File.Exists(planPath))
            {
                planPresent = true;
                var planResult = LoadDeployPlan(planPath, game, planTargets, out conflictOverlay);
                if (!planResult.Ok)
                {
                    return ForgeResult<OverlayManifestData>.Failure(planResult.Error!);
                }

                if (planTargets.Count > 0)
                {
                    var manifestPaths = new HashSet<string>(
                        files.Select(file => file.Path),
                        StringComparer.OrdinalIgnoreCase);
                    var targets = new HashSet<string>(planTargets, StringComparer.OrdinalIgnoreCase);
                    if (!manifestPaths.SetEquals(targets))
                    {
                        return Failure<OverlayManifestData>(
                            ForgeErrorCodes.ArtifactInvalid,
                            "deploy_plan.json 的 copy 步骤与 a11y_manifest.json 的文件列表不一致。",
                            "重新 generate；生成器应保证两份清单一致。");
                    }
                }
            }

            return ForgeResult<OverlayManifestData>.Success(new OverlayManifestData(
                files,
                gameRoot,
                GetString(root, "patch_version"),
                conflictOverlay,
                planPresent));
        }
    }

    private static ForgeResult<OverlayManifestData> LoadDeployPlan(
        string planPath,
        string game,
        List<string> targets,
        out bool conflictOverlay)
    {
        conflictOverlay = false;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(planPath, Encoding.UTF8));
        }
        catch (JsonException ex)
        {
            return Failure<OverlayManifestData>(
                ForgeErrorCodes.ArtifactInvalid,
                $"部署计划不是有效 JSON：{ex.Message}",
                "重新 generate；不要手工编辑部署计划。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<OverlayManifestData>(
                ForgeErrorCodes.Io,
                $"读取部署计划失败：{ex.Message}",
                "检查文件权限。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(GetString(root, "schema"), DeployPlanSchema, StringComparison.Ordinal))
            {
                return Failure<OverlayManifestData>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "部署计划 schema 不匹配。",
                    $"需要 {DeployPlanSchema}；重新 generate。");
            }

            if (root.TryGetProperty("game_root", out var rootElement)
                && rootElement.ValueKind == JsonValueKind.String)
            {
                var gameRoot = rootElement.GetString();
                if (!string.IsNullOrWhiteSpace(gameRoot))
                {
                    try
                    {
                        if (!PathGuard.SamePath(gameRoot!, game))
                        {
                            return Failure<OverlayManifestData>(
                                ForgeErrorCodes.ArtifactInvalid,
                                "部署计划属于另一个游戏目录。",
                                "在目标游戏副本上重新运行 generate。");
                        }
                    }
                    catch (ForgePathException ex)
                    {
                        return ForgeResult<OverlayManifestData>.Failure(ex.Error!);
                    }
                }
            }

            if (root.TryGetProperty("conflict_overlay", out var conflict)
                && conflict.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                conflictOverlay = conflict.GetBoolean();
            }

            if (!root.TryGetProperty("steps", out var steps)
                || steps.ValueKind != JsonValueKind.Array)
            {
                return Failure<OverlayManifestData>(
                    ForgeErrorCodes.ArtifactInvalid,
                    "部署计划缺少 steps 数组。",
                    "重新 generate。");
            }

            foreach (var step in steps.EnumerateArray())
            {
                if (!string.Equals(GetString(step, "op"), "copy", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = GetString(step, "target");
                if (string.IsNullOrWhiteSpace(target))
                {
                    return Failure<OverlayManifestData>(
                        ForgeErrorCodes.ArtifactInvalid,
                        "部署计划的 copy 步骤缺少 target。",
                        "重新 generate。");
                }

                try
                {
                    targets.Add(PathSafety.ValidateRelative(target));
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<OverlayManifestData>.Failure(ex.Error!);
                }
            }
        }

        return ForgeResult<OverlayManifestData>.Success(new OverlayManifestData(
            new List<OverlayFileSpec>(),
            null,
            null,
            conflictOverlay,
            true));
    }

    private static List<string> CollectCreatedDirectories(string game, IEnumerable<OverlayOperation> operations)
    {
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            var target = PathGuard.ResolveGameRelative(game, operation.RelativePath);
            var parent = Path.GetDirectoryName(target);
            while (!string.IsNullOrWhiteSpace(parent)
                   && PathSafety.IsUnder(game, parent)
                   && !PathGuard.SamePath(parent, game))
            {
                if (!Directory.Exists(parent))
                {
                    created.Add(PathSafety.ValidateRelative(Path.GetRelativePath(game, parent)));
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        return created
            .OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AutoRollback(
        string game,
        string backupDirectory,
        OverlayDeploymentPlan plan,
        IReadOnlyList<OverlayOperation> applied,
        List<string> warnings)
    {
        for (var index = applied.Count - 1; index >= 0; index--)
        {
            var operation = applied[index];
            try
            {
                var target = PathGuard.ResolveGameRelative(game, operation.RelativePath);
                if (operation.ExistedBefore)
                {
                    if (string.IsNullOrWhiteSpace(operation.BackupRelativePath))
                    {
                        continue;
                    }

                    var backup = Path.Combine(
                        backupDirectory,
                        operation.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    _workspace.EnsureBackupPath(backup);
                    if (File.Exists(backup))
                    {
                        WriteAtomic(target, File.ReadAllBytes(backup));
                    }
                }
                else
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    TryDeleteCompiledRpy(target, operation.RelativePath, warnings);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                warnings.Add($"自动回滚失败：{operation.RelativePath}（{ex.Message}）");
            }
        }

        foreach (var relative in plan.CreatedDirectories
                     .OrderByDescending(path => path.Count(character => character == '/'))
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = PathGuard.ResolveGameRelative(game, relative);
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                warnings.Add($"自动回滚清理目录失败：{relative}（{ex.Message}）");
            }
        }
    }

    private void RestoreBaseline(string baselinePath, byte[]? previousBaseline)
    {
        try
        {
            if (previousBaseline is null)
            {
                if (File.Exists(baselinePath))
                {
                    File.Delete(baselinePath);
                }

                return;
            }

            var directory = Path.GetDirectoryName(baselinePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(baselinePath, previousBaseline);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 基线恢复失败不改变部署失败的主结论。
        }
    }

    private static void CleanupEmptyDirectories(
        string game,
        IEnumerable<string> relativeDirectories,
        RollbackReport report)
    {
        foreach (var relative in (relativeDirectories ?? Enumerable.Empty<string>())
                     .OrderByDescending(path => path.Count(character => character == '/'))
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = PathGuard.ResolveGameRelative(game, relative);
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                    report.CleanedDirectories.Add(relative);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                report.Warnings.Add($"清理空目录失败：{relative}（{ex.Message}）");
            }
        }
    }

    /// <summary>Ren'Py 会把 .rpy 编译成同名 .rpyc；只删我们自己的 .rpy 对应的编译缓存。</summary>
    private static void TryDeleteCompiledRpy(string target, string relative, List<string> warnings)
    {
        if (!relative.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var compiled = target + "c";
        try
        {
            if (File.Exists(compiled))
            {
                File.Delete(compiled);
                warnings.Add($"已删除 Ren'Py 编译缓存：{Path.GetFileName(compiled)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"删除编译缓存失败：{Path.GetFileName(compiled)}（{ex.Message}）");
        }
    }

    private static void WriteAtomic(string target, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 临时文件清理失败不改变主结果。
                }
            }
        }
    }

    private ForgeResult<string> ResolveGameDirectory(GameContext? context)
    {
        if (context is null)
        {
            return Failure<string>(ForgeErrorCodes.ArgumentInvalid, "缺少游戏上下文。", "检查调用参数。");
        }

        if (string.IsNullOrWhiteSpace(context.GameDirectory))
        {
            return Failure<string>(ForgeErrorCodes.ArgumentInvalid, "游戏目录为空。", "传入 --game <游戏目录>。");
        }

        string game;
        try
        {
            game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure<string>(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不是合法路径：{context.GameDirectory}",
                "传入一个有效的目录路径。");
        }

        return Directory.Exists(game)
            ? ForgeResult<string>.Success(game)
            : Failure<string>(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{game}",
                "确认游戏仍安装在原路径。");
    }

    private static string ResolveSlug(GameContext? context, string game)
    {
        var slug = context?.Slug;
        if (!PathSafety.IsValidSlug(slug))
        {
            slug = PathSafety.SlugFromGameDirectory(game);
        }

        return slug!;
    }

    private static string SourcePath(string generationDirectory, string relative)
        => Path.Combine(generationDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string NewDeploymentId()
        => $"renpy-{DateTime.Now:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";

    private static ForgeResult<RollbackReport> PartialRollback(RollbackReport report)
    {
        report.Ok = false;
        var details = new Dictionary<string, string>
        {
            ["errors"] = string.Join(" | ", report.Errors),
            ["hashMismatches"] = string.Join(" | ", report.HashMismatches),
        };
        return ForgeResult<RollbackReport>.Failure(ForgeError.Create(
            ForgeErrorCodes.Partial,
            "覆盖层回滚未完全成功；游戏目录可能处于部分恢复状态。",
            "按备份目录人工核对；不要直接启动游戏，先确认所有替换文件已恢复。",
            details));
    }

    private static List<string> EnumerateOverlayDirectories(string game)
    {
        var directories = new List<string>();
        try
        {
            var overlayRoot = Path.Combine(game, "game");
            if (!Directory.Exists(overlayRoot))
            {
                return directories;
            }

            foreach (var directory in Directory.EnumerateDirectories(overlayRoot))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith("a11y", StringComparison.OrdinalIgnoreCase))
                {
                    directories.Add("game/" + name);
                }
            }
        }
        catch (IOException)
        {
            // 目录不可读时只返回已发现的项，安装闸门的错误信息仍然是可用的。
        }
        catch (UnauthorizedAccessException)
        {
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        return directories;
    }

    private static ForgeResult<T> Failure<T>(string code, string message, string? suggestion = null)
        => ForgeResult<T>.Failure(ForgeError.Create(code, message, suggestion));

    private static ForgeResult<T> Failure<T>(
        string code,
        string message,
        string? suggestion,
        IReadOnlyDictionary<string, string> details)
        => ForgeResult<T>.Failure(ForgeError.Create(code, message, suggestion, details));

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long GetLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt64(out var value)
            ? value
            : -1;

    private sealed record OverlayFileSpec(string Path, string Sha256, long Bytes);

    private sealed record OverlayManifestData(
        List<OverlayFileSpec> Files,
        string? GameRoot,
        string? PatchVersion,
        bool ConflictOverlay,
        bool PlanPresent);

    private sealed record RollbackItem(
        InstallFileEntry Entry,
        string Relative,
        string Target,
        string? Backup);
}
