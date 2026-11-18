using GalA11yForge.Adapters.Il2Cpp.Internal;

namespace GalA11yForge.Adapters.Il2Cpp.Tests;

/// <summary>
/// 打分与已知识别测试。重点回归 R18 的三个结论：
/// 1) 同一行台词会打到多条通道，只有实测确认过的是出口；
/// 2) <c>set_Text</c>（大写）是出口，<c>set_text</c>（小写）是基类控件噪声；
/// 3) 运行期 0 次非空的通道（TMPro 分支）不能进自动补丁。
/// </summary>
public sealed class ScoringTests
{
    private static Il2CppScoreResult Score(string typeFullName, Il2CppMethodFact method, Il2CppTypeFact? type = null, int cap = 100, Il2CppRuntimeFact? runtime = null)
    {
        type ??= Facts.Type(typeFullName, "naninovel", isUi: true);
        return Il2CppScoring.Score(method, type, hasTextEventOnType: true, runtime, cap);
    }

    [Fact]
    public void KnownPrintTextAsync_IsCappedByAsyncPenalty()
    {
        var method = Facts.Method(
            "Naninovel.TextPrinterManager",
            "PrintTextAsync",
            Facts.Param("printerId", "System.String"),
            Facts.Param("text", "System.String"),
            Facts.Param("authorId", "System.String"),
            Facts.Param("speed", "System.Single"));

        var result = Score("Naninovel.TextPrinterManager", method);

        // R21 真机实测：给 async 方法打 Harmony 补丁会破坏 await 状态机并卡死游戏，
        // 即使真机通道表给 100 分也必须压到 60 分（不再进 A 级自动路线）。
        Assert.Equal(60, result.Score);
        Assert.Equal("B", result.Tier);
        Assert.True(result.Known);
        Assert.Equal(0, result.Rank);
        Assert.Equal(Il2CppChannelRole.Dialogue, result.Role.GetValueOrDefault());
        Assert.Contains("known=naninovel:G", result.RuleLine);
        Assert.Contains("N9=-60", result.RuleLine);
    }

    [Fact]
    public void AsyncMethodNamePattern_IsDetected()
    {
        Assert.True(Il2CppScoring.IsAsyncCandidate(Facts.Method("Naninovel.TextPrinterManager", "PrintTextAsync")));
        Assert.True(Il2CppScoring.IsAsyncCandidate(Facts.Method("X.Y", "AsyncLoad")));
        Assert.True(Il2CppScoring.IsAsyncCandidate(Facts.Method("X.Y", "DoAsyncStateMachine")));
        Assert.False(Il2CppScoring.IsAsyncCandidate(Facts.Method("Naninovel.UITextPrinter", "set_Text")));
    }

    [Fact]
    public void KnownKnownTable_IsMatchedByDeclaringTypeNotBaseType()
    {
        // R18 陷阱：补丁必须打声明类型。子类上的同名方法不应当成已知识别命中。
        var subclass = Facts.Setter("Naninovel.UI.MyCustomPrinterPanel", "Text");
        var result = Score("Naninovel.UI.MyCustomPrinterPanel", subclass);

        Assert.False(result.Known);
        Assert.Equal(50, result.Rank);
    }

    [Fact]
    public void AuthorNamePanelSetText_IsPenalizedBelowAutoThreshold()
    {
        var setter = Facts.Setter("Naninovel.UI.AuthorNamePanel", "Text");

        var result = Score("Naninovel.UI.AuthorNamePanel", setter);

        Assert.True(result.Score < 70);
        Assert.Contains("N7=-25", result.RuleLine);
        Assert.NotEqual("A", result.Tier);
    }

    [Fact]
    public void RuntimeMissedChannel_IsCappedAtSixty()
    {
        var method = Facts.Setter("Naninovel.UI.RevealableTMProText", "Text");
        var runtime = new Il2CppRuntimeFact
        {
            TypeFullName = "Naninovel.UI.RevealableTMProText",
            MethodName = "set_Text",
            Calls = 0,
            NonEmptyHits = 0,
            Verdict = "运行时不触发",
        };

        var result = Score("Naninovel.UI.RevealableTMProText", method, runtime: runtime);

        Assert.Equal(60, result.Score);
        Assert.Equal("B", result.Tier);
    }

    [Fact]
    public void RuntimeNonEmpty_AddsConfidenceForUnknownChannel()
    {
        var method = Facts.Method("Naninovel.UI.SomePrinter", "set_Text", Facts.Param("value", "System.String"));
        var type = Facts.Type("Naninovel.UI.SomePrinter", "naninovel", isUi: true);
        var runtime = new Il2CppRuntimeFact
        {
            TypeFullName = "Naninovel.UI.SomePrinter",
            MethodName = "set_Text",
            Calls = 3,
            NonEmptyHits = 2,
            Verdict = "实测有台词",
        };

        var with = Il2CppScoring.Score(method, type, hasTextEventOnType: false, runtime);
        var without = Il2CppScoring.Score(method, type, hasTextEventOnType: false, null);

        Assert.True(with.Score > without.Score);
        Assert.Contains("S9=10", with.RuleLine);
    }

    [Fact]
    public void LowercaseSetText_IsPlumbing()
    {
        var setter = Facts.Method("TMPro.TMP_Text", "set_text", Facts.Param("value", "System.String"));
        var type = Facts.Type("TMPro.TMP_Text");

        Assert.True(Il2CppScoring.IsPlumbing(setter, type));
        Assert.True(Il2CppScoring.IsPlumbing(setter, null));
    }

    [Fact]
    public void UppercaseSetText_OnUiText_IsNotPlumbing()
    {
        var setter = Facts.Setter("Naninovel.UI.RevealableUIText", "Text");
        var type = Facts.Type("Naninovel.UI.RevealableUIText", "naninovel", isUi: true);

        Assert.False(Il2CppScoring.IsPlumbing(setter, type));
    }

    [Fact]
    public void DumpCap_DowngradesTierAndScore()
    {
        var method = Facts.Setter("Naninovel.UITextPrinter", "Text");

        var result = Score("Naninovel.UITextPrinter", method, cap: Il2CppScoring.DumpScoreCap);

        Assert.Equal(Il2CppScoring.DumpScoreCap, result.Score);
        Assert.Equal("B", result.Tier);
    }

    [Fact]
    public void Getter_IsNotTextInflow()
    {
        var getter = Facts.Method("Naninovel.UITextPrinter", "get_Text");

        Assert.True(Il2CppScoring.IsNonTextInflow(getter));
    }

    [Fact]
    public void KnownTargets_RowCountMatchesR18Table()
    {
        Assert.Equal(9, Il2CppKnownTargets.Count);
    }
}
