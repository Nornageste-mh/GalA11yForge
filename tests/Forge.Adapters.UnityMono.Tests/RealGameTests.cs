using GalA11yForge.Adapters.UnityMono.Internal;
using GalA11yForge.Core.Models;
using GalA11yForge.Detection;
using GalA11yForge.Generators;

namespace GalA11yForge.Adapters.UnityMono.Tests;

/// <summary>
/// 真实游戏只读回归：D:\Steam\steamapps\common\Build（Unity Mono 6000.0.49f1）。
/// 目录不存在时由 <see cref="RealGameFactAttribute"/> 标记 Skip，并明确写“未实测”。
/// </summary>
public sealed class RealGameTests
{
    [RealGameFact]
    public async Task RealGame_Analyze_FindsFourUitTextEntryPoints_AndS9()
    {
        using var bed = new TestBed();
        var before = TestBed.Sha256(RealGamePaths.AssemblyPath);
        var context = GameContext.Create(RealGamePaths.GameDirectory, bed.Workspace, "5omeday");
        var adapter = new UnityMonoAdapter();

        var detection = await adapter.DetectAsync(context);
        Assert.True(detection.Ok, detection.Error?.ToString());
        Assert.Equal(UnityMonoAdapter.EngineId, detection.Value!.Family);

        var analysis = await adapter.AnalyzeAsync(context, detection.Value!);
        Assert.True(analysis.Ok, analysis.Error?.ToString());
        var report = analysis.Value!;
        Assert.True(report.Candidates.Count >= 4, $"candidates={report.Candidates.Count}");

        var sources = report.Candidates.Select(c => c.Source).ToList();
        Assert.Contains(sources, s => s.Contains("UIText::AnimateText", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Contains("UIText::DOText", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Contains("UIText::Choice", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Contains("UIText::HandleMessageText", StringComparison.Ordinal));

        var animate = report.Candidates.Single(c => c.Source.Contains("::AnimateText", StringComparison.Ordinal));
        Assert.True(animate.Score >= 70, $"AnimateText score={animate.Score}");
        Assert.Contains(animate.Evidence, line => line.Contains("S9+20", StringComparison.Ordinal));
        Assert.Contains(animate.Evidence, line => line.Contains("System.String text", StringComparison.Ordinal));
        Assert.Contains(animate.Evidence, line => line.Contains("System.Int32 Num", StringComparison.Ordinal));

        var dot = report.Candidates.Single(c => c.Source.Contains("::DOText", StringComparison.Ordinal));
        Assert.True(dot.Score >= 50, $"DOText score={dot.Score}");

        Assert.Equal(before, TestBed.Sha256(RealGamePaths.AssemblyPath));
    }

    [RealGameFact]
    public async Task RealGame_Plan_ProducesFourHooksWithGeneratorCompatibleTargets()
    {
        using var bed = new TestBed();
        var context = GameContext.Create(RealGamePaths.GameDirectory, bed.Workspace, "5omeday");
        var adapter = new UnityMonoAdapter();
        var detection = await adapter.DetectAsync(context);
        Assert.True(detection.Ok, detection.Error?.ToString());
        var analysis = await adapter.AnalyzeAsync(context, detection.Value!);
        Assert.True(analysis.Ok, analysis.Error?.ToString());

        var planResult = await adapter.PlanAsync(context, analysis.Value!);
        Assert.True(planResult.Ok, planResult.Error?.ToString());
        var plan = planResult.Value!;
        Assert.Equal(PlanRoute.AutomaticWithConfirmation, plan.Route);
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":AnimateText", StringComparison.Ordinal));
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":DOText", StringComparison.Ordinal));
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":Choice", StringComparison.Ordinal));
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":HandleMessageText", StringComparison.Ordinal));
        Assert.All(plan.PatchPoints, p => Assert.DoesNotContain('(', p.Target));
    }

    [RealGameFact]
    public async Task RealGame_PlanThenGenerate_WritesBepInExProject()
    {
        using var bed = new TestBed();
        bed.InstallTestAssemblyAsGame();
        var context = GameContext.Create(RealGamePaths.GameDirectory, bed.Workspace, "5omeday");
        var framework = FindFrameworkDirectory();
        Assert.False(string.IsNullOrEmpty(framework), "未找到 Framework/platform/Speech.cs，无法验证真实生成。");
        var adapter = new UnityMonoAdapter(
            new UnityMonoDetector(new EngineDetector()),
            new UnityMonoGenerator(null, new GeneratorOptions { FrameworkDirectory = framework }));

        var detection = await adapter.DetectAsync(context);
        Assert.True(detection.Ok, detection.Error?.ToString());
        var analysis = await adapter.AnalyzeAsync(context, detection.Value!);
        Assert.True(analysis.Ok, analysis.Error?.ToString());
        var plan = await adapter.PlanAsync(context, analysis.Value!);
        Assert.True(plan.Ok, plan.Error?.ToString());

        var output = Path.Combine(bed.Workspace, "games", "5omeday", "05-generate");
        var before = TestBed.Sha256(RealGamePaths.AssemblyPath);
        var generated = await adapter.GenerateAsync(context, plan.Value!, output);

        Assert.True(generated.Ok, generated.Error?.ToString());
        Assert.True(File.Exists(Path.Combine(output, "Plugin.cs")));
        Assert.True(File.Exists(Path.Combine(output, "forge.manifest.json")));
        var hookArtifact = generated.Value!.Artifacts.First(a =>
            a.RelativePath.StartsWith("Hooks/Hook_", StringComparison.Ordinal));
        Assert.EndsWith(".cs", hookArtifact.RelativePath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, hookArtifact.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(before, TestBed.Sha256(RealGamePaths.AssemblyPath));
    }

    private static string? FindFrameworkDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Framework");
            if (File.Exists(Path.Combine(candidate, "platform", "Speech.cs")))
            {
                return candidate;
            }
        }

        return null;
    }
}
