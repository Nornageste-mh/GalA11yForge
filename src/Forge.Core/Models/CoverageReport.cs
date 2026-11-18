namespace GalA11yForge.Core.Models;

/// <summary>
/// 覆盖声明。每一步都必须回答“覆盖了什么、没覆盖什么、下一步人工怎么做”，
/// 不允许用空列表暗示“全部覆盖”。
/// </summary>
public sealed class CoverageReport
{
    public List<string> Covered { get; set; } = new();

    public List<string> NotCovered { get; set; } = new();

    public List<string> ManualTodo { get; set; } = new();
}
