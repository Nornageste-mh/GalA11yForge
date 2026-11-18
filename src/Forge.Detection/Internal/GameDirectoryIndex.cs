namespace GalA11yForge.Detection.Internal;

/// <summary>扫描到的文件条目。相对路径保留原始大小写（证据可读），匹配用小写版本。</summary>
internal sealed class IndexedFile
{
    public IndexedFile(string name, string relativePath, string lowerRelativePath, string fullPath, long length)
    {
        Name = name;
        RelativePath = relativePath;
        LowerRelativePath = lowerRelativePath;
        FullPath = fullPath;
        Length = length;
    }

    public string Name { get; }

    /// <summary>相对根目录路径，'/' 分隔、无前导 '/'、原始大小写。</summary>
    public string RelativePath { get; }

    /// <summary>小写相对路径，用于 glob 匹配。</summary>
    public string LowerRelativePath { get; }

    public string FullPath { get; }

    public long Length { get; }
}

/// <summary>
/// 一次性目录索引：递归枚举文件名/目录名（不读大文件内容），供 7 种条件原语查询。
/// 安全与确定性：
/// - 只读枚举，不创建/修改/删除任何文件；
/// - 跳过 reparse point（junction/符号链接），避免目录环与越出游戏目录；
/// - 不可访问的子项忽略（IgnoreInaccessible），文件头读不到由 SafeFileReader 返回 false；
/// - 枚举结果按相对路径排序，保证同一目录多次检测结果一致（参考实现依赖 OS 目录顺序）。
/// 上限：20 万文件 / 5 万目录 / 深度 64；超出置 Truncated=true（结果可能漏检，不静默）。
/// </summary>
internal sealed class GameDirectoryIndex
{
    public const int MaxFiles = 200_000;
    public const int MaxDirectories = 50_000;
    public const int MaxDepth = 64;

    private readonly Dictionary<string, IReadOnlyList<IndexedFile>> _matchCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly List<IndexedFile> _files = new();

    private GameDirectoryIndex(string rootPath) => RootPath = rootPath;

    public string RootPath { get; }

    public IReadOnlyList<IndexedFile> Files => _files;

    /// <summary>根目录第一层的文件/目录名（目录带尾 '/')，用于未知引擎的证据。</summary>
    public IReadOnlyList<string> RootEntries { get; private set; } = Array.Empty<string>();

    public int DirectoriesScanned { get; private set; }

    public int ReparsePointsSkipped { get; private set; }

    public bool Truncated { get; private set; }

    public static GameDirectoryIndex Scan(string rootPath, CancellationToken cancellationToken)
    {
        var index = new GameDirectoryIndex(rootPath);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple,
        };

        var stack = new Stack<(DirectoryInfo Info, string Relative, int Depth)>();
        stack.Push((new DirectoryInfo(rootPath), string.Empty, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relativeDirectory, depth) = stack.Pop();

            FileSystemInfo[] entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos("*", options).ToArray();
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or System.Security.SecurityException
                                           or DirectoryNotFoundException)
            {
                continue;
            }

            Array.Sort(entries, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            index.DirectoriesScanned++;

            var rootEntries = new List<string>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // junction / 符号链接：跳过，既不进环也不越界。
                    index.ReparsePointsSkipped++;
                    continue;
                }

                var childRelative = relativeDirectory.Length == 0
                    ? entry.Name
                    : relativeDirectory + "/" + entry.Name;
                var isRoot = relativeDirectory.Length == 0;

                if (entry is DirectoryInfo childDirectory)
                {
                    if (isRoot)
                    {
                        rootEntries.Add(entry.Name + "/");
                    }

                    if (depth + 1 > MaxDepth || index._directories.Count >= MaxDirectories)
                    {
                        index.Truncated = true;
                        continue;
                    }

                    index._directories.Add(NormalizePath(childRelative));
                    stack.Push((childDirectory, childRelative, depth + 1));
                }
                else
                {
                    if (isRoot)
                    {
                        rootEntries.Add(entry.Name);
                    }

                    if (index._files.Count >= MaxFiles)
                    {
                        index.Truncated = true;
                        continue;
                    }

                    var length = 0L;
                    try
                    {
                        length = ((FileInfo)entry).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        // 长度读不到就按 0 处理；依赖长度的 pe_string 条件会因此跳过该文件。
                    }

                    index._files.Add(new IndexedFile(
                        entry.Name,
                        childRelative,
                        NormalizePath(childRelative),
                        entry.FullName,
                        length));
                }
            }

            if (relativeDirectory.Length == 0)
            {
                index.RootEntries = rootEntries
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        index._files.Sort(static (a, b) => string.CompareOrdinal(a.LowerRelativePath, b.LowerRelativePath));
        return index;
    }

    /// <summary>返回相对路径匹配 glob 的文件（缓存，保持排序）。</summary>
    public IReadOnlyList<IndexedFile> Match(string glob)
    {
        if (string.IsNullOrEmpty(glob))
        {
            return Array.Empty<IndexedFile>();
        }

        if (_matchCache.TryGetValue(glob, out var cached))
        {
            return cached;
        }

        var matches = new List<IndexedFile>();
        foreach (var file in _files)
        {
            if (GlobMatcher.IsMatch(file.LowerRelativePath, glob))
            {
                matches.Add(file);
            }
        }

        _matchCache[glob] = matches;
        return matches;
    }

    public bool DirectoryExists(string directory) =>
        !string.IsNullOrWhiteSpace(directory) && _directories.Contains(NormalizePath(directory));

    /// <summary>相对路径规范化：'\'→'/'、去 './'、折叠 '//'、去首尾 '/'、小写。</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.Trim('/').ToLowerInvariant();
    }
}
