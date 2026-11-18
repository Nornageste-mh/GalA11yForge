using System.Text.Json;
using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>
/// data → 中文朗读文本。读屏用户听 JSON 很痛苦，所以这里既测「翻得对」，
/// 也测「不会一次刷几百行」。
/// </summary>
public sealed class NarrationTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void KnownFieldsGetChineseLabels()
    {
        var lines = ForgeDataNarrator.Narrate(
            Json("{\"game\":\"D:\\\\Games\",\"engine\":\"renpy\",\"confidence\":0.87,\"dryRun\":true}"));

        Assert.Equal(new[] { "游戏目录：D:\\Games", "引擎：renpy", "置信度：87%", "演练模式：是" }, lines);
    }

    [Fact]
    public void ConfidenceOutsideUnitRangeStaysRaw()
    {
        var lines = ForgeDataNarrator.Narrate(Json("{\"confidence\":87}"));

        Assert.Equal(new[] { "置信度：87" }, lines);
    }

    [Fact]
    public void UnknownFieldsKeepOriginalKey()
    {
        var lines = ForgeDataNarrator.Narrate(Json("{\"weirdKey\":\"x\"}"));

        Assert.Equal(new[] { "weirdKey：x" }, lines);
    }

    [Fact]
    public void NestedObjectsAreIndented()
    {
        var lines = ForgeDataNarrator.Narrate(Json("{\"coverage\":{\"total\":3,\"covered\":2}}"));

        Assert.Equal(new[] { "覆盖：", "  合计：3", "  已覆盖：2" }, lines);
    }

    [Fact]
    public void GhidraEstimateAndDurationGetReadableLabels()
    {
        var lines = ForgeDataNarrator.Narrate(Json(
            "{\"ghidra\":{\"status\":\"timeout\",\"executable\":\"D:\\\\Games\\\\Game.exe\"," +
            "\"executableSizeBytes\":4583168,\"estimatedSeconds\":402,\"suggestedTimeoutSeconds\":462,\"elapsedSeconds\":30.1}}"));

        Assert.Equal(
            new[]
            {
                "高级分析：",
                "  状态：timeout",
                "  分析程序：D:\\Games\\Game.exe",
                "  分析程序大小（字节）：4583168",
                "  预计分析耗时（秒）：402",
                "  建议超时上限（秒）：462",
                "  实际分析耗时（秒）：30.1",
            },
            lines);
    }

    [Fact]
    public void NullBecomesWeiTiGongAndEmptyArrayBecomesWu()
    {
        var lines = ForgeDataNarrator.Narrate(Json("{\"workspace\":null,\"risks\":[]}"));

        Assert.Equal(new[] { "工作区：未提供", "风险：无" }, lines);
    }

    [Fact]
    public void ChecksArrayRendersPassFailWithDetail()
    {
        var lines = ForgeDataNarrator.Narrate(Json(
            "{\"checks\":[{\"name\":\"运行时\",\"passed\":true},{\"name\":\"工作区\",\"passed\":false,\"detail\":\"不可写\"}]}"));

        Assert.Equal(
            new[] { "检查项（2 项）：", "  通过　运行时", "  未通过　工作区：不可写" },
            lines);
    }

    [Fact]
    public void CheckWithoutNameFallsBackToKeyValueRendering()
    {
        // 缺 name 的「检查项」不算标准形状，退回通用对象数组渲染，不猜。
        var lines = ForgeDataNarrator.Narrate(Json("{\"checks\":[{\"passed\":true}]}"));

        Assert.Equal(new[] { "检查项（1 项）：", "  · 通过=是" }, lines);
    }

    [Fact]
    public void ObjectArrayUsesKeyValuePairs()
    {
        var lines = ForgeDataNarrator.Narrate(Json("{\"candidates\":[{\"id\":1,\"score\":0.5}]}"));

        Assert.Equal(new[] { "候选（1 项）：", "  · 编号=1，分数=0.5" }, lines);
    }

    [Fact]
    public void ScalarArrayIsInlineWithEnumerationComma()
    {
        var lines = ForgeDataNarrator.Narrate(Json("{\"risks\":[\"a\",\"b\"]}"));

        Assert.Equal(new[] { "风险：a、b" }, lines);
    }

    [Fact]
    public void LongScalarArrayIsTruncatedWithTotal()
    {
        var items = string.Join(',', Enumerable.Range(0, 15).Select(index => index.ToString()));
        var lines = ForgeDataNarrator.Narrate(Json("{\"risks\":[" + items + "]}"));

        Assert.Single(lines);
        Assert.Contains("（共 15 项）", lines[0]);
        Assert.Contains("、11", lines[0]);
        Assert.DoesNotContain("12", lines[0]);
    }

    [Fact]
    public void TopLevelArrayIsLabelledData()
    {
        var lines = ForgeDataNarrator.Narrate(Json("[1,2]"));

        Assert.Equal(new[] { "数据：1、2" }, lines);
    }

    [Fact]
    public void TopLevelScalarIsNarratedDirectly()
    {
        var lines = ForgeDataNarrator.Narrate(Json("true"));

        Assert.Equal(new[] { "是" }, lines);
    }

    [Fact]
    public void OutputIsCappedAtMaxLines()
    {
        var properties = string.Join(
            ',',
            Enumerable.Range(0, 200).Select(index => $"\"key{index}\":{index}"));

        var lines = ForgeDataNarrator.Narrate(Json("{" + properties + "}"));

        Assert.True(lines.Count <= ForgeDataNarrator.MaxLines, lines.Count.ToString());
        Assert.Equal(ForgeDataNarrator.MaxLines, lines.Count);
    }

    [Fact]
    public void MultilineStringIsFoldedIntoOneLine()
    {
        var lines = ForgeDataNarrator.Narrate(Json("{\"message\":\"第一行\\n第二行\"}"));

        Assert.Equal(new[] { "消息：第一行 第二行" }, lines);
    }

    [Fact]
    public void NarratesBlockingTodosFromReportData()
    {
        var lines = ForgeDataNarrator.Narrate(Json(
            "{\"humanTodoCount\":4,\"blockingCount\":1,\"blockingTodos\":[\"M-A01 该引擎没有内置适配器，不能自动生成可安装补丁\"]}"));

        Assert.Equal(new[]
        {
            "人工待办数：4", "阻断项数：1",
            "阻断项：M-A01 该引擎没有内置适配器，不能自动生成可安装补丁",
        }, lines);
    }

    [Fact]
    public void NarratesSuggestedTodosFromReportData()
    {
        var lines = ForgeDataNarrator.Narrate(Json(
            "{\"humanTodoCount\":4,\"suggestedTodos\":[\"M-A02 在 x64dbg/Textractor 中定位文本函数后人工接钩子\"]}"));

        Assert.Equal(new[]
        {
            "人工待办数：4",
            "建议待办：M-A02 在 x64dbg/Textractor 中定位文本函数后人工接钩子",
        }, lines);
    }

    [Fact]
    public void LabelForReturnsNullForUnknownKeys()
    {
        Assert.Equal("引擎", ForgeDataNarrator.LabelFor("ENGINE"));
        Assert.Null(ForgeDataNarrator.LabelFor("no-such-key"));
    }
}
