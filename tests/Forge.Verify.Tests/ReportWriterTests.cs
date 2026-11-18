using System.Text;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Verify.Models;
using GalA11yForge.Verify.Reporting;

namespace GalA11yForge.Verify.Tests;

public sealed class ReportWriterTests
{
    [Fact]
    public async Task Writes_json_and_markdown_with_required_sections()
    {
        using var fixture = new VerifyFixture();
        var verification = await fixture.RunAsync();
        Assert.True(verification.Ok, verification.Error?.Message);

        var outputDirectory = fixture.Temp.Sub("reports");
        var writer = new VerifyReportWriter(fixture.GameDir);
        var written = await writer.WriteAsync(verification.Value!, outputDirectory);

        Assert.True(written.Ok, written.Error?.Message);
        Assert.True(File.Exists(written.Value!.JsonPath));
        Assert.True(File.Exists(written.Value.MarkdownPath));

        var json = await File.ReadAllTextAsync(written.Value.JsonPath);
        Assert.Contains("\"artifact\": \"verify\"", json, StringComparison.Ordinal);
        Assert.Contains("\"overall\": \"pass\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"pass\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);

        var markdown = await File.ReadAllTextAsync(written.Value.MarkdownPath);
        Assert.Contains("## 检查结果", markdown, StringComparison.Ordinal);
        Assert.Contains("## 覆盖声明", markdown, StringComparison.Ordinal);
        Assert.Contains("## 未验证项", markdown, StringComparison.Ordinal);
        Assert.Contains("## 清理与还原", markdown, StringComparison.Ordinal);
        Assert.Contains("## 环境事实", markdown, StringComparison.Ordinal);
        Assert.Contains("## 指标", markdown, StringComparison.Ordinal);
        Assert.Contains("## 证据", markdown, StringComparison.Ordinal);
        Assert.Contains("skip 表示该项本次未验证，不是通过", markdown, StringComparison.Ordinal);

        var bytes = await File.ReadAllBytesAsync(written.Value.JsonPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task Json_roundtrips_through_forge_json()
    {
        using var fixture = new VerifyFixture();
        var verification = await fixture.RunAsync();
        var outputDirectory = fixture.Temp.Sub("roundtrip");
        var written = await new VerifyReportWriter(fixture.GameDir)
            .WriteAsync(verification.Value!, outputDirectory);
        Assert.True(written.Ok);

        var json = await File.ReadAllTextAsync(written.Value!.JsonPath);
        var roundtrip = ForgeJson.Deserialize<VerifyReport>(json);

        Assert.NotNull(roundtrip);
        Assert.Equal(VerifyOverall.Pass, roundtrip!.Overall);
        Assert.Equal(17, roundtrip.Checks.Count);
        Assert.All(roundtrip.Checks, check => Assert.Equal(VerifyCheckStatus.Pass, check.Status));
        Assert.Equal(VerifyMode.Install, roundtrip.Mode);
    }

    [Fact]
    public async Task Refuses_to_write_into_game_directory()
    {
        using var fixture = new VerifyFixture();
        var verification = await fixture.RunAsync();
        var outputDirectory = Path.Combine(fixture.GameDir, "reports");
        var writer = new VerifyReportWriter(fixture.GameDir);

        var written = await writer.WriteAsync(verification.Value!, outputDirectory);

        Assert.False(written.Ok);
        Assert.Equal("E_GAME_WRITE_DENIED", written.Error!.Code);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task Empty_unverified_uses_exact_phrase()
    {
        using var fixture = new VerifyFixture();
        var report = new VerifyReport
        {
            GeneratedAt = "2026-11-14T00:00:00+08:00",
            Overall = VerifyOverall.Pass,
            Checks = new List<VerifyCheck>
            {
                new()
                {
                    Id = "x-01",
                    Name = "示例检查",
                    Priority = VerifyPriority.P0,
                    Status = VerifyCheckStatus.Pass,
                    Detail = "ok",
                },
            },
        };

        var written = await new VerifyReportWriter().WriteAsync(report, fixture.Temp.Sub("empty"));
        Assert.True(written.Ok);

        var markdown = await File.ReadAllTextAsync(written.Value!.MarkdownPath);
        Assert.Contains("（无显式未验证项；这不等于全部已验证）", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_check_is_rendered_as_fail_not_pass()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.LogPath);
        var verification = await fixture.RunAsync();
        Assert.Equal(VerifyOverall.Fail, verification.Value!.Overall);

        var outputDirectory = fixture.Temp.Sub("skips");
        var written = await new VerifyReportWriter(fixture.GameDir)
            .WriteAsync(verification.Value, outputDirectory);
        Assert.True(written.Ok);

        var markdown = await File.ReadAllTextAsync(written.Value!.MarkdownPath);
        Assert.Contains("| 失败 |", markdown, StringComparison.Ordinal);
        Assert.Contains("b-06", markdown, StringComparison.Ordinal);
        Assert.Contains("未通过", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("结论：**通过（离线检查全部通过）**", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_and_write_combines_verification_and_rendering()
    {
        using var fixture = new VerifyFixture();
        var outputDirectory = fixture.Temp.Sub("combined");

        var written = await VerifyReportWriter.VerifyAndWriteAsync(fixture.Options(), outputDirectory);

        Assert.True(written.Ok, written.Error?.Message);
        Assert.True(File.Exists(written.Value!.JsonPath));
        Assert.True(File.Exists(written.Value.MarkdownPath));
        var json = await File.ReadAllTextAsync(written.Value.JsonPath, Encoding.UTF8);
        Assert.Contains("\"overall\": \"pass\"", json, StringComparison.Ordinal);
    }
}
