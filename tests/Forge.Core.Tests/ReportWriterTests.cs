using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Reports;

namespace GalA11yForge.Core.Tests;

public sealed class ReportWriterTests
{
    [Fact]
    public async Task WriteAsync_CreatesJsonAndMarkdown_WithCoverageAndUnverified()
    {
        using var temp = new TempFolder();
        var writer = new JsonMarkdownReportWriter();
        var report = TestData.Report();

        var result = await writer.WriteAsync(report, temp.Sub("reports"));

        Assert.True(result.Ok);
        Assert.True(File.Exists(result.Value!.JsonPath));
        Assert.True(File.Exists(result.Value.MarkdownPath));

        var markdown = await File.ReadAllTextAsync(result.Value.MarkdownPath);
        Assert.Contains("## 覆盖声明", markdown);
        Assert.Contains("## 未验证项", markdown);
        Assert.Contains("选择支 UI", markdown);
        Assert.Contains("未在真机运行验证朗读顺序", markdown);
        Assert.Contains("未实测项不作为通过依据", markdown);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.Value.JsonPath));
        Assert.True(json.RootElement.TryGetProperty("coverage", out var coverage));
        Assert.Equal(2, coverage.GetProperty("covered").GetArrayLength());
        Assert.Equal(2, coverage.GetProperty("notCovered").GetArrayLength());
        Assert.True(json.RootElement.TryGetProperty("unverified", out var unverified));
        Assert.Equal(1, unverified.GetArrayLength());
    }

    [Fact]
    public async Task WriteAsync_EmptyReport_StillDeclaresNoCoverage()
    {
        using var temp = new TempFolder();
        var writer = new JsonMarkdownReportWriter();
        var report = new ReportDocument
        {
            GameSlug = "empty",
            Coverage = new CoverageReport(),
            Unverified = [],
        };

        var result = await writer.WriteAsync(report, temp.Sub("reports"));

        Assert.True(result.Ok);
        var markdown = await File.ReadAllTextAsync(result.Value!.MarkdownPath);
        Assert.Contains("覆盖：已覆盖 0 项", markdown);
        Assert.Contains("（无）", markdown); // 空的风险/待办分类也必须明确写出来
        Assert.Contains("（无显式未验证项；这不等于全部已验证）", markdown);
        Assert.False(string.IsNullOrWhiteSpace(report.GeneratedAt));
    }

    [Fact]
    public async Task WriteAsync_RejectsGameDirectory()
    {
        using var temp = new TempFolder();
        var gameDirectory = temp.Sub("Game");
        var writer = new JsonMarkdownReportWriter(gameDirectory: gameDirectory);

        var result = await writer.WriteAsync(new ReportDocument(), Path.Combine(gameDirectory, "reports"));

        Assert.False(result.Ok);
        Assert.Equal("E_GAME_WRITE_DENIED", result.Error!.Code);
    }

    [Fact]
    public async Task WriteAsync_RejectsEmptyOutputDirectory()
    {
        var writer = new JsonMarkdownReportWriter();
        var result = await writer.WriteAsync(new ReportDocument(), " ");

        Assert.False(result.Ok);
        Assert.Equal("E_ARG_INVALID", result.Error!.Code);
    }
}
