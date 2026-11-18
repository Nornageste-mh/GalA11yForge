using GalA11yForge.Core.Models;

namespace GalA11yForge.Core.Tests;

public sealed class PlanQualityInspectorTests
{
    [Fact]
    public void Plan_WithNoDialogueEntryRisk_IsFlagged()
    {
        // 0.2.48 复盘：Utage 游戏的计划只有系统对话框入口时，CLI 必须能识别出来并提醒。
        var plan = new PatchPlan
        {
            GameSlug = "mystery-lover-2",
            Risks =
            [
                new RiskItem { Id = "R-101", Severity = RiskSeverity.High, Description = "运行期未复验。" },
                new RiskItem
                {
                    Id = PlanQualityInspector.NoDialogueEntryRiskId,
                    Severity = RiskSeverity.High,
                    Description = "本计划里没有已识别的正文入口（台词管线）。",
                },
            ],
        };

        Assert.True(PlanQualityInspector.HasNoDialogueEntryRisk(plan));
        Assert.Contains("正文入口", PlanQualityInspector.DescribeNoDialogueEntry(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WithDialogueEntryRiskOnly_IsNotFlagged()
    {
        var plan = new PatchPlan
        {
            GameSlug = "galmaster",
            Risks =
            [
                new RiskItem { Id = "R-101", Severity = RiskSeverity.High, Description = "运行期未复验。" },
            ],
        };

        Assert.False(PlanQualityInspector.HasNoDialogueEntryRisk(plan));
    }

    [Fact]
    public void NullOrEmptyPlan_IsNotFlagged()
    {
        Assert.False(PlanQualityInspector.HasNoDialogueEntryRisk(null));
        Assert.False(PlanQualityInspector.HasNoDialogueEntryRisk(new PatchPlan { GameSlug = "x" }));
    }
}
