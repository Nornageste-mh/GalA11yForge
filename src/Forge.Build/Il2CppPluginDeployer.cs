using System.Globalization;
using System.Text.Json;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Deploy;
using GalA11yForge.Core.Logging;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>
/// IL2CPP（BepInEx 6 + Il2CppInterop）部署器。
///
/// <para>
/// 与 Mono 的 <see cref="PluginDeployer"/> 共用同一套账本/备份/回滚模型
/// （<see cref="InstallManifest"/>、<see cref="InstallManifestStore"/>、<see cref="RollbackReport"/>），
/// 但有三点不同：
/// 1) 安装整包 BepInEx 6（含 dotnet/ 运行时与 Il2CppInterop），而 Mono 只补 BepInEx 5 核心；
/// 2) 运行期产物 <c>BepInEx/interop</c>（首次启动生成、实测 97 个 DLL/74MB）不属于安装内容，
///    回滚时按安装前快照清理，避免把用户原有 BepInEx 文件删掉；
/// 3) 语音运行期 DLL（Tolk 等）不随产品分发，由 <see cref="DeploymentOptions.SpeechRuntimeDirectory"/>
///    指向用户自备目录；nvdaControllerClient64.dll 默认再放一份到游戏 exe 根（R21 实测唯一生效位置）。
/// </para>
/// </summary>
public sealed class Il2CppPluginDeployer : IDeployer
{
    private const string PluginDirectory = "BepInEx/plugins";

    /// <summary>Tolk 通过默认 DLL 搜索路径（exe 所在目录）找它；只放 plugins 时 NVDA 不生效。</summary>
    public const string NvdaClientFileName = "nvdaControllerClient64.dll";

    private static readonly string[] RuntimeDirectoryRoots =
    [
        "BepInEx/interop",
        "BepInEx/cache",
        "BepInEx/config",
        "BepInEx/patchers",
    ];

    /// <summary>基线里要排除的运行期目录（带尾斜杠，避免前缀误伤）。</summary>
    private static readonly string[] RuntimeChurnDirectoryRoots =
    [
        "BepInEx/interop/",
        "BepInEx/cache/",
        "BepInEx/config/",
        "BepInEx/patchers/",
    ];

    private readonly ForgeWorkspace _workspace;
    private readonly DeploymentOptions _options;
    private readonly IFlowLogger? _logger;

    public Il2CppPluginDeployer(ForgeWorkspace workspace, DeploymentOptions? options = null, IFlowLogger? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = (options ?? DefaultOptions()).Validate();
        if (!string.Equals(_options.BepInExVersion, BepInEx6Package.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"IL2CPP 部署器只支持 BepInEx {BepInEx6Package.Version}，当前选项是 {_options.BepInExVersion}。",
                nameof(options));
        }

        if (_options.VerifyPackageHash && _options.BepInExSha256 is null)
        {
            throw new ArgumentException(
                $"IL2CPP 部署器必须校验 BepInEx 6 包哈希（{BepInEx6Package.PinnedSha256}），不能把 BepInExSha256 设为 null。",
                nameof(options));
        }

        _logger = logger;
    }

    /// <summary>IL2CPP 部署默认选项：BepInEx 6.0.0-be.788 + 固定 SHA-256。</summary>
    public static DeploymentOptions DefaultOptions()
        => new()
        {
            BepInExVersion = BepInEx6Package.Version,
            BepInExSha256 = BepInEx6Package.PinnedSha256,
            VerifyPackageHash = true,
        };

    public Task<ForgeResult<DeployResult>> DeployAsync(
        GameContext context,
        BuildResult build,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(DeployCore(context, build, cancellationToken));
        }
        catch (ForgePathException ex)
        {
            return Task.FromResult(ForgeResult<DeployResult>.Failure(ex.Error));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Task.FromResult(ForgeResult<DeployResult>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"IL2CPP 部署失败：{ex.Message}",
                "检查磁盘空间、杀软拦截与游戏目录权限后重试；必要时先 forge verify --rollback。")));
        }
    }

    /// <summary>按接口回滚；没有 rollback 选项时用默认值（不覆盖被玩家改动过的文件）。</summary>
    public async Task<ForgeResult<DeployRollbackResult>> RollbackAsync(
        GameContext context,
        DeployManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var slug = ResolveSlug(context);
        var installManifest = InstallManifest.FromDeployManifest(
            manifest,
            new ForgeWorkspaceInfo { RootPath = _workspace.RootPath, Slug = slug });
        var reportResult = await RollbackCoreAsync(context, installManifest, null, new RollbackOptions(), cancellationToken)
            .ConfigureAwait(false);
        if (!reportResult.Ok || reportResult.Value is null)
        {
            return ForgeResult<DeployRollbackResult>.Failure(reportResult.Error!);
        }

        var report = reportResult.Value;
        return ForgeResult<DeployRollbackResult>.Success(new DeployRollbackResult
        {
            RestoredFiles = report.RestoredFiles,
            DeletedFiles = report.DeletedFiles,
            AlreadyClean = report.AlreadyClean,
        });
    }

    /// <summary>按 install-manifest.json 回滚（CLI <c>forge verify --rollback</c> 使用）。</summary>
    public async Task<ForgeResult<RollbackReport>> RollbackFromFileAsync(
        GameContext context,
        string manifestPath,
        RollbackOptions options,
        CancellationToken cancellationToken = default)
    {
        var loaded = InstallManifestStore.Load(_workspace, manifestPath);
        if (!loaded.Ok || loaded.Value is null)
        {
            return ForgeResult<RollbackReport>.Failure(loaded.Error!);
        }

        return await RollbackCoreAsync(context, loaded.Value, manifestPath, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private ForgeResult<DeployResult> DeployCore(
        GameContext context,
        BuildResult build,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return Fail(ForgeErrorCodes.ArgumentInvalid, "部署上下文为空。", "重新运行 forge install。");
        }

        string gameDirectory;
        try
        {
            gameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(ForgeErrorCodes.ArgumentInvalid, $"游戏目录不合法：{ex.Message}", "用 --game 指向有效目录。");
        }

        if (!Directory.Exists(gameDirectory))
        {
            return Fail(ForgeErrorCodes.ArgumentInvalid, $"游戏目录不存在：{gameDirectory}", "确认游戏已安装。");
        }

        var slug = ResolveSlug(context);
        if (!PathSafety.IsValidSlug(slug))
        {
            return Fail(ForgeErrorCodes.ArgumentInvalid, $"游戏 slug 不合法：{slug}", "用 --slug 指定小写 slug。");
        }

        if (PathSafety.IsUnder(gameDirectory, _workspace.RootPath))
        {
            return Fail(
                ForgeErrorCodes.GameWriteDenied,
                $"工作区不能位于游戏目录内：工作区 {_workspace.RootPath}，游戏 {gameDirectory}。",
                "把 --workspace 指向游戏目录之外的独立目录。");
        }

        var deploymentId = string.IsNullOrWhiteSpace(_options.DeploymentId)
            ? $"dep-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{RandomSuffix()}"
            : _options.DeploymentId;
        if (!PathSafety.IsValidSlug(deploymentId))
        {
            return Fail(
                ForgeErrorCodes.ArgumentInvalid,
                $"部署批次 id 不合法：{deploymentId}",
                "只用小写字母、数字、点、下划线、连字符。");
        }

        var manifestPath = InstallManifestStore.ResolvePath(_workspace, _options.ManifestPath, slug);
        var backupRoot = _workspace.BackupDirectory(deploymentId);

        if (File.Exists(manifestPath))
        {
            if (!_options.AllowRedeploy)
            {
                return Fail(
                    ForgeErrorCodes.ArgumentInvalid,
                    $"该游戏已有安装账本：{manifestPath}；为避免叠加两层备份，默认拒绝重复安装。",
                    "先运行 forge verify --rollback 回滚；确认要覆盖时加 --force（旧账本会归档为 install-manifest.previous-*.json）。");
            }

            var archive = Path.Combine(
                Path.GetDirectoryName(manifestPath)!,
                $"install-manifest.previous-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.Copy(manifestPath, _workspace.EnsureWriteAllowed(archive), overwrite: true);
        }

        if (Directory.Exists(backupRoot) && !_options.AllowRedeploy)
        {
            return Fail(
                ForgeErrorCodes.ArgumentInvalid,
                $"备份目录已存在：{backupRoot}",
                "换一个 --deployment-id，或先回滚旧安装。");
        }

        var package = LoadPackage();
        if (!package.Ok || package.Value is null)
        {
            return ForgeResult<DeployResult>.Failure(package.Error!);
        }

        var planned = new List<PlannedFile>();
        var warnings = new List<string>();

        // 1) BepInEx 6 整包（winhttp.dll、doorstop_config.ini、BepInEx/**、dotnet/**）。
        foreach (var entry in package.Value.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = package.Value.ReadBytes(entry.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Fail(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"读取 BepInEx 6 包条目失败（{entry.RelativePath}）：{ex.Message}",
                    "重新下载官方 BepInEx 6 压缩包并核对 SHA-256。");
            }

            if (bytes.LongLength != entry.Length)
            {
                return Fail(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"BepInEx 6 包条目长度不一致：{entry.RelativePath}（清单 {entry.Length}，实际 {bytes.LongLength}）。",
                    "压缩包损坏或被重新打包，重新下载官方包。");
            }

            planned.Add(new PlannedFile
            {
                RelativePath = entry.RelativePath,
                Bytes = bytes,
                Source = "bepinex",
                Version = BepInEx6Package.Version,
            });
        }

        // 2) 插件 DLL（BuildResult.Artifacts 里顶层的 *.dll）。dry-run 时 metadata 里是 plannedArtifacts。
        var pluginCollect = CollectPluginArtifacts(build, warnings);
        if (!pluginCollect.Ok || pluginCollect.Value is null)
        {
            return ForgeResult<DeployResult>.Failure(pluginCollect.Error!);
        }

        foreach (var artifact in pluginCollect.Value)
        {
            var fileName = Path.GetFileName(artifact.Path);
            planned.Add(new PlannedFile
            {
                RelativePath = PathSafety.ValidateRelative($"{PluginDirectory}/{fileName}"),
                Bytes = artifact.Bytes,
                Source = "plugin",
                Version = _options.PluginVersion,
            });
        }

        if (pluginCollect.Value.Count == 0)
        {
            warnings.Add("本次构建没有产出任何 DLL：安装会只有 BepInEx 6，没有任何无障碍补丁。");
        }

        // 3) 语音运行期 DLL（用户自备，不分发）：Tolk.dll 及其依赖 → BepInEx/plugins。
        var speechCollect = CollectSpeechRuntime(warnings);
        if (!speechCollect.Ok || speechCollect.Value is null)
        {
            return ForgeResult<DeployResult>.Failure(speechCollect.Error!);
        }

        foreach (var file in speechCollect.Value)
        {
            planned.Add(new PlannedFile
            {
                RelativePath = PathSafety.ValidateRelative($"{PluginDirectory}/{file.FileName}"),
                Bytes = file.Bytes,
                Source = "speech",
                Version = null,
            });
        }

        var controller = speechCollect.Value.FirstOrDefault(file =>
            string.Equals(file.FileName, NvdaClientFileName, StringComparison.OrdinalIgnoreCase));
        if (_options.PlaceNvdaClientInGameRoot && controller is not null)
        {
            // 双放：plugins 里一份（随 Tolk 依赖一起，便于排障）+ exe 根一份（R21 实测 NVDA 唯一生效位置）。
            planned.Add(new PlannedFile
            {
                RelativePath = NvdaClientFileName,
                Bytes = controller.Bytes,
                Source = "speech",
                Version = null,
            });
        }
        else if (_options.PlaceNvdaClientInGameRoot)
        {
            warnings.Add(
                $"{NvdaClientFileName} 不在语音运行期目录里，未放到游戏 exe 根；NVDA 很可能退回 SAPI。"
                + "把 NVDA 客户端 DLL 放进 SpeechRuntimeDirectory 后重新安装。");
        }

        // 4) 额外移除（旧补丁等）；先备份再删。
        foreach (var removal in _options.AdditionalRemovals)
        {
            string relative;
            try
            {
                relative = PathSafety.ValidateRelative(removal);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<DeployResult>.Failure(ex.Error);
            }

            planned.Add(new PlannedFile
            {
                RelativePath = relative,
                Bytes = Array.Empty<byte>(),
                Source = "remove",
                ActionOverride = DeployFileAction.Remove,
            });
        }

        // 同一路径只能有一个动作。
        var duplicates = planned
            .GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            return Fail(
                ForgeErrorCodes.ArtifactInvalid,
                $"部署内容里有重复文件：{string.Join("、", duplicates)}。",
                "插件 DLL 与 BepInEx 包内文件不能同名；重命名插件或改用 --option。");
        }

        // 计算动作与备份需求。
        foreach (var file in planned)
        {
            var target = PathGuard.ResolveGameRelative(gameDirectory, file.RelativePath);
            file.ExistedBefore = File.Exists(target);
            if (file.ExistedBefore)
            {
                file.Sha256Before = FileHashes.Sha256File(target);
                file.SizeBefore = new FileInfo(target).Length;
            }

            if (file.ActionOverride == DeployFileAction.Remove)
            {
                file.Action = DeployFileAction.Remove;
                file.Sha256After = null;
                file.SizeAfter = 0;
                if (!file.ExistedBefore)
                {
                    warnings.Add($"要删除的文件本来就不存在：{file.RelativePath}");
                }
            }
            else
            {
                file.Action = file.ExistedBefore ? DeployFileAction.Replace : DeployFileAction.Add;
                file.Sha256After = FileHashes.Sha256Bytes(file.Bytes);
                file.SizeAfter = file.Bytes.LongLength;
            }
        }

        var plan = BuildPlan(context, slug, deploymentId, gameDirectory, backupRoot, manifestPath,
            package.Value, planned, warnings);
        var createdDirectories = FindCreatedDirectories(gameDirectory, planned);
        plan.CreatedDirectories = createdDirectories;

        // 基线必须在第一次写游戏目录之前采集。
        var baseline = DeploymentBaseline.Capture(gameDirectory, plan);
        if (!baseline.Ok || baseline.Value is null)
        {
            return ForgeResult<DeployResult>.Failure(baseline.Error!);
        }

        var trimmedRuntimeFiles = TrimRuntimeChurnFromBaseline(baseline.Value);
        if (trimmedRuntimeFiles > 0)
        {
            plan.Warnings.Add(
                $"基线已排除 {trimmedRuntimeFiles} 个 BepInEx 运行期文件（interop/缓存/配置/patchers/日志）："
                + "这些文件在游戏启动后必然变化，留着会让 Verify 的 m-07 把正常运行误判成篡改。");
        }

        var baselineSaved = DeploymentBaseline.Save(_workspace, slug, baseline.Value);
        if (!baselineSaved.Ok)
        {
            return ForgeResult<DeployResult>.Failure(baselineSaved.Error!);
        }

        var bepInExBaseline = CaptureBepInExBaseline(gameDirectory);

        // 备份 + 写入。任何一步失败都自动回滚已写文件，不留半装状态。
        var written = new List<PlannedFile>();
        try
        {
            Directory.CreateDirectory(backupRoot);
            foreach (var file in planned.Where(file => file.ExistedBefore))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = PathGuard.ResolveGameRelative(gameDirectory, file.RelativePath);
                var backup = _workspace.EnsureBackupPath(
                    Path.Combine(backupRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: true);
                file.BackupRelativePath = file.RelativePath;
                if (!FileHashes.MatchesFile(backup, file.Sha256Before))
                {
                    throw new IOException($"备份校验失败：{file.RelativePath}");
                }
            }

            foreach (var file in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = PathGuard.ResolveGameRelative(gameDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (file.Action == DeployFileAction.Remove)
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                else
                {
                    WriteAtomic(target, file.Bytes);
                }

                written.Add(file);
            }

            foreach (var file in planned.Where(file => file.Action != DeployFileAction.Remove))
            {
                var target = PathGuard.ResolveGameRelative(gameDirectory, file.RelativePath);
                if (!FileHashes.MatchesFile(target, file.Sha256After))
                {
                    throw new IOException($"安装后校验失败：{file.RelativePath}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var rolledBack = UndoWrites(gameDirectory, backupRoot, written);
            return Fail(
                ForgeErrorCodes.Io,
                $"写入游戏目录失败：{ex.Message}；已自动回滚 {rolledBack} 个文件。",
                "检查游戏目录权限/磁盘空间/杀软后重试；详细备份在 " + backupRoot);
        }

        var interop = Il2CppInteropProbe.Probe(gameDirectory);
        var speechStatus = speechCollect.Value.Count == 0 ? "missing" : "installed";

        var manifest = new InstallManifest
        {
            SchemaVersion = 1,
            DeploymentId = deploymentId,
            GameKey = slug,
            GamePath = gameDirectory,
            WorkspacePath = _workspace.RootPath,
            BackupRoot = backupRoot,
            CreatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            BepInExVersion = BepInEx6Package.Version,
            BepInExPackage = package.Value.Path,
            BepInExPackageSha256 = package.Value.Sha256,
            PluginName = _options.PluginName ?? (pluginCollect.Value.Count > 0
                ? Path.GetFileNameWithoutExtension(pluginCollect.Value[0].Path)
                : null),
            PluginVersion = _options.PluginVersion,
            DryRun = false,
            ManifestPath = manifestPath,
            RollbackHint = $"forge verify --rollback --game \"{gameDirectory}\" --workspace \"{_workspace.RootPath}\""
                           + (string.Equals(slug, PathSafety.SlugFromGameDirectory(gameDirectory), StringComparison.OrdinalIgnoreCase)
                               ? string.Empty
                               : $" --slug {slug}"),
            CreatedDirectories = createdDirectories,
            BepInExBaseline = bepInExBaseline,
            Files = written.Select(ToEntry).ToList(),
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["engine"] = JsonSerializer.SerializeToElement("unity_il2cpp"),
                ["interopAssemblies"] = JsonSerializer.SerializeToElement(interop.InteropAssemblyCount),
                ["speechRuntime"] = JsonSerializer.SerializeToElement(speechStatus),
                ["nvdaClientInGameRoot"] = JsonSerializer.SerializeToElement(
                    _options.PlaceNvdaClientInGameRoot && controller is not null),
            },
        };

        var saved = InstallManifestStore.Save(_workspace, manifest, manifestPath);
        if (!saved.Ok)
        {
            var rolledBack = UndoWrites(gameDirectory, backupRoot, written);
            return Fail(
                ForgeErrorCodes.Io,
                $"安装账本写入失败：{saved.Error?.Message}；已自动回滚 {rolledBack} 个文件。",
                "检查工作区磁盘空间与权限。");
        }

        if (speechCollect.Value.Count == 0)
        {
            warnings.Add(
                "未提供语音运行期目录（SpeechRuntimeDirectory）：补丁会加载，但很可能无法朗读。"
                + "把 Tolk.dll 与 nvdaControllerClient64.dll 放进用户在 --speech-runtime 指定的目录后重装。");
        }

        _logger?.Log(FlowLogLevel.Info, "deploy", "IL2CPP 安装完成", new Dictionary<string, string>
        {
            ["deploymentId"] = deploymentId,
            ["files"] = written.Count.ToString(CultureInfo.InvariantCulture),
            ["backupDirectory"] = backupRoot,
            ["manifestPath"] = manifestPath,
        });

        return ForgeResult<DeployResult>.Success(new DeployResult
        {
            Manifest = manifest.ToDeployManifest(),
            BackupDirectory = backupRoot,
        });
    }

    private ForgeResult<BepInEx6Package> LoadPackage()
    {
        var found = BepInEx6Locator.FindCandidates(_workspace.RootPath, _options.BepInExPackagePath);
        if (!found.Ok || found.Value is null || found.Value.Count == 0)
        {
            return ForgeResult<BepInEx6Package>.Failure(found.Error ?? ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                $"找不到 BepInEx {BepInEx6Package.Version} 离线包。",
                $"下载 {BepInEx6Package.OfficialDownloadUrl} 放进工作区，或用 --bepinex <zip> 指定路径。",
                new Dictionary<string, string> { ["downloadUrl"] = BepInEx6Package.OfficialDownloadUrl }));
        }

        ForgeResult<BepInEx6Package>? last = null;
        foreach (var candidate in found.Value)
        {
            var opened = BepInEx6Package.Open(candidate, _options.BepInExSha256, _options.VerifyPackageHash);
            if (opened.Ok)
            {
                return opened;
            }

            last = opened;
            if (!string.IsNullOrWhiteSpace(_options.BepInExPackagePath))
            {
                break;
            }
        }

        return last ?? ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
            ForgeErrorCodes.DependencyMissing,
            "BepInEx 6 离线包无法读取。",
            $"重新下载 {BepInEx6Package.OfficialDownloadUrl}。"));
    }

    private ForgeResult<List<PluginArtifact>> CollectPluginArtifacts(BuildResult build, List<string> warnings)
    {
        var artifacts = new List<PluginArtifact>();
        if (build?.Artifacts is null)
        {
            return ForgeResult<List<PluginArtifact>>.Success(artifacts);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in build.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact))
            {
                continue;
            }

            var full = Path.GetFullPath(artifact);
            if (!full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"跳过非 DLL 构建产物：{Path.GetFileName(full)}");
                continue;
            }

            try
            {
                _workspace.EnsureWriteAllowed(full);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<List<PluginArtifact>>.Failure(ex.Error);
            }

            if (!File.Exists(full))
            {
                return ForgeResult<List<PluginArtifact>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArtifactMissing,
                    $"构建产物不存在：{full}",
                    "重新运行 forge build；不要手工删除 work/ 下的输出。"));
            }

            var fileName = Path.GetFileName(full);
            try
            {
                PathSafety.ValidateRelative(fileName);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<List<PluginArtifact>>.Failure(ex.Error);
            }

            if (!seen.Add(fileName))
            {
                return ForgeResult<List<PluginArtifact>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"构建产物文件名重复：{fileName}",
                    "一个游戏只能装一个同名插件 DLL。"));
            }

            artifacts.Add(new PluginArtifact(full, File.ReadAllBytes(full)));
        }

        return ForgeResult<List<PluginArtifact>>.Success(artifacts);
    }

    private ForgeResult<List<SpeechFile>> CollectSpeechRuntime(List<string> warnings)
    {
        var files = new List<SpeechFile>();
        if (string.IsNullOrWhiteSpace(_options.SpeechRuntimeDirectory))
        {
            return ForgeResult<List<SpeechFile>>.Success(files);
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(_options.SpeechRuntimeDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<List<SpeechFile>>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"语音运行期目录不合法：{ex.Message}",
                "用 --speech-runtime 指向包含 Tolk.dll 的目录。"));
        }

        if (!Directory.Exists(directory))
        {
            warnings.Add($"语音运行期目录不存在：{directory}；本次不安装 Tolk，朗读很可能不可用。");
            return ForgeResult<List<SpeechFile>>.Success(files);
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(path);
                try
                {
                    PathSafety.ValidateRelative(fileName);
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<List<SpeechFile>>.Failure(ex.Error);
                }

                files.Add(new SpeechFile(fileName, File.ReadAllBytes(path)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<List<SpeechFile>>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"读取语音运行期目录失败：{ex.Message}",
                "检查目录权限后重试。"));
        }

        if (!files.Any(file => string.Equals(file.FileName, "Tolk.dll", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"{directory} 里没有 Tolk.dll：补丁会加载，但 Tolk 初始化会失败并退回仅日志。");
        }

        return ForgeResult<List<SpeechFile>>.Success(files);
    }

    private DeploymentPlan BuildPlan(
        GameContext context,
        string slug,
        string deploymentId,
        string gameDirectory,
        string backupRoot,
        string manifestPath,
        BepInEx6Package package,
        List<PlannedFile> planned,
        List<string> warnings)
    {
        return new DeploymentPlan
        {
            SchemaVersion = 1,
            DeploymentId = deploymentId,
            Slug = slug,
            GameKey = slug,
            GameDirectory = gameDirectory,
            WorkspaceRoot = _workspace.RootPath,
            BackupDirectory = backupRoot,
            ManifestPath = manifestPath,
            BepInExVersion = BepInEx6Package.Version,
            BepInExPackagePath = package.Path,
            BepInExPackageSha256 = package.Sha256,
            PluginName = _options.PluginName,
            PluginVersion = _options.PluginVersion,
            DryRun = false,
            AllowRedeploy = _options.AllowRedeploy,
            CreatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            Warnings = warnings.ToList(),
            Operations = planned.Select(file => new DeploymentOperation
            {
                RelativePath = file.RelativePath,
                Action = file.Action,
                ExistedBefore = file.ExistedBefore,
                Sha256Before = file.Sha256Before,
                Sha256After = file.Sha256After,
                SizeBefore = file.SizeBefore,
                SizeAfter = file.SizeAfter,
                Source = file.Source,
                Version = file.Version,
                BackupRelativePath = file.BackupRelativePath,
            }).ToList(),
        };
    }

    private static List<string> FindCreatedDirectories(string gameDirectory, List<PlannedFile> planned)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in planned)
        {
            var parent = Path.GetDirectoryName(file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                var relative = parent.Replace('\\', '/');
                if (!Directory.Exists(Path.Combine(gameDirectory, parent)))
                {
                    directories.Add(relative);
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        return directories
            .OrderBy(directory => directory.Count(ch => ch == '/'))
            .ThenBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 从安装前基线中剔除 BepInEx 运行期会自行改写的文件（interop、cache、config、patchers、日志）。
    /// 这些文件在游戏启动后必然变化（interop 生成、chainloader 缓存、日志增长），
    /// 留在基线里会让 Verify 的 m-07「账本外原文件未变」把正常运行时行为误判成篡改。
    /// 用户文件和本次安装目标仍然保留。
    /// </summary>
    private static int TrimRuntimeChurnFromBaseline(Verify.Models.GameBaseline baseline)
    {
        if (baseline.Files is null || baseline.Files.Count == 0)
        {
            return 0;
        }

        return baseline.Files.RemoveAll(file => file is not null && IsRuntimeChurnPath(file.RelativePath));
    }

    private static bool IsRuntimeChurnPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var path = relativePath.Replace('\\', '/').Trim();
        foreach (var root in RuntimeChurnDirectoryRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // BepInEx 根目录下的日志（LogOutput.log、ErrorLog.log 等），不含子目录。
        return path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && path.IndexOf('/', "BepInEx/".Length) < 0;
    }

    private static BepInExInstallBaseline CaptureBepInExBaseline(string gameDirectory)
    {
        var baseline = new BepInExInstallBaseline { Complete = true };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bepInEx = Path.Combine(gameDirectory, "BepInEx");
        if (Directory.Exists(bepInEx))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(bepInEx, "*", SearchOption.AllDirectories))
                {
                    directories.Add(Path.GetRelativePath(gameDirectory, directory).Replace('\\', '/'));
                }

                foreach (var file in Directory.EnumerateFiles(bepInEx, "*", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetRelativePath(gameDirectory, file).Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                baseline.Complete = false;
            }
        }

        try
        {
            foreach (var log in Directory.EnumerateFiles(gameDirectory, "*.log", SearchOption.TopDirectoryOnly))
            {
                files.Add(Path.GetRelativePath(gameDirectory, log).Replace('\\', '/'));
            }

            foreach (var log in Directory.EnumerateFiles(gameDirectory, "preloader_*.log", SearchOption.TopDirectoryOnly))
            {
                files.Add(Path.GetRelativePath(gameDirectory, log).Replace('\\', '/'));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            baseline.Complete = false;
        }

        baseline.Files = files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        baseline.Directories = directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        return baseline;
    }

    private async Task<ForgeResult<RollbackReport>> RollbackCoreAsync(
        GameContext context,
        InstallManifest manifest,
        string? manifestPath,
        RollbackOptions options,
        CancellationToken cancellationToken)
    {
        var report = new RollbackReport
        {
            DeploymentId = manifest.DeploymentId,
            GameDirectory = context.GameDirectory,
            WorkspaceRoot = _workspace.RootPath,
            ManifestPath = manifestPath,
            Ok = true,
        };

        string gameDirectory;
        try
        {
            gameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            report.Ok = false;
            report.Errors.Add($"游戏目录不合法：{ex.Message}");
            return ForgeResult<RollbackReport>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                report.Errors[^1],
                "用 --game 指向有效目录。"));
        }

        if (!PathGuard.SamePath(gameDirectory, manifest.GamePath))
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"账本记录的游戏目录是 {manifest.GamePath}，当前是 {gameDirectory}；拒绝在别的目录回滚。",
                "用安装时同一个 --game 目录回滚。");
            report.Ok = false;
            report.Errors.Add(error.Message);
            return ForgeResult<RollbackReport>.Failure(error);
        }

        var backupRoot = _workspace.EnsureBackupPath(manifest.BackupRoot);
        var order = 0;
        foreach (var file in manifest.Files.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            order++;
            var action = new RollbackAction
            {
                Order = order,
                RelativePath = file.RelativePath,
                Sha256Expected = file.Sha256After,
                Action = file.Action == DeployFileAction.Add ? DeployRollbackAction.Delete : DeployRollbackAction.Restore,
            };

            string target;
            try
            {
                target = PathGuard.ResolveGameRelative(gameDirectory, file.RelativePath);
            }
            catch (ForgePathException ex)
            {
                action.Outcome = "failed";
                action.Error = ex.Error.Message;
                report.Errors.Add($"{file.RelativePath}：{ex.Error.Message}");
                report.Ok = false;
                report.Actions.Add(action);
                continue;
            }

            if (file.Action == DeployFileAction.Add)
            {
                if (!File.Exists(target))
                {
                    action.Outcome = "skipped";
                    report.SkippedFiles.Add(file.RelativePath);
                }
                else
                {
                    var actual = FileHashes.Sha256File(target);
                    action.Sha256Actual = actual;
                    if (!options.Force && !string.IsNullOrWhiteSpace(file.Sha256After)
                        && !FileHashes.EqualsHash(actual, file.Sha256After))
                    {
                        action.Outcome = "skipped";
                        action.Error = "当前文件哈希与安装时不一致（可能被玩家或其他补丁改动）";
                        report.HashMismatches.Add(file.RelativePath);
                        report.SkippedFiles.Add(file.RelativePath);
                        report.Errors.Add($"{file.RelativePath}：{action.Error}；确认要强删请加 --force。");
                        report.Ok = false;
                    }
                    else
                    {
                        try
                        {
                            File.Delete(target);
                            action.Outcome = "deleted";
                            report.DeletedFiles.Add(file.RelativePath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            action.Outcome = "failed";
                            action.Error = ex.Message;
                            report.Errors.Add($"{file.RelativePath}：{ex.Message}");
                            report.Ok = false;
                        }
                    }
                }

                report.Actions.Add(action);
                continue;
            }

            // Replace / Remove：从备份还原。
            if (string.IsNullOrWhiteSpace(file.BackupPath))
            {
                action.Outcome = "failed";
                action.Error = "账本缺少备份路径";
                report.Errors.Add($"{file.RelativePath}：{action.Error}");
                report.Ok = false;
                report.Actions.Add(action);
                continue;
            }

            string backup;
            try
            {
                backup = _workspace.EnsureBackupPath(
                    Path.Combine(backupRoot, file.BackupPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (ForgePathException ex)
            {
                action.Outcome = "failed";
                action.Error = ex.Error.Message;
                report.Errors.Add($"{file.RelativePath}：{ex.Error.Message}");
                report.Ok = false;
                report.Actions.Add(action);
                continue;
            }

            action.BackupPath = backup;
            if (!File.Exists(backup))
            {
                action.Outcome = "failed";
                action.Error = "备份文件不存在";
                report.Errors.Add($"{file.RelativePath}：备份文件不存在（{backup}）");
                report.Ok = false;
                report.Actions.Add(action);
                continue;
            }

            var backupHash = FileHashes.Sha256File(backup);
            if (!string.IsNullOrWhiteSpace(file.Sha256Before) && !FileHashes.EqualsHash(backupHash, file.Sha256Before))
            {
                action.Outcome = "failed";
                action.Error = "备份哈希与账本不一致";
                report.Errors.Add($"{file.RelativePath}：备份哈希与账本不一致，拒绝用损坏的备份覆盖。");
                report.Ok = false;
                report.Actions.Add(action);
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                WriteAtomic(target, backup);
                if (!FileHashes.MatchesFile(target, file.Sha256Before))
                {
                    throw new IOException("还原后哈希校验失败");
                }

                action.Outcome = "restored";
                report.RestoredFiles.Add(file.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                action.Outcome = "failed";
                action.Error = ex.Message;
                report.Errors.Add($"{file.RelativePath}：{ex.Message}");
                report.Ok = false;
            }

            report.Actions.Add(action);
        }

        if (options.CleanBepInExRuntimeArtifacts)
        {
            CleanRuntimeArtifacts(gameDirectory, manifest, report);
        }

        if (options.CleanupEmptyDirectories)
        {
            CleanEmptyDirectories(gameDirectory, manifest, report);
        }

        report.AlreadyClean = report.Ok
                              && report.RestoredFiles.Count == 0
                              && report.DeletedFiles.Count == 0
                              && report.CleanedRuntimeArtifacts.Count == 0
                              && report.CleanedDirectories.Count == 0
                              && report.SkippedFiles.Count == 0
                              && report.HashMismatches.Count == 0;

        _logger?.Log(FlowLogLevel.Info, "rollback", "IL2CPP 回滚完成", new Dictionary<string, string>
        {
            ["deploymentId"] = manifest.DeploymentId,
            ["restored"] = report.RestoredFiles.Count.ToString(CultureInfo.InvariantCulture),
            ["deleted"] = report.DeletedFiles.Count.ToString(CultureInfo.InvariantCulture),
            ["runtimeCleaned"] = report.CleanedRuntimeArtifacts.Count.ToString(CultureInfo.InvariantCulture),
            ["ok"] = report.Ok.ToString(),
        });

        return report.Ok
            ? ForgeResult<RollbackReport>.Success(report)
            : ForgeResult<RollbackReport>.Failure(ForgeError.Create(
                ForgeErrorCodes.Partial,
                $"回滚未完全成功：{report.Errors.Count} 个错误、{report.HashMismatches.Count} 个哈希冲突。",
                "查看 rollback 报告；确认文件可覆盖后加 --force 重试。",
                new Dictionary<string, string>
                {
                    ["report"] = report.ToJson(),
                    ["errors"] = string.Join("；", report.Errors.Take(5)),
                }));
    }

    private void CleanRuntimeArtifacts(string gameDirectory, InstallManifest manifest, RollbackReport report)
    {
        var baseline = manifest.BepInExBaseline;
        var baselineFiles = baseline is null
            ? null
            : new HashSet<string>(baseline.Files, StringComparer.OrdinalIgnoreCase);
        var createdBepInExTree = manifest.CreatedDirectories.Any(directory =>
                                    string.Equals(directory, "BepInEx", StringComparison.OrdinalIgnoreCase))
                                 || manifest.Files.Any(file =>
                                     file.RelativePath.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)
                                     && !file.ExistedBefore);
        var canClean = baselineFiles is not null || createdBepInExTree;

        foreach (var relative in EnumerateRuntimeCandidates(gameDirectory))
        {
            if (!canClean)
            {
                report.SkippedRuntimeArtifacts.Add(relative);
                continue;
            }

            if (baselineFiles is not null && baselineFiles.Contains(relative))
            {
                report.SkippedRuntimeArtifacts.Add(relative);
                continue;
            }

            try
            {
                var full = PathGuard.ResolveGameRelative(gameDirectory, relative);
                File.Delete(full);
                report.CleanedRuntimeArtifacts.Add(relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                report.Warnings.Add($"运行期产物清理失败：{relative}（{ex.Message}）");
            }
        }

        if (!canClean && report.SkippedRuntimeArtifacts.Count > 0)
        {
            report.Warnings.Add(
                $"账本没有安装前的 BepInEx 基线，无法证明 {report.SkippedRuntimeArtifacts.Count} 个运行期文件是本次安装创建的，已保留。");
        }
    }

    private static IEnumerable<string> EnumerateRuntimeCandidates(string gameDirectory)
    {
        var candidates = new List<string>();
        foreach (var root in RuntimeDirectoryRoots)
        {
            var directory = Path.Combine(gameDirectory, root.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    candidates.Add(Path.GetRelativePath(gameDirectory, file).Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 枚举失败就跳过这一根；不影响已回滚的账本文件。
            }
        }

        var bepInEx = Path.Combine(gameDirectory, "BepInEx");
        if (Directory.Exists(bepInEx))
        {
            try
            {
                foreach (var log in Directory.EnumerateFiles(bepInEx, "*.log", SearchOption.TopDirectoryOnly))
                {
                    candidates.Add(Path.GetRelativePath(gameDirectory, log).Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 同上。
            }
        }

        try
        {
            foreach (var pattern in new[] { "*.log", "preloader_*.log" })
            {
                foreach (var log in Directory.EnumerateFiles(gameDirectory, pattern, SearchOption.TopDirectoryOnly))
                {
                    candidates.Add(Path.GetRelativePath(gameDirectory, log).Replace('\\', '/'));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 同上。
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void CleanEmptyDirectories(string gameDirectory, InstallManifest manifest, RollbackReport report)
    {
        var directories = new HashSet<string>(manifest.CreatedDirectories, StringComparer.OrdinalIgnoreCase);
        directories.Add("BepInEx/interop");
        directories.Add("BepInEx/cache");
        directories.Add("BepInEx/config");
        directories.Add("BepInEx/patchers");
        directories.Add("BepInEx/plugins");
        directories.Add("BepInEx");

        foreach (var relative in directories
                     .OrderByDescending(directory => directory.Count(ch => ch == '/'))
                     .ThenByDescending(directory => directory, StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!Directory.Exists(full))
                {
                    continue;
                }

                if (Directory.EnumerateFileSystemEntries(full).Any())
                {
                    continue;
                }

                Directory.Delete(full, recursive: false);
                report.CleanedDirectories.Add(relative);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Warnings.Add($"清理空目录失败：{relative}（{ex.Message}）");
            }
        }
    }

    private static int UndoWrites(string gameDirectory, string backupRoot, List<PlannedFile> written)
    {
        var count = 0;
        foreach (var file in written.AsEnumerable().Reverse())
        {
            try
            {
                var target = PathGuard.ResolveGameRelative(gameDirectory, file.RelativePath);
                if (file.ExistedBefore && !string.IsNullOrWhiteSpace(file.BackupRelativePath))
                {
                    var backup = Path.Combine(
                        backupRoot,
                        file.BackupRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        WriteAtomic(target, backup);
                        count++;
                    }
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                    count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
            {
                // 自动回滚尽力而为；原始错误仍会返回给用户，备份目录保留。
            }
        }

        return count;
    }

    private static InstallFileEntry ToEntry(PlannedFile file)
        => new()
        {
            RelativePath = file.RelativePath,
            Action = file.Action,
            ExistedBefore = file.ExistedBefore,
            BackupPath = file.BackupRelativePath,
            Sha256Before = file.Sha256Before,
            Sha256After = file.Sha256After,
            SizeBefore = file.SizeBefore,
            SizeAfter = file.SizeAfter,
            Source = file.Source,
            Version = file.Version,
            DeployedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            Planned = false,
        };

    private static void WriteAtomic(string target, byte[] bytes)
    {
        var temporary = target + ".a11y-tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, target, overwrite: true);
    }

    private static void WriteAtomic(string target, string sourcePath)
    {
        var temporary = target + ".a11y-tmp";
        File.Copy(sourcePath, temporary, overwrite: true);
        File.Move(temporary, target, overwrite: true);
    }

    private string ResolveSlug(GameContext context)
        => string.IsNullOrWhiteSpace(context.Slug)
            ? PathSafety.SlugFromGameDirectory(context.GameDirectory)
            : context.Slug;

    private static string RandomSuffix()
        => Guid.NewGuid().ToString("N")[..4];

    private static ForgeResult<DeployResult> Fail(string code, string message, string? hint = null)
        => ForgeResult<DeployResult>.Failure(ForgeError.Create(code, message, hint));

    private sealed class PlannedFile
    {
        public string RelativePath { get; init; } = string.Empty;

        public byte[] Bytes { get; init; } = Array.Empty<byte>();

        public string Source { get; init; } = "plugin";

        public string? Version { get; init; }

        public DeployFileAction ActionOverride { get; init; }

        public DeployFileAction Action { get; set; }

        public bool ExistedBefore { get; set; }

        public string? Sha256Before { get; set; }

        public string? Sha256After { get; set; }

        public long SizeBefore { get; set; }

        public long SizeAfter { get; set; }

        public string? BackupRelativePath { get; set; }
    }

    private sealed record PluginArtifact(string Path, byte[] Bytes);

    private sealed record SpeechFile(string FileName, byte[] Bytes);
}
