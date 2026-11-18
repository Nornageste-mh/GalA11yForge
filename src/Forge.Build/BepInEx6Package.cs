using System.IO.Compression;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>
/// BepInEx 6（IL2CPP 用）离线包。与 BepInEx 5 的差别：
/// 1) 核心在 BepInEx/core/BepInEx.Unity.IL2CPP.dll + BepInEx.Core.dll，没有 BepInEx.dll；
/// 2) 包内自带 dotnet/ 运行时与 Il2CppInterop/Cpp2IL，解压后体积约 34MB；
/// 3) 首次启动会在游戏目录生成 BepInEx/interop（约 97 个 DLL、74MB），那是运行期派生物，不在包里。
/// 因此不能复用 5.x 的 BepInExPackage（marker 校验必失败），单独实现并钉死版本与哈希。
/// </summary>
public sealed class BepInEx6Package
{
    /// <summary>R21 真机验证过的唯一版本；换版本必须重新做 E2E 并更新本常量。</summary>
    public const string Version = "6.0.0-be.788";

    /// <summary>官方 BepInEx_win_x64_6.0.0-be.788.zip 的 SHA-256（2026-11-14 本机实测）。</summary>
    public const string PinnedSha256 = "F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A";

    public const string OfficialDownloadUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-be.788/BepInEx_win_x64_6.0.0-be.788.zip";

    /// <summary>包内必须存在的 marker。BepInEx 6 不再有 BepInEx/core/BepInEx.dll。</summary>
    public static readonly IReadOnlyList<string> CoreMarkers =
    [
        "winhttp.dll",
        "doorstop_config.ini",
        "BepInEx/core/BepInEx.Unity.IL2CPP.dll",
        "BepInEx/core/BepInEx.Core.dll",
    ];

    private readonly Dictionary<string, string> _entryNames;

    private BepInEx6Package(
        string path,
        string sha256,
        IReadOnlyList<BepInExEntry> entries,
        Dictionary<string, string> entryNames)
    {
        Path = path;
        Sha256 = sha256;
        Entries = entries;
        _entryNames = entryNames;
    }

    public string Path { get; }

    public string Sha256 { get; }

    public IReadOnlyList<BepInExEntry> Entries { get; }

    public static ForgeResult<BepInEx6Package> Open(
        string path,
        string? expectedSha256 = PinnedSha256,
        bool verifyHash = true)
    {
        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"BepInEx 6 包路径非法：{ex.Message}",
                "使用普通绝对路径。"));
        }

        if (!File.Exists(fullPath))
        {
            return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                $"BepInEx 6 包不存在：{fullPath}",
                $"先下载 BepInEx {Version}（win x64）放到工作区，或用 --bepinex 指定 zip：{OfficialDownloadUrl}",
                new Dictionary<string, string>
                {
                    ["packagePath"] = fullPath,
                    ["downloadUrl"] = OfficialDownloadUrl,
                    ["version"] = Version,
                }));
        }

        string actualHash;
        try
        {
            actualHash = FileHashes.Sha256File(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"无法读取 BepInEx 6 包：{ex.Message}",
                "检查文件权限与磁盘状态。",
                new Dictionary<string, string> { ["packagePath"] = fullPath }));
        }

        var requiredHash = string.IsNullOrWhiteSpace(expectedSha256) ? PinnedSha256 : expectedSha256;
        if (verifyHash && !FileHashes.EqualsHash(actualHash, requiredHash))
        {
            return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid,
                $"BepInEx 6 包哈希不匹配：期望 {requiredHash}，实际 {actualHash}。",
                $"IL2CPP 安装链只认 BepInEx {Version}；从官方地址重新下载：{OfficialDownloadUrl}",
                new Dictionary<string, string>
                {
                    ["packagePath"] = fullPath,
                    ["expectedSha256"] = requiredHash,
                    ["actualSha256"] = actualHash,
                    ["downloadUrl"] = OfficialDownloadUrl,
                }));
        }

        try
        {
            using var archive = ZipFile.OpenRead(fullPath);
            var entries = new List<BepInExEntry>();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                string relative;
                try
                {
                    relative = PathSafety.ValidateRelative(entry.FullName);
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"BepInEx 6 包内含非法路径 “{entry.FullName}”：{ex.Error.Message}",
                        $"只使用官方发布的 BepInEx {Version} 压缩包。",
                        new Dictionary<string, string> { ["packagePath"] = fullPath }));
                }

                if (!names.TryAdd(relative, entry.FullName))
                {
                    return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"BepInEx 6 包内路径重复（大小写不敏感）：{relative}",
                        "压缩包已损坏或被重新打包，请从官方地址重新下载。",
                        new Dictionary<string, string> { ["packagePath"] = fullPath }));
                }

                entries.Add(new BepInExEntry { RelativePath = relative, Length = entry.Length });
            }

            foreach (var marker in CoreMarkers)
            {
                if (!names.ContainsKey(marker))
                {
                    return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"BepInEx 6 包缺少必要文件：{marker}（这可能是 BepInEx 5.x 包或非 IL2CPP 包）。",
                        $"IL2CPP 路线需要 BepInEx {Version} win x64：{OfficialDownloadUrl}",
                        new Dictionary<string, string> { ["packagePath"] = fullPath, ["missingMarker"] = marker }));
                }
            }

            entries.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
            return ForgeResult<BepInEx6Package>.Success(new BepInEx6Package(fullPath, actualHash, entries, names));
        }
        catch (InvalidDataException ex)
        {
            return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid,
                $"BepInEx 6 包不是有效的 zip：{ex.Message}",
                $"从官方地址重新下载：{OfficialDownloadUrl}",
                new Dictionary<string, string> { ["packagePath"] = fullPath }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<BepInEx6Package>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"无法读取 BepInEx 6 包：{ex.Message}",
                "检查文件权限与磁盘状态。",
                new Dictionary<string, string> { ["packagePath"] = fullPath }));
        }
    }

    public byte[] ReadBytes(string relativePath)
    {
        var normalized = PathSafety.ValidateRelative(relativePath);
        if (!_entryNames.TryGetValue(normalized, out var actualName))
        {
            throw new FileNotFoundException($"BepInEx 6 包内不存在条目：{normalized}", normalized);
        }

        using var archive = ZipFile.OpenRead(Path);
        var entry = archive.GetEntry(actualName)
                    ?? throw new FileNotFoundException($"BepInEx 6 包内不存在条目：{normalized}", normalized);
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

/// <summary>
/// 在工作区里找 BepInEx_win_x64_6*.zip。优先精确的 be.788 文件名，其次目录名含 _a11y、浅层、修改时间新。
/// 注意：不是“找到 6.x 就装”，部署器仍会用 BepInEx6Package 的固定 SHA-256 校验；
/// 版本不对会在打开时明确失败，而不是把未知版本静默装进游戏。
/// </summary>
public static class BepInEx6Locator
{
    public const string ExactFileName = "BepInEx_win_x64_6.0.0-be.788.zip";

    /// <summary>可用环境变量显式指定包路径，便于 CI / 离线缓存。</summary>
    public const string EnvironmentVariable = "GALA11Y_BEPINEX6";

    /// <summary>单根搜索（兼容旧调用）。</summary>
    public static ForgeResult<IReadOnlyList<string>> FindCandidates(string searchRoot, string? explicitPath = null)
        => FindCandidates(new[] { searchRoot }, explicitPath);

    /// <summary>
    /// 多根搜索：显式路径最优先，其次 <see cref="EnvironmentVariable"/>，最后按工作区、forge
    /// 程序旁等搜索根顺序枚举；发布包自带 app\bepinex6\ 时 install 不必再传 --bepinex。
    /// </summary>
    public static ForgeResult<IReadOnlyList<string>> FindCandidates(
        IReadOnlyList<string>? searchRoots,
        string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full;
            try
            {
                full = Path.GetFullPath(explicitPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArgumentInvalid,
                    $"指定的 BepInEx 6 包路径非法：{ex.Message}",
                    "使用普通绝对路径。"));
            }

            if (!File.Exists(full))
            {
                return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.DependencyMissing,
                    $"指定的 BepInEx 6 包不存在：{full}；下载地址：{BepInEx6Package.OfficialDownloadUrl}",
                    $"下载 BepInEx {BepInEx6Package.Version}（win x64）：{BepInEx6Package.OfficialDownloadUrl}",
                    new Dictionary<string, string>
                    {
                        ["packagePath"] = full,
                        ["downloadUrl"] = BepInEx6Package.OfficialDownloadUrl,
                    }));
            }

            return ForgeResult<IReadOnlyList<string>>.Success(new[] { full });
        }

        var candidates = new List<string>();
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            candidates.Add(Path.GetFullPath(fromEnvironment));
        }

        if (searchRoots is not null && searchRoots.Count > 0)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            foreach (var searchRoot in searchRoots)
            {
                if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
                {
                    continue;
                }

                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(searchRoot, "BepInEx_win_x64_6*.zip", options));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                        ForgeErrorCodes.Io,
                        $"搜索 BepInEx 6 缓存失败：{ex.Message}",
                        "用 --bepinex 显式指定 BepInEx 6 zip 路径。"));
                }
            }
        }

        var sorted = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderByDescending(path => string.Equals(
                Path.GetFileName(path), ExactFileName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => path.Contains("bepinex6", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => path.Contains("_a11y", StringComparison.OrdinalIgnoreCase))
            .ThenBy(Depth)
            .ThenByDescending(SafeLastWrite)
            .ToList();

        return ForgeResult<IReadOnlyList<string>>.Success(sorted);
    }

    private static int Depth(string path)
        => path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
