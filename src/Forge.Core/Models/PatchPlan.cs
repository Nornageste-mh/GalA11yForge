namespace GalA11yForge.Core.Models;

/// <summary>路线分级：A 自动通过、B 自动+人工确认、C 半自动、D 不支持。</summary>
public enum PlanRoute
{
    Automatic = 0,
    AutomaticWithConfirmation = 1,
    SemiAutomatic = 2,
    Unsupported = 3,
}

/// <summary>补丁方案的目标信息。</summary>
public sealed class PlanTarget
{
    public string? GamePath { get; set; }

    /// <summary>引擎 id，与 EngineDetection.Family 一致。</summary>
    public string Engine { get; set; } = "unknown";

    public string? EngineVersion { get; set; }

    public int Bitness { get; set; }
}

/// <summary>
/// 补丁方案（02-decide/plan.json）。包含目标、补丁点、计划生成物、风险、人工待办；
/// 任何“机器搞不定”的部分必须进入 ManualTodo，而不是硬编码猜测。
/// </summary>
public sealed class PatchPlan
{
    public int SchemaVersion { get; set; } = 1;

    public string? GameSlug { get; set; }

    public PlanRoute Route { get; set; } = PlanRoute.Unsupported;

    public PlanTarget Target { get; set; } = new();

    /// <summary>规划置信度 0..1。</summary>
    public double Confidence { get; set; }

    public List<PatchPoint> PatchPoints { get; set; } = new();

    public List<GeneratedArtifact> Artifacts { get; set; } = new();

    public List<RiskItem> Risks { get; set; } = new();

    public List<ManualTodoItem> ManualTodo { get; set; } = new();
}
