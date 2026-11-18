using System.Diagnostics;
using System.Text;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// IL2CPP 游戏的目录布局与离线原料位置。
///
/// <para>
/// 与 Unity Mono 最大的不同：IL2CPP 没有 <c>Managed/</c> 程序集可读。
/// 能拿到的静态原料只有两类：
/// 1) <c>BepInEx/interop/*.dll</c>（BepInEx 6 首次启动用 Cpp2IL + Il2CppInterop 生成的托管代理程序集）——
///    这是**首选**，因为生成的插件链接的就是这一份元数据，钩子名不会对不上；
/// 2) Il2CppDumper 的 <c>dump.cs</c>（纯文本签名）——**降级**用，只在没装 BepInEx 6 时读。
/// </para>
/// </summary>
internal sealed class Il2CppLayout
{
    public string GameDirectory { get; init; } = string.Empty;

    public string? DataDirectory { get; init; }

    public string? GameAssemblyPath { get; init; }

    public string? UnityPlayerPath { get; init; }

    public string? MetadataPath { get; init; }

    /// <summary>global-metadata.dat 头部：magic 是否合法 + 原始版本号。0 表示未读到。</summary>
    public uint MetadataMagic { get; init; }

    public int MetadataVersionRaw { get; init; }

    public string? BepInExCoreDirectory { get; init; }

    public string? InteropDirectory { get; init; }

    public int InteropAssemblyCount { get; init; }

    public long InteropTotalBytes { get; init; }

    /// <summary>BepInEx interop 缓存键（assembly-hash.txt 内容）；换游戏版本会变。</summary>
    public string? InteropAssemblyHash { get; init; }

    public string? DumpPath { get; init; }

    public List<string> Notes { get; } = new();

    public bool HasGameAssembly => GameAssemblyPath is not null;

    public bool HasMetadata => MetadataPath is not null;

    public bool HasInterop => InteropAssemblyCount > 0;

    public bool HasDump => DumpPath is not null;

    /// <summary>能拿到的最高质量原料。</summary>
    public Il2CppSourceKind BestSource => HasInterop ? Il2CppSourceKind.Interop
        : HasDump ? Il2CppSourceKind.Dump
        : Il2CppSourceKind.None;

    public string Describe()
    {
        static string Fmt(long bytes) => bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "MB"
            : (bytes / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "KB";

        var sb = new StringBuilder();
        sb.Append("GameAssembly=").Append(HasGameAssembly ? "yes" : "no");
        sb.Append(", metadata=").Append(HasMetadata ? "yes(v" + MetadataVersionRaw + ")" : "no");
        if (InteropDirectory is not null)
        {
            sb.Append(", interop=").Append(InteropAssemblyCount).Append("dll/").Append(Fmt(InteropTotalBytes));
        }
        else
        {
            sb.Append(", interop=no");
        }

        sb.Append(", dump=").Append(HasDump ? Path.GetFileName(DumpPath) : "no");
        return sb.ToString();
    }
}

/// <summary>静态原料来源等级。以后加新来源时只允许往“更可信”方向排序。</summary>
internal enum Il2CppSourceKind
{
    None = 0,
    Dump = 1,
    Interop = 2,
}

/// <summary>
/// 只读探测 IL2CPP 布局。所有探测都带异常兜底：任何一个文件读不到都不能让分析整体失败。
/// </summary>
internal static class Il2CppLayoutProbe
{
    internal const uint MetadataMagic = 0xFAB11BAF;

    private static readonly string[] SkipInteropPrefixes =
    [
        "Il2Cppmscorlib", "Il2CppSystem", "mscorlib", "System", "UnityEngine", "Unity.", "Mono.",
        "Microsoft.", "Newtonsoft", "0Harmony", "BepInEx", "HarmonyLib", "SemanticVersioning",
        "AsmResolver", "AssetRipper", "StableNameDotNet", "Cpp2IL", "LibCpp2IL", "Iced",
        "Disarm", "Gee.External", "WasmDisassembler", "Il2CppInterop",
    ];

    public static Il2CppLayout Probe(string gameDirectory, IReadOnlyDictionary<string, string>? options = null)
    {
        var root = new DirectoryInfo(gameDirectory);
        var notes = new List<string>();

        var gameAssembly = Path.Combine(gameDirectory, "GameAssembly.dll");
        if (!File.Exists(gameAssembly))
        {
            gameAssembly = null;
        }

        var unityPlayer = Path.Combine(gameDirectory, "UnityPlayer.dll");
        if (!File.Exists(unityPlayer))
        {
            unityPlayer = null;
        }

        var dataDir = FindDataDirectory(root, notes);
        string? metadataPath = null;
        if (dataDir is not null)
        {
            var candidate = Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat");
            if (File.Exists(candidate))
            {
                metadataPath = candidate;
            }
            else
            {
                // 少数构建把 Metadata 目录名大小写换掉，兜底再找一层。
                var alt = Directory.Exists(Path.Combine(dataDir, "il2cpp_data"))
                    ? Directory.GetFiles(Path.Combine(dataDir, "il2cpp_data"), "global-metadata.dat", SearchOption.AllDirectories)
                    : [];
                if (alt.Length > 0)
                {
                    metadataPath = alt[0];
                }
            }
        }

        var (magic, version) = ReadMetadataHeader(metadataPath, notes);

        var bepInEx = Path.Combine(gameDirectory, "BepInEx");
        var coreDir = Directory.Exists(Path.Combine(bepInEx, "core")) ? Path.Combine(bepInEx, "core") : null;
        var interopDir = Directory.Exists(Path.Combine(bepInEx, "interop")) ? Path.Combine(bepInEx, "interop") : null;

        var interopCount = 0;
        var interopBytes = 0L;
        string? assemblyHash = null;
        if (interopDir is not null)
        {
            foreach (var file in new DirectoryInfo(interopDir).EnumerateFiles("*.dll", SearchOption.TopDirectoryOnly))
            {
                if (IsSkippedInteropAssembly(file.Name))
                {
                    continue;
                }

                interopCount++;
                interopBytes += file.Length;
            }

            var hashFile = Path.Combine(interopDir, "assembly-hash.txt");
            if (File.Exists(hashFile))
            {
                try
                {
                    assemblyHash = File.ReadAllText(hashFile).Trim();
                }
                catch (IOException)
                {
                    notes.Add("warn: assembly-hash.txt 读取失败");
                }
            }
        }
        else if (bepInEx is not null && Directory.Exists(bepInEx))
        {
            notes.Add("BepInEx 已装但缺少 interop/：说明 BepInEx 6 还没成功启动过一次（首次启动约 229s）。");
        }

        var dumpPath = ResolveDumpPath(gameDirectory, options, notes);

        if (metadataPath is null && dataDir is null)
        {
            notes.Add("warn: 未找到 *_Data/il2cpp_data/Metadata/global-metadata.dat，无法确认 IL2CPP 元数据版本。");
        }

        return new Il2CppLayout
        {
            GameDirectory = gameDirectory,
            DataDirectory = dataDir,
            GameAssemblyPath = gameAssembly,
            UnityPlayerPath = unityPlayer,
            MetadataPath = metadataPath,
            MetadataMagic = magic,
            MetadataVersionRaw = version,
            BepInExCoreDirectory = coreDir,
            InteropDirectory = interopDir,
            InteropAssemblyCount = interopCount,
            InteropTotalBytes = interopBytes,
            InteropAssemblyHash = assemblyHash,
            DumpPath = dumpPath,
        }.Also(layout =>
        {
            foreach (var note in notes)
            {
                layout.Notes.Add(note);
            }
        });
    }

    public static bool IsSkippedInteropAssembly(string fileName)
    {
        foreach (var prefix in SkipInteropPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindDataDirectory(DirectoryInfo root, List<string> notes)
    {
        if (!root.Exists)
        {
            return null;
        }

        DirectoryInfo[] candidates;
        try
        {
            candidates = root.GetDirectories("*_Data", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add("warn: 枚举 *_Data 目录失败：" + ex.Message);
            return null;
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        foreach (var dir in candidates)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "il2cpp_data")))
            {
                return dir.FullName;
            }
        }

        return candidates[0].FullName;
    }

    private static (uint Magic, int Version) ReadMetadataHeader(string? metadataPath, List<string> notes)
    {
        if (metadataPath is null)
        {
            return (0, 0);
        }

        try
        {
            using var stream = File.OpenRead(metadataPath);
            var buffer = new byte[8];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read < 8)
            {
                notes.Add("warn: global-metadata.dat 头部不足 8 字节");
                return (0, 0);
            }

            var magic = BitConverter.ToUInt32(buffer, 0);
            var version = BitConverter.ToInt32(buffer, 4);
            if (magic != MetadataMagic)
            {
                notes.Add("warn: global-metadata.dat magic=0x" + magic.ToString("X8") + " 与 IL2CPP 标准 magic 不符（可能被加密/加壳）");
            }

            return (magic, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add("warn: 读取 global-metadata.dat 头失败：" + ex.Message);
            return (0, 0);
        }
    }

    private static string? ResolveDumpPath(string gameDirectory, IReadOnlyDictionary<string, string>? options, List<string> notes)
    {
        if (options is not null)
        {
            foreach (var key in new[] { "il2cpp.dumpPath", "dumpPath", "il2cpp.dump" })
            {
                if (options.TryGetValue(key, out var configured) && !string.IsNullOrWhiteSpace(configured))
                {
                    var full = Path.IsPathRooted(configured) ? configured : Path.Combine(gameDirectory, configured);
                    if (File.Exists(full))
                    {
                        return full;
                    }

                    notes.Add("warn: profile 指定的 dump 路径不存在：" + full);
                }
            }
        }

        foreach (var relative in new[]
                 {
                     "dump.cs",
                     Path.Combine("il2cppdumper", "dump.cs"),
                     Path.Combine("Il2CppDumper", "dump.cs"),
                     Path.Combine("..", "il2cppdumper", "dump.cs"),
                 })
        {
            var candidate = Path.GetFullPath(Path.Combine(gameDirectory, relative));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>IL2CPP 元数据版本号 → 人读版本（只覆盖常见值，其余原样给出 raw）。</summary>
    public static string DescribeMetadataVersion(int raw) => raw switch
    {
        0 => "未读到",
        16 => "16 (Unity 5.3)",
        19 => "19 (Unity 5.6)",
        20 => "20 (Unity 2017)",
        21 => "21 (Unity 2018)",
        22 => "22 (Unity 2018.3)",
        23 => "23 (Unity 2018.4)",
        24 => "24 (Unity 2019.1-2019.4)",
        25 => "25 (Unity 2020.1)",
        27 => "27 (Unity 2020.2-2021.1)",
        28 => "28 (Unity 2021.2-2021.3)",
        29 => "29 (Unity 2022.1)",
        31 => "31 (Unity 2022.3+/2023)",
        _ => "未知(" + raw + ")",
    };
}

internal static class Il2CppObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
