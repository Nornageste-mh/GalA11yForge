using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>
/// Ghidra 后端配置。全部来自 profile.options（--option ghidra.* 合并后的结果）与默认值；
/// 工具本身不读用户目录里的隐式配置。
/// </summary>
public sealed class GhidraBackendOptions
{
    public const string PathKey = "ghidra.path";
    public const string ExecutableKey = "ghidra.executablePath";
    public const string EnabledKey = "ghidra.enabled";
    public const string TimeoutKey = "ghidra.timeoutSeconds";
    public const string AnalysisTimeoutKey = "ghidra.analysisTimeoutSeconds";
    public const string MaxCpuKey = "ghidra.maxCpu";
    public const string MaxMemoryKey = "ghidra.maxMem";
    public const string KeepProjectKey = "ghidra.keepProject";

    private static readonly Regex MemoryPattern = new(@"^[1-9]\d{0,4}[mMgG]$", RegexOptions.Compiled);

    /// <summary>子进程硬超时默认值（秒）。2026-11-14 实测：SenrenBanka.exe 冷跑 240s 仍停在分析阶段，故默认 600s。</summary>
    private const int DefaultTimeoutSeconds = 600;

    private const int DefaultAnalysisTimeoutSeconds = 900;

    /// <summary>工作区根目录；Ghidra 的工程、脚本、日志、派生 JSON 只能写在这下面。</summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>--option ghidra.path 的原始值（可为目录或 analyzeHeadless.bat）。</summary>
    public string? GhidraPath { get; init; }

    /// <summary>
    /// --option ghidra.executablePath：显式指定要分析的可执行文件（多子游戏合集/主程序不在根层时用）。
    /// 不存在则忽略并给出 warning，回落到自动选择。
    /// </summary>
    public string? ExecutablePath { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>子进程硬超时（秒）。2026-11-14 实测：SenrenBanka.exe 冷跑 240s 仍停在分析阶段，故默认 600s。</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary>传给 -analysisTimeoutPerFile 的 Ghidra 内部分析上限（秒）。</summary>
    public int AnalysisTimeoutSeconds { get; init; } = DefaultAnalysisTimeoutSeconds;

    public int MaxCpu { get; init; } = 4;

    /// <summary>GHIDRA_HEADLESS_MAXMEM；null 表示用 Ghidra 默认（2G）。</summary>
    public string? MaxMemory { get; init; }

    /// <summary>保留 Ghidra 工程目录便于复查（默认 false，-deleteProject）。</summary>
    public bool KeepProject { get; init; }

    /// <summary>配置解析中发现的问题；不阻断，但会写进证据与 unverified。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static GhidraBackendOptions FromProfile(
        IReadOnlyDictionary<string, JsonElement>? options,
        string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var warnings = new List<string>();
        var values = options ?? new Dictionary<string, JsonElement>();

        var enabled = ReadBool(values, EnabledKey, defaultValue: true, warnings);
        var path = ReadString(values, PathKey, warnings);
        var executablePath = ReadString(values, ExecutableKey, warnings);
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(executablePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                warnings.Add($"{ExecutableKey}={executablePath} 不是合法路径，已忽略（将自动选择可执行文件）。");
                fullPath = "";
            }

            if (fullPath.Length > 0 && File.Exists(fullPath))
            {
                executablePath = fullPath;
            }
            else
            {
                if (fullPath.Length > 0)
                {
                    warnings.Add($"{ExecutableKey}={executablePath} 不存在，已忽略（将自动选择可执行文件）。");
                }

                executablePath = null;
            }
        }
        var timeout = Clamp(ReadInt(values, TimeoutKey, DefaultTimeoutSeconds, warnings), 30, 3600);
        var analysisTimeout = Clamp(ReadInt(values, AnalysisTimeoutKey, DefaultAnalysisTimeoutSeconds, warnings), 60, 7200);
        var maxCpu = Clamp(ReadInt(values, MaxCpuKey, 4, warnings), 1, 64);
        var memory = ReadString(values, MaxMemoryKey, warnings);
        if (memory is not null && !MemoryPattern.IsMatch(memory))
        {
            warnings.Add($"{MaxMemoryKey}={memory} 不是合法内存值（示例 2G / 4096M），已忽略。");
            memory = null;
        }

        var keepProject = ReadBool(values, KeepProjectKey, defaultValue: false, warnings);

        return new GhidraBackendOptions
        {
            WorkspaceRoot = Path.GetFullPath(workspaceRoot),
            GhidraPath = path,
            ExecutablePath = executablePath,
            Enabled = enabled,
            TimeoutSeconds = timeout,
            AnalysisTimeoutSeconds = analysisTimeout,
            MaxCpu = maxCpu,
            MaxMemory = memory,
            KeepProject = keepProject,
            Warnings = warnings,
        };
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> values, string key, List<string> warnings)
    {
        if (!TryGet(values, key, out var element))
        {
            return null;
        }

        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            warnings.Add($"{key} 不是非空字符串，已忽略。");
            return null;
        }

        return text.Trim();
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback, List<string> warnings)
    {
        if (!TryGet(values, key, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        warnings.Add($"{key}={element.GetRawText()} 不是整数，使用默认值 {fallback}。");
        return fallback;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> values, string key, bool defaultValue, List<string> warnings)
    {
        if (!TryGet(values, key, out var element))
        {
            return defaultValue;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        warnings.Add($"{key}={element.GetRawText()} 不是布尔值，使用默认值 {defaultValue}。");
        return defaultValue;
    }

    private static bool TryGet(IReadOnlyDictionary<string, JsonElement> values, string key, out JsonElement element)
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                element = pair.Value;
                return true;
            }
        }

        element = default;
        return false;
    }
}
