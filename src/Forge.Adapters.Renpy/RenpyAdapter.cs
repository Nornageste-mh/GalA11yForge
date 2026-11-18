using GalA11yForge.Adapters.Renpy.Internal;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Renpy;

/// <summary>
/// Ren'Py 适配器（零归档覆盖层方案）。
/// 五个步骤都只读游戏目录；generate 只写工作区输出目录；verify 不启动游戏。
/// </summary>
public sealed class RenpyAdapter :
    IEngineDetector,
    IEngineAnalyzer,
    IPlanner,
    ICodeGenerator,
    IVerifier
{
    public const string EngineId = "renpy";
    public const string AdapterName = "renpy";
    public const string AdapterVersion = "0.1.0.0";

    private readonly RenpyDetector _detector = new();
    private readonly RenpyAnalyzer _analyzer = new();
    private readonly RenpyPlanner _planner = new();
    private readonly RenpyGenerator _generator = new();
    private readonly RenpyVerifier _verifier = new();

    public Task<ForgeResult<EngineDetection>> DetectAsync(
        GameContext context,
        CancellationToken cancellationToken = default)
        => _detector.DetectAsync(context, cancellationToken);

    public Task<ForgeResult<AnalysisReport>> AnalyzeAsync(
        GameContext context,
        EngineDetection detection,
        CancellationToken cancellationToken = default)
        => _analyzer.AnalyzeAsync(context, detection, cancellationToken);

    public Task<ForgeResult<PatchPlan>> PlanAsync(
        GameContext context,
        AnalysisReport analysis,
        CancellationToken cancellationToken = default)
        => _planner.PlanAsync(context, analysis, cancellationToken);

    public Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => _generator.GenerateAsync(context, plan, outputDirectory, cancellationToken);

    /// <summary>
    /// 校验默认生成目录（CLI 的 verify 只有 --game，没有 --out 时走这里）。
    /// </summary>
    public Task<ForgeResult<VerificationReport>> VerifyAsync(
        GameContext context,
        DeployResult? deployResult = null,
        CancellationToken cancellationToken = default)
        => _verifier.VerifyAsync(context, deployResult, cancellationToken);

    /// <summary>
    /// 校验指定目录；供测试与 CLI 的 --out 路径使用（Core 的 IVerifier 没有 output 参数）。
    /// </summary>
    public Task<ForgeResult<VerificationReport>> VerifyDirectoryAsync(
        GameContext context,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => _verifier.VerifyDirectoryAsync(context, outputDirectory, cancellationToken);
}
