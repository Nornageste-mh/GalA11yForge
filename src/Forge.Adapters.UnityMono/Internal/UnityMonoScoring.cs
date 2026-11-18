using System.Text.Json;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>language 配置解析（docs/14：默认 sc，可显式 sc|tc|ja|en）。</summary>
internal sealed class UnityMonoLanguage
{
    public const string DefaultCode = "sc";

    public required string Code { get; init; }

    /// <summary>配置来源（profile 键名或 default）。</summary>
    public required string Source { get; init; }

    /// <summary>显式给了值但不认识时为 false。</summary>
    public bool Recognized { get; init; } = true;

    public string? Requested { get; init; }

    public bool WasConfigured => !string.Equals(Source, "default", StringComparison.Ordinal);

    public static UnityMonoLanguage Resolve(ProfileDocument? profile)
    {
        if (profile?.Options is not { Count: > 0 } options)
        {
            return new UnityMonoLanguage { Code = DefaultCode, Source = "default" };
        }

        string[] keys = { "language", "lang", "unity_mono.language", "a11y.language" };
        foreach (var key in keys)
        {
            if (!options.TryGetValue(key, out var element))
            {
                continue;
            }

            var raw = ReadString(element);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var requested = raw.Trim();
            var mapped = Map(requested);
            return new UnityMonoLanguage
            {
                Code = mapped ?? DefaultCode,
                Source = key,
                Recognized = mapped is not null,
                Requested = requested,
            };
        }

        return new UnityMonoLanguage { Code = DefaultCode, Source = "default" };
    }

    private static string? ReadString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "language", "code", "value", "lang" })
            {
                if (element.TryGetProperty(property, out var nested) && nested.ValueKind == JsonValueKind.String)
                {
                    return nested.GetString();
                }
            }
        }

        return null;
    }

    private static string? Map(string value) => value.ToLowerInvariant() switch
    {
        "sc" or "zh-cn" or "zh-hans" or "zh_cn" or "chs" or "simplified" or "zh" => "sc",
        "tc" or "zh-tw" or "zh-hant" or "zh_tw" or "cht" or "traditional" => "tc",
        "ja" or "jp" or "jpn" or "japanese" => "ja",
        "en" or "us" or "eng" or "english" => "en",
        _ => null,
    };
}

/// <summary>
/// 具体规则打分。S1–S9 为加分，N1–N4 为减分；最终 clamp 到 0–100。
/// 规则来源：src/experiments/unity-mono-analyzer/src/Analysis/Scoring.cs（W2-H 复制并新增 S9/类型级管线）。
/// </summary>
internal static class UnityMonoScoring
{
    public const string RuleVersion = "w2h-1";

    public static UnityMonoScore? Evaluate(UnityMonoMethodFacts facts)
    {
        if (IsPlumbing(facts))
        {
            return null;
        }

        var evidence = new List<string>();
        var s1 = 0;
        var s2 = 0;
        var s3 = 0;
        var s4 = 0;
        var s5 = 0;
        var s6 = 0;
        var s7 = 0;
        var s8 = 0;
        var s9 = 0;
        var s10 = 0;

        if (facts.FrameworkId is not null)
        {
            s1 = 25;
            evidence.Add($"S1+25：命中已知对话框架 {facts.FrameworkId}（类型 {facts.TypeFullName}）。");
        }
        else if (facts.IsCustomDialogueType)
        {
            s1 = 25;
            evidence.Add($"S1+25：类型路径含自定义对话痕迹（dialog/talk/conversation）：{facts.TypeFullName}。");
        }
        else if (facts.TypeLevelPipeline)
        {
            s1 = 25;
            evidence.Add(
                $"S1+25：类型级对话管线（TMP 字段 {string.Join("/", facts.TextFieldNames)} + 直接文本写入 + string 打字机入口）：{facts.TypeFullName}。");
        }

        if (facts.IsSemanticName)
        {
            s2 = 20;
            evidence.Add($"S2+20：方法名含对话语义 token：{facts.MethodName}。");
        }
        else if (facts.TypeLevelPipeline && MonoTextPatterns.HasTextToken(facts.MethodName))
        {
            s2 = 20;
            evidence.Add($"S2+20：类型级对话管线内方法名含 text token：{facts.MethodName}。");
        }

        if (facts.HasDirectTextSink)
        {
            s3 = 20;
            var sink = facts.TextSinkCalls[0];
            evidence.Add(
                $"S3+20：直接写文本组件 {sink.Api}（IL_{sink.Offset:X4}，实参来源 {sink.ArgumentSource}）。");
        }

        if (facts.TextFieldCount > 0)
        {
            s4 = 10;
            evidence.Add($"S4+10：声明类型持有文本组件字段 {string.Join("/", facts.TextFieldNames)}。");
        }

        if (facts.IsUiName || MonoTextPatterns.HasUiName(facts.MethodName))
        {
            s5 = 5;
            evidence.Add($"S5+5：UI/Text 命名：{facts.TypeName}::{facts.MethodName}。");
        }

        if (facts.CjkLiteralCount > 0)
        {
            s6 = 10;
            evidence.Add($"S6+10：方法体含 {facts.CjkLiteralCount} 条 CJK 字面量（只计数，不读台词原文）。");
        }

        if (facts.HasDynamicTextSink)
        {
            s7 = 10;
            evidence.Add("S7+10：文本 sink 的实参来自参数/字段/本地变量（动态文本）。");
        }
        else if (facts.HasTypewriterSink && facts.HasStringParameter)
        {
            s7 = 10;
            evidence.Add("S7+10：打字机 sink 的文本来自 string 参数。");
        }

        if (facts.CallerCount > 1 || facts.AnyCallerChoiceLike)
        {
            s8 = 5;
            evidence.Add(
                $"S8+5：程序集内调用者 {facts.CallerCount} 个{(facts.AnyCallerChoiceLike ? "（含 choice 调用者）" : string.Empty)}。");
        }

        if (facts.HasStringParameter && facts.HasTypewriterSink)
        {
            s9 = 20;
            var hit = facts.TypewriterSinkCalls[0];
            evidence.Add(
                $"S9+20：参数含 string 且调用打字机/Tween sink {hit.Api}（IL_{hit.Offset:X4}，实参来源 {hit.ArgumentSource}）。");
        }

        if (MonoTextPatterns.TryUtagePropertyChain(facts.TypeFullName, facts.MethodName, out var utageChain))
        {
            s10 = 55;
            evidence.Add(
                $"S10+55：Utage（宴）已知正文入口，文本在实例属性链：{MonoTextPatterns.DescribePropertyChain(utageChain)}（0.2.48 依据真实游戏取证，仍需运行期复核）。");
            evidence.Add($"文本属性链：{MonoTextPatterns.DescribePropertyChain(utageChain)}。");
        }

        var n1 = 0;
        var n2 = 0;
        var n3 = 0;
        var n4 = 0;

        if (facts.IsDebugName)
        {
            n1 = -20;
            evidence.Add($"N1-20：debug/log/version 等非台词命名：{facts.TypeName}::{facts.MethodName}。");
        }

        if (facts.LdstrCount > 0 && facts.NonDialogueLiteralCount * 2 >= facts.LdstrCount)
        {
            n2 = -15;
            evidence.Add(
                $"N2-15：非台词字面量占比 {facts.NonDialogueLiteralCount}/{facts.LdstrCount}（路径/URL/格式串/短标识符等）。");
        }

        if (facts.CallerCount > 0 && facts.AllCallersInitLike)
        {
            n3 = -10;
            evidence.Add("N3-10：调用者全部是初始化/生命周期方法，可能是 UI 初始化而非每句入口。");
        }

        if (facts.LdstrCount > 0 && facts.MaxLiteralLength < 2)
        {
            n4 = -10;
            evidence.Add("N4-10：全部字面量都短于 2 字符。");
        }

        var score = Math.Clamp(s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8 + s9 + s10 + n1 + n2 + n3 + n4, 0, 100);
        var tier = score >= 70 && (s1 > 0 || s10 > 0) ? "A" : score >= 50 ? "B" : "C";
        var confidence = ConfidenceFor(score, s1, s2, s3 + s9 + s10);

        return new UnityMonoScore
        {
            Facts = facts,
            Score = score,
            Tier = tier,
            Confidence = confidence,
            Kind = KindFor(facts),
            S1 = s1,
            S2 = s2,
            S3 = s3,
            S4 = s4,
            S5 = s5,
            S6 = s6,
            S7 = s7,
            S8 = s8,
            S9 = s9,
            S10 = s10,
            N1 = n1,
            N2 = n2,
            N3 = n3,
            N4 = n4,
            Evidence = evidence,
        };
    }

    private static bool IsPlumbing(UnityMonoMethodFacts facts)
    {
        if (facts.MethodName is "set_text" or "SetText")
        {
            return true;
        }

        // TMP/TextMeshPro 自身的属性/工具方法不是游戏文本入口；运行期实证里 set_text 只用于计数。
        if (facts.TypeFullName.StartsWith("TMPro.", StringComparison.Ordinal) && !facts.TypeLevelPipeline)
        {
            return true;
        }

        if (facts.TypeFullName.StartsWith("System.", StringComparison.Ordinal) ||
            facts.TypeFullName.StartsWith("Microsoft.", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static TextCandidateKind KindFor(UnityMonoMethodFacts facts)
    {
        if (MonoTextPatterns.TryUtagePropertyChain(facts.TypeFullName, facts.MethodName, out _))
        {
            return TextCandidateKind.Dialogue;
        }

        if (facts.IsChoiceLike)
        {
            return TextCandidateKind.Choice;
        }

        if (facts.IsMenuLike)
        {
            return TextCandidateKind.Menu;
        }

        if (facts.TypeLevelPipeline || facts.IsSemanticName || facts.HasDirectTextSink || facts.HasTypewriterSink)
        {
            return TextCandidateKind.Dialogue;
        }

        return TextCandidateKind.Unknown;
    }

    private static string ConfidenceFor(int score, int s1, int s2, int direct)
    {
        if (score >= 70 && s1 > 0 && s2 > 0 && direct > 0)
        {
            return "high";
        }

        if (score >= 50 && (s1 > 0 || s2 > 0))
        {
            return "medium";
        }

        return "low";
    }
}

internal sealed class UnityMonoScore
{
    public required UnityMonoMethodFacts Facts { get; init; }

    public int Score { get; init; }

    public required string Tier { get; init; }

    public required string Confidence { get; init; }

    public TextCandidateKind Kind { get; init; }

    public int S1 { get; init; }

    public int S2 { get; init; }

    public int S3 { get; init; }

    public int S4 { get; init; }

    public int S5 { get; init; }

    public int S6 { get; init; }

    public int S7 { get; init; }

    public int S8 { get; init; }

    public int S9 { get; init; }

    public int S10 { get; init; }

    public int N1 { get; init; }

    public int N2 { get; init; }

    public int N3 { get; init; }

    public int N4 { get; init; }

    public List<string> Evidence { get; init; } = new();

    public string RuleSummary =>
        $"rules S1={S1} S2={S2} S3={S3} S4={S4} S5={S5} S6={S6} S7={S7} S8={S8} S9={S9} S10={S10} " +
        $"N1={N1} N2={N2} N3={N3} N4={N4}; tier={Tier}; confidence={Confidence}; ruleVersion={UnityMonoScoring.RuleVersion}";
}
