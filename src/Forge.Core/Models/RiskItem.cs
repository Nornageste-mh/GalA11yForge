namespace GalA11yForge.Core.Models;

public enum RiskSeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>风险条目。Severity 为 low / medium / high。</summary>
public sealed class RiskItem
{
    public string Id { get; set; } = string.Empty;

    public RiskSeverity Severity { get; set; } = RiskSeverity.Medium;

    public string Description { get; set; } = string.Empty;

    public string? Mitigation { get; set; }
}

/// <summary>人工待办。Blocking 表示不完成该项就不能宣称可用。</summary>
public sealed class ManualTodoItem
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public bool Blocking { get; set; }
}
