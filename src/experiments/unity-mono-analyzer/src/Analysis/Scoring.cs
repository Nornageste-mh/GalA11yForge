using System.Globalization;

namespace UnityMonoAnalyzer.Analysis;

/// <summary>打分输入 —— 与 Cecil 解耦，便于单测。</summary>
public sealed class CandidateFeatures
{
    public string TypeFullName { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string MethodFullName { get; set; } = "";
    public int ParameterCount { get; set; }
    public bool HasStringParameter { get; set; }
    public bool IsConstructor { get; set; }

    /// <summary>类型本身命中已知框架的 id（未命中为空）。</summary>
    public string FrameworkId { get; set; } = "";
    public bool OnKnownFrameworkType { get; set; }
    public bool OnCustomDialogueType { get; set; }
    public bool FrameworkHitViaCaller { get; set; }
    public bool IsDialogueNamedType { get; set; }

    public int TextSinkCalls { get; set; }
    public List<string> SinkApis { get; set; } = new();
    public int CjkLiteralCount { get; set; }
    public int LiteralCount { get; set; }
    public int NonDialogueLiteralCount { get; set; }
    public int ConstantSinkCalls { get; set; }
    public int DynamicSinkCalls { get; set; }
    public int MaxConstantLength { get; set; }
    public int TextFieldCount { get; set; }
    public int CallerCount { get; set; }
    public bool UpstreamHasChoiceType { get; set; }
    public bool AllCallersInitLike { get; set; }
    public bool GameHasCjk { get; set; } = true;
    public bool DiscoveredBySink { get; set; } = true;
    public bool IsSemanticNamed { get; set; }
}

public sealed class ScoreResult
{
    public double Total { get; set; }
    public Dictionary<string, double> Rules { get; set; } = new();
    public List<string> Evidence { get; set; } = new();
    public string Tier { get; set; } = "excluded";
    public string Kind { get; set; } = "unknown";
    public string Confidence { get; set; } = "low";
}

/// <summary>
/// R4 打分实现（research/04-engines/REPORT.md §11.1-§11.3）。
/// S1 框架 +25 / S2 方法名语义 +20 / S3 文本写入调用者 +20 / S4 类型持文本字段 +10 /
/// S5 UI 命名 +5 / S6 CJK 字面量 +10 / S7 数据来源非硬编码 +10 / S8 多处调用或选项上游 +5
/// N1 调试命名 −20 / N2 非台词字面量 −15 / N3 仅初始化期调用 −10 / N4 固定短串 −10
/// </summary>
public static class Scoring
{
    public const double TierAThreshold = 70;
    public const double TierBThreshold = 50;
    public const double TierCThreshold = 30;

    private static readonly HashSet<string> ChoiceTokens = new(StringComparer.OrdinalIgnoreCase)
    { "choice", "choices", "select", "selection", "option", "options", "answer", "branch" };

    private static readonly HashSet<string> MenuTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "menu", "setting", "settings", "config", "configuration", "save", "load", "title", "pause",
        "gallery", "backlog", "history", "achievement", "option", "options", "system",
    };

    public static ScoreResult Score(CandidateFeatures f)
    {
        var rules = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["S1"] = 0, ["S2"] = 0, ["S3"] = 0, ["S4"] = 0, ["S5"] = 0, ["S6"] = 0, ["S7"] = 0, ["S8"] = 0,
            ["N1"] = 0, ["N2"] = 0, ["N3"] = 0, ["N4"] = 0,
        };
        var ev = new List<string>();

        // S1 已知对话框架 / 自定义 Dialog 命名 / 上游调用图命中
        string frameworkLabel = "";
        if (f.OnKnownFrameworkType)
        {
            rules["S1"] = 25;
            frameworkLabel = f.FrameworkId.Length > 0 ? TextPatterns.FrameworkDisplayName(f.FrameworkId) : "已知对话框架";
            ev.Add($"S1 +25 类型属于已知对话框架 {frameworkLabel}");
        }
        else if (f.OnCustomDialogueType)
        {
            rules["S1"] = 25;
            frameworkLabel = "自定义 *Dialog*/*Talk*";
            ev.Add("S1 +25 类型名命中自定义对话命名（Dialog/Talk/Conversation）");
        }
        else if (f.FrameworkHitViaCaller)
        {
            rules["S1"] = 25;
            frameworkLabel = "调用者命中对话框架";
            ev.Add("S1 +25 方法的调用者位于已知对话框架类型（调用图上游命中）");
        }

        // S2 方法名语义
        if (TextPatterns.HasSemanticName(f.MethodName))
        {
            rules["S2"] = 20;
            ev.Add($"S2 +20 方法名 {f.MethodName} 命中对话语义关键词");
        }

        // S3 文本写入调用者
        if (f.TextSinkCalls > 0)
        {
            rules["S3"] = 20;
            var apis = f.SinkApis.Count > 0 ? string.Join(", ", f.SinkApis.Distinct()) : "文本 setter";
            ev.Add($"S3 +20 直接调用 {apis} 共 {f.TextSinkCalls} 处");
        }

        // S4 类型持有文本字段
        if (f.TextFieldCount > 0)
        {
            rules["S4"] = 10;
            ev.Add($"S4 +10 所属类型持有 {f.TextFieldCount} 个 Text/TMP 字段");
        }

        // S5 类名 UI/Window
        if (TextPatterns.HasUiName(f.TypeName))
        {
            rules["S5"] = 5;
            ev.Add($"S5 +5 类名 {f.TypeName} 含 Window/UI/Dialog/Box/Panel 等 UI 语义");
        }

        // S6 CJK 字面量
        if (f.CjkLiteralCount > 0)
        {
            rules["S6"] = 10;
            ev.Add($"S6 +10 方法内有 {f.CjkLiteralCount} 条 CJK 字面量");
        }

        // S7 数据来源非硬编码
        bool s7 = f.TextSinkCalls > 0 && f.DynamicSinkCalls > 0;
        if (s7)
        {
            rules["S7"] = 10;
            ev.Add($"S7 +10 文本写入有 {f.DynamicSinkCalls}/{f.TextSinkCalls} 处来自参数/字段/调用（非硬编码）");
        }

        // S8 多处调用 / 选项上游
        if (f.CallerCount > 1 || f.UpstreamHasChoiceType)
        {
            rules["S8"] = 5;
            ev.Add(f.UpstreamHasChoiceType
                ? "S8 +5 调用者类型含 Choice/Select/Answer 语义"
                : $"S8 +5 被 {f.CallerCount} 个不同调用者调用");
        }

        // N1 调试/日志命名
        if (TextPatterns.HasDebugName(f.MethodName) || TextPatterns.HasDebugName(f.TypeName))
        {
            rules["N1"] = -20;
            ev.Add("N1 -20 方法名/类名含 Debug/Log/FPS/Version/Test/Stats 等调试命名");
        }

        // N2 非台词文本特征
        if (f.LiteralCount > 0 && f.CjkLiteralCount == 0)
        {
            double ratio = (double)f.NonDialogueLiteralCount / f.LiteralCount;
            if (ratio >= 0.5)
            {
                rules["N2"] = -15;
                ev.Add($"N2 -15 {f.NonDialogueLiteralCount}/{f.LiteralCount} 的字面量是路径/URL/格式串/数字/标识符（非台词样）");
            }
        }

        // N3 仅初始化期调用
        if (f.CallerCount > 0 && f.AllCallersInitLike)
        {
            rules["N3"] = -10;
            ev.Add("N3 -10 只被 Awake/Start/OnEnable/构造 等初始化期方法调用");
        }

        // N4 固定短串
        if (f.TextSinkCalls > 0 && f.ConstantSinkCalls == f.TextSinkCalls && f.MaxConstantLength < 2)
        {
            rules["N4"] = -10;
            ev.Add("N4 -10 全部文本写入实参都是长度<2 的固定短串");
        }

        double total = rules.Values.Sum();
        var tier = TierFor(total, rules["S1"]);
        var kind = KindFor(f);
        var confidence = ConfidenceFor(tier, total, f);

        return new ScoreResult
        {
            Total = total,
            Rules = rules,
            Evidence = ev,
            Tier = tier,
            Kind = kind,
            Confidence = confidence,
        };
    }

    public static string TierFor(double total, double s1)
    {
        if (total >= TierAThreshold && s1 > 0) return "A";
        if (total >= TierBThreshold) return "B";
        if (total >= TierCThreshold) return "C";
        return "excluded";
    }

    public static string KindFor(CandidateFeatures f)
    {
        var typeTokens = TextPatterns.Tokenize(f.TypeFullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var methodTokens = TextPatterns.Tokenize(f.MethodName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (TextPatterns.HasDebugName(f.MethodName) || TextPatterns.HasDebugName(f.TypeName)) return "debug";
        if (typeTokens.Overlaps(ChoiceTokens) || methodTokens.Overlaps(ChoiceTokens)) return "choice";

        bool menu = typeTokens.Overlaps(MenuTokens) || methodTokens.Overlaps(MenuTokens);
        bool dialogueSignal = f.TextSinkCalls > 0 &&
                              (rules_S1(f) || f.IsSemanticNamed || f.IsDialogueNamedType);
        if (menu && !dialogueSignal) return "menu";
        if (dialogueSignal) return "dialogue";
        return "unknown";
    }

    private static bool rules_S1(CandidateFeatures f) =>
        f.OnKnownFrameworkType || f.OnCustomDialogueType || f.FrameworkHitViaCaller;

    private static string ConfidenceFor(string tier, double total, CandidateFeatures f)
    {
        if (tier == "A")
        {
            if (total >= 85 && f.TextSinkCalls > 0 && (f.DynamicSinkCalls > 0 || f.CjkLiteralCount > 0)) return "high";
            return "medium";
        }
        if (tier == "B" && f.TextSinkCalls > 0 && total >= 60) return "medium";
        return "low";
    }

    public static string FormatScore(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
