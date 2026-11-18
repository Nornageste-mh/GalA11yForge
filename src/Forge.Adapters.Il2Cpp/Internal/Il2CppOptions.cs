using System.Globalization;
using System.Text.Json;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 把 <see cref="ProfileDocument.Options"/>（JSON 值）收敛成适配器内部用的字符串选项表。
/// 只读取与本适配器相关的键，未知键忽略；任何类型错误都退化为默认值，绝不抛异常。
/// </summary>
internal sealed class Il2CppOptions
{
    internal const string KeyPrefix = "il2cpp.";

    private readonly Dictionary<string, string> _values;

    private Il2CppOptions(Dictionary<string, string> values, string? slug)
    {
        _values = values;
        Slug = slug;
    }

    /// <summary>游戏 slug（来自 profile 或上下文），用于生成插件命名空间与工程目录名。</summary>
    public string? Slug { get; }

    /// <summary>dump.cs 路径（相对游戏目录或绝对路径）。</summary>
    public string? DumpPath => Get("dumpPath") ?? Get("dump");

    /// <summary>
    /// 既没有 interop 也没有 dump.cs 时，是否自动调用 Il2CppDumper 生成 dump.cs。默认开启；
    /// 关闭后行为回到 0.2.10（直接报 E_DEPENDENCY_MISSING）。
    /// </summary>
    public bool AutoDump => GetBool("autoDump", true);

    /// <summary>Il2CppDumper.exe 的显式路径（文件或目录）；为空时按 re.il2cppdumper.path / 工作区 / 程序旁顺序找。</summary>
    public string? DumperPath => Get("dumperPath") ?? Get("dumper");

    /// <summary>R18 这类运行时探针证据 CSV 的路径；给了才会启用运行时加分/降权。</summary>
    public string? RuntimeEvidencePath => Get("runtimeEvidence") ?? Get("evidence");

    public string Language => NormalizeLanguage(Get("language") ?? Get("lang") ?? "sc");

    /// <summary>候选最低分（0–1），低于它直接丢弃。默认 0.30，与 Unity Mono 对齐。</summary>
    public double MinConfidence => GetDouble("minConfidence", 0.30, 0.0, 1.0);

    /// <summary>最多产出多少个候选。默认 40（IL2CPP 候选噪声比 Mono 大，略低于 50）。</summary>
    public int MaxCandidates => GetInt("maxCandidates", 40, 1, 200);

    /// <summary>
    /// 是否允许把基础控件（<c>TMP_Text.set_text</c> / <c>UnityEngine.UI.Text.set_text</c>）
    /// 当作“诊断通道”打进补丁。默认关闭：R18 实测 <c>UnityEngine.UI.Text</c> 触发 5785 次，
    /// 会把 SAVE/LOAD/全屏之类 UI 文案一起读出来。
    /// </summary>
    public bool AllowBaseWidgetHooks => GetBool("allowBaseWidgetHooks", false);

    /// <summary>生成工程里是否内联 Tolk 朗读（默认 true）；false 时只写日志，供纯诊断构建用。</summary>
    public bool EnableSpeech => GetBool("speech", true);

    /// <summary>生成的插件工程目录名（默认 <c>{slug}.il2cpp.a11y</c>）。</summary>
    public string ProjectName => Get("projectName") ?? ((Slug ?? "game") + ".il2cpp.a11y");

    public static Il2CppOptions From(GameContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = context.Profile?.Options;
        if (options is not null)
        {
            foreach (var pair in options)
            {
                var text = pair.Value.ValueKind switch
                {
                    JsonValueKind.String => pair.Value.GetString(),
                    JsonValueKind.Number => pair.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => pair.Value.GetRawText(),
                };

                if (!string.IsNullOrWhiteSpace(text))
                {
                    values[pair.Key] = text;
                }
            }
        }

        var slug = context.Slug;
        if (string.IsNullOrWhiteSpace(slug) && values.TryGetValue("slug", out var profileSlug))
        {
            slug = profileSlug;
        }

        return new Il2CppOptions(values, string.IsNullOrWhiteSpace(slug) ? null : slug.Trim());
    }

    /// <summary>探测布局需要的选项视图（只读字符串字典）。</summary>
    public IReadOnlyDictionary<string, string> AsDictionary() => _values;

    private string? Get(string name)
    {
        foreach (var key in new[] { KeyPrefix + name, name })
        {
            if (_values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private double GetDouble(string name, double fallback, double min, double max)
    {
        var raw = Get(name);
        if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private int GetInt(string name, int fallback, int min, int max)
    {
        var raw = Get(name);
        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private bool GetBool(string name, bool fallback)
    {
        var raw = Get(name);
        if (raw is null)
        {
            return fallback;
        }

        return raw.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static string NormalizeLanguage(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sc" or "zh-cn" or "zh_hans" or "chs" or "simplified" => "sc",
        "tc" or "zh-tw" or "zh_hant" or "cht" => "tc",
        "ja" or "jp" or "japanese" => "ja",
        "en" or "english" => "en",
        var other => other,
    };
}
