using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>
/// JSON 信封解析：CLI 约定「最后一行是 JSON」，但真实运行会有编译日志、dotnet 警告混在 stdout，
/// 所以解析器必须从后往前找最后一条能解析的 JSON 对象。
/// </summary>
public sealed class EnvelopeTests
{
    [Fact]
    public void ParsesEnvelopeOnLastLine()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":true,\"command\":\"detect\"}\n");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Envelope);
        Assert.True(result.Envelope!.Ok);
        Assert.Equal("detect", result.Envelope.Command);
        Assert.Equal("{\"ok\":true,\"command\":\"detect\"}", result.JsonLine);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SkipsLeadingLogLines()
    {
        const string stdout = "正在扫描目录……\n发现 fingerprint.json\n{\"ok\":true,\"command\":\"analyze\",\"data\":{\"total\":3}}\n";

        var result = ForgeEnvelopeParser.Parse(stdout);

        Assert.True(result.Succeeded);
        Assert.Equal("analyze", result.Envelope!.Command);
        Assert.True(result.Envelope.HasData);
    }

    [Fact]
    public void FindsEnvelopeEvenWhenLogLinesFollowIt()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":true,\"command\":\"build\"}\n生成结束。\n");

        Assert.True(result.Succeeded);
        Assert.Equal("build", result.Envelope!.Command);
    }

    [Fact]
    public void ToleratesBomAndTrailingWhitespace()
    {
        var result = ForgeEnvelopeParser.Parse("\uFEFF{\"ok\":true,\"command\":\"verify\"}   \r\n");

        Assert.True(result.Succeeded);
        Assert.Equal("verify", result.Envelope!.Command);
    }

    [Fact]
    public void FieldNamesAreCaseInsensitive()
    {
        var result = ForgeEnvelopeParser.Parse("{\"OK\":true,\"Command\":\"doctor\",\"CODE\":\"E_NONE\",\"Message\":\"好\"}");

        Assert.True(result.Succeeded);
        Assert.True(result.Envelope!.Ok);
        Assert.Equal("doctor", result.Envelope.Command);
        Assert.Equal("E_NONE", result.Envelope.Code);
        Assert.Equal("好", result.Envelope.Message);
    }

    [Fact]
    public void ReadsFailureEnvelopeWithSuggestion()
    {
        const string json =
            "{\"ok\":false,\"command\":\"install\",\"code\":\"E_INSTALL_FAILED\",\"message\":\"部署失败\",\"suggestion\":\"先跑 doctor\"}";

        var result = ForgeEnvelopeParser.Parse(json);

        Assert.True(result.Succeeded);
        Assert.False(result.Envelope!.Ok);
        Assert.Equal("E_INSTALL_FAILED", result.Envelope.Code);
        Assert.Equal("部署失败", result.Envelope.Message);
        Assert.Equal("先跑 doctor", result.Envelope.Suggestion);
        Assert.False(result.Envelope.HasData);
    }

    [Fact]
    public void DataArrayCountsAsData()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":true,\"data\":[1,2,3]}");

        Assert.True(result.Succeeded);
        Assert.True(result.Envelope!.HasData);
        Assert.Equal(3, result.Envelope.Data.GetArrayLength());
    }

    [Fact]
    public void MalformedLastJsonFallsBackToEarlierValidLine()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":true,\"command\":\"detect\"}\n{\"ok\":");

        Assert.True(result.Succeeded);
        Assert.True(result.Envelope!.Ok);
        Assert.Equal("detect", result.Envelope.Command);
    }

    [Fact]
    public void FullyMalformedOutputReportsParseError()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":}");

        Assert.False(result.Succeeded);
        Assert.Null(result.Envelope);
        Assert.Contains("无法解析", result.Error);
    }

    [Fact]
    public void TextWithoutEnvelopeReportsMissingEnvelope()
    {
        var result = ForgeEnvelopeParser.Parse("hello\nworld");

        Assert.False(result.Succeeded);
        Assert.Contains("没有找到 JSON 信封", result.Error);
        Assert.Contains("2 行", result.Error);
    }

    [Fact]
    public void EmptyOutputReportsEmptyStdout()
    {
        var result = ForgeEnvelopeParser.Parse("   \r\n  ");

        Assert.False(result.Succeeded);
        Assert.Contains("stdout 为空", result.Error);
    }

    [Fact]
    public void NullOutput_DoesNotThrow()
    {
        var result = ForgeEnvelopeParser.Parse(null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void AllowsTrailingCommas()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":true,\"command\":\"detect\",}");

        Assert.True(result.Succeeded);
        Assert.Equal("detect", result.Envelope!.Command);
    }

    [Fact]
    public void StringFalseIsNotTreatedAsTrue()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":\"false\"}");

        Assert.True(result.Succeeded);
        Assert.False(result.Envelope!.Ok);
    }

    [Fact]
    public void FromJson_ThrowsWithParseReason()
    {
        Assert.Throws<InvalidOperationException>(() => ForgeEnvelope.FromJson("not json"));
    }

    [Fact]
    public void ParsesDetailsObjectForDiagnosability()
    {
        var result = ForgeEnvelopeParser.Parse(
            "{\"ok\":false,\"command\":\"verify\",\"details\":{\"plugin/a11y.dll\":\"删除失败：文件被占用\",\"backup\":\"缺少哈希\"}}");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Envelope!.Details);
        Assert.Equal(2, result.Envelope.Details!.Count);
        Assert.Equal("删除失败：文件被占用", result.Envelope.Details["plugin/a11y.dll"]);
    }

    [Fact]
    public void MissingDetailsBecomesNull()
    {
        var result = ForgeEnvelopeParser.Parse("{\"ok\":true,\"command\":\"detect\"}");

        Assert.True(result.Succeeded);
        Assert.Null(result.Envelope!.Details);
    }
}
