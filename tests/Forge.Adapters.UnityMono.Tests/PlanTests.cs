using GalA11yForge.Adapters.UnityMono.Internal;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.UnityMono.Tests;

public sealed class PlanTests
{
    [Fact]
    public async Task Plan_FakeGame_ProducesAutomaticWithConfirmationHookPlan()
    {
        using var bed = new TestBed();
        bed.InstallTestAssemblyAsGame();
        var analysis = await Analyze(bed);

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.True(result.Ok, result.Error?.ToString());
        var plan = result.Value!;
        Assert.Equal(PlanRoute.AutomaticWithConfirmation, plan.Route);
        Assert.Equal(UnityMonoAdapter.EngineId, plan.Target.Engine);
        Assert.Equal(bed.GameDirectory, plan.Target.GamePath);
        Assert.Equal("6000.0.49f1", plan.Target.EngineVersion);
        Assert.Equal(64, plan.Target.Bitness);
        Assert.True(plan.PatchPoints.Count >= 4, $"points={plan.PatchPoints.Count}");
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":AnimateText", StringComparison.Ordinal));
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":DOText", StringComparison.Ordinal));
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":Choice", StringComparison.Ordinal));
        Assert.Contains(plan.PatchPoints, p => p.Target.EndsWith(":HandleMessageText", StringComparison.Ordinal));

        foreach (var point in plan.PatchPoints)
        {
            Assert.Equal(PatchPointKind.Hook, point.Kind);
            Assert.StartsWith("Assembly-CSharp.dll!/FakeGame.UIText:", point.Target, StringComparison.Ordinal);
            Assert.DoesNotContain('(', point.Target);
            Assert.Contains("完整签名（Cecil）", point.Description, StringComparison.Ordinal);
            Assert.Contains("不含台词原文", point.Description, StringComparison.Ordinal);
            Assert.InRange(point.Confidence, 0.5, 0.95);
        }

        var animate = plan.PatchPoints.Single(p => p.Target.EndsWith(":AnimateText", StringComparison.Ordinal));
        Assert.Contains("System.String text", animate.Description, StringComparison.Ordinal);
        Assert.Contains("S9 命中", animate.Description, StringComparison.Ordinal);

        Assert.NotEmpty(plan.Artifacts);
        Assert.Contains(plan.Artifacts, a => a.RelativePath == "Plugin.cs");
        Assert.Contains(plan.Artifacts, a => a.RelativePath.EndsWith(".csproj", StringComparison.Ordinal));
        Assert.Contains(plan.Artifacts, a => a.RelativePath.StartsWith("Hooks/Hook_", StringComparison.Ordinal));
        Assert.Contains(plan.Artifacts, a => a.RelativePath == "forge.manifest.json");
        Assert.NotEmpty(plan.Risks);
        Assert.NotEmpty(plan.ManualTodo);
        Assert.Contains(plan.ManualTodo, t => t.Blocking);
    }

    [Fact]
    public async Task Plan_LauncherWithTextFields_StillRaisesNoDialogueEntryRisk()
    {
        using var bed = new TestBed();
        var analysis = new AnalysisReport
        {
            GamePath = bed.GameDirectory,
            Detection = TestBed.UnityMonoDetection(),
            Candidates = new List<TextCandidate>
            {
                new()
                {
                    Id = "tc-001",
                    Source = "assembly:Assembly-CSharp.dll!/SevenDaysLauncher.PPIViewer::Start",
                    Score = 90,
                    Evidence = new List<string>
                    {
                        "签名：[Assembly-CSharp.dll] System.Void SevenDaysLauncher.PPIViewer::Start()",
                        "类型文本字段：label。",
                    },
                },
            },
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.Contains(result.Value!.Risks, r => r.Id == "R-106");
    }

    [Fact]
    public async Task Plan_EmptyCandidates_ReturnsPartial()
    {
        using var bed = new TestBed();
        var analysis = new AnalysisReport
        {
            GamePath = bed.GameDirectory,
            Detection = TestBed.UnityMonoDetection(),
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.Partial, result.Error!.Code);
    }

    [Fact]
    public async Task Plan_WrongFamily_ReturnsEngineUnsupported()
    {
        using var bed = new TestBed();
        var analysis = new AnalysisReport
        {
            GamePath = bed.GameDirectory,
            Detection = new EngineDetection { Family = "renpy" },
            Candidates = new List<TextCandidate>
            {
                new() { Id = "tc-001", Source = "assembly:Assembly-CSharp.dll!/UIText::DOText", Score = 75 },
            },
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, result.Error!.Code);
    }

    [Fact]
    public void Targets_ParseNestedTypeAndBuildGeneratorCompatibleTarget()
    {
        Assert.True(UnityMonoTargets.TryParseSource(
            "assembly:Assembly-CSharp.dll!/UIText/CharaStatus::Show",
            out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("UIText+CharaStatus", parsed!.TypeFullName);
        Assert.Equal("Show", parsed.MethodName);
        Assert.Equal("Assembly-CSharp.dll!/UIText+CharaStatus:Show", UnityMonoTargets.BuildHookTarget(parsed));

        Assert.False(UnityMonoTargets.TryParseSource("not a source", out _));
        Assert.False(UnityMonoTargets.TryParseSource("assembly:Assembly-CSharp.dll!/::Broken", out _));
    }

    [Fact]
    public async Task Plan_NoStringParameterButInstanceTextFields_IsTreatedAsReadable()
    {
        using var bed = new TestBed();
        var analysis = new AnalysisReport
        {
            GamePath = bed.GameDirectory,
            Detection = TestBed.UnityMonoDetection(),
            Candidates = new List<TextCandidate>
            {
                new()
                {
                    Id = "tc-001",
                    Source = "assembly:Assembly-CSharp.dll!/DialogueSceneManager::UpdateDialogueInfo",
                    Score = 100,
                    Evidence = new List<string>
                    {
                        "签名：[Assembly-CSharp.dll] System.Void DialogueSceneManager::UpdateDialogueInfo()",
                        "rules S1=25 S2=20; tier=A; confidence=high; ruleVersion=w2h-1",
                        "类型文本字段：dialogueText/characterNameText。",
                    },
                },
            },
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.True(result.Ok, result.Error?.ToString());
        var plan = result.Value!;
        var point = Assert.Single(plan.PatchPoints);
        Assert.Contains("实例文本字段（dialogueText/characterNameText）", point.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Risks, r => r.Id == "R-106");
    }

    [Fact]
    public async Task Plan_OnlyNonDialogueStringParameter_RaisesNoDialogueEntryRisk()
    {
        using var bed = new TestBed();
        var analysis = new AnalysisReport
        {
            GamePath = bed.GameDirectory,
            Detection = TestBed.UnityMonoDetection(),
            Candidates = new List<TextCandidate>
            {
                new()
                {
                    Id = "tc-001",
                    Source = "assembly:Assembly-CSharp.dll!/Game.LocationUI::SetLocation",
                    Score = 80,
                    Evidence = new List<string>
                    {
                        "签名：[Assembly-CSharp.dll] System.Void Game.LocationUI::SetLocation(System.String locationText)",
                        "rules S1=25 S2=20; tier=B; confidence=medium; ruleVersion=w2h-1",
                    },
                },
            },
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.Contains(result.Value!.Risks, r => r.Id == "R-106");
    }

    [Fact]
    public async Task Plan_DialogueStringParameter_DoesNotRaiseNoDialogueEntryRisk()
    {
        using var bed = new TestBed();
        var analysis = new AnalysisReport
        {
            GamePath = bed.GameDirectory,
            Detection = TestBed.UnityMonoDetection(),
            Candidates = new List<TextCandidate>
            {
                new()
                {
                    Id = "tc-001",
                    Source = "assembly:Assembly-CSharp.dll!/Game.DialogueUI::SetLine",
                    Score = 80,
                    Evidence = new List<string>
                    {
                        "签名：[Assembly-CSharp.dll] System.Void Game.DialogueUI::SetLine(System.String text)",
                        "rules S1=25 S2=20; tier=B; confidence=medium; ruleVersion=w2h-1",
                    },
                },
            },
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.DoesNotContain(result.Value!.Risks, r => r.Id == "R-106");
    }

    [Fact]
    public async Task Plan_ReservesSlotForDialogueCandidate_WhenMenusFillTheQuota()
    {
        using var bed = new TestBed();
        var candidates = new List<TextCandidate>
        {
            new()
            {
                Id = "tc-001",
                Source = "assembly:Assembly-CSharp.dll!/Game.DialogueUI::ShowText",
                Score = 60,
                Kind = TextCandidateKind.Dialogue,
                Evidence = new List<string> { "签名：[Assembly-CSharp.dll] System.Void Game.DialogueUI::ShowText()" },
            },
        };

        for (var i = 2; i <= 7; i++)
        {
            candidates.Add(new TextCandidate
            {
                Id = "tc-00" + i,
                Source = "assembly:Assembly-CSharp.dll!/Game.MenuUI::OpenPanel" + i,
                Score = 100,
                Kind = TextCandidateKind.Menu,
                Evidence = new List<string> { "签名：[Assembly-CSharp.dll] System.Void Game.MenuUI::OpenPanel" + i + "()" },
            });
        }

        var analysis = new AnalysisReport
        {
            GamePath = bed.GameDirectory,
            Detection = TestBed.UnityMonoDetection(),
            Candidates = candidates,
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.True(result.Ok, result.Error?.ToString());
        var plan = result.Value!;
        Assert.Equal(6, plan.PatchPoints.Count);
        Assert.Contains(plan.PatchPoints, p => p.Target!.EndsWith("Game.DialogueUI:ShowText", StringComparison.Ordinal));
    }

    private static async Task<AnalysisReport> Analyze(TestBed bed)
    {
        var result = await new UnityMonoAnalyzer().AnalyzeAsync(bed.Context(), TestBed.UnityMonoDetection());
        Assert.True(result.Ok, result.Error?.ToString());
        return result.Value!;
    }
}
