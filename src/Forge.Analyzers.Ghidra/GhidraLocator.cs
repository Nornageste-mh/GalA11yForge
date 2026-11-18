using System.Text.RegularExpressions;

namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>定位请求。测试通过 <see cref="Environment"/> 与 <see cref="ProbeRoots"/> 隔离本机环境。</summary>
public sealed class GhidraLocationRequest
{
    /// <summary>--option ghidra.path 的值；优先于一切环境变量与自动探测。</summary>
    public string? ExplicitPath { get; init; }

    /// <summary>自定义环境变量表；null 表示读真实进程环境。键名大小写不敏感。</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>自定义探测根目录；null 表示扫描本机常见安装位置。</summary>
    public IReadOnlyList<string>? ProbeRoots { get; init; }
}

/// <summary>
/// 按「--option ghidra.path → GHIDRA_HOME → GHIDRA_INSTALL_DIR → 常见安装目录」定位 Ghidra。
///
/// <para>
/// 判定标准只有一条：<c>support/analyzeHeadless.bat</c> 存在。Ghidra 是 BYO（自带 JDK 21），
/// 工具不随包分发；定位失败不是错误，而是「高级分析未启用」的可解释状态。
/// </para>
/// </summary>
public static class GhidraLocator
{
    public const string OptionPathKey = "ghidra.path";

    public const string HomeEnvironmentVariable = "GHIDRA_HOME";

    public const string InstallDirEnvironmentVariable = "GHIDRA_INSTALL_DIR";

    private const int MaxProbes = 20;

    private static readonly Regex VersionPattern = new(@"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.Compiled);

    public static GhidraLocation Locate(GhidraLocationRequest? request = null)
    {
        request ??= new GhidraLocationRequest();
        var probes = new List<string>();
        var env = request.Environment ?? ReadProcessEnvironment();

        if (!string.IsNullOrWhiteSpace(request.ExplicitPath))
        {
            var explicitPath = request.ExplicitPath.Trim();
            if (TryResolve(explicitPath, probes, out var home, out var headless))
            {
                return Found(home, headless, "option:" + OptionPathKey, probes);
            }

            return new GhidraLocation
            {
                Available = false,
                HomeDirectory = null,
                AnalyzeHeadlessPath = null,
                Source = "option:" + OptionPathKey,
                Error = $"指定的 Ghidra 路径无效：{explicitPath}（未找到 support\\analyzeHeadless.bat）。",
                Probes = Trim(probes),
            };
        }

        var envCandidates = new[]
        {
            (Name: HomeEnvironmentVariable, Value: Get(env, HomeEnvironmentVariable)),
            (Name: InstallDirEnvironmentVariable, Value: Get(env, InstallDirEnvironmentVariable)),
        };

        foreach (var (name, value) in envCandidates)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (TryResolve(value.Trim(), probes, out var home, out var headless))
            {
                return Found(home, headless, "env:" + name, probes);
            }
        }

        foreach (var candidate in EnumerateProbeCandidates(request.ProbeRoots))
        {
            if (TryResolve(candidate, probes, out var home, out var headless))
            {
                return Found(home, headless, "probe", probes);
            }
        }

        return new GhidraLocation
        {
            Available = false,
            Source = "probe",
            Error = "未找到可用的 Ghidra（缺少 support\\analyzeHeadless.bat）。",
            Probes = Trim(probes),
            Version = null,
            HomeDirectory = null,
            AnalyzeHeadlessPath = null,
        };
    }

    private static GhidraLocation Found(string home, string headless, string source, List<string> probes)
    {
        probes.Add($"{home}（已找到）");
        return new GhidraLocation
        {
            Available = true,
            HomeDirectory = home,
            AnalyzeHeadlessPath = headless,
            Version = ReadVersion(home),
            Source = source,
            Probes = Trim(probes),
        };
    }

    private static bool TryResolve(string candidate, List<string> probes, out string home, out string headless)
    {
        home = string.Empty;
        headless = string.Empty;
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            probes.Add($"{candidate}（路径不合法）");
            return false;
        }

        if (File.Exists(full) &&
            string.Equals(Path.GetFileName(full), "analyzeHeadless.bat", StringComparison.OrdinalIgnoreCase))
        {
            var support = Path.GetDirectoryName(full);
            var parent = support is null ? null : Path.GetDirectoryName(support);
            if (parent is not null)
            {
                home = parent;
                headless = full;
                return true;
            }
        }

        if (!Directory.Exists(full))
        {
            probes.Add($"{full}（目录不存在）");
            return false;
        }

        var bat = Path.Combine(full, "support", "analyzeHeadless.bat");
        if (File.Exists(bat))
        {
            home = Path.TrimEndingDirectorySeparator(full);
            headless = bat;
            return true;
        }

        probes.Add($"{full}（未找到 support\\analyzeHeadless.bat）");
        return false;
    }

    private static IEnumerable<string> EnumerateProbeCandidates(IReadOnlyList<string>? customRoots)
    {
        var roots = customRoots is { Count: > 0 } ? customRoots : DefaultProbeRoots();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // 根目录本身就是 Ghidra 安装目录（例如测试里的临时根 = D:\tmp\ghidra_12.1.4_PUBLIC）。
            if (seen.Add(full))
            {
                yield return full;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(full, "ghidra*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var child in OrderByVersion(children))
            {
                if (seen.Add(child))
                {
                    yield return child;
                }
            }
        }
    }

    private static IEnumerable<string> OrderByVersion(IEnumerable<string> directories)
        => directories.OrderByDescending(
            path => ParseVersion(Path.GetFileName(path)) ?? new Version(0, 0),
            Comparer<Version>.Default)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase);

    private static List<string> DefaultProbeRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 盘符枚举失败时仍继续尝试下面的固定目录。
        }

        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // 研究/任务卡里出现的已知位置，避免非系统盘扫描顺序影响结果。
        roots.Add(@"D:\ghidra_12.1.4_PUBLIC");
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ReadVersion(string home)
    {
        var properties = Path.Combine(home, "Ghidra", "application.properties");
        try
        {
            if (File.Exists(properties))
            {
                foreach (var line in File.ReadLines(properties))
                {
                    if (line.StartsWith("application.version=", StringComparison.Ordinal))
                    {
                        var value = line["application.version=".Length..].Trim();
                        if (value.Length > 0)
                        {
                            return value;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 版本读不到不影响可用性；再用目录名兜底。
        }

        var parsed = ParseVersion(Path.GetFileName(home));
        return parsed?.ToString();
    }

    private static Version? ParseVersion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = VersionPattern.Match(name);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor))
        {
            return null;
        }

        var build = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var parsed) ? parsed : 0;
        return new Version(major, minor, build);
    }

    private static IReadOnlyList<string> Trim(List<string> probes)
    {
        if (probes.Count <= MaxProbes)
        {
            return probes;
        }

        var trimmed = probes.Take(MaxProbes).ToList();
        trimmed.Add($"……其余 {probes.Count - MaxProbes} 个候选路径已省略");
        return trimmed;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> env, string key)
    {
        foreach (var pair in env)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string?> ReadProcessEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [HomeEnvironmentVariable] = Environment.GetEnvironmentVariable(HomeEnvironmentVariable),
            [InstallDirEnvironmentVariable] = Environment.GetEnvironmentVariable(InstallDirEnvironmentVariable),
        };
        return values;
    }
}
