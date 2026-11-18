using System.Text;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Internal;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Baseline;

/// <summary>
/// 部署前基线采集与存取。只记录路径、大小、SHA-256，不复制游戏文件、不保存文本内容。
/// </summary>
public static class GameBaselineBuilder
{
    public static async Task<ForgeResult<GameBaseline>> CaptureAsync(
        string gameDirectory,
        IEnumerable<string>? excludeRelativePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "采集基线时游戏目录不能为空。",
                "传入已安装游戏的根目录。"));
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不合法：{ex.Message}",
                "检查路径中是否含有非法字符。"));
        }

        if (!Directory.Exists(root))
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{root}",
                "确认游戏已安装，并传入正确的目录。"));
        }

        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludeRelativePaths is not null)
        {
            foreach (var item in excludeRelativePaths)
            {
                try
                {
                    excludes.Add(PathSafety.ValidateRelative(item));
                }
                catch (ForgePathException)
                {
                    // 排除项本身不合法时忽略；它不应该让整次采集失败。
                }
            }
        }

        var entries = new List<BaselineFileEntry>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                try
                {
                    relativePath = PathSafety.ValidateRelative(relativePath);
                }
                catch (ForgePathException)
                {
                    // 游戏目录里出现无法安全表达的文件名时跳过，并在报告里由调用方声明局限。
                    continue;
                }

                if (excludes.Contains(relativePath))
                {
                    continue;
                }

                var probe = await FileFacts.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                if (!probe.Exists || probe.Sha256 is null)
                {
                    return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                        ForgeErrorCodes.Io,
                        $"无法读取文件 {relativePath}：{probe.Error ?? "未知错误"}",
                        "关闭占用该文件的程序后重试，或检查文件权限。"));
                }

                entries.Add(new BaselineFileEntry
                {
                    RelativePath = relativePath,
                    Size = probe.Size,
                    Sha256 = probe.Sha256,
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"采集基线失败：{ex.Message}",
                "检查游戏目录权限后重试。"));
        }

        entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));

        return ForgeResult<GameBaseline>.Success(new GameBaseline
        {
            SchemaVersion = 1,
            Artifact = GameBaseline.ArtifactName,
            GamePath = root,
            CreatedAt = DateTimeOffset.Now.ToString("O"),
            Files = entries,
        });
    }

    /// <summary>
    /// 定向采集一组相对路径的部署前状态：存在则记录大小与 SHA-256，不存在也记录一条 Exists=false。
    /// 供安装流程记录“本次部署目标 + BepInEx 运行期白名单”，只读、不复制文件内容。
    /// </summary>
    public static ForgeResult<GameBaseline> CapturePaths(
        string gameDirectory,
        IEnumerable<string> relativePaths)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "采集基线时游戏目录不能为空。",
                "传入已安装游戏的根目录。"));
        }

        if (relativePaths is null)
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "基线路径集合不能为空。",
                "传入本次部署会触碰的文件。"));
        }

        string root;
        try
        {
            root = PathSafety.NormalizeRoot(gameDirectory);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<GameBaseline>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不合法：{ex.Message}",
                "检查路径中是否含有非法字符。"));
        }

        if (!Directory.Exists(root))
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{root}",
                "确认游戏已安装，并传入正确的目录。"));
        }

        var entries = new Dictionary<string, BaselineFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArgumentInvalid,
                    "基线路径不能为空。",
                    "检查部署计划与 BepInEx 白名单。"));
            }

            string relative;
            string fullPath;
            try
            {
                relative = PathSafety.ValidateRelative(item);
                fullPath = PathSafety.ResolveUnder(root, relative);
                EnsureNoReparse(root, fullPath);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<GameBaseline>.Failure(ex.Error);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                    ForgeErrorCodes.Io,
                    $"无法检查基线路径 {item}：{ex.Message}",
                    "检查游戏目录权限或关闭占用文件的程序后重试。"));
            }

            if (entries.ContainsKey(relative))
            {
                continue;
            }

            var probe = FileFacts.Probe(fullPath);
            if (!probe.Exists)
            {
                entries[relative] = new BaselineFileEntry
                {
                    RelativePath = relative,
                    Exists = false,
                    Size = 0,
                    Sha256 = string.Empty,
                };
                continue;
            }

            if (probe.Sha256 is null)
            {
                return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                    ForgeErrorCodes.Io,
                    $"无法读取基线文件 {relative}：{probe.Error ?? "未知错误"}",
                    "关闭占用该文件的程序后重试，或检查文件权限。"));
            }

            entries[relative] = new BaselineFileEntry
            {
                RelativePath = relative,
                Exists = true,
                Size = probe.Size,
                Sha256 = probe.Sha256,
            };
        }

        var files = entries.Values
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ForgeResult<GameBaseline>.Success(new GameBaseline
        {
            SchemaVersion = 1,
            Artifact = GameBaseline.ArtifactName,
            GamePath = root,
            CreatedAt = DateTimeOffset.Now.ToString("O"),
            Files = files,
        });
    }

    /// <summary>只读采集时拒绝穿过符号链接/联接点，避免基线记录到游戏目录之外的文件。</summary>
    private static void EnsureNoReparse(string root, string fullPath)
    {
        if (!PathSafety.IsUnder(root, fullPath))
        {
            throw ForgePathException.Escape(fullPath, root);
        }

        if ((File.Exists(fullPath) || Directory.Exists(fullPath))
            && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ForgePathException(ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"基线路径是符号链接/联接点：{fullPath}",
                "移除链接后重试。"));
        }

        var parent = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(parent)
               && !string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase)
               && PathSafety.IsUnder(root, parent))
        {
            if (Directory.Exists(parent)
                && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ForgePathException(ForgeError.Create(
                    ForgeErrorCodes.PathUnsafe,
                    $"基线路径的上层目录是符号链接/联接点：{parent}",
                    "移除链接后重试。"));
            }

            var next = Path.GetDirectoryName(parent);
            if (string.IsNullOrEmpty(next) || string.Equals(next, parent, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            parent = next;
        }
    }
}

/// <summary>基线的 JSON 读写。UTF-8 无 BOM；拒绝把基线写进游戏目录。</summary>
public static class GameBaselineStore
{
    public static async Task<ForgeResult<GameBaseline>> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "基线路径不能为空。",
                "指定 05-deploy/baseline.json。"));
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArtifactMissing,
                    $"基线不存在：{fullPath}",
                    "先执行基线采集，或去掉 --baseline 参数。"));
            }

            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                    ForgeErrorCodes.PathUnsafe,
                    $"基线文件是符号链接/联接点，拒绝读取：{fullPath}",
                    "使用工作区内由本工具生成的普通 baseline.json。"));
            }

            var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var baseline = ForgeJson.Deserialize<GameBaseline>(json);
            if (baseline is null)
            {
                return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"基线内容为空：{fullPath}",
                    "重新采集基线。"));
            }

            return ForgeResult<GameBaseline>.Success(baseline);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid,
                $"基线 JSON 解析失败：{ex.Message}",
                "确认基线由本工具生成，且 schemaVersion 为 1。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"读取基线失败：{ex.Message}",
                "检查文件权限后重试。"));
        }
    }

    /// <summary>同步保存（安装流程是同步 API）；原子写：临时文件写完再替换目标。</summary>
    public static ForgeResult<string> Save(GameBaseline baseline, string path)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        try
        {
            var target = ValidateTarget(baseline, path);
            if (!target.Ok)
            {
                return ForgeResult<string>.Failure(target.Error!);
            }

            var fullPath = target.Value!;
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = TempPathFor(fullPath);
            try
            {
                File.WriteAllText(tempPath, ForgeJson.Serialize(baseline), Utf8NoBom);
                File.Move(tempPath, fullPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            return ForgeResult<string>.Success(fullPath);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<string>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"保存基线失败：{ex.Message}",
                "检查目标目录权限与磁盘空间。"));
        }
    }

    public static async Task<ForgeResult<string>> SaveAsync(
        GameBaseline baseline,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        try
        {
            var target = ValidateTarget(baseline, path);
            if (!target.Ok)
            {
                return ForgeResult<string>.Failure(target.Error!);
            }

            var fullPath = target.Value!;
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = TempPathFor(fullPath);
            try
            {
                await File.WriteAllTextAsync(
                    tempPath,
                    ForgeJson.Serialize(baseline),
                    Utf8NoBom,
                    cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, fullPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            return ForgeResult<string>.Success(fullPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<string>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"保存基线失败：{ex.Message}",
                "检查目标目录权限与磁盘空间。"));
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static ForgeResult<string> ValidateTarget(GameBaseline baseline, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "基线保存路径不能为空。",
                "指定工作区内的 05-deploy/baseline.json。"));
        }

        var fullPath = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(baseline.GamePath)
            && PathSafety.IsUnder(baseline.GamePath, fullPath))
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.GameWriteDenied,
                $"拒绝把基线写进游戏目录：{fullPath}",
                "基线属于工作区产物，请写到 05-deploy/baseline.json。"));
        }

        if (File.Exists(fullPath)
            && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"基线目标是符号链接/联接点，拒绝写入：{fullPath}",
                "删除链接后重试，基线只写到工作区普通文件。"));
        }

        return ForgeResult<string>.Success(fullPath);
    }

    private static string TempPathFor(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var name = Path.GetFileName(fullPath);
        return Path.Combine(directory, $".{name}.{Guid.NewGuid():N}.tmp");
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
            // 临时文件清理失败不改变“保存失败”的结论。
        }
    }
}
