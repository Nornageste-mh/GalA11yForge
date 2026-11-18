using System.Text.Json;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Logging;

namespace GalA11yForge.Core.Tests;

public sealed class FlowLoggerTests
{
    [Fact]
    public void JsonlFlowLogger_WritesOneJsonObjectPerLine()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Root, "reports", "flow.jsonl");

        using (var logger = new JsonlFlowLogger(path))
        {
            logger.Log(FlowLogLevel.Info, "doctor", "环境检查开始", new Dictionary<string, string> { ["workspace"] = temp.Root });
            logger.Log(FlowLogLevel.Warning, "detect", "未找到适配器");
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("info", first.RootElement.GetProperty("level").GetString());
        Assert.Equal("doctor", first.RootElement.GetProperty("stage").GetString());
        Assert.True(first.RootElement.TryGetProperty("timestamp", out _));
        Assert.Equal(temp.Root, first.RootElement.GetProperty("data").GetProperty("workspace").GetString());

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("warning", second.RootElement.GetProperty("level").GetString());
        Assert.False(second.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void NullFlowLogger_AcceptsEverything()
    {
        NullFlowLogger.Instance.Log(FlowLogLevel.Error, "test", "message");
    }
}
