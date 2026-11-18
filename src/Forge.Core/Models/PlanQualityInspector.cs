namespace GalA11yForge.Core.Models;

/// <summary>
/// 计划质量检查：判断“这份补丁计划有没有可能读到正文”。
///
/// 背景（0.2.48 复盘）：用户给 Utage 引擎的《Mystery Lover 2》装了补丁，
/// CLI 报“安装完成”，但游戏里一句台词都不读——因为计划里只有系统对话框的入口，
/// 没有任何正文（台词管线）入口。把判断集中在这里，CLI/GUI 就能在安装前提醒。
/// </summary>
public static class PlanQualityInspector
{
    /// <summary>Unity Mono 计划里“没有已识别正文入口”的风险编号。</summary>
    public const string NoDialogueEntryRiskId = "R-106";

    /// <summary>计划里是否带着“没有正文入口”的高风险项。</summary>
    public static bool HasNoDialogueEntryRisk(PatchPlan? plan) =>
        plan?.Risks is { Count: > 0 } risks
        && risks.Any(risk => string.Equals(risk.Id, NoDialogueEntryRiskId, StringComparison.OrdinalIgnoreCase));

    /// <summary>给用户看的一句话说明（不含台词原文）。</summary>
    public static string DescribeNoDialogueEntry() =>
        "这个计划里没有已识别的正文入口（台词管线）：装好后可能只有对话框/菜单弹出时才会朗读，正常剧情可能一句都不读。";
}
