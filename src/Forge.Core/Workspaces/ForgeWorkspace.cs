using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Workspaces;

/// <summary>
/// 工作区布局：
/// &lt;root&gt;/games/&lt;slug&gt;/{profile.json,overrides.json,00-detect..06-verify,logs}
/// &lt;root&gt;/work/&lt;id&gt;/    临时分析/构建目录
/// &lt;root&gt;/backup/          部署前备份
/// &lt;root&gt;/reports/         汇总报告与 flow.jsonl
/// &lt;root&gt;/logs/            产品运行日志
/// 默认 root = %LOCALAPPDATA%\GalA11yForge，可用 --workspace 覆盖。
/// </summary>
public sealed class ForgeWorkspace
{
    public const string ProductFolderName = "GalA11yForge";

    public ForgeWorkspace(string rootPath, string? gameDirectory = null)
    {
        RootPath = PathSafety.NormalizeRoot(rootPath);
        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            var fullGame = Path.GetFullPath(gameDirectory);
            if (PathSafety.IsUnder(fullGame, RootPath))
            {
                throw new ForgePathException(ForgeError.Create(
                    ForgeErrorCodes.GameWriteDenied,
                    $"工作区不能位于游戏目录内：{RootPath}（游戏目录 {fullGame}）。",
                    "把工作区放到 %LOCALAPPDATA%\\GalA11yForge 或其他独立目录。"));
            }

            GameDirectory = fullGame;
        }
    }

    public string RootPath { get; }

    /// <summary>当前目标游戏目录（可空）。只读：工作区任何写盘都不得落在这里。</summary>
    public string? GameDirectory { get; }

    public string GamesRoot => Path.Combine(RootPath, "games");

    public string WorkRoot => Path.Combine(RootPath, "work");

    public string BackupRoot => Path.Combine(RootPath, "backup");

    public string ReportsRoot => Path.Combine(RootPath, "reports");

    public string LogsRoot => Path.Combine(RootPath, "logs");

    public string FlowLogPath => Path.Combine(ReportsRoot, "flow.jsonl");

    public static string DefaultRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductFolderName);

    public static ForgeWorkspace CreateDefault(string? rootPath = null, string? gameDirectory = null)
        => new(string.IsNullOrWhiteSpace(rootPath) ? DefaultRootPath : rootPath, gameDirectory);

    /// <summary>创建全部顶层目录。返回 ForgeResult 便于 CLI 直接映射退出码。</summary>
    public ForgeResult<bool> EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(GamesRoot);
            Directory.CreateDirectory(WorkRoot);
            Directory.CreateDirectory(BackupRoot);
            Directory.CreateDirectory(ReportsRoot);
            Directory.CreateDirectory(LogsRoot);
            return ForgeResult<bool>.Success(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<bool>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"无法创建工作区目录 {RootPath}：{ex.Message}",
                "检查磁盘空间与目录权限，或用 --workspace 指定其他可写目录。"));
        }
    }

    public string GameRoot(string slug)
    {
        EnsureValidSlug(slug);
        var path = Path.Combine(GamesRoot, slug);
        return EnsureWriteAllowed(path);
    }

    public string StageDirectory(string slug, PipelineStage stage)
        => Path.Combine(GameRoot(slug), stage.DirectoryName());

    public string GameLogsDirectory(string slug) => Path.Combine(GameRoot(slug), "logs");

    public string ProfilePath(string slug) => Path.Combine(GameRoot(slug), "profile.json");

    public string OverridesPath(string slug) => Path.Combine(GameRoot(slug), "overrides.json");

    public string WorkDirectory(string workId)
    {
        EnsureValidToken(workId, nameof(workId));
        return EnsureWriteAllowed(Path.Combine(WorkRoot, workId));
    }

    public string NewWorkId(string prefix = "job")
    {
        EnsureValidToken(prefix, nameof(prefix));
        return $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..Math.Min(64, prefix.Length + 1 + 15 + 1 + 8)];
    }

    public string BackupDirectory(string deploymentId)
    {
        EnsureValidToken(deploymentId, nameof(deploymentId));
        return EnsureWriteAllowed(Path.Combine(BackupRoot, deploymentId));
    }

    public string ReportDirectory(string slug)
    {
        EnsureValidSlug(slug);
        return EnsureWriteAllowed(Path.Combine(ReportsRoot, slug));
    }

    /// <summary>为游戏创建 00-detect..06-verify 与 logs 目录；已存在则跳过。</summary>
    public ForgeResult<bool> EnsureGameLayout(string slug)
    {
        try
        {
            var root = GameRoot(slug);
            Directory.CreateDirectory(root);
            foreach (var stage in Enum.GetValues<PipelineStage>())
            {
                Directory.CreateDirectory(StageDirectory(slug, stage));
            }

            Directory.CreateDirectory(GameLogsDirectory(slug));
            return ForgeResult<bool>.Success(true);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<bool>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<bool>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"无法创建游戏工作目录 {slug}：{ex.Message}",
                "检查磁盘空间与目录权限。"));
        }
    }

    /// <summary>所有写盘前的最终闸门：工作区内、非游戏目录。</summary>
    public string EnsureWriteAllowed(string path) => PathSafety.EnsureWriteAllowed(RootPath, path, GameDirectory);

    /// <summary>把相对路径解析到工作区内并校验。</summary>
    public string ResolveWritePath(string relativePath, string? baseDirectory = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(baseDirectory) ? RootPath : Path.GetFullPath(baseDirectory);
        if (!PathSafety.IsUnder(RootPath, baseDir))
        {
            throw ForgePathException.Escape(baseDir, RootPath);
        }

        var resolved = PathSafety.ResolveUnder(baseDir, relativePath);
        return EnsureWriteAllowed(resolved);
    }

    /// <summary>校验部署备份路径必须落在 backup/ 下，防止账本被篡改后写到游戏目录。</summary>
    public string EnsureBackupPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!PathSafety.IsUnder(BackupRoot, full))
        {
            throw ForgePathException.Escape(full, BackupRoot);
        }

        return full;
    }

    private static void EnsureValidSlug(string slug)
    {
        if (!PathSafety.IsValidSlug(slug))
        {
            throw ForgePathException.InvalidSlug(slug);
        }
    }

    private static void EnsureValidToken(string token, string name)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Contains('/') || token.Contains('\\')
            || token is "." or ".." || token.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            throw new ForgePathException(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"非法的 {name} “{token}”。",
                "只使用字母、数字、点、下划线和连字符，不能包含路径分隔符。"));
        }
    }
}
