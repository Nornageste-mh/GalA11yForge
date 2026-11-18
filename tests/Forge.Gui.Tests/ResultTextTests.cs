using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>结果区排版：第一行必须是结论，其次是命令/退出码/消息，最后才是数据与原始输出。</summary>
public sealed class ResultTextTests
{
    private const string DetectLine = "forge detect --game D:\\G --json";

    private static ForgeRunOutcome Success()
        => ForgeRunOutcome.Completed(
            0,
            DetectLine,
            "{\"ok\":true,\"command\":\"detect\",\"message\":\"识别为 renpy\",\"data\":{\"engine\":\"renpy\",\"confidence\":0.93}}",
            null,
            TimeSpan.FromSeconds(1));

    [Fact]
    public void FirstLineIsTheConclusion()
    {
        var text = ForgeResultTextBuilder.Build(Success());
        var firstLine = text.Split('\n')[0].TrimEnd('\r');

        Assert.Equal("【识别引擎】成功", firstLine);
    }

    [Fact]
    public void IncludesCommandExitCodeAndMessage()
    {
        var text = ForgeResultTextBuilder.Build(Success());

        Assert.Contains("命令：" + DetectLine, text);
        Assert.Contains("退出码：0（成功）", text);
        Assert.Contains("耗时：1.0 秒", text);
        Assert.Contains("消息：识别为 renpy", text);
    }

    [Fact]
    public void NarratedDataIsIndentedUnderDataHeading()
    {
        var text = ForgeResultTextBuilder.Build(Success());

        Assert.Contains("数据：", text);
        Assert.Contains("  引擎：renpy", text);
        Assert.Contains("  置信度：93%", text);
    }

    [Fact]
    public void RawJsonIsHiddenByDefaultAndShownOnDemand()
    {
        var quiet = ForgeResultTextBuilder.Build(Success());
        var loud = ForgeResultTextBuilder.Build(Success(), new ForgeTextOptions { IncludeRawJson = true });

        Assert.DoesNotContain("原始输出（stdout）：", quiet);
        Assert.Contains("原始输出（stdout）：", loud);
    }

    [Fact]
    public void NonJsonStdoutLinesAreEchoed()
    {
        var outcome = ForgeRunOutcome.Completed(
            0, DetectLine, "warning: fallback\n{\"ok\":true}\n", null, TimeSpan.Zero);

        var text = ForgeResultTextBuilder.Build(outcome);

        Assert.Contains("标准输出（非 JSON 行）：", text);
        Assert.Contains("warning: fallback", text);
        Assert.DoesNotContain("{\"ok\":true}", text);
    }

    [Fact]
    public void StderrIsAlwaysShown()
    {
        var outcome = ForgeRunOutcome.Completed(1, DetectLine, null, "boom\n", TimeSpan.Zero);

        var text = ForgeResultTextBuilder.Build(outcome);

        Assert.Contains("标准错误：", text);
        Assert.Contains("  boom", text);
    }

    [Fact]
    public void StderrIsTruncatedWithCount()
    {
        var outcome = ForgeRunOutcome.Completed(1, DetectLine, null, "a\nb\nc\n", TimeSpan.Zero);

        var text = ForgeResultTextBuilder.Build(outcome, new ForgeTextOptions { MaxStderrLines = 2 });

        Assert.Contains("……还有 1 行未显示。", text);
    }

    [Fact]
    public void FailureShowsCodeAndSuggestion()
    {
        var outcome = ForgeRunOutcome.Completed(
            3,
            "forge install --game D:\\G --json",
            "{\"ok\":false,\"code\":\"E_NOT_IMPLEMENTED\",\"message\":\"还没有实现\",\"suggestion\":\"先跑 doctor\"}",
            null,
            TimeSpan.FromSeconds(2));

        var text = ForgeResultTextBuilder.Build(outcome);

        Assert.Contains("【安装补丁】失败", text);
        Assert.Contains("退出码：3（依赖或能力缺失；未实现的命令也返回这个码）", text);
        Assert.Contains("错误码：E_NOT_IMPLEMENTED", text);
        Assert.Contains("消息：还没有实现", text);
        Assert.Contains("建议：先跑 doctor", text);
    }

    [Fact]
    public void CanceledExplainsRiskAndHasNoExitCode()
    {
        var outcome = ForgeRunOutcome.Canceled(DetectLine, "partial\n", null, TimeSpan.FromSeconds(3));

        var text = ForgeResultTextBuilder.Build(outcome);

        Assert.Contains("【识别引擎】已取消", text);
        Assert.Contains("退出码：未产生（进程没有正常结束）", text);
        Assert.Contains("说明：本次执行被用户中止", text);
    }

    [Fact]
    public void LaunchFailedPointsAtExecutableField()
    {
        var outcome = ForgeRunOutcome.LaunchFailed(DetectLine, "找不到可执行文件：C:\\nope\\forge.exe", TimeSpan.Zero);

        var text = ForgeResultTextBuilder.Build(outcome);

        Assert.Contains("【识别引擎】无法启动", text);
        Assert.DoesNotContain("退出码：", text);
        Assert.Contains("建议：在「forge.exe 路径」里指定", text);
        Assert.Contains("找不到可执行文件", text);
    }

    [Fact]
    public void MissingEnvelopeIsReportedNotHidden()
    {
        var outcome = ForgeRunOutcome.Completed(0, DetectLine, "done", null, TimeSpan.Zero);

        var text = ForgeResultTextBuilder.Build(outcome);

        Assert.Contains("信封解析：", text);
    }

    [Fact]
    public void HeadlineIsOneLineWithStatusAndMessage()
    {
        var headline = ForgeResultTextBuilder.BuildHeadline(Success());

        Assert.Equal("识别引擎：成功（退出码 0）：识别为 renpy", headline);
    }

    [Fact]
    public void HeadlineForLaunchFailureSaysNoExitCode()
    {
        var headline = ForgeResultTextBuilder.BuildHeadline(
            ForgeRunOutcome.LaunchFailed(DetectLine, "找不到可执行文件", TimeSpan.Zero));

        Assert.Contains("未产生退出码", headline);
    }

    [Theory]
    [InlineData("forge detect --game D:\\G --json", "识别引擎")]
    [InlineData("forge analyze --game D:\\G --json", "分析文本")]
    [InlineData("forge verify --game D:\\G --rollback --json", "回滚补丁")]
    [InlineData("forge verify --game D:\\G --json", "安装后自检")]
    [InlineData("forge doctor --json", "环境自检")]
    [InlineData("", "forge")]
    [InlineData("something-else", "forge")]
    public void TitleOfMapsCommandLineTokens(string commandLine, string expected)
    {
        Assert.Equal(expected, ForgeResultTextBuilder.TitleOf(commandLine));
    }

    [Fact]
    public void SerializeDataRoundTripsEnvelopeData()
    {
        var envelope = ForgeEnvelope.FromJson("{\"ok\":true,\"data\":{\"engine\":\"renpy\"}}");

        Assert.Contains("\"engine\"", ForgeResultTextBuilder.SerializeData(envelope));
        Assert.Equal(string.Empty, ForgeResultTextBuilder.SerializeData(null));
    }

    [Fact]
    public void TextEndsWithSingleNewLine()
    {
        var text = ForgeResultTextBuilder.Build(Success());

        Assert.EndsWith(Environment.NewLine, text);
        Assert.False(text.EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void PrintsStructuredDetailsWhenCliProvidesThem()
    {
        var outcome = ForgeRunOutcome.Completed(
            5,
            "forge verify --rollback --json",
            "{\"ok\":false,\"command\":\"verify\",\"code\":\"E_PARTIAL\",\"message\":\"回滚部分失败。\",\"details\":{\"plugin/a11y.dll\":\"删除失败：文件被占用\"}}",
            null,
            TimeSpan.FromSeconds(2));

        var text = ForgeResultTextBuilder.Build(outcome);

        Assert.Contains("详情：", text);
        Assert.Contains("plugin/a11y.dll：删除失败：文件被占用", text);
    }
}
