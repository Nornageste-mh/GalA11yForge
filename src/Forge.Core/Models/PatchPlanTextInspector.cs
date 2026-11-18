namespace GalA11yForge.Core.Models;

/// <summary>
/// 判断补丁方案里是否存在“带字符串参数”的补丁点。
///
/// <para>
/// 这是选点诚实闸门的共用逻辑：一个方案如果没有任何带 <c>System.String</c> 形参的补丁点，
/// 生成物只能记录调用次数、不会朗读，CLI 必须提前告诉用户，而不能把它当成自动成功。
/// </para>
///
/// <para>
/// 注意两种引擎的签名位置不同：Unity Mono 的完整 Cecil 签名写在
/// <see cref="PatchPoint.Description"/>；Unity IL2CPP 的签名写在
/// <see cref="PatchPoint.Target"/>（例如
/// <c>"Elringus.Naninovel.Runtime.dll!Naninovel.UITextPrinter::set_Text(System.String|value)"</c>）。
/// 两处都要查，否则 IL2CPP 的正常方案会被误报（task-37 P2）。
/// </para>
/// </summary>
public static class PatchPlanTextInspector
{
    private const string TextFieldMarker = "类型文本字段：";
    /// <summary>方案里是否有任一补丁点带字符串形参（任一处出现 <c>System.String</c> 即算）。</summary>
    public static bool HasStringParameter(PatchPlan? plan)
    {
        if (plan?.PatchPoints is null)
        {
            return false;
        }

        foreach (var point in plan.PatchPoints)
        {
            if (ContainsStringParameter(point.Description) || ContainsStringParameter(point.Target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 运行时才拿文本的引擎：它们的补丁点通常没有静态字符串形参，但生成物在游戏里
    /// 从 say screen / focus_list 等运行时结构取文本，不能按“静态签名缺失”判死。
    /// Ren'Py 覆盖层已由 task-53 真机证伪该误报（自动路线能读，旧的“不会朗读”警告是错话）。
    /// </summary>
    private static readonly string[] RuntimeTextEngines = ["renpy"];

    /// <summary>
    /// 是否该给用户打“没有文本来源、不会朗读”的警告：有文本来源时不警告；
    /// 引擎本身是运行时取文本（Ren'Py）时也不警告——否则会误导用户去建无必要的人工待办。
    /// </summary>
    public static bool WarnsNoTextSource(PatchPlan? plan)
    {
        if (plan is null || plan.PatchPoints.Count == 0 || HasTextSource(plan))
        {
            return false;
        }

        var engine = plan.Target?.Engine ?? string.Empty;
        foreach (var id in RuntimeTextEngines)
        {
            if (engine.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>文本里是否出现字符串形参标记（大小写不敏感，容忍 <c>system.string</c>）。</summary>

    /// <summary>
    /// 计划的补丁点是否有可朗读文本来源：方法带 System.String 参数，或描述里带“类型文本字段：…”。
    /// 后者是 0.2.5 的实例字段兜底（无字符串参数时反射读实例字段），比只看参数更贴近“能不能读”。
    /// </summary>
    public static bool HasTextSource(PatchPlan? plan)
    {
        if (plan is null || plan.PatchPoints.Count == 0)
        {
            return false;
        }

        foreach (var point in plan.PatchPoints)
        {
            if (ContainsStringParameter(point.Description) || ContainsStringParameter(point.Target))
            {
                return true;
            }

            if (ContainsTextFieldMarker(point.Description))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTextFieldMarker(string? text)
    {
        return !string.IsNullOrEmpty(text)
            && text.Contains(TextFieldMarker, StringComparison.Ordinal);
    }

    public static bool ContainsStringParameter(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Contains("System.String", StringComparison.OrdinalIgnoreCase);
}
