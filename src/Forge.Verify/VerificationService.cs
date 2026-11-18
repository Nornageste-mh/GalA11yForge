using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Abstractions;
using GalA11yForge.Verify.Internal;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify;

/// <summary>
/// 安装后/回滚后的离线验证入口。
/// 只读游戏目录，不启动游戏、不执行安装或卸载；跳过项一律写 skip 并说明原因。
/// </summary>
public sealed class VerificationService
{
    private readonly IEnvironmentProbe _defaultProbe;

    public VerificationService(IEnvironmentProbe? environmentProbe = null)
    {
        _defaultProbe = environmentProbe ?? new WindowsEnvironmentProbe();
    }

    public async Task<ForgeResult<VerifyReport>> VerifyAsync(
        VerifyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var context = Prepare(options, out var prepareError);
            if (context is null)
            {
                return ForgeResult<VerifyReport>.Failure(prepareError!);
            }

            var report = new VerifyReport
            {
                GeneratedAt = DateTimeOffset.Now.ToString("O"),
                Slug = context.Slug,
                GamePath = context.GameDirectory,
                Mode = context.Mode,
            };
            var run = new VerificationRun(report);

            if (context.Mode == VerifyMode.Install)
            {
                run.Unverified("未启动游戏：游戏内朗读、焦点顺序、选项/回听/存档等运行期行为未验证。");
                run.Unverified("未执行卸载回滚：补丁可还原性与原版文件恢复未验证。");
            }
            else
            {
                run.Unverified("未启动游戏：回滚后原版能否启动、Steam 完整性校验是否通过未验证。");
            }

            var load = await InstallManifestLoader
                .LoadAsync(options.Manifest, context.ManifestPath, context.Workspace.RootPath, context.Slug, cancellationToken)
                .ConfigureAwait(false);
            report.ManifestPath = load.Path;

            if (load.Manifest is null)
            {
                run.Fail(
                    "m-01",
                    "安装账本可读",
                    VerifyPriority.P0,
                    load.Error ?? "安装账本不存在或不可读。");
                return Finish(run);
            }

            var manifest = load.Manifest;
            report.DeploymentId = manifest.DeploymentId;
            run.Metric("manifestFiles", manifest.Files?.Count ?? 0);
            run.Pass(
                "m-01",
                "安装账本可读",
                VerifyPriority.P0,
                $"已读取安装账本：{load.Path ?? "（调用方传入内存账本）"}");

            var validation = ManifestRules.Validate(manifest, context.GameDirectory);
            if (validation.IsValid)
            {
                run.Pass("m-02", "账本结构有效", VerifyPriority.P0, validation.Detail);
            }
            else
            {
                run.Fail(
                    "m-02",
                    "账本结构有效",
                    VerifyPriority.P0,
                    validation.Detail,
                    metrics: new Dictionary<string, double> { ["manifestIssues"] = validation.Issues.Count });
            }

            LedgerChecks.RunManifestPaths(run, manifest);

            if (!validation.IsValid)
            {
                foreach (var (id, name) in new[]
                {
                    ("m-04", "安装态文件在位"),
                    ("m-05", "安装态哈希一致"),
                    ("m-06", "安装文件非空"),
                    ("m-07", "账本外原文件未变"),
                    ("k-01", "备份文件存在"),
                    ("k-02", "备份哈希一致"),
                    ("k-03", "备份位置合规"),
                    ("r-01", "原有文件已还原"),
                    ("r-02", "新增文件已删除"),
                    ("r-03", "清理结论自洽"),
                })
                {
                    run.Skip(id, name, VerifyPriority.P0, "账本结构未通过校验，跳过依赖账本字段的检查。");
                }

                if (context.Mode == VerifyMode.Install)
                {
                    await EnvironmentChecks
                        .RunAsync(run, context, options, _defaultProbe, cancellationToken)
                        .ConfigureAwait(false);
                    report.Cleanup = NotRolledBack();
                }

                return Finish(run);
            }

            if (context.Mode == VerifyMode.Install)
            {
                await LedgerChecks
                    .RunInstallFilesAsync(run, context, manifest, cancellationToken)
                    .ConfigureAwait(false);
                await BaselineChecks
                    .RunAsync(run, context, options, manifest, cancellationToken)
                    .ConfigureAwait(false);
                await BackupChecks
                    .RunAsync(run, context, manifest, cancellationToken)
                    .ConfigureAwait(false);
                await BepInExChecks
                    .RunAsync(run, context, options, manifest, cancellationToken)
                    .ConfigureAwait(false);
                await EnvironmentChecks
                    .RunAsync(run, context, options, _defaultProbe, cancellationToken)
                    .ConfigureAwait(false);
                await SpeechLogChecks
                    .RunAsync(run, context, options, cancellationToken)
                    .ConfigureAwait(false);
                report.Cleanup = NotRolledBack();
            }
            else
            {
                await BaselineChecks
                    .RunAsync(run, context, options, manifest, cancellationToken)
                    .ConfigureAwait(false);
                await BackupChecks
                    .RunAsync(run, context, manifest, cancellationToken)
                    .ConfigureAwait(false);
                await RollbackChecks
                    .RunAsync(run, context, manifest, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Finish(run);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<VerifyReport>.Failure(ex.Error);
        }
        catch (Exception ex)
        {
            return ForgeResult<VerifyReport>.Failure(ForgeError.Create(
                ForgeErrorCodes.Internal,
                $"验证过程中出现未处理异常：{ex.GetType().Name}: {ex.Message}",
                "保留现场并查看详细堆栈；这属于工具缺陷，不应被当作通过。",
                new Dictionary<string, string> { ["exception"] = ex.ToString() }));
        }
    }

    private static PreparedContext? Prepare(VerifyOptions options, out ForgeError? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(options.GameDirectory))
        {
            error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "gameDirectory 不能为空。",
                "传入游戏安装目录。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.WorkspaceRoot))
        {
            error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "workspaceRoot 不能为空。",
                "传入工作区根目录（不得位于游戏目录内）。");
            return null;
        }

        string gameDirectory;
        string workspaceRoot;
        try
        {
            gameDirectory = PathSafety.NormalizeRoot(options.GameDirectory);
            workspaceRoot = PathSafety.NormalizeRoot(options.WorkspaceRoot);
        }
        catch (ForgePathException ex)
        {
            error = ex.Error;
            return null;
        }

        if (!Directory.Exists(gameDirectory))
        {
            error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{gameDirectory}",
                "确认游戏已安装，并传入正确的目录。");
            return null;
        }

        if (PathSafety.IsUnder(gameDirectory, workspaceRoot))
        {
            error = ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                "工作区不得位于游戏目录内。",
                "把 --workspace 指到游戏目录之外。");
            return null;
        }

        string slug;
        try
        {
            slug = string.IsNullOrWhiteSpace(options.Slug)
                ? PathSafety.SlugFromGameDirectory(gameDirectory)
                : PathSafety.SanitizeSlug(options.Slug);
        }
        catch (Exception ex) when (ex is ForgePathException or ArgumentException)
        {
            error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"无法确定合法的游戏 slug：{ex.Message}",
                "显式传入由字母、数字、短横线组成的 slug。");
            return null;
        }

        if (string.IsNullOrWhiteSpace(slug) || !PathSafety.IsValidSlug(slug))
        {
            error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏 slug 不合法：{slug}",
                "slug 只能包含小写字母、数字与短横线。");
            return null;
        }

        ForgeWorkspace workspace;
        try
        {
            workspace = new ForgeWorkspace(workspaceRoot, gameDirectory);
        }
        catch (ForgePathException ex)
        {
            error = ex.Error;
            return null;
        }

        string? manifestPath = null;
        string? baselinePath = null;
        var baselineExplicit = false;
        string logPath;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.ManifestPath))
            {
                manifestPath = ResolveUserPath(options.ManifestPath, workspaceRoot);
            }

            if (!string.IsNullOrWhiteSpace(options.BaselinePath))
            {
                baselinePath = ResolveUserPath(options.BaselinePath, workspaceRoot);
                baselineExplicit = true;
            }
            else if (options.Baseline is null)
            {
                baselinePath = Path.Combine(workspace.GamesRoot, slug, "05-deploy", "baseline.json");
            }

            logPath = string.IsNullOrWhiteSpace(options.LogPath)
                ? Path.Combine(gameDirectory, "BepInEx", "LogOutput.log")
                : ResolveUserPath(options.LogPath, gameDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"验证路径不合法：{ex.Message}",
                "检查 manifest/baseline/log 路径中是否含有非法字符。");
            return null;
        }

        if (baselinePath is not null)
        {
            var baselineError = ValidateBaselinePath(workspaceRoot, baselinePath);
            if (baselineError is not null)
            {
                error = baselineError;
                return null;
            }
        }

        return new PreparedContext
        {
            GameDirectory = gameDirectory,
            Workspace = workspace,
            Slug = slug,
            ManifestPath = manifestPath,
            LogPath = logPath,
            BaselinePath = baselinePath,
            BaselinePathExplicit = baselineExplicit,
            Mode = options.Mode,
            InstallerKind = options.InstallerKind,
            LogTailLines = options.LogTailLines,
        };
    }

    /// <summary>基线只从工作区读取；拒绝越界、工作区根本身与符号链接/联接点。</summary>
    private static ForgeError? ValidateBaselinePath(string workspaceRoot, string baselinePath)
    {
        if (!PathSafety.IsUnder(workspaceRoot, baselinePath)
            || string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)),
                baselinePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"基线路径必须位于工作区内：{baselinePath}",
                "把基线放到 games/<slug>/05-deploy/baseline.json。");
        }

        try
        {
            if (File.Exists(baselinePath)
                && (File.GetAttributes(baselinePath) & FileAttributes.ReparsePoint) != 0)
            {
                return ForgeError.Create(
                    ForgeErrorCodes.PathUnsafe,
                    $"基线文件是符号链接/联接点，拒绝读取：{baselinePath}",
                    "使用工作区内由本工具生成的普通 baseline.json。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeError.Create(
                ForgeErrorCodes.Io,
                $"无法检查基线文件属性：{ex.Message}",
                "检查文件权限后重试。");
        }

        return null;
    }

    private static string ResolveUserPath(string path, string baseDirectory)
    {
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(baseDirectory, path));
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static CleanupReport NotRolledBack()
        => new()
        {
            Required = false,
            Uninstalled = false,
            Restored = new List<string>(),
            HashMatch = null,
            Detail = "安装态验证不执行卸载/回滚；可还原性未验证。",
        };

    private static ForgeResult<VerifyReport> Finish(VerificationRun run)
    {
        var report = run.Report;
        report.Overall = VerifyReport.ComputeOverall(report.Checks);
        report.Metrics["checksPass"] = report.PassCount;
        report.Metrics["checksFail"] = report.FailCount;
        report.Metrics["checksSkip"] = report.SkipCount;
        report.Metrics["p0Fail"] = report.P0FailCount;
        return ForgeResult<VerifyReport>.Success(report);
    }
}
