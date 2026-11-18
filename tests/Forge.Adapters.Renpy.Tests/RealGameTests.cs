using GalA11yForge.Core.Models;
using Xunit.Sdk;

namespace GalA11yForge.Adapters.Renpy.Tests;

/// <summary>
/// 真实安装目录的只读用例。不存在则动态 Skip；存在时只读、不启动游戏，
/// 生成物写到临时工作区，并在前后对比游戏目录的文件清单与大小。
/// </summary>
public sealed class RealGameTests
{
    private readonly RenpyAdapter _adapter = new();

    [Theory]
    [InlineData(@"D:\Steam\steamapps\common\Doki Doki Literature Club", "6.99.12.4", "real-ddlc")]
    [InlineData(@"D:\Steam\steamapps\common\mei", "8.5.2.26010301", "real-mei")]
    [InlineData(@"D:\Steam\steamapps\common\永恒与星辰与日常", "8.6.0.26032101", "real-obe")]
    public async Task RealGame_DetectAnalyzePlanGenerateVerify_IsReadOnly(
        string gamePath,
        string expectedVersion,
        string slug)
    {
        if (!Directory.Exists(gamePath))
        {
            throw SkipException.ForSkip($"本机未安装该游戏：{gamePath}");
        }

        var before = Snapshot(gamePath);
        using var temp = new TempWorkspace(slug);
        var context = GameContext.Create(gamePath, temp.Workspace, slug);

        var detection = await _adapter.DetectAsync(context, CancellationToken.None);
        Assert.True(detection.Ok, detection.Error?.Message);
        Assert.Equal("renpy", detection.Value!.Family);
        Assert.Equal(expectedVersion, detection.Value.Version);
        Assert.True(detection.Value.Confidence >= 0.85, $"置信度 {detection.Value.Confidence}");

        var analysis = await _adapter.AnalyzeAsync(context, detection.Value, CancellationToken.None);
        Assert.True(analysis.Ok, analysis.Error?.Message);
        Assert.NotEmpty(analysis.Value!.Candidates);
        Assert.Contains(analysis.Value.Unverified, item => item.Contains("未启动游戏", StringComparison.Ordinal));

        var plan = await _adapter.PlanAsync(context, analysis.Value, CancellationToken.None);
        Assert.True(plan.Ok, plan.Error?.Message);
        Assert.Equal(PlanRoute.Automatic, plan.Value!.Route);
        Assert.Equal(11, plan.Value.Artifacts.Count);
        if (slug == "real-obe")
        {
            Assert.Contains(plan.Value.PatchPoints, p => p.Id == "pp-009");
            Assert.Contains(plan.Value.ManualTodo, t => t.Id == "mt-001" && t.Blocking);
        }

        var output = Path.Combine(temp.Workspace, "generated");
        var generated = await _adapter.GenerateAsync(context, plan.Value, output, CancellationToken.None);
        Assert.True(generated.Ok, generated.Error?.Message);
        Assert.Equal(11, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);

        var verified = await _adapter.VerifyDirectoryAsync(context, output, CancellationToken.None);
        Assert.True(verified.Ok, verified.Error?.Message);
        Assert.True(
            verified.Value!.Passed,
            string.Join("; ", verified.Value.Checks.Where(c => !c.Passed).Select(c => c.Name + ":" + c.Detail)));

        Assert.Equal(before, Snapshot(gamePath));
    }

    private static string[] Snapshot(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}")
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
}
