using System.IO.Compression;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>BepInEx 包内的一个文件（目录条目不进清单）。</summary>
public sealed class BepInExEntry
{
    public string RelativePath { get; init; } = string.Empty;

    public long Length { get; init; }
}

/// <summary>
/// 打开并校验 BepInEx 缓存 zip。官方 5.4.23.5 win_x64 包没有外层目录：
/// 根下是 winhttp.dll / doorstop_config.ini，插件框架在 BepInEx/core/。
/// </summary>
public sealed class BepInExPackage
{
    public const string OfficialDownloadUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";

    public const string PinnedSha256 = "82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4";

    private readonly Dictionary<string, string> _entryNames;

    private BepInExPackage(
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

    public static ForgeResult<BepInExPackage> Open(string path, string? expectedSha256 = PinnedSha256, bool verifyHash = true)
    {
        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"BepInEx 包路径非法：{ex.Message}",
                "使用普通绝对路径。"));
        }

        if (!File.Exists(fullPath))
        {
            return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                $"BepInEx 包不存在：{fullPath}",
                $"下载 BepInEx 5.4.23.5（win x64）放到工作区后重试：{OfficialDownloadUrl}",
                new Dictionary<string, string> { ["packagePath"] = fullPath, ["downloadUrl"] = OfficialDownloadUrl }));
        }

        string actualHash;
        try
        {
            actualHash = FileHashes.Sha256File(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"无法读取 BepInEx 包：{ex.Message}",
                "检查文件权限与磁盘状态。",
                new Dictionary<string, string> { ["packagePath"] = fullPath }));
        }

        var requiredHash = string.IsNullOrWhiteSpace(expectedSha256) ? PinnedSha256 : expectedSha256;
        if (verifyHash && !FileHashes.EqualsHash(actualHash, requiredHash))
        {
            return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid,
                $"BepInEx 包哈希不匹配：期望 {requiredHash}，实际 {actualHash}。",
                $"不要使用来源不明的包；从官方地址重新下载：{OfficialDownloadUrl}",
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
                    return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"BepInEx 包内含非法路径 “{entry.FullName}”：{ex.Error.Message}",
                        "只使用官方发布的 BepInEx 5.4.23.5 压缩包。",
                        new Dictionary<string, string> { ["packagePath"] = fullPath }));
                }

                if (!names.TryAdd(relative, entry.FullName))
                {
                    return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"BepInEx 包内路径重复（大小写不敏感）：{relative}",
                        "压缩包已损坏或被重新打包，请从官方地址重新下载。",
                        new Dictionary<string, string> { ["packagePath"] = fullPath }));
                }

                entries.Add(new BepInExEntry { RelativePath = relative, Length = entry.Length });
            }

            foreach (var marker in new[] { "winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.dll" })
            {
                if (!names.ContainsKey(marker))
                {
                    return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"BepInEx 包缺少必要文件：{marker}",
                        $"确认下载的是 BepInEx 5.4.23.5 win x64 包：{OfficialDownloadUrl}",
                        new Dictionary<string, string> { ["packagePath"] = fullPath }));
                }
            }

            entries.Sort((left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
            return ForgeResult<BepInExPackage>.Success(new BepInExPackage(fullPath, actualHash, entries, names));
        }
        catch (InvalidDataException ex)
        {
            return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid,
                $"BepInEx 包不是有效的 zip：{ex.Message}",
                $"从官方地址重新下载：{OfficialDownloadUrl}",
                new Dictionary<string, string> { ["packagePath"] = fullPath }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<BepInExPackage>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"无法读取 BepInEx 包：{ex.Message}",
                "检查文件权限与磁盘状态。",
                new Dictionary<string, string> { ["packagePath"] = fullPath }));
        }
    }

    public byte[] ReadBytes(string relativePath)
    {
        var normalized = PathSafety.ValidateRelative(relativePath);
        if (!_entryNames.TryGetValue(normalized, out var actualName))
        {
            throw new FileNotFoundException($"BepInEx 包内不存在条目：{normalized}", normalized);
        }

        using var archive = ZipFile.OpenRead(Path);
        var entry = archive.GetEntry(actualName)
                    ?? throw new FileNotFoundException($"BepInEx 包内不存在条目：{normalized}", normalized);
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
