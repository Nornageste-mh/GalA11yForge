using System.Text;
using System.Text.Json;
using GalA11yForge.Detection.Fingerprints;

namespace GalA11yForge.Detection;

/// <summary>
/// 指纹库加载与校验。
/// 规则：
/// 1) 只接受 schema = "gala11y.engine-fingerprints/1"（升级必须显式改代码，避免悄悄读错库）；
/// 2) JSON 必须严格合法（不允许注释、尾逗号）；
/// 3) 必填字段、规则引用的引擎、条件参数、消歧可达性全查；
/// 4) 未知字段保留在 ExtensionData 里，不丢上游数据；
/// 5) 任何问题都抛 <see cref="FingerprintFormatException"/>，不静默降级。
/// </summary>
public static class FingerprintLoader
{
    /// <summary>当前唯一支持的 schema 标识。</summary>
    public const string ExpectedSchema = "gala11y.engine-fingerprints/1";

    /// <summary>指纹库文件名。</summary>
    public const string FileName = "engine-fingerprints.json";

    /// <summary>程序集内嵌资源的逻辑名。</summary>
    public const string EmbeddedResourceName = "GalA11yForge.Detection.Fingerprints.engine-fingerprints.json";

    /// <summary>本层内置的 7 种条件原语（库里的 condition_types 与之对齐）。</summary>
    internal static readonly string[] DefaultConditionTypes =
    {
        "file_glob", "file_any", "dir_exists", "magic", "pe_string", "text", "count",
    };

    /// <summary>只认 ascii / utf16 / both 三种 pe_string 编码模式（与参考实现 detect.py 一致）。</summary>
    private static readonly string[] PeStringModes = { "ascii", "utf16", "both" };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// 随程序集输出的默认指纹库路径（…/Fingerprints/engine-fingerprints.json）。
    /// 磁盘上存在则优先用它，方便不重新编译就换库。
    /// </summary>
    public static string DefaultFingerprintPath => Path.Combine(AppContext.BaseDirectory, "Fingerprints", FileName);

    /// <summary>从磁盘文件加载并校验。</summary>
    public static FingerprintDatabase Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("指纹库路径不能为空", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"指纹库文件不存在：{fullPath}", fullPath);
        }

        string json;
        try
        {
            json = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FingerprintFormatException(fullPath, $"读取失败：{ex.Message}", ex);
        }

        return LoadFromJson(json, fullPath);
    }

    /// <summary>从 JSON 字符串加载并校验；sourceLabel 用于错误定位。</summary>
    public static FingerprintDatabase LoadFromJson(string json, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FingerprintFormatException(sourceLabel, "JSON 为空");
        }

        FingerprintDatabase? database;
        try
        {
            database = JsonSerializer.Deserialize<FingerprintDatabase>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is null ? "?" : (ex.LineNumber + 1).ToString();
            var pos = ex.BytePositionInLine is null ? "?" : (ex.BytePositionInLine + 1).ToString();
            throw new FingerprintFormatException(
                sourceLabel,
                $"JSON 解析失败（第 {line} 行，第 {pos} 字节）：{ex.Message}",
                ex);
        }

        if (database is null)
        {
            throw new FingerprintFormatException(sourceLabel, "JSON 解析结果为 null");
        }

        Normalize(database);
        var problems = Validate(database);
        if (problems.Count > 0)
        {
            throw new FingerprintFormatException(sourceLabel, problems);
        }

        return database;
    }

    /// <summary>
    /// 加载默认库：优先磁盘（随程序集输出的 Fingerprints/engine-fingerprints.json），
    /// 否则回退到程序集内嵌资源。两处都没有时抛异常，而不是返回空库。
    /// </summary>
    public static FingerprintDatabase LoadDefault()
    {
        var path = DefaultFingerprintPath;
        if (File.Exists(path))
        {
            return Load(path);
        }

        var assembly = typeof(FingerprintLoader).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(FileName, StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new FingerprintFormatException(
                "(assembly)",
                $"未找到指纹库：磁盘路径 {path} 不存在，且程序集内没有嵌入 {FileName}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FingerprintFormatException(resourceName, "无法打开嵌入式指纹库流");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return LoadFromJson(reader.ReadToEnd(), $"embedded:{resourceName}");
    }

    private static void Normalize(FingerprintDatabase database)
    {
        database.Engines ??= new List<EngineFingerprint>();
        database.Rules ??= new List<DetectionRule>();
        database.Disambiguation ??= new List<DisambiguationRule>();
        database.ConditionTypes ??= new List<string>();
        database.DifficultyLegend ??= new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var engine in database.Engines)
        {
            if (engine is null)
            {
                continue;
            }

            engine.Fingerprints ??= new EngineFingerprintFiles();
            engine.TextStorage ??= new List<string>();
            engine.HookPoints ??= new List<string>();
            engine.Tools ??= new List<string>();
            engine.Evidence ??= new List<string>();
        }

        foreach (var rule in database.Rules)
        {
            if (rule is null)
            {
                continue;
            }

            rule.All ??= new List<FingerprintCondition>();
            rule.Any ??= new List<FingerprintCondition>();
            NormalizeConditions(rule.All);
            NormalizeConditions(rule.Any);
        }

        foreach (var disambiguation in database.Disambiguation)
        {
            if (disambiguation is not null)
            {
                disambiguation.When ??= new List<string>();
            }
        }
    }

    private static void NormalizeConditions(List<FingerprintCondition> conditions)
    {
        for (var i = 0; i < conditions.Count; i++)
        {
            conditions[i] ??= new FingerprintCondition();
        }
    }

    /// <summary>逐项校验，返回全部问题（空表示通过）。</summary>
    internal static List<string> Validate(FingerprintDatabase database)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(database.Schema))
        {
            problems.Add("缺少顶层必填字段 schema");
        }
        else if (!string.Equals(database.Schema, ExpectedSchema, StringComparison.Ordinal))
        {
            problems.Add($"schema 不匹配：期望 \"{ExpectedSchema}\"，实际 \"{database.Schema}\"（升级 schema 需同步改 FingerprintLoader.ExpectedSchema）");
        }

        if (database.ConditionTypes.Count == 0)
        {
            problems.Add("condition_types 为空：无法确定支持的条件原语");
        }

        if (database.Engines.Count == 0)
        {
            problems.Add("engines 为空");
        }

        if (database.Rules.Count == 0)
        {
            problems.Add("rules 为空");
        }

        var engineIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < database.Engines.Count; i++)
        {
            var engine = database.Engines[i];
            if (engine is null)
            {
                problems.Add($"engines[{i}] 为 null");
                continue;
            }

            var at = $"engines[{i}]";
            if (string.IsNullOrWhiteSpace(engine.Id))
            {
                problems.Add($"{at}.id 为空");
                continue;
            }

            at = $"{at}({engine.Id})";
            if (!engineIds.Add(engine.Id))
            {
                problems.Add($"{at}.id 重复");
            }

            if (string.IsNullOrWhiteSpace(engine.Name))
            {
                problems.Add($"{at}.name 为空");
            }

            if (string.IsNullOrWhiteSpace(engine.Family))
            {
                problems.Add($"{at}.family 为空");
            }

            if (string.IsNullOrWhiteSpace(engine.Difficulty))
            {
                problems.Add($"{at}.difficulty 为空");
            }
            else if (database.DifficultyLegend.Count > 0 && !database.DifficultyLegend.ContainsKey(engine.Difficulty))
            {
                problems.Add($"{at}.difficulty=\"{engine.Difficulty}\" 不在 difficulty_legend 中");
            }
        }

        var knownTypes = new HashSet<string>(
            database.ConditionTypes.Count > 0 ? database.ConditionTypes : DefaultConditionTypes,
            StringComparer.OrdinalIgnoreCase);

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < database.Rules.Count; i++)
        {
            var rule = database.Rules[i];
            if (rule is null)
            {
                problems.Add($"rules[{i}] 为 null");
                continue;
            }

            var at = $"rules[{i}]";
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                problems.Add($"{at}.id 为空");
            }
            else
            {
                at = $"{at}({rule.Id})";
                if (!ruleIds.Add(rule.Id))
                {
                    problems.Add($"{at}.id 重复");
                }
            }

            if (string.IsNullOrWhiteSpace(rule.Engine))
            {
                problems.Add($"{at}.engine 为空");
            }
            else if (!engineIds.Contains(rule.Engine))
            {
                problems.Add($"{at}.engine=\"{rule.Engine}\" 在 engines 中不存在");
            }

            if (rule.Score is < 0 or > 100)
            {
                problems.Add($"{at}.score={rule.Score} 超出 0..100");
            }

            var hasAll = rule.All.Count > 0;
            var hasAny = rule.Any.Count > 0;
            if (hasAll == hasAny)
            {
                problems.Add($"{at} 必须且只能给出 all 或 any 之一");
                continue;
            }

            var conditions = hasAll ? rule.All : rule.Any;
            var kind = hasAll ? "all" : "any";
            for (var c = 0; c < conditions.Count; c++)
            {
                ValidateCondition(conditions[c], $"{at}.{kind}[{c}]", knownTypes, problems);
            }
        }

        for (var i = 0; i < database.Disambiguation.Count; i++)
        {
            var item = database.Disambiguation[i];
            if (item is null)
            {
                problems.Add($"disambiguation[{i}] 为 null");
                continue;
            }

            var at = $"disambiguation[{i}]";
            if (item.When.Count == 0)
            {
                problems.Add($"{at}.when 为空");
            }
            else
            {
                foreach (var when in item.When)
                {
                    if (string.IsNullOrWhiteSpace(when))
                    {
                        problems.Add($"{at}.when 含空项");
                    }
                    else if (!engineIds.Contains(when))
                    {
                        problems.Add($"{at}.when 含未知引擎 \"{when}\"");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(item.Resolve))
            {
                problems.Add($"{at}.resolve 为空");
            }
            else
            {
                if (!engineIds.Contains(item.Resolve))
                {
                    problems.Add($"{at}.resolve=\"{item.Resolve}\" 在 engines 中不存在");
                }

                if (!item.When.Contains(item.Resolve, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{at}.resolve 必须属于 when，否则消歧结果不可达");
                }
            }

            if (string.IsNullOrWhiteSpace(item.Why))
            {
                problems.Add($"{at}.why 为空（消歧必须给出可追溯理由）");
            }
        }

        return problems;
    }

    private static void ValidateCondition(
        FingerprintCondition condition,
        string at,
        HashSet<string> knownTypes,
        List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(condition.Type))
        {
            problems.Add($"{at}.type 为空");
            return;
        }

        if (!knownTypes.Contains(condition.Type))
        {
            problems.Add($"{at}.type=\"{condition.Type}\" 不在 condition_types 中");
            return;
        }

        switch (condition.Type.ToLowerInvariant())
        {
            case "file_glob":
                RequireGlob(condition, at, problems);
                break;

            case "file_any":
                if (condition.Globs is null || condition.Globs.Count == 0)
                {
                    problems.Add($"{at}.globs 为空");
                }
                else if (condition.Globs.Any(string.IsNullOrWhiteSpace))
                {
                    problems.Add($"{at}.globs 含空项");
                }

                break;

            case "dir_exists":
                if (string.IsNullOrWhiteSpace(condition.Dir))
                {
                    problems.Add($"{at}.dir 为空");
                }

                break;

            case "count":
                RequireGlob(condition, at, problems);
                if (condition.Min is null)
                {
                    problems.Add($"{at}.min 缺失");
                }
                else if (condition.Min < 0)
                {
                    problems.Add($"{at}.min={condition.Min} 为负");
                }

                break;

            case "magic":
                RequireGlob(condition, at, problems);
                if (condition.Offset is null)
                {
                    problems.Add($"{at}.offset 缺失");
                }
                else if (condition.Offset < 0)
                {
                    problems.Add($"{at}.offset={condition.Offset} 为负");
                }

                ValidateHex(condition, at, problems);
                break;

            case "pe_string":
                RequireGlob(condition, at, problems);
                if (string.IsNullOrEmpty(condition.Contains))
                {
                    problems.Add($"{at}.contains 为空");
                }

                if (string.IsNullOrWhiteSpace(condition.Mode))
                {
                    problems.Add($"{at}.mode 缺失");
                }
                else if (!PeStringModes.Contains(condition.Mode, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"{at}.mode=\"{condition.Mode}\" 不在 ascii/utf16/both 中");
                }

                break;

            case "text":
                RequireGlob(condition, at, problems);
                if (string.IsNullOrEmpty(condition.Contains))
                {
                    problems.Add($"{at}.contains 为空");
                }

                break;

            default:
                problems.Add($"{at}.type=\"{condition.Type}\" 未实现（本层只实现 7 种条件原语）");
                break;
        }
    }

    private static void RequireGlob(FingerprintCondition condition, string at, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(condition.Glob))
        {
            problems.Add($"{at}.glob 为空");
        }
    }

    private static void ValidateHex(FingerprintCondition condition, string at, List<string> problems)
    {
        if (condition.Hex is null || condition.Hex.Count == 0)
        {
            problems.Add($"{at}.hex 为空");
            return;
        }

        for (var i = 0; i < condition.Hex.Count; i++)
        {
            var hex = condition.Hex[i];
            if (string.IsNullOrWhiteSpace(hex))
            {
                problems.Add($"{at}.hex[{i}] 为空");
            }
            else if (hex.Length % 2 != 0)
            {
                problems.Add($"{at}.hex[{i}]=\"{hex}\" 长度为奇数（应按字节成对）");
            }
            else if (!hex.All(Uri.IsHexDigit))
            {
                problems.Add($"{at}.hex[{i}]=\"{hex}\" 含非十六进制字符");
            }
        }
    }
}
