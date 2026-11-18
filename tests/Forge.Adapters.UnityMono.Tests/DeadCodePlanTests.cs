using GalA11yForge.Adapters.UnityMono.Internal;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.UnityMono.Tests;

// 0.2.64（F4 弱判定）：本程序集内调用者 0 个的钩子可能是死代码，
// 计划应把有调用者的入口排前面，并给出 R-107 提醒。
public sealed class DeadCodePlanTests
{
    [Fact]
    public async Task Plan_PrefersLiveHookOverDeadCode_AndRaisesR107()
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
                    Source = "assembly:Assembly-CSharp.dll!/ShiMeng.DialogueSceneManager::UpdateDialogueInfo",
                    Score = 100,
                    Evidence = new List<string>
                    {
                        "签名：[Assembly-CSharp.dll] System.Void ShiMeng.DialogueSceneManager::UpdateDialogueInfo()",
                        "程序集内调用者 0 个：（无）",
                        "类型文本字段：dialogueText。",
                    },
                },
                new()
                {
                    Id = "tc-002",
                    Source = "assembly:Assembly-CSharp.dll!/ShiMeng.DialogueV2::ShowText",
                    Score = 90,
                    Evidence = new List<string>
                    {
                        "签名：[Assembly-CSharp.dll] System.Void ShiMeng.DialogueV2::ShowText(System.String text)",
                        "程序集内调用者 3 个：ShiMeng.DialogueV2::Next / ShiMeng.DialogueRunner::Update / ShiMeng.Main::Start",
                    },
                },
            },
        };

        var result = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);

        Assert.True(result.Ok, result.Error?.ToString());
        var plan = result.Value!;
        Assert.NotEmpty(plan.PatchPoints);
        Assert.EndsWith("ShiMeng.DialogueV2:ShowText", plan.PatchPoints[0].Target, StringComparison.Ordinal);
        Assert.Contains(plan.Risks, risk => risk.Id == "R-107");
        var risk = plan.Risks.Single(r => r.Id == "R-107");
        Assert.Contains("死代码", risk.Description, StringComparison.Ordinal);
        Assert.Contains("UpdateDialogueInfo", risk.Description, StringComparison.Ordinal);
    }
}
