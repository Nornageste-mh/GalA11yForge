using System.Diagnostics;
using System.Text;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Deploy;
using GalA11yForge.Core.Logging;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>
/// IDeployer 实现：把构建产物与 BepInEx 装进游戏目录。
/// 所有游戏内写入都经过 PathGuard；写前先备份并校验，账本写失败或写盘异常会尽力自动回滚。
/// </summary>
public sealed class PluginDeployer : IDeployer
{
    private const string Stage = "05-deploy";

    private readonly ForgeWorkspace _workspace;
    private readonly DeploymentOptions _options;
    private readonly IFlowLogger _logger;

    public PluginDeployer(ForgeWorkspace workspace, DeploymentOptions? options = null, IFlowLogger? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = (options ?? new DeploymentOptions()).Validate();
        _logger = logger ?? NullFlowLogger.Instance;
    }

    /// <summary>只算部署计划；dry-run 的 JSON 直接来自这里的返回值，不落盘、不备份。</summary>
    public ForgeResult<DeploymentPlan> Plan(GameContext context, BuildResult? build = null)
    {
        if (context is null)
        {
            return Failure<DeploymentPlan>(ForgeErrorCodes.ArgumentInvalid, "缺少游戏上下文。", "先识别游戏目录再部署。");
        }

        try
        {
            return PlanCore(context, build);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<DeploymentPlan>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<DeploymentPlan>(ForgeErrorCodes.Io, $"计算部署计划时读写失败：{ex.Message}", "检查游戏目录与工作区权限。");
        }
        catch (Exception ex)
        {
            return Failure<DeploymentPlan>(ForgeErrorCodes.Internal, $"计算部署计划失败（未预期错误）：{ex.Message}", "保留日志并反馈给开发者。");
        }
    }

    public Task<ForgeResult<DeploymentPlan>> PlanAsync(
        GameContext context,
        BuildResult? build = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure<DeploymentPlan>(ForgeErrorCodes.Io, "部署计划已取消。", "重新运行部署。"));
        }

        return Task.FromResult(Plan(context, build));
    }

    /// <summary>按已有计划部署；计划与真实游戏状态不一致时拒绝执行，不覆盖计划外的变化。</summary>
    public ForgeResult<DeploymentOutcome> DeployWithPlan(GameContext context, BuildResult? build, DeploymentPlan plan)
    {
        if (context is null)
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.ArgumentInvalid, "缺少游戏上下文。", "先识别游戏目录再部署。");
        }

        if (plan is null)
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.ArgumentInvalid, "缺少部署计划。", "先运行 dry-run 生成计划。");
        }

        try
        {
            return DeployPlanCore(context, build, plan);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<DeploymentOutcome>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.Io, $"部署时读写失败：{ex.Message}", "检查游戏目录与工作区权限，必要时按备份手工恢复。");
        }
        catch (Exception ex)
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.Internal, $"部署失败（未预期错误）：{ex.Message}", "保留日志与备份目录并反馈给开发者。");
        }
    }

    public Task<ForgeResult<DeploymentOutcome>> DeployWithPlanAsync(
        GameContext context,
        BuildResult? build,
        DeploymentPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure<DeploymentOutcome>(ForgeErrorCodes.Io, "部署已取消。", "重新运行部署。"));
        }

        return Task.FromResult(DeployWithPlan(context, build, plan));
    }

    public Task<ForgeResult<DeployResult>> DeployAsync(
        GameContext context,
        BuildResult build,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure<DeployResult>(ForgeErrorCodes.Io, "部署已取消。", "重新运行部署。"));
        }

        var plan = Plan(context, build);
        if (!plan.Ok)
        {
            return Task.FromResult(ForgeResult<DeployResult>.Failure(plan.Error!));
        }

        var outcome = DeployWithPlan(context, build, plan.Value!);
        if (!outcome.Ok)
        {
            return Task.FromResult(ForgeResult<DeployResult>.Failure(outcome.Error!));
        }

        return Task.FromResult(ForgeResult<DeployResult>.Success(new DeployResult
        {
            Manifest = outcome.Value!.Manifest.ToDeployManifest(),
            BackupDirectory = outcome.Value.BackupDirectory,
        }));
    }

    /// <summary>从 install-manifest.json 回滚；返回带每一步结果的详细报告。</summary>
    public ForgeResult<RollbackReport> RollbackFromFile(GameContext context, string manifestPath, RollbackOptions? options = null)
    {
        if (context is null)
        {
            return Failure<RollbackReport>(ForgeErrorCodes.ArgumentInvalid, "缺少游戏上下文。", "先识别游戏目录再回滚。");
        }

        try
        {
            var load = InstallManifestStore.Load(_workspace, manifestPath);
            if (!load.Ok)
            {
                return ForgeResult<RollbackReport>.Failure(load.Error!);
            }

            var manifest = load.Value!;
            var report = RollbackWithManifest(context, manifest, options);
            if (report.Ok)
            {
                report.Value!.ManifestPath = Path.GetFullPath(manifestPath);
                if (report.Value!.Ok && !report.Value!.DryRun)
                {
                    // 0.2.47：回滚成功后标记账本“已回滚”，下次安装不再要求 --force（账本保留用于追溯）。
                    var marked = InstallManifestStore.MarkRolledBack(_workspace, manifest, report.Value!.ManifestPath!);
                    if (!marked.Ok)
                    {
                        report.Value!.Warnings.Add(
                            $"回滚成功，但账本未能标记“已回滚”：{marked.Error?.Message}；下次安装可能仍要求 --force。");
                    }
                }
            }

            return report;
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<RollbackReport>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<RollbackReport>(ForgeErrorCodes.Io, $"读取账本失败：{ex.Message}", "检查文件权限。");
        }
    }

    public Task<ForgeResult<RollbackReport>> RollbackFromFileAsync(
        GameContext context,
        string manifestPath,
        RollbackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure<RollbackReport>(ForgeErrorCodes.Io, "回滚已取消。", "重新运行回滚。"));
        }

        return Task.FromResult(RollbackFromFile(context, manifestPath, options));
    }

    /// <summary>用已读入的账本回滚；适合 Verify/Uninstaller 复用。</summary>
    public ForgeResult<RollbackReport> RollbackWithManifest(
        GameContext context,
        InstallManifest manifest,
        RollbackOptions? options = null)
    {
        if (context is null || manifest is null)
        {
            return Failure<RollbackReport>(ForgeErrorCodes.ArgumentInvalid, "缺少游戏上下文或账本。", "检查调用参数。");
        }

        try
        {
            return RollbackCore(context, manifest, options ?? new RollbackOptions());
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<RollbackReport>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<RollbackReport>(ForgeErrorCodes.Io, $"回滚时读写失败：{ex.Message}", "检查游戏目录与备份目录权限。");
        }
        catch (Exception ex)
        {
            return Failure<RollbackReport>(ForgeErrorCodes.Internal, $"回滚失败（未预期错误）：{ex.Message}", "保留备份目录并反馈给开发者。");
        }
    }

    public Task<ForgeResult<RollbackReport>> RollbackWithManifestAsync(
        GameContext context,
        InstallManifest manifest,
        RollbackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure<RollbackReport>(ForgeErrorCodes.Io, "回滚已取消。", "重新运行回滚。"));
        }

        return Task.FromResult(RollbackWithManifest(context, manifest, options));
    }

    public Task<ForgeResult<DeployRollbackResult>> RollbackAsync(
        GameContext context,
        DeployManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(Failure<DeployRollbackResult>(ForgeErrorCodes.Io, "回滚已取消。", "重新运行回滚。"));
        }

        if (manifest is null || context is null)
        {
            return Task.FromResult(Failure<DeployRollbackResult>(ForgeErrorCodes.ArgumentInvalid, "缺少游戏上下文或账本。", "检查调用参数。"));
        }

        var slug = !string.IsNullOrWhiteSpace(context.Slug)
            ? context.Slug
            : PathSafety.SlugFromGameDirectory(context.GameDirectory);
        var rich = InstallManifest.FromDeployManifest(manifest, new ForgeWorkspaceInfo
        {
            RootPath = _workspace.RootPath,
            Slug = slug,
        });

        var report = RollbackWithManifest(context, rich);
        if (!report.Ok)
        {
            return Task.FromResult(ForgeResult<DeployRollbackResult>.Failure(report.Error!));
        }

        var value = report.Value!;
        return Task.FromResult(ForgeResult<DeployRollbackResult>.Success(new DeployRollbackResult
        {
            RestoredFiles = value.RestoredFiles.ToList(),
            DeletedFiles = value.DeletedFiles.ToList(),
            AlreadyClean = value.AlreadyClean,
        }));
    }

    private ForgeResult<DeploymentPlan> PlanCore(GameContext context, BuildResult? build)
    {
        var resolved = ResolveContext(context, null);
        if (!resolved.Ok)
        {
            return ForgeResult<DeploymentPlan>.Failure(resolved.Error!);
        }

        var state = resolved.Value!;
        var warnings = new List<string>();

        var packageResult = LoadPackage();
        if (!packageResult.Ok)
        {
            return ForgeResult<DeploymentPlan>.Failure(packageResult.Error!);
        }

        var package = packageResult.Value!;
        var pluginResult = CollectPlugins(build, state, context.DryRun, warnings);
        if (!pluginResult.Ok)
        {
            return ForgeResult<DeploymentPlan>.Failure(pluginResult.Error!);
        }

        var plugins = pluginResult.Value!;

        var manifestExists = File.Exists(state.ManifestPath);
        // 0.2.47：上一批部署成功回滚后，账本保留但标记 rolledBack；这时不该再要求 --force。
        var previousRolledBack = manifestExists && InstallManifestStore.IsRolledBack(_workspace, state.ManifestPath);
        if (manifestExists && !_options.AllowRedeploy && !previousRolledBack)
        {
            return Failure<DeploymentPlan>(
                ForgeErrorCodes.ArtifactInvalid,
                $"目标工作区已有部署账本：{state.ManifestPath}",
                "先回滚上一次部署，或显式设置 AllowRedeploy=true（会归档旧账本，不会删除旧备份）。",
                new Dictionary<string, string> { ["manifestPath"] = state.ManifestPath });
        }

        if (manifestExists)
        {
            warnings.Add(previousRolledBack
                ? "上一批部署已回滚（账本已标记 rolledBack），将在备份后归档到 backup/<新批次>/previous-install-manifest.json 再重新安装。"
                : $"已有部署账本，将在备份后归档到 backup/{state.DeploymentId}/previous-install-manifest.json。");
        }

        var operations = new List<DeploymentOperation>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in package.Entries)
        {
            if (!paths.Add(entry.RelativePath))
            {
                return DuplicatePath<DeploymentPlan>(entry.RelativePath);
            }

            byte[] bytes;
            try
            {
                bytes = package.ReadBytes(entry.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
            {
                return Failure<DeploymentPlan>(ForgeErrorCodes.Io, $"读取 BepInEx 包内 {entry.RelativePath} 失败：{ex.Message}", "重新下载官方 BepInEx 包。");
            }

            var operation = DescribeOperation(state.GameDirectory, entry.RelativePath, "bepinex",
                FileHashes.Sha256Bytes(bytes), bytes.LongLength, _options.BepInExVersion, planned: false, warnings);
            if (!operation.Ok)
            {
                return ForgeResult<DeploymentPlan>.Failure(operation.Error!);
            }

            operations.Add(operation.Value!);
        }

        var pluginName = _options.PluginName;
        if (string.IsNullOrWhiteSpace(pluginName) && build is not null
            && build.Metadata.TryGetValue("pluginName", out var metadataName))
        {
            pluginName = metadataName;
        }

        if (string.IsNullOrWhiteSpace(pluginName) && plugins.Count > 0)
        {
            pluginName = Path.GetFileNameWithoutExtension(plugins[0].FileName);
        }

        var pluginVersion = _options.PluginVersion;
        if (string.IsNullOrWhiteSpace(pluginVersion) && build is not null
            && build.Metadata.TryGetValue("version", out var metadataVersion))
        {
            pluginVersion = metadataVersion;
        }

        pluginVersion ??= plugins.Select(item => item.Version)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version)) ?? "0.0.0.0";

        foreach (var plugin in plugins)
        {
            var relative = $"BepInEx/plugins/{plugin.FileName}";
            if (!paths.Add(relative))
            {
                return DuplicatePath<DeploymentPlan>(relative);
            }

            var operation = DescribeOperation(state.GameDirectory, relative, "plugin",
                plugin.Sha256, plugin.Size, pluginVersion, plugin.Planned, warnings);
            if (!operation.Ok)
            {
                return ForgeResult<DeploymentPlan>.Failure(operation.Error!);
            }

            operations.Add(operation.Value!);
        }

        foreach (var removal in _options.AdditionalRemovals ?? new List<string>())
        {
            string relative;
            try
            {
                relative = PathSafety.ValidateRelative(removal);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<DeploymentPlan>.Failure(ForgeError.Create(
                    ex.Error.Code,
                    $"要删除的游戏内路径非法：“{removal}”（{ex.Error.Message}）",
                    "只传游戏目录内的相对路径，不能含 ..、盘符或绝对路径。"));
            }

            if (!paths.Add(relative))
            {
                return DuplicatePath<DeploymentPlan>(relative);
            }

            var target = PathGuard.ResolveGameRelative(state.GameDirectory, relative);
            if (Directory.Exists(target))
            {
                return Failure<DeploymentPlan>(ForgeErrorCodes.ArtifactInvalid, $"要删除的路径是目录，不是文件：{relative}", "只移除文件；目录清理由部署账本记录的空目录完成。");
            }

            if (!File.Exists(target))
            {
                warnings.Add($"要删除的文件本来就不存在，已跳过：{relative}");
                continue;
            }

            var operation = DescribeOperation(state.GameDirectory, relative, "remove", null, 0, null, planned: false, warnings);
            if (!operation.Ok)
            {
                return ForgeResult<DeploymentPlan>.Failure(operation.Error!);
            }

            operations.Add(operation.Value!);
        }

        var createdDirectories = FindCreatedDirectories(state.GameDirectory, operations);
        var plan = new DeploymentPlan
        {
            SchemaVersion = 1,
            DeploymentId = state.DeploymentId,
            Slug = state.Slug,
            GameKey = state.Slug,
            GameDirectory = state.GameDirectory,
            WorkspaceRoot = _workspace.RootPath,
            BackupDirectory = state.BackupDirectory,
            ManifestPath = state.ManifestPath,
            BepInExVersion = _options.BepInExVersion,
            BepInExPackagePath = package.Path,
            BepInExPackageSha256 = package.Sha256,
            PluginName = pluginName,
            PluginVersion = pluginVersion,
            DryRun = context.DryRun,
            AllowRedeploy = _options.AllowRedeploy,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Operations = operations,
            CreatedDirectories = createdDirectories,
            Warnings = warnings,
        };

        _logger.Log(FlowLogLevel.Info, Stage, context.DryRun ? "dry-run：只计算部署计划，不写盘。" : "已计算部署计划。", new Dictionary<string, string>
        {
            ["deploymentId"] = plan.DeploymentId,
            ["operations"] = plan.Operations.Count.ToString(),
            ["game"] = plan.GameDirectory,
        });

        return ForgeResult<DeploymentPlan>.Success(plan);
    }

    private ForgeResult<DeploymentOutcome> DeployPlanCore(GameContext context, BuildResult? build, DeploymentPlan plan)
    {
        var resolved = ResolveContext(context, plan.DeploymentId);
        if (!resolved.Ok)
        {
            return ForgeResult<DeploymentOutcome>.Failure(resolved.Error!);
        }

        var state = resolved.Value!;
        if (!PathGuard.SamePath(plan.GameDirectory, state.GameDirectory)
            || !PathGuard.SamePath(plan.WorkspaceRoot, _workspace.RootPath)
            || !PathGuard.SamePath(plan.BackupDirectory, state.BackupDirectory))
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.ArgumentInvalid, "部署计划与当前游戏/工作区不匹配。", "重新运行 dry-run 生成计划。");
        }

        if (!PathSafety.IsUnder(_workspace.RootPath, plan.ManifestPath)
            || PathSafety.IsUnder(state.GameDirectory, plan.ManifestPath))
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.PathUnsafe, $"账本路径不在工作区内：{plan.ManifestPath}", "使用工作区内的 install-manifest.json。");
        }

        if (plan.Operations is null || plan.Operations.Count == 0)
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.ArtifactInvalid, "部署计划没有任何文件操作。", "确认构建产物与 BepInEx 包。");
        }

        if (context.DryRun || plan.DryRun)
        {
            return ForgeResult<DeploymentOutcome>.Success(new DeploymentOutcome
            {
                Manifest = plan.ToManifest(),
                ManifestPath = plan.ManifestPath,
                BackupDirectory = plan.BackupDirectory,
                Plan = plan,
                Warnings = plan.Warnings.ToList(),
            });
        }

        if (build is null)
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.ArtifactMissing, "实际部署需要构建结果，但传入为空。", "先完成构建，再部署。");
        }

        var hadExistingManifest = File.Exists(plan.ManifestPath);
        var hadRolledBackManifest = hadExistingManifest && InstallManifestStore.IsRolledBack(_workspace, plan.ManifestPath);
        if (hadExistingManifest && !_options.AllowRedeploy && !hadRolledBackManifest)
        {
            return Failure<DeploymentOutcome>(ForgeErrorCodes.ArtifactInvalid,
                $"目标工作区已有部署账本：{plan.ManifestPath}",
                "先回滚上一次部署，或显式允许重复部署。");
        }

        var pluginsResult = CollectPlugins(build, state, dryRun: false, new List<string>());
        if (!pluginsResult.Ok)
        {
            return ForgeResult<DeploymentOutcome>.Failure(pluginsResult.Error!);
        }

        var sourceByRelative = new Dictionary<string, PluginArtifact>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in pluginsResult.Value!)
        {
            sourceByRelative[$"BepInEx/plugins/{plugin.FileName}"] = plugin;
        }

        BepInExPackage? package = null;
        if (plan.Operations.Any(operation => string.Equals(operation.Source, "bepinex", StringComparison.OrdinalIgnoreCase)))
        {
            var packageResult = BepInExPackage.Open(
                plan.BepInExPackagePath ?? string.Empty,
                plan.BepInExPackageSha256,
                verifyHash: _options.VerifyPackageHash);
            if (!packageResult.Ok)
            {
                return ForgeResult<DeploymentOutcome>.Failure(packageResult.Error!);
            }

            package = packageResult.Value!;
            if (!string.IsNullOrWhiteSpace(plan.BepInExPackageSha256)
                && !FileHashes.EqualsHash(package.Sha256, plan.BepInExPackageSha256))
            {
                return Failure<DeploymentOutcome>(ForgeErrorCodes.ArtifactInvalid, "BepInEx 包在计划后发生变化。", "重新运行 dry-run。");
            }
        }

        foreach (var operation in plan.Operations)
        {
            if (!string.Equals(operation.Source, "plugin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!sourceByRelative.TryGetValue(operation.RelativePath, out var source))
            {
                return Failure<DeploymentOutcome>(ForgeErrorCodes.ArtifactMissing, $"计划中的插件不在本次构建产物里：{operation.RelativePath}", "重新运行构建与计划。");
            }

            if (!string.IsNullOrWhiteSpace(operation.Sha256After)
                && !FileHashes.MatchesFile(source.SourcePath, operation.Sha256After))
            {
                return Failure<DeploymentOutcome>(ForgeErrorCodes.ArtifactInvalid, $"构建产物在计划后发生变化：{operation.RelativePath}", "重新运行构建与计划。");
            }
        }

        var preflight = PreflightGameState(state.GameDirectory, plan);
        if (!preflight.Ok)
        {
            return ForgeResult<DeploymentOutcome>.Failure(preflight.Error!);
        }

        // 必须在第一次写游戏目录之前拍快照：回滚时靠它区分“安装前就有”和“本次安装/运行期新建”。
        var bepInExBaseline = CaptureBepInExBaseline(state.GameDirectory);

        // 安装前基线：本次部署目标（含原本不存在的文件）+ BepInEx 运行期白名单。
        // 采集失败时立即停止，绝不带着不完整的基线继续写游戏目录。
        var deploymentBaseline = DeploymentBaseline.Capture(state.GameDirectory, plan);
        if (!deploymentBaseline.Ok)
        {
            return ForgeResult<DeploymentOutcome>.Failure(deploymentBaseline.Error!);
        }

        // 剔除 BepInEx 运行期会自行改写的文件：否则 Verify 的 m-07 会把正常运行（生成 interop、
        // 写日志、chainloader 缓存）误判成“账本外文件被篡改”（R21/R22 真机踩过）。
        if (deploymentBaseline.Value is not null)
        {
            // Mono：保留 BepInEx/config（用户可核对配置；m-07 仍需能发现配置被改），
            // 剔除 interop/cache/patchers 与根日志这些每次启动必变的运行期文件。
            DeploymentBaseline.TrimRuntimeChurn(deploymentBaseline.Value, includeConfig: false);
        }

        var backupResult = PrepareBackups(state, plan);
        if (!backupResult.Ok)
        {
            return ForgeResult<DeploymentOutcome>.Failure(backupResult.Error!);
        }

        var applied = new List<DeploymentOperation>();
        try
        {
            foreach (var operation in plan.Operations)
            {
                var target = PathGuard.ResolveGameRelative(state.GameDirectory, operation.RelativePath);
                if (operation.Action == DeployFileAction.Add)
                {
                    if (File.Exists(target))
                    {
                        throw new InvalidOperationException($"计划新增的文件已存在：{operation.RelativePath}");
                    }
                }
                else
                {
                    if (!File.Exists(target))
                    {
                        throw new InvalidOperationException($"计划替换/删除的文件已不存在：{operation.RelativePath}");
                    }

                    if (!string.IsNullOrWhiteSpace(operation.Sha256Before)
                        && !FileHashes.MatchesFile(target, operation.Sha256Before))
                    {
                        throw new InvalidOperationException($"目标文件在部署过程中被改动：{operation.RelativePath}");
                    }
                }

                if (operation.Action == DeployFileAction.Remove)
                {
                    File.Delete(target);
                }
                else
                {
                    var bytes = ReadOperationBytes(operation, package, sourceByRelative);
                    if (string.IsNullOrWhiteSpace(operation.Sha256After)
                        || !FileHashes.EqualsHash(FileHashes.Sha256Bytes(bytes), operation.Sha256After))
                    {
                        throw new InvalidOperationException($"待写入内容的哈希与计划不一致：{operation.RelativePath}");
                    }

                    WriteAtomic(target, bytes);
                }

                applied.Add(operation);
                if (!VerifyOperationWithRetry(operation, target))
                {
                    throw new InvalidOperationException($"写入后哈希校验失败：{operation.RelativePath}");
                }
            }

            var verification = VerifyAll(state.GameDirectory, plan);
            if (!verification.Ok)
            {
                var verificationError = verification.Error!;
                var rollback = AutoRollback(state, plan, applied);
                return rollback.Count == 0
                    ? Failure<DeploymentOutcome>(ForgeErrorCodes.ArtifactInvalid,
                        $"部署后校验失败：{verificationError.Message}",
                        "已自动回滚本次写入；检查磁盘与杀毒软件后重试。",
                        new Dictionary<string, string> { ["backup"] = state.BackupDirectory })
                    : Failure<DeploymentOutcome>(ForgeErrorCodes.Partial,
                        $"部署后校验失败，且自动回滚未完全成功：{verificationError.Message}",
                        "立即按备份目录人工恢复；不要继续启动游戏。",
                        new Dictionary<string, string>
                        {
                            ["backup"] = state.BackupDirectory,
                            ["rollbackFailures"] = string.Join(" | ", rollback),
                        });
            }

            var manifest = plan.ToManifest();
            manifest.BepInExBaseline = bepInExBaseline;
            var now = DateTimeOffset.UtcNow.ToString("O");
            manifest.CreatedAt = now;
            manifest.DryRun = false;
            manifest.ManifestPath = plan.ManifestPath;
            foreach (var file in manifest.Files)
            {
                file.DeployedAt = now;
            }

            var save = InstallManifestStore.Save(_workspace, manifest, plan.ManifestPath);
            if (!save.Ok)
            {
                var saveError = save.Error!;
                var rollback = AutoRollback(state, plan, applied);
                return rollback.Count == 0
                    ? Failure<DeploymentOutcome>(ForgeErrorCodes.Io,
                        $"部署已写入，但账本保存失败：{saveError.Message}",
                        "已自动回滚本次写入；修复工作区权限后重试。",
                        new Dictionary<string, string> { ["backup"] = state.BackupDirectory })
                    : Failure<DeploymentOutcome>(ForgeErrorCodes.Partial,
                        $"账本保存失败，且自动回滚未完全成功：{saveError.Message}",
                        "立即按备份目录人工恢复；不要继续启动游戏。",
                        new Dictionary<string, string>
                        {
                            ["backup"] = state.BackupDirectory,
                            ["rollbackFailures"] = string.Join(" | ", rollback),
                        });
            }

            var savedManifestPath = save.Value!;
            manifest.ManifestPath = savedManifestPath;

            // 基线只在整个部署成功、账本写完后落盘；原子写失败会连同写入一起回滚，
            // 不在游戏目录或工作区留下“半截 baseline”误导回滚校验。
            var baselineSave = DeploymentBaseline.Save(_workspace, state.Slug, deploymentBaseline.Value!);
            if (!baselineSave.Ok)
            {
                var baselineError = baselineSave.Error!;
                var rollback = AutoRollback(state, plan, applied);
                if (rollback.Count == 0)
                {
                    if (!hadExistingManifest)
                    {
                        TryDelete(savedManifestPath);
                    }

                    return Failure<DeploymentOutcome>(ForgeErrorCodes.Io,
                        $"部署已写入，但安装前基线保存失败：{baselineError.Message}",
                        "已自动回滚本次写入并移除本次账本；修复工作区权限后重试。",
                        new Dictionary<string, string> { ["backup"] = state.BackupDirectory });
                }

                return Failure<DeploymentOutcome>(ForgeErrorCodes.Partial,
                    $"安装前基线保存失败，且自动回滚未完全成功：{baselineError.Message}",
                    "立即按备份目录人工恢复；不要继续启动游戏。",
                    new Dictionary<string, string>
                    {
                        ["backup"] = state.BackupDirectory,
                        ["rollbackFailures"] = string.Join(" | ", rollback),
                    });
            }

            TryWriteBackupManifest(state.BackupDirectory, manifest, plan.Warnings);

            _logger.Log(FlowLogLevel.Info, Stage, "部署完成并已写账本。", new Dictionary<string, string>
            {
                ["deploymentId"] = plan.DeploymentId,
                ["backup"] = state.BackupDirectory,
                ["manifest"] = savedManifestPath,
            });

            return ForgeResult<DeploymentOutcome>.Success(new DeploymentOutcome
            {
                Manifest = manifest,
                ManifestPath = savedManifestPath,
                BackupDirectory = state.BackupDirectory,
                Plan = plan,
                Warnings = plan.Warnings,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ForgePathException)
        {
            var rollback = AutoRollback(state, plan, applied);
            if (rollback.Count == 0)
            {
                return Failure<DeploymentOutcome>(ForgeErrorCodes.Io,
                    $"部署失败，已自动回滚：{ex.Message}",
                    "检查游戏目录权限、杀毒软件与磁盘状态后重试。",
                    new Dictionary<string, string> { ["backup"] = state.BackupDirectory, ["autoRollback"] = "true" });
            }

            return Failure<DeploymentOutcome>(ForgeErrorCodes.Partial,
                $"部署失败，且自动回滚未完全成功：{ex.Message}",
                "立即按备份目录人工恢复；不要继续启动游戏。",
                new Dictionary<string, string>
                {
                    ["backup"] = state.BackupDirectory,
                    ["rollbackFailures"] = string.Join(" | ", rollback),
                });
        }
    }

    private ForgeResult<bool> PreflightGameState(string gameDirectory, DeploymentPlan plan)
    {
        foreach (var operation in plan.Operations)
        {
            var target = PathGuard.ResolveGameRelative(gameDirectory, operation.RelativePath);
            if (Directory.Exists(target))
            {
                return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"目标路径是目录：{operation.RelativePath}", "移除同名目录后重试。");
            }

            if (operation.Action == DeployFileAction.Add)
            {
                if (File.Exists(target))
                {
                    return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"计划新增的文件已存在：{operation.RelativePath}", "游戏目录在计划后发生变化，请重新运行 dry-run。");
                }

                continue;
            }

            if (!File.Exists(target))
            {
                return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"计划替换/删除的文件不存在：{operation.RelativePath}", "游戏目录在计划后发生变化，请重新运行 dry-run。");
            }

            if (!string.IsNullOrWhiteSpace(operation.Sha256Before)
                && !FileHashes.MatchesFile(target, operation.Sha256Before))
            {
                return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"目标文件哈希与计划不一致：{operation.RelativePath}", "游戏文件被改动；重新运行计划并确认变更来源。");
            }
        }

        return ForgeResult<bool>.Success(true);
    }

    private ForgeResult<bool> PrepareBackups(ResolvedContext state, DeploymentPlan plan)
    {
        try
        {
            _workspace.EnsureBackupPath(state.BackupDirectory);
            if (Directory.Exists(state.BackupDirectory)
                && Directory.EnumerateFileSystemEntries(state.BackupDirectory).Any())
            {
                return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"备份目录已存在且非空：{state.BackupDirectory}", "换一个部署 id，或先归档旧备份。");
            }

            Directory.CreateDirectory(state.BackupDirectory);

            if (File.Exists(plan.ManifestPath))
            {
                var archived = Path.Combine(state.BackupDirectory, "previous-install-manifest.json");
                File.Copy(plan.ManifestPath, archived, overwrite: false);
            }

            foreach (var operation in plan.Operations)
            {
                if (operation.Action == DeployFileAction.Add)
                {
                    continue;
                }

                var target = PathGuard.ResolveGameRelative(state.GameDirectory, operation.RelativePath);
                if (!File.Exists(target))
                {
                    return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"准备备份时目标文件已不存在：{operation.RelativePath}", "重新运行 dry-run。");
                }

                if (!string.IsNullOrWhiteSpace(operation.Sha256Before)
                    && !FileHashes.MatchesFile(target, operation.Sha256Before))
                {
                    return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"准备备份时目标文件哈希变化：{operation.RelativePath}", "重新运行 dry-run。");
                }

                var relativeBackup = operation.RelativePath;
                var backupPath = _workspace.EnsureBackupPath(
                    Path.Combine(state.BackupDirectory, relativeBackup.Replace('/', Path.DirectorySeparatorChar)));
                var backupDirectory = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }

                File.Copy(target, backupPath, overwrite: false);
                operation.BackupRelativePath = relativeBackup;
                if (!string.IsNullOrWhiteSpace(operation.Sha256Before)
                    && !FileHashes.MatchesFile(backupPath, operation.Sha256Before))
                {
                    return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"备份后哈希校验失败：{operation.RelativePath}", "检查磁盘状态；不要继续部署。");
                }
            }

            return ForgeResult<bool>.Success(true);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<bool>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure<bool>(ForgeErrorCodes.Io, $"准备备份失败：{ex.Message}", "检查工作区 backup/ 与游戏目录权限。");
        }
    }

    private static byte[] ReadOperationBytes(
        DeploymentOperation operation,
        BepInExPackage? package,
        IReadOnlyDictionary<string, PluginArtifact> plugins)
    {
        if (string.Equals(operation.Source, "bepinex", StringComparison.OrdinalIgnoreCase))
        {
            if (package is null)
            {
                throw new InvalidOperationException("部署计划包含 BepInEx 文件，但包未打开。");
            }

            return package.ReadBytes(operation.RelativePath);
        }

        if (string.Equals(operation.Source, "plugin", StringComparison.OrdinalIgnoreCase))
        {
            if (!plugins.TryGetValue(operation.RelativePath, out var plugin))
            {
                throw new InvalidOperationException($"找不到插件源文件：{operation.RelativePath}");
            }

            return File.ReadAllBytes(plugin.SourcePath);
        }

        throw new InvalidOperationException($"未知的部署来源：{operation.Source}");
    }

    private static bool VerifyOperation(DeploymentOperation operation, string target)
    {
        if (operation.Action == DeployFileAction.Remove)
        {
            return !File.Exists(target);
        }

        return File.Exists(target)
               && !string.IsNullOrWhiteSpace(operation.Sha256After)
               && FileHashes.MatchesFile(target, operation.Sha256After);
    }

    private static bool VerifyOperationWithRetry(DeploymentOperation operation, string target)
    {
        // 真机 E2E 观测到：首次安装偶发哈希校验失败（.doorstop_version 等文件被杀软/索引器短暂占用）。
        // 这不是内容错误，短暂重试即可恢复；连续失败才判定为真失败。
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (VerifyOperation(operation, target))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // 文件被短暂占用：继续重试。
            }
            catch (UnauthorizedAccessException)
            {
                // 杀软/索引器临时锁：继续重试。
            }

            if (attempt < 5)
            {
                System.Threading.Thread.Sleep(120);
            }
        }

        return false;
    }

    private static ForgeResult<bool> VerifyAll(string gameDirectory, DeploymentPlan plan)
    {
        foreach (var operation in plan.Operations)
        {
            var target = PathGuard.ResolveGameRelative(gameDirectory, operation.RelativePath);
            if (!VerifyOperationWithRetry(operation, target))
            {
                return Failure<bool>(ForgeErrorCodes.ArtifactInvalid, $"文件校验失败：{operation.RelativePath}", "重新运行部署或按备份恢复。");
            }
        }

        return ForgeResult<bool>.Success(true);
    }

    private List<string> AutoRollback(ResolvedContext state, DeploymentPlan plan, IReadOnlyList<DeploymentOperation> applied)
    {
        var failures = new List<string>();
        foreach (var operation in applied.Reverse())
        {
            try
            {
                var target = PathGuard.ResolveGameRelative(state.GameDirectory, operation.RelativePath);
                if (operation.Action == DeployFileAction.Add)
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(operation.BackupRelativePath))
                    {
                        failures.Add($"{operation.RelativePath}: 缺少备份路径");
                        continue;
                    }

                    var backup = _workspace.EnsureBackupPath(
                        Path.Combine(state.BackupDirectory, operation.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(backup))
                    {
                        failures.Add($"{operation.RelativePath}: 备份文件不存在");
                        continue;
                    }

                    WriteAtomic(target, File.ReadAllBytes(backup));
                    if (!string.IsNullOrWhiteSpace(operation.Sha256Before)
                        && !FileHashes.MatchesFile(target, operation.Sha256Before))
                    {
                        failures.Add($"{operation.RelativePath}: 恢复后哈希不匹配");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ForgePathException)
            {
                failures.Add($"{operation.RelativePath}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
        {
            CleanupEmptyDirectories(state.GameDirectory, plan.CreatedDirectories);
        }

        return failures;
    }

    private static void CleanupEmptyDirectories(string gameDirectory, IEnumerable<string> relativeDirectories)
    {
        foreach (var relative in relativeDirectories
                     .OrderByDescending(path => path.Count(ch => ch == '/'))
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = PathGuard.ResolveGameRelative(gameDirectory, relative);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                // 目录清理失败不影响文件回滚结果。
            }
        }
    }

    private ForgeResult<RollbackReport> RollbackCore(GameContext context, InstallManifest manifest, RollbackOptions options)
    {
        var resolved = ResolveContext(context, null);
        if (!resolved.Ok)
        {
            return ForgeResult<RollbackReport>.Failure(resolved.Error!);
        }

        var state = resolved.Value!;
        if (manifest.DryRun)
        {
            return Failure<RollbackReport>(ForgeErrorCodes.ArtifactInvalid, "这是 dry-run 账本，没有实际部署，不能回滚。", "使用真实部署生成的 install-manifest.json。");
        }

        var validation = InstallManifestValidation.Validate(_workspace, manifest, requireBackupFiles: true, requireHashes: true);
        if (!validation.Ok)
        {
            return ForgeResult<RollbackReport>.Failure(validation.Error!);
        }

        var contextCheck = ResolveRollbackContext(context, manifest, state);
        if (!contextCheck.Ok)
        {
            return ForgeResult<RollbackReport>.Failure(contextCheck.Error!);
        }

        var ledger = DeploymentLedger.BuildRollbackPlan(_workspace, manifest.ToDeployManifest());
        if (!ledger.Ok)
        {
            return ForgeResult<RollbackReport>.Failure(ledger.Error!);
        }

        var steps = ledger.Value!;
        var ordered = steps.Where(step => step.Action == DeployRollbackAction.Restore)
            .Concat(steps.Where(step => step.Action == DeployRollbackAction.Delete))
            .ToList();
        var entries = manifest.Files.ToDictionary(file => PathSafety.ValidateRelative(file.RelativePath), StringComparer.OrdinalIgnoreCase);
        var report = new RollbackReport
        {
            DeploymentId = manifest.DeploymentId,
            GameDirectory = state.GameDirectory,
            WorkspaceRoot = _workspace.RootPath,
            ManifestPath = manifest.ManifestPath,
            DryRun = context.DryRun,
            Warnings = new List<string>(),
        };

        var restoreActions = new List<RollbackAction>();
        var deleteActions = new List<RollbackAction>();
        var index = 0;
        foreach (var step in ordered)
        {
            index++;
            var relative = PathSafety.ValidateRelative(step.TargetRelativePath);
            var entry = entries[relative];
            var action = new RollbackAction
            {
                Order = index,
                Action = step.Action,
                RelativePath = relative,
                BackupPath = step.BackupPath,
                Sha256Expected = step.Action == DeployRollbackAction.Restore ? entry.Sha256Before : entry.Sha256After,
            };

            var target = PathGuard.ResolveGameRelative(state.GameDirectory, relative);
            if (Directory.Exists(target))
            {
                return Failure<RollbackReport>(ForgeErrorCodes.ArtifactInvalid, $"回滚目标是目录，不是文件：{relative}", "人工确认后再回滚。");
            }

            if (step.Action == DeployRollbackAction.Restore)
            {
                if (string.IsNullOrWhiteSpace(step.BackupPath) || !File.Exists(step.BackupPath))
                {
                    return Failure<RollbackReport>(ForgeErrorCodes.ArtifactMissing, $"回滚缺少备份文件：{relative}", "备份被移动或删除，无法安全回滚。");
                }

                _workspace.EnsureBackupPath(step.BackupPath);
                if (!string.IsNullOrWhiteSpace(entry.Sha256Before)
                    && !FileHashes.MatchesFile(step.BackupPath, entry.Sha256Before))
                {
                    return Failure<RollbackReport>(ForgeErrorCodes.ArtifactInvalid,
                        $"备份文件哈希不匹配，拒绝用损坏的备份回滚：{relative}",
                        "从其他备份恢复，或重新部署；不要使用被改动的备份。");
                }

                if (File.Exists(target))
                {
                    var actual = FileHashes.Sha256File(target);
                    action.Sha256Actual = actual;
                    if (!string.IsNullOrWhiteSpace(entry.Sha256After)
                        && !FileHashes.EqualsHash(actual, entry.Sha256After))
                    {
                        if (!options.Force)
                        {
                            return Failure<RollbackReport>(ForgeErrorCodes.ArtifactInvalid,
                                $"目标文件在部署后被改动，拒绝覆盖：{relative}",
                                "确认改动来源；如确认要放弃改动，使用 Force=true 回滚。",
                                new Dictionary<string, string>
                                {
                                    ["relativePath"] = relative,
                                    ["expectedSha256"] = entry.Sha256After!,
                                    ["actualSha256"] = actual,
                                });
                        }

                        report.Warnings.Add($"强制回滚已改动的文件：{relative}");
                    }

                    if (File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                    {
                        report.Warnings.Add($"目标文件只读，回滚可能失败：{relative}");
                    }
                }

                restoreActions.Add(action);
            }
            else
            {
                if (!File.Exists(target))
                {
                    action.Outcome = "skipped";
                    report.SkippedFiles.Add(relative);
                }
                else
                {
                    var actual = FileHashes.Sha256File(target);
                    action.Sha256Actual = actual;
                    if (!string.IsNullOrWhiteSpace(entry.Sha256After)
                        && !FileHashes.EqualsHash(actual, entry.Sha256After))
                    {
                        if (!options.Force)
                        {
                            return Failure<RollbackReport>(ForgeErrorCodes.ArtifactInvalid,
                                $"待删除文件在部署后被改动，拒绝删除：{relative}",
                                "确认改动来源；如确认要删除，使用 Force=true 回滚。",
                                new Dictionary<string, string>
                                {
                                    ["relativePath"] = relative,
                                    ["expectedSha256"] = entry.Sha256After!,
                                    ["actualSha256"] = actual,
                                });
                        }

                        report.Warnings.Add($"强制删除已改动的文件：{relative}");
                    }
                }

                deleteActions.Add(action);
            }
        }

        report.Actions = restoreActions.Concat(deleteActions).ToList();
        report.AlreadyClean = restoreActions.Count == 0 && deleteActions.All(action => action.Outcome == "skipped");

        if (context.DryRun)
        {
            report.Ok = true;
            return ForgeResult<RollbackReport>.Success(report);
        }

        foreach (var action in restoreActions)
        {
            try
            {
                var target = PathGuard.ResolveGameRelative(state.GameDirectory, action.RelativePath);
                var backup = _workspace.EnsureBackupPath(action.BackupPath!);
                WriteAtomic(target, File.ReadAllBytes(backup));
                var entry = entries[action.RelativePath];
                if (!string.IsNullOrWhiteSpace(entry.Sha256Before)
                    && !FileHashes.MatchesFile(target, entry.Sha256Before))
                {
                    action.Outcome = "failed";
                    action.Error = "恢复后哈希不匹配";
                    report.HashMismatches.Add(action.RelativePath);
                    report.Errors.Add($"{action.RelativePath}: 恢复后哈希不匹配");
                    continue;
                }

                action.Outcome = "restored";
                report.RestoredFiles.Add(action.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                action.Outcome = "failed";
                action.Error = ex.Message;
                report.Errors.Add($"{action.RelativePath}: {ex.Message}");
            }
        }

        if (report.Errors.Count > 0)
        {
            report.Warnings.Add("恢复阶段有失败，已停止删除 Add 文件，避免把游戏弄成更不完整的状态。");
            return PartialRollback(report);
        }

        foreach (var action in deleteActions)
        {
            if (action.Outcome == "skipped")
            {
                continue;
            }

            try
            {
                var target = PathGuard.ResolveGameRelative(state.GameDirectory, action.RelativePath);
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                if (File.Exists(target))
                {
                    action.Outcome = "failed";
                    action.Error = "删除后文件仍然存在";
                    report.Errors.Add($"{action.RelativePath}: 删除后文件仍然存在");
                }
                else
                {
                    action.Outcome = "deleted";
                    report.DeletedFiles.Add(action.RelativePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                action.Outcome = "failed";
                action.Error = ex.Message;
                report.Errors.Add($"{action.RelativePath}: {ex.Message}");
            }
        }

        if (report.Errors.Count > 0)
        {
            return PartialRollback(report);
        }

        if (options.CleanBepInExRuntimeArtifacts)
        {
            CleanBepInExRuntimeArtifactsReport(state.GameDirectory, manifest, report, options.CleanupEmptyDirectories);
        }

        if (options.CleanupEmptyDirectories)
        {
            CleanupEmptyDirectoriesReport(state.GameDirectory, manifest.CreatedDirectories, report);
        }

        report.Ok = report.HashMismatches.Count == 0 && report.Errors.Count == 0;
        _logger.Log(FlowLogLevel.Info, Stage, "回滚完成。", new Dictionary<string, string>
        {
            ["deploymentId"] = manifest.DeploymentId,
            ["restored"] = report.RestoredFiles.Count.ToString(),
            ["deleted"] = report.DeletedFiles.Count.ToString(),
        });
        return report.Ok
            ? ForgeResult<RollbackReport>.Success(report)
            : PartialRollback(report);
    }

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
            "回滚未完全成功；游戏目录可能处于部分恢复状态。",
            "按备份目录人工核对；不要直接启动游戏，先确认所有替换文件已恢复。",
            details));
    }

    private static void CleanupEmptyDirectoriesReport(string gameDirectory, IEnumerable<string> relativeDirectories, RollbackReport report)
    {
        foreach (var relative in relativeDirectories
                     .OrderByDescending(path => path.Count(ch => ch == '/'))
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = PathGuard.ResolveGameRelative(gameDirectory, relative);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
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

    private static readonly string[] BepInExRuntimeDirectoryRoots =
    {
        "BepInEx/cache",
        "BepInEx/config",
        "BepInEx/patchers",
    };

    private static readonly EnumerationOptions BepInExScanOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    /// <summary>
    /// 记录部署前 BepInEx 白名单区域里已存在的文件与目录。必须在第一次写游戏目录之前调用。
    /// 只记录 cache/config/patchers 及其子目录、BepInEx 根目录的 *.log；枚举失败时把 Complete 置 false。
    /// </summary>
    private static BepInExInstallBaseline CaptureBepInExBaseline(string gameDirectory)
    {
        var baseline = new BepInExInstallBaseline();
        var bepInEx = Path.Combine(gameDirectory, "BepInEx");
        if (!Directory.Exists(bepInEx))
        {
            return baseline;
        }

        baseline.Directories.Add("BepInEx");
        try
        {
            foreach (var relativeRoot in BepInExRuntimeDirectoryRoots)
            {
                var root = Path.Combine(gameDirectory, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(root))
                {
                    continue;
                }

                baseline.Directories.Add(relativeRoot);
                foreach (var directory in Directory.EnumerateDirectories(root, "*", BepInExScanOptions))
                {
                    baseline.Directories.Add(RelativeForBaseline(gameDirectory, directory));
                }

                foreach (var file in Directory.EnumerateFiles(root, "*", BepInExScanOptions))
                {
                    baseline.Files.Add(RelativeForBaseline(gameDirectory, file));
                }
            }

            foreach (var file in Directory.EnumerateFiles(bepInEx, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetExtension(file), ".log", StringComparison.OrdinalIgnoreCase))
                {
                    baseline.Files.Add(RelativeForBaseline(gameDirectory, file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 快照不完整时回滚会保守处理，不猜“哪些是新建的”。
            baseline.Complete = false;
        }

        baseline.Files = baseline.Files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        baseline.Directories = baseline.Directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(ch => ch == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return baseline;
    }

    private static string RelativeForBaseline(string gameDirectory, string fullPath)
        => Path.GetRelativePath(gameDirectory, fullPath).Replace('\\', '/');

    /// <summary>
    /// 回滚时清理 BepInEx 运行期生成的白名单产物。只删能证明是“本次安装新建”的文件/空目录：
    /// 有基线时看基线快照，旧账本（无基线）时看 CreatedDirectories，安装前已有的用户文件一律保留。
    /// </summary>
    private static void CleanBepInExRuntimeArtifactsReport(
        string gameDirectory,
        InstallManifest manifest,
        RollbackReport report,
        bool cleanupEmptyDirectories)
    {
        var managedFiles = new HashSet<string>(
            (manifest.Files ?? new List<InstallFileEntry>()).Select(file => PathSafety.ValidateRelative(file.RelativePath)),
            StringComparer.OrdinalIgnoreCase);
        var createdDirectories = new HashSet<string>(
            (manifest.CreatedDirectories ?? new List<string>()).Select(PathSafety.ValidateRelative),
            StringComparer.OrdinalIgnoreCase);
        var baseline = manifest.BepInExBaseline;
        var preexistingFiles = baseline is null ? null : NormalizeBaselinePaths(baseline.Files);
        var preexistingDirectories = baseline is null ? null : NormalizeBaselinePaths(baseline.Directories);
        var wholeTreeNew = preexistingDirectories is not null
            ? !preexistingDirectories.Contains("BepInEx")
            : createdDirectories.Contains("BepInEx");

        if (baseline is not null && !baseline.Complete && !wholeTreeNew)
        {
            report.Warnings.Add("BepInEx 基线快照不完整，无法确认哪些运行期产物是本次安装新建的；已跳过自动清理。");
            return;
        }

        var bepInEx = Path.Combine(gameDirectory, "BepInEx");
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeRoot in BepInExRuntimeDirectoryRoots)
        {
            var root = Path.Combine(gameDirectory, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", BepInExScanOptions))
                {
                    AddRuntimeCandidate(gameDirectory, file, candidates, report);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Warnings.Add($"扫描 BepInEx 运行期产物失败：{relativeRoot}（{ex.Message}）");
            }
        }

        if (Directory.Exists(bepInEx))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(bepInEx, "*", SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(Path.GetExtension(file), ".log", StringComparison.OrdinalIgnoreCase))
                    {
                        AddRuntimeCandidate(gameDirectory, file, candidates, report);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Warnings.Add($"扫描 BepInEx 根目录日志失败：{ex.Message}");
            }
        }

        foreach (var relative in candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (managedFiles.Contains(relative))
            {
                // 账本里有记录的文件走正常的 restore/delete 流程，这里不重复处理。
                continue;
            }

            if (!IsProvablyNewRuntimeArtifact(relative, wholeTreeNew, preexistingFiles, createdDirectories))
            {
                report.SkippedRuntimeArtifacts.Add(relative);
                report.Warnings.Add($"保留无法确认是本次安装新建的 BepInEx 产物：{relative}");
                continue;
            }

            try
            {
                var target = PathGuard.ResolveGameRelative(gameDirectory, relative);
                if (!File.Exists(target))
                {
                    continue;
                }

                File.Delete(target);
                report.CleanedRuntimeArtifacts.Add(relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                report.Warnings.Add($"清理 BepInEx 运行期产物失败：{relative}（{ex.Message}）");
            }
        }

        if (!cleanupEmptyDirectories)
        {
            return;
        }

        var directoriesToTry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeRoot in BepInExRuntimeDirectoryRoots)
        {
            var root = Path.Combine(gameDirectory, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(root))
            {
                continue;
            }

            directoriesToTry.Add(relativeRoot);
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "*", BepInExScanOptions))
                {
                    directoriesToTry.Add(RelativeForBaseline(gameDirectory, directory));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Warnings.Add($"扫描 BepInEx 运行期目录失败：{relativeRoot}（{ex.Message}）");
            }
        }

        foreach (var relative in directoriesToTry
                     .OrderByDescending(path => path.Count(ch => ch == '/'))
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsProvablyNewRuntimeDirectory(relative, wholeTreeNew, preexistingDirectories, createdDirectories))
            {
                continue;
            }

            try
            {
                var directory = PathGuard.ResolveGameRelative(gameDirectory, relative);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                    report.CleanedDirectories.Add(relative);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                report.Warnings.Add($"清理 BepInEx 空目录失败：{relative}（{ex.Message}）");
            }
        }

        // 安装前没有 BepInEx 时，白名单产物清完且目录已空，就把 BepInEx 也删掉。
        if (wholeTreeNew && Directory.Exists(bepInEx))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(bepInEx).Any())
                {
                    Directory.Delete(bepInEx, recursive: false);
                    report.CleanedDirectories.Add("BepInEx");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Warnings.Add($"清理空 BepInEx 目录失败：{ex.Message}");
            }
        }
    }

    private static void AddRuntimeCandidate(
        string gameDirectory,
        string fullPath,
        ISet<string> candidates,
        RollbackReport report)
    {
        var relative = RelativeForBaseline(gameDirectory, fullPath);
        try
        {
            var normalized = PathSafety.ValidateRelative(relative);
            if (IsBepInExRuntimeArtifact(normalized))
            {
                candidates.Add(normalized);
            }
        }
        catch (ForgePathException)
        {
            report.Warnings.Add($"跳过路径非法的 BepInEx 产物：{relative}");
        }
    }

    private static bool IsBepInExRuntimeArtifact(string relative)
    {
        foreach (var root in BepInExRuntimeDirectoryRoots)
        {
            if (relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!relative.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = relative["BepInEx/".Length..];
        return !name.Contains('/')
               && string.Equals(Path.GetExtension(name), ".log", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProvablyNewRuntimeArtifact(
        string relative,
        bool wholeTreeNew,
        HashSet<string>? preexistingFiles,
        HashSet<string> createdDirectories)
    {
        if (wholeTreeNew)
        {
            return true;
        }

        if (preexistingFiles is not null)
        {
            return !preexistingFiles.Contains(relative);
        }

        // 旧账本没有基线：只有父目录被记录为“本次新建”时，才能证明文件是新的。
        return IsUnderCreatedDirectory(relative, createdDirectories);
    }

    private static bool IsProvablyNewRuntimeDirectory(
        string relative,
        bool wholeTreeNew,
        HashSet<string>? preexistingDirectories,
        HashSet<string> createdDirectories)
    {
        if (wholeTreeNew)
        {
            return true;
        }

        if (preexistingDirectories is not null)
        {
            return !preexistingDirectories.Contains(relative);
        }

        if (createdDirectories.Contains(relative))
        {
            return true;
        }

        var parent = ParentDirectory(relative);
        while (!string.IsNullOrEmpty(parent) && !parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
        {
            if (createdDirectories.Contains(parent))
            {
                return true;
            }

            parent = ParentDirectory(parent);
        }

        return false;
    }

    private static bool IsUnderCreatedDirectory(string relative, HashSet<string> createdDirectories)
    {
        var parent = ParentDirectory(relative);
        while (!string.IsNullOrEmpty(parent))
        {
            if (createdDirectories.Contains(parent))
            {
                return true;
            }

            parent = ParentDirectory(parent);
        }

        return false;
    }

    private static string ParentDirectory(string relative)
    {
        var index = relative.LastIndexOf('/');
        return index <= 0 ? string.Empty : relative[..index];
    }

    private static HashSet<string> NormalizeBaselinePaths(IEnumerable<string> paths)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths ?? Enumerable.Empty<string>())
        {
            try
            {
                normalized.Add(PathSafety.ValidateRelative(path));
            }
            catch (ForgePathException)
            {
                // 基线来自本程序写的账本；非法项忽略，最多导致少清理，不会误删。
            }
        }

        return normalized;
    }

    private ForgeResult<bool> ResolveRollbackContext(GameContext context, InstallManifest manifest, ResolvedContext state)
    {
        if (!PathGuard.SamePath(manifest.GamePath, state.GameDirectory))
        {
            return Failure<bool>(ForgeErrorCodes.ArgumentInvalid,
                $"账本属于其他游戏目录：{manifest.GamePath}",
                "用生成该账本的同一游戏目录回滚。");
        }

        if (!string.IsNullOrWhiteSpace(context.Slug) && !PathGuard.SamePath(context.GameDirectory, state.GameDirectory))
        {
            return Failure<bool>(ForgeErrorCodes.ArgumentInvalid, "游戏上下文与账本不匹配。", "检查 --game 参数。");
        }

        return ForgeResult<bool>.Success(true);
    }

    private ForgeResult<BepInExPackage> LoadPackage()
    {
        var candidatesResult = BepInExLocator.FindCandidates(_workspace.RootPath, _options.BepInExPackagePath);
        if (!candidatesResult.Ok)
        {
            return ForgeResult<BepInExPackage>.Failure(candidatesResult.Error!);
        }

        var candidates = candidatesResult.Value!;
        if (candidates.Count == 0)
        {
            return Failure<BepInExPackage>(
                ForgeErrorCodes.DependencyMissing,
                "工作区里找不到 BepInEx 5.4.23.5 的缓存 zip。",
                $"下载 BepInEx 5.4.23.5（win x64）放到工作区，或用 --bepinex <zip> 指定：{BepInExPackage.OfficialDownloadUrl}",
                new Dictionary<string, string>
                {
                    ["searchRoot"] = _workspace.RootPath,
                    ["downloadUrl"] = BepInExPackage.OfficialDownloadUrl,
                });
        }

        ForgeError? firstError = null;
        foreach (var candidate in candidates)
        {
            var package = BepInExPackage.Open(candidate, _options.BepInExSha256, _options.VerifyPackageHash);
            if (package.Ok)
            {
                return package;
            }

            firstError ??= package.Error;
        }

        return ForgeResult<BepInExPackage>.Failure(firstError!);
    }

    private ForgeResult<List<PluginArtifact>> CollectPlugins(
        BuildResult? build,
        ResolvedContext state,
        bool dryRun,
        List<string> warnings)
    {
        var artifacts = new List<PluginArtifact>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = build?.Artifacts ?? new List<string>();

        if (listed.Count == 0)
        {
            if (dryRun && build is not null
                && build.Metadata.TryGetValue("plannedArtifacts", out var plannedText)
                && !string.IsNullOrWhiteSpace(plannedText))
            {
                foreach (var planned in plannedText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var full = ResolveArtifactPath(planned, build, state);
                    if (!full.Ok)
                    {
                        return ForgeResult<List<PluginArtifact>>.Failure(full.Error!);
                    }

                    var fileName = Path.GetFileName(full.Value!);
                    if (!names.Add(fileName))
                    {
                        return DuplicatePath<List<PluginArtifact>>(fileName);
                    }

                    artifacts.Add(new PluginArtifact
                    {
                        SourcePath = full.Value!,
                        FileName = fileName,
                        Sha256 = null,
                        Size = 0,
                        Version = "0.0.0.0",
                        Planned = true,
                    });
                }
            }

            if (artifacts.Count == 0)
            {
                if (!dryRun)
                {
                    return Failure<List<PluginArtifact>>(ForgeErrorCodes.ArtifactMissing, "没有可部署的插件 DLL。", "先完成构建，确认 BuildResult.Artifacts 含插件 DLL。");
                }

                warnings.Add("dry-run 没有构建产物，计划只包含 BepInEx；真正部署前必须先构建。");
                return ForgeResult<List<PluginArtifact>>.Success(artifacts);
            }

            return ForgeResult<List<PluginArtifact>>.Success(artifacts);
        }

        foreach (var listedPath in listed)
        {
            if (string.IsNullOrWhiteSpace(listedPath))
            {
                continue;
            }

            var full = ResolveArtifactPath(listedPath, build, state);
            if (!full.Ok)
            {
                return ForgeResult<List<PluginArtifact>>.Failure(full.Error!);
            }

            var source = full.Value!;
            if (!File.Exists(source))
            {
                return Failure<List<PluginArtifact>>(ForgeErrorCodes.ArtifactMissing, $"构建产物不存在：{source}", "重新运行构建。");
            }

            if (!string.Equals(Path.GetExtension(source), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"跳过非 DLL 产物：{Path.GetFileName(source)}");
                continue;
            }

            var fileName = Path.GetFileName(source);
            try
            {
                PathSafety.ValidateRelative(fileName);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<List<PluginArtifact>>.Failure(ex.Error);
            }

            if (!names.Add(fileName))
            {
                return DuplicatePath<List<PluginArtifact>>(fileName);
            }

            PathGuard.EnsureNoReparsePoints(_workspace.RootPath, source);
            artifacts.Add(new PluginArtifact
            {
                SourcePath = source,
                FileName = fileName,
                Sha256 = FileHashes.Sha256File(source),
                Size = new FileInfo(source).Length,
                Version = TryFileVersion(source),
                Planned = false,
            });
        }

        if (artifacts.Count == 0)
        {
            return Failure<List<PluginArtifact>>(ForgeErrorCodes.ArtifactMissing, "构建产物里没有 DLL。", "确认生成工程设置了 OutputType=Library。");
        }

        return ForgeResult<List<PluginArtifact>>.Success(artifacts);
    }

    private ForgeResult<string> ResolveArtifactPath(string path, BuildResult? build, ResolvedContext state)
    {
        string full;
        try
        {
            if (Path.IsPathFullyQualified(path))
            {
                full = Path.GetFullPath(path);
            }
            else
            {
                var baseDirectory = string.IsNullOrWhiteSpace(build?.OutputDirectory) ? _workspace.RootPath : build.OutputDirectory;
                full = Path.GetFullPath(Path.Combine(baseDirectory, path));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure<string>(ForgeErrorCodes.ArgumentInvalid, $"构建产物路径非法：{ex.Message}", "使用普通绝对路径。");
        }

        if (!PathSafety.IsUnder(_workspace.RootPath, full) || PathSafety.IsUnder(state.GameDirectory, full))
        {
            return Failure<string>(ForgeErrorCodes.PathUnsafe, $"构建产物不在工作区内：{full}", "只部署工作区内的构建产物。");
        }

        return ForgeResult<string>.Success(full);
    }

    private ForgeResult<DeploymentOperation> DescribeOperation(
        string gameDirectory,
        string relativePath,
        string source,
        string? sha256After,
        long sizeAfter,
        string? version,
        bool planned,
        List<string> warnings)
    {
        string relative;
        try
        {
            relative = PathSafety.ValidateRelative(relativePath);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<DeploymentOperation>.Failure(ex.Error);
        }

        var target = PathGuard.ResolveGameRelative(gameDirectory, relative);
        if (Directory.Exists(target))
        {
            return Failure<DeploymentOperation>(ForgeErrorCodes.ArtifactInvalid, $"目标路径是目录：{relative}", "移除同名目录后重试。");
        }

        var exists = File.Exists(target);
        if (exists && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
        {
            warnings.Add($"目标文件只读，写入可能失败：{relative}");
        }

        var operation = new DeploymentOperation
        {
            RelativePath = relative,
            Source = source,
            Version = version,
            Planned = planned,
        };

        if (exists)
        {
            operation.ExistedBefore = true;
            operation.Sha256Before = FileHashes.Sha256File(target);
            operation.SizeBefore = new FileInfo(target).Length;
            operation.BackupRelativePath = relative;
            operation.Action = string.Equals(source, "remove", StringComparison.OrdinalIgnoreCase)
                ? DeployFileAction.Remove
                : DeployFileAction.Replace;
        }
        else
        {
            operation.ExistedBefore = false;
            operation.Action = DeployFileAction.Add;
        }

        if (operation.Action == DeployFileAction.Remove)
        {
            operation.Sha256After = null;
            operation.SizeAfter = 0;
        }
        else
        {
            operation.Sha256After = sha256After;
            operation.SizeAfter = sizeAfter;
        }

        return ForgeResult<DeploymentOperation>.Success(operation);
    }

    private static List<string> FindCreatedDirectories(string gameDirectory, IEnumerable<DeploymentOperation> operations)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            var target = PathGuard.ResolveGameRelative(gameDirectory, operation.RelativePath);
            var current = Path.GetDirectoryName(target);
            while (!string.IsNullOrEmpty(current)
                   && !PathGuard.SamePath(current, gameDirectory)
                   && PathSafety.IsUnder(gameDirectory, current))
            {
                if (Directory.Exists(current))
                {
                    break;
                }

                var relative = Path.GetRelativePath(gameDirectory, current).Replace('\\', '/');
                try
                {
                    directories.Add(PathSafety.ValidateRelative(relative));
                }
                catch (ForgePathException)
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return directories
            .OrderBy(path => path.Count(ch => ch == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void TryWriteBackupManifest(string backupDirectory, InstallManifest manifest, ICollection<string> warnings)
    {
        try
        {
            var path = _workspace.EnsureBackupPath(Path.Combine(backupDirectory, InstallManifestStore.FileName));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, ForgeJson.Serialize(manifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
        {
            warnings.Add($"备份目录里的账本副本写入失败（主账本已保存）：{ex.Message}");
        }
    }

    private ForgeResult<ResolvedContext> ResolveContext(GameContext context, string? deploymentId)
    {
        if (string.IsNullOrWhiteSpace(context.GameDirectory))
        {
            return Failure<ResolvedContext>(ForgeErrorCodes.ArgumentInvalid, "游戏目录为空。", "先识别游戏目录再部署。");
        }

        string gameDirectory;
        try
        {
            gameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure<ResolvedContext>(ForgeErrorCodes.ArgumentInvalid, $"游戏目录路径非法：{ex.Message}", "使用普通绝对路径。");
        }

        if (!Directory.Exists(gameDirectory))
        {
            return Failure<ResolvedContext>(ForgeErrorCodes.ArtifactMissing, $"游戏目录不存在：{gameDirectory}", "检查 --game 参数。");
        }

        if (PathSafety.IsUnder(gameDirectory, _workspace.RootPath))
        {
            return Failure<ResolvedContext>(ForgeErrorCodes.GameWriteDenied,
                $"工作区不能位于游戏目录内：{_workspace.RootPath}",
                "把工作区放到游戏目录之外。");
        }

        if (_workspace.GameDirectory is not null && !PathGuard.SamePath(_workspace.GameDirectory, gameDirectory))
        {
            return Failure<ResolvedContext>(ForgeErrorCodes.ArgumentInvalid,
                $"工作区绑定的是另一个游戏：{_workspace.GameDirectory}，当前 {gameDirectory}",
                "为每个游戏使用独立工作区，或重新创建绑定当前游戏的工作区。");
        }

        var slug = context.Slug;
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = PathSafety.SlugFromGameDirectory(gameDirectory);
        }

        if (!PathSafety.IsValidSlug(slug))
        {
            return Failure<ResolvedContext>(ForgeErrorCodes.ArgumentInvalid, $"游戏 slug 非法：{slug}", "slug 只能包含小写字母、数字、点、下划线和连字符。");
        }

        var id = string.IsNullOrWhiteSpace(deploymentId) ? _options.DeploymentId : deploymentId;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"dep-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..29];
        }

        if (!PathSafety.IsValidSlug(id))
        {
            return Failure<ResolvedContext>(ForgeErrorCodes.ArgumentInvalid, $"部署 id 非法：{id}", "部署 id 只能包含小写字母、数字、点、下划线和连字符。");
        }

        string backupDirectory;
        string manifestPath;
        try
        {
            backupDirectory = _workspace.BackupDirectory(id);
            manifestPath = InstallManifestStore.ResolvePath(_workspace, _options.ManifestPath, slug);
            if (PathSafety.IsUnder(gameDirectory, manifestPath) || PathSafety.IsUnder(gameDirectory, backupDirectory))
            {
                return Failure<ResolvedContext>(ForgeErrorCodes.PathUnsafe, "工作区把部署数据放进了游戏目录。", "换一个游戏目录外的工作区。");
            }

            PathGuard.EnsureNoReparsePoints(_workspace.RootPath, manifestPath);
            PathGuard.EnsureNoReparsePoints(_workspace.RootPath, backupDirectory);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<ResolvedContext>.Failure(ex.Error);
        }

        return ForgeResult<ResolvedContext>.Success(new ResolvedContext
        {
            GameDirectory = gameDirectory,
            Slug = slug,
            DeploymentId = id,
            BackupDirectory = backupDirectory,
            ManifestPath = manifestPath,
        });
    }

    private static string TryFileVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? "0.0.0.0" : version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "0.0.0.0";
        }
    }

    private static void WriteAtomic(string target, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(target)
                        ?? throw new InvalidOperationException($"无法确定目标目录：{target}");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".forge-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不改变主错误。
        }
    }

    private static ForgeResult<T> DuplicatePath<T>(string relativePath)
        => Failure<T>(ForgeErrorCodes.ArtifactInvalid, $"部署计划包含重复路径：{relativePath}", "检查构建产物与 BepInEx 包内容。");

    private static ForgeResult<T> Failure<T>(
        string code,
        string message,
        string? suggestion = null,
        IReadOnlyDictionary<string, string>? details = null)
        => ForgeResult<T>.Failure(ForgeError.Create(code, message, suggestion, details));

    private sealed class ResolvedContext
    {
        public string GameDirectory { get; init; } = string.Empty;

        public string Slug { get; init; } = string.Empty;

        public string DeploymentId { get; init; } = string.Empty;

        public string BackupDirectory { get; init; } = string.Empty;

        public string ManifestPath { get; init; } = string.Empty;
    }

    private sealed class PluginArtifact
    {
        public string SourcePath { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string? Sha256 { get; init; }

        public long Size { get; init; }

        public string? Version { get; init; }

        public bool Planned { get; init; }
    }
}
