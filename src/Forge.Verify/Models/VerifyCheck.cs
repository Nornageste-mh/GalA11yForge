namespace GalA11yForge.Verify.Models;

/// <summary>
/// 单项检查状态。skip 表示“本次没有验证”，不是通过。
/// </summary>
public enum VerifyCheckStatus
{
    Pass = 0,
    Fail = 1,
    Skip = 2,
}

/// <summary>
/// 检查优先级。P0 任一失败会让整体结论变为 fail；P1/P2 失败只会让整体变为 partial。
/// 优先级沿用验收标准 A/B/C 的分级口径。
/// </summary>
public static class VerifyPriority
{
    public const string P0 = "P0";
    public const string P1 = "P1";
    public const string P2 = "P2";

    public static bool IsKnown(string? value) =>
        string.Equals(value, P0, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, P1, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, P2, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 一条验证检查。字段命名与 docs/09 verify.json 对齐。
/// </summary>
public sealed class VerifyCheck
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Priority { get; set; } = VerifyPriority.P1;

    public VerifyCheckStatus Status { get; set; } = VerifyCheckStatus.Skip;

    public string Detail { get; set; } = string.Empty;

    public List<string> Evidence { get; set; } = new();

    public Dictionary<string, double> Metrics { get; set; } = new();
}
