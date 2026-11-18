using System.Text;

namespace GalA11yForge.Build;

/// <summary>
/// 加载器/构建兼容性探针（只读）。
///
/// <para>
/// 背景：研究 R20（research/26-loader-compat/REPORT.md）实测发现，部分 Unity Mono 游戏被厂商
/// 裁剪过运行库：mscorlib 缺 <c>System.Reflection.Module.GetPEKind</c> 时 BepInEx 5/6 预加载器
/// 会在 PreloaderPreMain 崩溃（且不一定留日志）；InputLegacyModule 缺 <c>Input.anyKeyDown</c>
/// 时生成工程编译失败。本探针在生成/构建/安装前用只读字节扫描提前判定，避免「花了时间才失败」
/// 或「失败得像工具 bug」。
/// </para>
///
/// <para>
/// 判定规则来自实测：在 9 个本机 Unity Mono 游戏上，按成员名扫描的判定与真机结果 100% 一致
/// （NEEDY 缺 GetPEKind → 崩；《五天一夜》0 缺失 → 正常）。成员判定按名字对齐、不校验签名，
/// 属于保守预检而非安全证明。
/// </para>
/// </summary>
public static class LoaderCompatProbe
{
    public const string VerdictSupported = "supported";
    public const string VerdictDegraded = "degraded";
    public const string VerdictUnsupported = "unsupported";
    public const string VerdictUnknown = "unknown";

    private static readonly byte[] GetPeKindBytes = Encoding.UTF8.GetBytes("GetPEKind");
    private static readonly byte[] AnyKeyDownBytes = Encoding.UTF8.GetBytes("anyKeyDown");

    public static LoaderCompatReport Probe(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return Unknown("未提供游戏目录，跳过加载器兼容性预检。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(gameDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Unknown($"游戏目录不合法：{gameDirectory}");
        }

        if (!Directory.Exists(full))
        {
            return Unknown($"游戏目录不存在：{full}");
        }

        var evidence = new List<string>();
        var todos = new List<string>();
        var dataDirectory = FindDataDirectory(full);
        if (dataDirectory is null)
        {
            return Unknown("没有找到 Unity 数据目录（*_Data/Managed），无法做加载器兼容性预检。");
        }

        evidence.Add($"数据目录：{dataDirectory}");

        string? mscorlibPath = null;
        long? mscorlibSize = null;
        var mscorlibHasGetPEKind = false;
        var managed = Path.Combine(dataDirectory, "Managed");
        var mscorlibCandidate = Path.Combine(managed, "mscorlib.dll");
        if (File.Exists(mscorlibCandidate))
        {
            mscorlibPath = mscorlibCandidate;
            mscorlibSize = new FileInfo(mscorlibCandidate).Length;
            mscorlibHasGetPEKind = ContainsAscii(mscorlibCandidate, GetPeKindBytes);
            evidence.Add($"mscorlib：{mscorlibSize.Value:N0} B，(GetPEKind：{(mscorlibHasGetPEKind ? "有" : "无")})");
        }
        else
        {
            evidence.Add("mscorlib：未找到（可能不是 Mono 布局）");
        }

        bool? inputHasAnyKeyDown = null;
        var inputModule = Path.Combine(managed, "UnityEngine.InputLegacyModule.dll");
        if (File.Exists(inputModule))
        {
            inputHasAnyKeyDown = ContainsAscii(inputModule, AnyKeyDownBytes);
            evidence.Add($"InputLegacyModule：{new FileInfo(inputModule).Length:N0} B，(anyKeyDown：{(inputHasAnyKeyDown.Value ? "有" : "无")})");
        }
        else
        {
            evidence.Add("InputLegacyModule：未找到");
        }

        if (mscorlibPath is not null && !mscorlibHasGetPEKind)
        {
            todos.Add("该游戏裁剪了 .NET 基础库（mscorlib 缺 Module.GetPEKind）：BepInEx 5 与 BepInEx 6 Mono 预加载器都会在此崩溃，MelonLoader 同版本也不可用。自动注入不可行，请提交样本或改用手工方案。");
            return new LoaderCompatReport
            {
                Verdict = VerdictUnsupported,
                DataDirectory = dataDirectory,
                MscorlibPath = mscorlibPath,
                MscorlibSizeBytes = mscorlibSize,
                MscorlibHasGetPEKind = false,
                InputHasAnyKeyDown = inputHasAnyKeyDown,
                ManualTodos = todos,
                Evidence = evidence,
            };
        }

        if (mscorlibPath is not null && inputHasAnyKeyDown == false)
        {
            todos.Add("该游戏裁剪了 Unity API Input.anyKeyDown：生成的键位读取代码可能编译失败。可先尝试安装/构建，若报 CS0117 请按 README 的人工待办降级（当前版本尚未自动降级）。");
            return new LoaderCompatReport
            {
                Verdict = VerdictDegraded,
                DataDirectory = dataDirectory,
                MscorlibPath = mscorlibPath,
                MscorlibSizeBytes = mscorlibSize,
                MscorlibHasGetPEKind = true,
                InputHasAnyKeyDown = false,
                ManualTodos = todos,
                Evidence = evidence,
            };
        }

        if (mscorlibPath is not null && mscorlibHasGetPEKind)
        {
            return new LoaderCompatReport
            {
                Verdict = VerdictSupported,
                DataDirectory = dataDirectory,
                MscorlibPath = mscorlibPath,
                MscorlibSizeBytes = mscorlibSize,
                MscorlibHasGetPEKind = true,
                InputHasAnyKeyDown = inputHasAnyKeyDown,
                ManualTodos = todos,
                Evidence = evidence,
            };
        }

        evidence.Add("未能判定加载器兼容性。");
        return new LoaderCompatReport
        {
            Verdict = VerdictUnknown,
            DataDirectory = dataDirectory,
            MscorlibPath = mscorlibPath,
            MscorlibSizeBytes = mscorlibSize,
            InputHasAnyKeyDown = inputHasAnyKeyDown,
            Evidence = evidence,
        };
    }

    private static LoaderCompatReport Unknown(string why)
        => new()
        {
            Verdict = VerdictUnknown,
            Evidence = new List<string> { why },
        };

    private static string? FindDataDirectory(string gameDirectory)
    {
        try
        {
            var withManaged = Directory
                .GetDirectories(gameDirectory, "*_Data")
                .Where(directory => Directory.Exists(Path.Combine(directory, "Managed")))
                .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (withManaged.Count == 1)
            {
                return withManaged[0];
            }

            if (withManaged.Count > 1)
            {
                // 与生成器一致的启发：优先与游戏目录同名的 *\u005fData。
                var gameName = Path.GetFileName(gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var exact = withManaged.FirstOrDefault(directory =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(directory),
                        gameName,
                        StringComparison.OrdinalIgnoreCase));
                return exact ?? withManaged[0];
            }

            var managedAtRoot = Path.Combine(gameDirectory, "Managed");
            if (Directory.Exists(managedAtRoot))
            {
                return gameDirectory;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static bool ContainsAscii(string path, byte[] needle)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (needle.Length == 0 || bytes.Length < needle.Length)
            {
                return false;
            }

            return bytes.AsSpan().IndexOf(needle) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>加载器兼容性预检结果（只读、可序列化）。</summary>
public sealed class LoaderCompatReport
{
    /// <summary>supported | degraded | unsupported | unknown。</summary>
    public string Verdict { get; init; } = LoaderCompatProbe.VerdictUnknown;

    public string? DataDirectory { get; init; }

    public string? MscorlibPath { get; init; }

    public long? MscorlibSizeBytes { get; init; }

    public bool MscorlibHasGetPEKind { get; init; }

    public bool? InputHasAnyKeyDown { get; init; }

    public List<string> ManualTodos { get; init; } = new();

    public List<string> Evidence { get; init; } = new();

    public bool IsUnsupported => string.Equals(Verdict, LoaderCompatProbe.VerdictUnsupported, StringComparison.OrdinalIgnoreCase);

    public bool IsDegraded => string.Equals(Verdict, LoaderCompatProbe.VerdictDegraded, StringComparison.OrdinalIgnoreCase);

    public bool IsSupported => string.Equals(Verdict, LoaderCompatProbe.VerdictSupported, StringComparison.OrdinalIgnoreCase);

    public string VerdictText => Verdict switch
    {
        LoaderCompatProbe.VerdictSupported => "可加载（BepInEx 预检通过）",
        LoaderCompatProbe.VerdictDegraded => "有裁剪风险（缺 Unity API，可能需降级）",
        LoaderCompatProbe.VerdictUnsupported => "不可自动注入（运行库被裁剪，BepInEx 必崩）",
        _ => "未判定",
    };
}
