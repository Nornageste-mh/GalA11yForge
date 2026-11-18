using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Renpy.Tests;

public sealed class AnalysisPlanTests
{
    private readonly RenpyAdapter _adapter = new();

    [Fact]
    public async Task Analyze_ReportsHookCandidatesAndHonestCoverage()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var detection = await DetectAsync(fixture);

        var result = await _adapter.AnalyzeAsync(context, detection);

        Assert.True(result.Ok, result.Error?.Message);
        var report = result.Value!;
        Assert.Equal(4, report.Candidates.Count);
        Assert.Contains(report.Candidates, c => c.Kind == TextCandidateKind.Dialogue && c.Id == "tc-001");
        Assert.Contains(report.Candidates, c => c.Kind == TextCandidateKind.Choice && c.Id == "tc-002");
        Assert.Contains(report.Candidates, c => c.Kind == TextCandidateKind.Menu && c.Id == "tc-003");
        Assert.All(report.Candidates, c => Assert.StartsWith("engine:hook:", c.Source, StringComparison.Ordinal));
        Assert.All(report.Candidates, c => Assert.InRange(c.Score, 1, 100));

        var dialogue = report.Candidates.Single(c => c.Id == "tc-001");
        Assert.Contains(dialogue.Evidence, e => e.Contains("renpy/ast.py", StringComparison.Ordinal)
                                                && e.Contains("say_menu_text_filter", StringComparison.Ordinal));
        Assert.Contains(dialogue.Evidence, e => e.Contains("松散 .rpy", StringComparison.Ordinal)
                                                && e.Contains("00accessibility.rpy", StringComparison.Ordinal)
                                                && e.Contains("nvdaControllerClient", StringComparison.Ordinal));

        Assert.NotEmpty(report.Coverage.Covered);
        Assert.Contains(report.Coverage.NotCovered, item => item.Contains("rpa", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Coverage.NotCovered, item => item.Contains("图片", StringComparison.Ordinal));
        Assert.NotEmpty(report.Coverage.ManualTodo);
        Assert.Contains(report.Unverified, item => item.Contains("未启动游戏", StringComparison.Ordinal));
        Assert.NotEmpty(report.Risks);
    }

    [Fact]
    public async Task Analyze_NonRenpyDetection_ReturnsEngineUnsupported()
    {
        using var fixture = new RenpyFixture("6.99");
        var detection = new EngineDetection
        {
            Family = "unity",
            Version = "2022.3",
            Bitness = 64,
            Confidence = 0.9,
            Evidence = { "file:UnityPlayer.dll" },
        };

        var result = await _adapter.AnalyzeAsync(fixture.Context(), detection);

        Assert.False(result.Ok);
        Assert.Equal("E_ENGINE_UNSUPPORTED", result.Error!.Code);
    }

    [Fact]
    public async Task Plan_AutomaticWithEightPatchPointsAndElevenArtifacts()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var detection = await DetectAsync(fixture);
        var analysis = await AnalyzeAsync(context, detection);

        var result = await _adapter.PlanAsync(context, analysis);

        Assert.True(result.Ok, result.Error?.Message);
        var plan = result.Value!;
        Assert.Equal(PlanRoute.Automatic, plan.Route);
        Assert.Equal("renpy", plan.Target.Engine);
        Assert.Equal("8.5.2.26010301", plan.Target.EngineVersion);
        Assert.Equal(8, plan.PatchPoints.Count);
        Assert.Equal(11, plan.Artifacts.Count);
        Assert.Contains(plan.PatchPoints, p => p.Target == "game/a11y_platform/00_contract.rpy" && p.Kind == PatchPointKind.Script);
        Assert.Contains(plan.PatchPoints, p => p.Target == "a11y_say.vbs" && p.Kind == PatchPointKind.Asset);
        Assert.Contains(plan.PatchPoints, p => p.Id == "pp-008" && p.Description.Contains("不覆盖", StringComparison.Ordinal));
        Assert.All(plan.PatchPoints, p => Assert.InRange(p.Confidence, 0, 1));
        Assert.Contains(plan.ManualTodo, t => t.Id == "mt-001" && t.Blocking && t.Title.Contains("真机", StringComparison.Ordinal));
        Assert.Equal(0.85, plan.Confidence, 2);
    }

    [Fact]
    public async Task Plan_ExistingOverlay_LowersConfidenceAndRequiresUninstall()
    {
        using var fixture = new RenpyFixture("8.5").WithExistingOverlay();
        var context = fixture.Context();
        var detection = await DetectAsync(fixture);
        var analysis = await AnalyzeAsync(context, detection);

        var result = await _adapter.PlanAsync(context, analysis);

        Assert.True(result.Ok, result.Error?.Message);
        var plan = result.Value!;
        Assert.Equal(0.75, plan.Confidence, 2);
        Assert.Contains(plan.PatchPoints, p => p.Id == "pp-009" && p.Target.Contains("a11y_platform", StringComparison.Ordinal));
        Assert.Contains(plan.ManualTodo, t => t.Id == "mt-001" && t.Blocking && t.Title.Contains("卸载", StringComparison.Ordinal));
        Assert.Contains(plan.Risks, r => r.Id == "r-101" && r.Severity == RiskSeverity.High);
    }

    private async Task<EngineDetection> DetectAsync(RenpyFixture fixture)
    {
        var result = await _adapter.DetectAsync(fixture.Context());
        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!;
    }

    private async Task<AnalysisReport> AnalyzeAsync(GameContext context, EngineDetection detection)
    {
        var result = await _adapter.AnalyzeAsync(context, detection);
        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!;
    }
}
