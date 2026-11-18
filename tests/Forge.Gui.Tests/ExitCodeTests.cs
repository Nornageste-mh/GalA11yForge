using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>
/// 退出码 → 状态映射。硬规则：非 0 绝不显示成成功；0 但信封 ok=false 也要标失败。
/// </summary>
public sealed class ExitCodeTests
{
    private const string CommandLine = "forge detect --game D:\\G --json";

    [Theory]
    [InlineData(0, "成功")]
    [InlineData(1, "内部错误；工具缺陷，不是游戏问题")]
    [InlineData(2, "参数错误；路径或选项不合法")]
    [InlineData(3, "依赖或能力缺失；未实现的命令也返回这个码")]
    [InlineData(4, "引擎不支持")]
    [InlineData(5, "部分成功；有未完成项")]
    public void Describe_MapsKnownExitCodes(int code, string expected)
    {
        Assert.Equal(expected, ForgeExitCodes.Describe(code));
    }

    [Fact]
    public void Describe_HandlesUnknownCode()
    {
        Assert.Equal("未知退出码 42", ForgeExitCodes.Describe(42));
    }

    [Fact]
    public void Text_IncludesNumberAndMeaning()
    {
        Assert.Equal("3（依赖或能力缺失；未实现的命令也返回这个码）", ForgeExitCodes.Text(3));
    }

    [Fact]
    public void ZeroWithOkEnvelope_IsSuccess()
    {
        var outcome = ForgeRunOutcome.Completed(
            0, CommandLine, "{\"ok\":true,\"command\":\"detect\",\"message\":\"renpy\"}", null, TimeSpan.FromSeconds(1));

        Assert.Equal(ForgeRunStatus.Success, outcome.Status);
        Assert.True(outcome.IsGood);
        Assert.Null(outcome.EnvelopeError);
        Assert.Equal("renpy", outcome.Message);
    }

    [Fact]
    public void ZeroWithOkFalseEnvelope_IsFailure()
    {
        var outcome = ForgeRunOutcome.Completed(
            0, CommandLine, "{\"ok\":false,\"code\":\"E_INTERNAL\",\"message\":\"坏了\"}", null, TimeSpan.Zero);

        Assert.Equal(ForgeRunStatus.Failed, outcome.Status);
        Assert.False(outcome.IsGood);
        Assert.Equal("E_INTERNAL", outcome.Code);
    }

    [Fact]
    public void ZeroWithoutEnvelope_IsSuccessButReportsParseError()
    {
        var outcome = ForgeRunOutcome.Completed(0, CommandLine, "done", null, TimeSpan.Zero);

        Assert.Equal(ForgeRunStatus.Success, outcome.Status);
        Assert.Null(outcome.Envelope);
        Assert.NotNull(outcome.EnvelopeError);
        Assert.Equal("done", outcome.Message);
    }

    [Fact]
    public void FiveIsPartialSuccess()
    {
        var outcome = ForgeRunOutcome.Completed(5, CommandLine, null, null, TimeSpan.Zero);

        Assert.Equal(ForgeRunStatus.PartialSuccess, outcome.Status);
        Assert.True(outcome.IsGood);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NonZeroIsNeverSuccess(int code)
    {
        var outcome = ForgeRunOutcome.Completed(code, CommandLine, null, null, TimeSpan.Zero);

        Assert.Equal(ForgeRunStatus.Failed, outcome.Status);
        Assert.False(outcome.IsGood);
        Assert.Equal(code, outcome.ExitCode);
    }

    [Fact]
    public void CanceledHasNoExitCode()
    {
        var outcome = ForgeRunOutcome.Canceled(CommandLine, "partial", null, TimeSpan.FromSeconds(2));

        Assert.Equal(ForgeRunStatus.Canceled, outcome.Status);
        Assert.Equal(-1, outcome.ExitCode);
        Assert.False(outcome.IsGood);
        Assert.Equal("已取消", outcome.StatusText);
    }

    [Fact]
    public void LaunchFailedCarriesReason()
    {
        var outcome = ForgeRunOutcome.LaunchFailed(CommandLine, "找不到可执行文件：C:\\nope\\forge.exe", TimeSpan.Zero);

        Assert.Equal(ForgeRunStatus.LaunchFailed, outcome.Status);
        Assert.Equal("无法启动", outcome.StatusText);
        Assert.Equal("找不到可执行文件：C:\\nope\\forge.exe", outcome.Message);
        Assert.Null(outcome.Code);
        Assert.False(outcome.HasStderr);
    }

    [Fact]
    public void StderrIsDetected()
    {
        var outcome = ForgeRunOutcome.Completed(1, CommandLine, null, "boom\n", TimeSpan.Zero);

        Assert.True(outcome.HasStderr);
        Assert.Equal("boom", outcome.Stderr.Trim());
    }

    [Fact]
    public void MessageFallsBackToFirstNonJsonStdoutLine()
    {
        var outcome = ForgeRunOutcome.Completed(2, CommandLine, "\n{\"ok\":false}\n第三行说明\n", null, TimeSpan.Zero);

        Assert.Equal("第三行说明", outcome.Message);
    }
}
