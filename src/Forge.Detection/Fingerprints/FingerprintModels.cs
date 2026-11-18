using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalA11yForge.Detection.Fingerprints;

/// <summary>
/// 引擎指纹库根对象，对应 research/16-engines-more/evidence/engine-fingerprints.json。
/// 未知字段一律收进 <see cref="ExtensionData"/>，保证上游加字段时本层不丢数据。
/// 字段名与 JSON 逐字对应（system.text.json 默认区分大小写），因此这里全部显式标注。
/// </summary>
public sealed class FingerprintDatabase
{
    /// <summary>schema 标识；本层只接受 "gala11y.engine-fingerprints/1"。</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = string.Empty;

    [JsonPropertyName("generated_at")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>难度等级说明（A / B / B+ / C / C- / D）。</summary>
    [JsonPropertyName("difficulty_legend")]
    public Dictionary<string, string> DifficultyLegend { get; set; } = new(StringComparer.Ordinal);

    /// <summary>本层实现的条件原语名，当前为 7 种。</summary>
    [JsonPropertyName("condition_types")]
    public List<string> ConditionTypes { get; set; } = new();

    /// <summary>评分口径说明（原样保留，便于报告引用）。</summary>
    [JsonPropertyName("scoring")]
    public string? Scoring { get; set; }

    [JsonPropertyName("disambiguation")]
    public List<DisambiguationRule> Disambiguation { get; set; } = new();

    [JsonPropertyName("engines")]
    public List<EngineFingerprint> Engines { get; set; } = new();

    [JsonPropertyName("rules")]
    public List<DetectionRule> Rules { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>按引擎 id 查元数据（不区分大小写）；库已校验过 id 唯一。</summary>
    public EngineFingerprint? FindEngine(string engineId) =>
        Engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>单个引擎的档案：指纹线索 + 文本存储 + 钩取点 + 证据链接（全部来自研究层）。</summary>
public sealed class EngineFingerprint
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    /// <summary>难度档位，取值见 difficulty_legend。</summary>
    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = string.Empty;

    [JsonPropertyName("needs_sample")]
    public bool NeedsSample { get; set; }

    /// <summary>是否有本机样本实测过；false 表示只有纸面规则。</summary>
    [JsonPropertyName("sample_verified")]
    public bool SampleVerified { get; set; }

    [JsonPropertyName("fingerprints")]
    public EngineFingerprintFiles Fingerprints { get; set; } = new();

    [JsonPropertyName("text_storage")]
    public List<string> TextStorage { get; set; } = new();

    [JsonPropertyName("hook_points")]
    public List<string> HookPoints { get; set; } = new();

    [JsonPropertyName("tools")]
    public List<string> Tools { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>引擎档案里的指纹清单（人读用；判定以 rules 为准）。</summary>
public sealed class EngineFingerprintFiles
{
    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = new();

    [JsonPropertyName("dirs")]
    public List<string> Dirs { get; set; } = new();

    // 注意：真实 JSON 里 magic / text 不是字符串数组，而是带 glob 的对象，
    // 这里按实际 schema 建模（文件头规则见 research/16-engines-more/evidence/engine-fingerprints.json）。
    [JsonPropertyName("magic")]
    public List<FingerprintMagicSpec> Magic { get; set; } = new();

    [JsonPropertyName("pe_strings")]
    public List<string> PeStrings { get; set; } = new();

    [JsonPropertyName("text")]
    public List<FingerprintTextSpec> Text { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>引擎档案里的一条文件头指纹（人读用；判定以 rules 的 magic 条件为准）。</summary>
public sealed class FingerprintMagicSpec
{
    [JsonPropertyName("glob")]
    public string Glob { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("hex")]
    public List<string> Hex { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>引擎档案里的一条文本指纹（人读用；判定以 rules 的 text 条件为准）。</summary>
public sealed class FingerprintTextSpec
{
    [JsonPropertyName("glob")]
    public string Glob { get; set; } = string.Empty;

    [JsonPropertyName("contains")]
    public string Contains { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>一条判定规则：id/engine/score + all（全部满足）或 any（任一满足）。</summary>
public sealed class DetectionRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("engine")]
    public string Engine { get; set; } = string.Empty;

    /// <summary>0..100；本层把 score/100 直接作为 confidence（0..1）。</summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("all")]
    public List<FingerprintCondition> All { get; set; } = new();

    [JsonPropertyName("any")]
    public List<FingerprintCondition> Any { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>true 表示 all 语义（全部条件成立）；false 表示 any 语义。</summary>
    public bool MatchesAll => All.Count > 0;
}

/// <summary>消歧规则：when 里的引擎同时命中时，保留 resolve，其余压低。</summary>
public sealed class DisambiguationRule
{
    [JsonPropertyName("when")]
    public List<string> When { get; set; } = new();

    [JsonPropertyName("resolve")]
    public string Resolve { get; set; } = string.Empty;

    [JsonPropertyName("why")]
    public string? Why { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 一种条件原语。7 种原语共用这一个形状，按 type 取用对应字段；
/// 多出来的未知字段照样进 ExtensionData。校验见 FingerprintLoader.ValidateCondition。
/// </summary>
public sealed class FingerprintCondition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>file_glob / count / magic / pe_string / text 使用的单个 glob。</summary>
    [JsonPropertyName("glob")]
    public string? Glob { get; set; }

    /// <summary>file_any 使用的多个 glob。</summary>
    [JsonPropertyName("globs")]
    public List<string>? Globs { get; set; }

    /// <summary>dir_exists 使用的目录相对路径（'/' 分隔）。</summary>
    [JsonPropertyName("dir")]
    public string? Dir { get; set; }

    /// <summary>magic 的读取偏移（字节）。</summary>
    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    /// <summary>magic 的期望字节序列（十六进制字符串，可多个，任一命中即可）。</summary>
    [JsonPropertyName("hex")]
    public List<string>? Hex { get; set; }

    /// <summary>pe_string / text 的期望内容。</summary>
    [JsonPropertyName("contains")]
    public string? Contains { get; set; }

    /// <summary>pe_string 的编码模式：ascii / utf16 / both。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>count 的最小数量。</summary>
    [JsonPropertyName("min")]
    public int? Min { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
