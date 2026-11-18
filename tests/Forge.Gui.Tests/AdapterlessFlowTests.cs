using GalA11yForge.Gui.Core;

namespace Forge.Gui.Tests;

public sealed class AdapterlessFlowTests
{
    private const string AdapterlessJson =
        "{\"ok\":false,\"command\":\"analyze\",\"code\":\"E_NOT_IMPLEMENTED\"," +
        "\"message\":\"识别为 kirikiriz，但该引擎的适配器尚未接线（当前已内置：unity_mono、unity_il2cpp、renpy）。\"," +
        "\"suggestion\":\"该引擎暂无内置适配器：可先用 `analyze --advanced` 跑 Ghidra 静态分析，再用 `report` 导出报告；真正可玩仍需运行时方案或人工分析。\"," +
        "\"data\":{\"engine\":\"kirikiriz\"}}";

    [Fact]
    public void RecognizesAdapterlessAnalyzeFailure()
    {
        var envelope = ForgeEnvelope.FromJson(AdapterlessJson);

        Assert.True(AdapterlessFlow.IsAdapterlessAnalyzeFailure(envelope));
        Assert.Equal("kirikiriz", AdapterlessFlow.TryGetEngineFamily(envelope));
    }

    [Fact]
    public void RecognizesBySuggestion_WhenMessageWordingChanges()
    {
        var envelope = ForgeEnvelope.FromJson(
            "{\"ok\":false,\"code\":\"E_NOT_IMPLEMENTED\",\"message\":\"该引擎暂不支持。\"," +
            "\"suggestion\":\"先用 analyze --advanced 再 report。\"}");

        Assert.True(AdapterlessFlow.IsAdapterlessAnalyzeFailure(envelope));
    }

    [Fact]
    public void IgnoresOtherFailuresAndNull()
    {
        Assert.False(AdapterlessFlow.IsAdapterlessAnalyzeFailure(null));
        Assert.False(AdapterlessFlow.IsAdapterlessAnalyzeFailure(ForgeEnvelope.FromJson(
            "{\"ok\":false,\"code\":\"E_DEPENDENCY_MISSING\",\"message\":\"缺少依赖。\"}")));
        Assert.False(AdapterlessFlow.IsAdapterlessAnalyzeFailure(ForgeEnvelope.FromJson(
            "{\"ok\":false,\"code\":\"E_NOT_IMPLEMENTED\",\"message\":\"别的原因，没有那句建议。\"}")));
    }

    [Fact]
    public void ExplainMentionsEngineAndReport()
    {
        var text = AdapterlessFlow.Explain("kirikiriz");

        Assert.Contains("kirikiriz", text);
        Assert.Contains("Ghidra", text);
        Assert.Contains("报告", text);
        Assert.Contains("人工", text);
    }

    [Fact]
    public void ExplainFallsBackToGenericEngineWord()
    {
        Assert.Contains("这个引擎", AdapterlessFlow.Explain(null));
    }
}
