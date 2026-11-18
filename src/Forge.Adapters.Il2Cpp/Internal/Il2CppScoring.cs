using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>单条候选的打分结果。</summary>
internal sealed class Il2CppScoreResult
{
    public int Score { get; init; }

    public string Tier { get; init; } = "C";

    public bool KnownEntry { get; init; }

    public bool HasDirectSink { get; init; }

    /// <summary>排序权重：已知通道按实测名次，未知通道统一 50（排在所有已知通道之后）。</summary>
    public int Rank { get; init; } = 50;

    /// <summary>是否命中 R18 实测通道表。</summary>
    public bool Known { get; init; }

    public Il2CppChannelRole? Role { get; init; }

    /// <summary>命中已知通道时的 R18 探针编号（如 A/B/G）。</summary>
    public string KnownProbe { get; init; } = string.Empty;

    public string RuleLine { get; init; } = string.Empty;
}

/// <summary>
/// IL2CPP 候选打分。<b>规则版本 il2cpp-w2q-1</b>。
///
/// <para>
/// 与 Unity Mono 的 w2h-1 对齐了大部分正向规则（S1–S8 同语义），但 IL2CPP 有两条硬约束必须体现：
/// 1) interop 程序集的方法体是调用桩，**没有调用图、没有字符串字面量** → 不能像 Mono 那样用
///    <c>ldstr</c> 中文字面量、调用者数量做证据，只能靠类型/属性/参数/框架识别；
/// 2) 基础控件（<c>TMPro.TMP_Text.set_text</c>、<c>UnityEngine.UI.Text.set_text</c>）必须重罚，
///    R18 实测它们会把「SAVE / LOAD / 全屏」之类的 UI 文案全抓进来（UnityEngine.UI.Text 触发 5785 次）。
/// </para>
/// </summary>
internal static class Il2CppScoring
{
    internal const string RuleVersion = "il2cpp-w2q-2";

    internal const int S1KnownFramework = 25;
    internal const int S2SemanticName = 20;
    internal const int S3DirectTextSink = 20;
    internal const int S4StringParameter = 10;
    internal const int S5StringPlusNumber = 5;
    internal const int S6TypeHoldsText = 10;
    internal const int S7TextEvent = 10;
    internal const int S8UiOrChoice = 5;
    internal const int S9RuntimeConfirmed = 10;
    internal const int S10TextEventAdder = 15;
    internal const int N1Plumbing = -20;
    internal const int N2NotATextInflow = -15;
    internal const int N3Constructor = -40;
    internal const int N4RuntimeMiss = -25;
    internal const int N5DebugName = -20;
    internal const int N6Obfuscated = -10;

    /// <summary>说话人名面板的 <c>set_Text</c> 不是台词出口（R18：AuthorNamePanel 只是人名）。</summary>
    internal const int N7AuthorNamePanel = -25;

    /// <summary>
    /// 已知框架里<b>没被真机实测过</b>的同类文本出口（例如 ChatPrinterPanel 的两个 setter）要降权：
    /// R18 实测一条台词会同时打到 6 条通道，未实测通道既可能是死路，也可能造成重复朗读。
    /// </summary>
    internal const int N8UntestedSinkOnKnownFramework = -15;

    /// <summary>
    /// async/状态机方法禁止自动打补丁：R21 真机实测（task-37）Harmony 补丁
    /// <c>Naninovel.TextPrinterManager::PrintTextAsync</c> 会破坏 await 状态机，
    /// 游戏在 <c>PrintTextAsync</c> await 时抛 <c>MethodAccessException</c> 并卡在首行。
    /// 这类方法（名字以 Async 结尾/开头，或含 AsyncStateMachine）的分数必须压到 60 以下，
    /// 只能作为人工确认候选，绝不进自动路线。
    /// </summary>
    internal const int N9AsyncMethod = -60;

    /// <summary>dump.cs 路线缺属性/事件结构，打分上限压到 85，禁止直接进 A 级自动路线。</summary>
    internal const int DumpScoreCap = 85;

    public static Il2CppScoreResult Score(
        Il2CppMethodFact method,
        Il2CppTypeFact? type,
        bool hasTextEventOnType,
        Il2CppRuntimeFact? runtime,
        int scoreCap = 100)
    {
        var s1 = type?.FrameworkId is not null ? S1KnownFramework : 0;
        var s2 = Il2CppPatterns.SemanticMethodNameRx.IsMatch(method.Name) ? S2SemanticName : 0;
        var isSinkProperty = method.IsPropertyMember &&
                             method.PropertyName is not null &&
                             Il2CppPatterns.TextSinkPropertyNames.Contains(method.PropertyName);
        var s3 = isSinkProperty ? S3DirectTextSink : 0;
        var s4 = method.HasStringParameter ? S4StringParameter : 0;
        var s5 = method.HasStringParameter && method.HasNumericParameter ? S5StringPlusNumber : 0;
        var s6 = type is { TextMemberNames.Count: > 0 } ? S6TypeHoldsText : 0;
        var s7 = hasTextEventOnType ? S7TextEvent : 0;
        var s8 = Il2CppPatterns.ChoiceNameRx.IsMatch(method.Name) ||
                 (type is not null && (type.IsUiType || Il2CppPatterns.IsTextLikeType(type.Name)))
            ? S8UiOrChoice
            : 0;

        var s9 = runtime switch
        {
            { NonEmptyHits: > 0 } => S9RuntimeConfirmed,
            _ => 0,
        };

        var s10 = method.IsEventAdder &&
                  method.EventName is not null &&
                  Il2CppPatterns.TextEventNameRx.IsMatch(method.EventName)
            ? S10TextEventAdder
            : 0;

        var n1 = IsPlumbing(method, type) ? N1Plumbing : 0;
        var n2 = IsNonTextInflow(method) ? N2NotATextInflow : 0;
        var n3 = method.IsConstructor ? N3Constructor : 0;
        var n4 = runtime is { Calls: > 0, NonEmptyHits: 0 } ? N4RuntimeMiss : 0;
        var n5 = Il2CppPatterns.DebugNameRx.IsMatch(method.Name) ? N5DebugName : 0;
        var n6 = Il2CppPatterns.ObfuscatedNameRx.IsMatch(method.Name) ? N6Obfuscated : 0;
        var n7 = type is not null &&
                 type.Name.Contains("AuthorName", StringComparison.Ordinal) &&
                 (method.Name == "set_Text" || method.Name == "set_text")
            ? N7AuthorNamePanel
            : 0;

        Il2CppKnownTarget? known = Il2CppKnownTargets.TryMatch(method, out var matched) ? matched : null;

        var n8 = s1 > 0 && known is null && s3 > 0 ? N8UntestedSinkOnKnownFramework : 0;
        var n9 = IsAsyncCandidate(method) ? N9AsyncMethod : 0;

        var raw = s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8 + s9 + s10 + n1 + n2 + n3 + n5 + n6 + n7 + n8 + n9;
        var score = Math.Clamp(raw, 0, 100);

        if (known is not null)
        {
            // R18 真机实测名次优先于通用规则：同样的 set_Text(string)，实测结果差异很大。
            score = known.Score;
        }

        if (n4 != 0)
        {
            score = Math.Max(0, score + n4);
            // 实测“调用过但一次非空都没有”的通道是死路（R18 的 D/E/F 探针）：
            // 不论静态分多高都不许自动打补丁，必须降级成人工确认。
            score = Math.Min(score, 60);
        }

        if (n9 != 0)
        {
            // async 方法即使静态分/真机名次很高也不许自动补（R21：补 async 会卡死游戏）。
            score = Math.Min(score, 60);
        }

        score = Math.Min(score, Math.Clamp(scoreCap, 0, 100));

        var tier = score >= 70 && (s1 > 0 || s3 > 0) ? "A" : score >= 50 ? "B" : "C";
        if (scoreCap < 100 && tier == "A")
        {
            // 原料不全（例如只有 dump.cs）时不允许直接给出 A 级自动路线。
            tier = "B";
        }

        var rules = new StringBuilder();
        rules.Append("rules S1=").Append(s1)
            .Append(" S2=").Append(s2)
            .Append(" S3=").Append(s3)
            .Append(" S4=").Append(s4)
            .Append(" S5=").Append(s5)
            .Append(" S6=").Append(s6)
            .Append(" S7=").Append(s7)
            .Append(" S8=").Append(s8)
            .Append(" S9=").Append(s9)
            .Append(" S10=").Append(s10)
            .Append(" N1=").Append(n1)
            .Append(" N2=").Append(n2)
            .Append(" N3=").Append(n3)
            .Append(" N4=").Append(n4)
            .Append(" N5=").Append(n5)
            .Append(" N6=").Append(n6)
            .Append(" N7=").Append(n7)
            .Append(" N8=").Append(n8)
            .Append(" N9=").Append(n9)
            .Append("; tier=").Append(tier)
            .Append("; ruleVersion=").Append(RuleVersion);

        if (known is not null)
        {
            rules.Append("; known=naninovel:").Append(known.Probe)
                .Append(" role=").Append(known.Role);
        }

        return new Il2CppScoreResult
        {
            Score = score,
            Tier = tier,
            KnownEntry = s1 > 0,
            HasDirectSink = s3 > 0,
            Rank = known?.Rank ?? 50,
            Known = known is not null,
            Role = known?.Role,
            KnownProbe = known?.Probe ?? string.Empty,
            RuleLine = rules.ToString(),
        };
    }

    /// <summary>
    /// 是否是 async/状态机方法。IL2CPP 里 <c>PrintTextAsync</c> 这类方法返回 UniTask/Task，
    /// 方法体是编译器生成的 await 状态机；Harmony 直接在方法入口打补丁会破坏状态机
    /// （R21 实测抛 MethodAccessException 卡死在第一行），因此一律禁止自动补。
    /// </summary>
    internal static bool IsAsyncCandidate(Il2CppMethodFact method)
    {
        var name = method.Name;
        return name.EndsWith("Async", StringComparison.Ordinal)
            || name.StartsWith("Async", StringComparison.Ordinal)
            || name.Contains("AsyncStateMachine", StringComparison.Ordinal);
    }

    /// <summary>
    /// 基础控件与“非入口”方法：不属于游戏文本出口，命中会把 UI 文案一起读出来。
    /// 注意 IL2CPP 的坑：<c>set_Text</c>（大写，IRevealableText 实现）是真正的出口，
    /// <c>set_text</c>（小写，基类控件属性）是噪声，二者必须分开处理。
    /// </summary>
    public static bool IsPlumbing(Il2CppMethodFact method, Il2CppTypeFact? type)
    {
        if (method.Name == "set_text" || method.Name == "get_text")
        {
            return true;
        }

        var ns = type?.Namespace ?? method.Namespace;
        if (ns.StartsWith("TMPro", StringComparison.Ordinal))
        {
            return true;
        }

        if (ns.StartsWith("UnityEngine.UI", StringComparison.Ordinal))
        {
            return true;
        }

        return method.TypeName is "Text" or "TMP_Text" or "TextMeshProUGUI" or "TextMeshPro";
    }

    /// <summary>属性 getter、Unity 生命周期、无参无字符串的方法都不可能是“文本流入”。</summary>
    public static bool IsNonTextInflow(Il2CppMethodFact method)
    {
        if (method.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            return true;
        }

        if (Il2CppPatterns.LifecycleNameRx.IsMatch(method.Name))
        {
            return true;
        }

        return method.Parameters.Count == 0 && !method.HasStringParameter;
    }
}
