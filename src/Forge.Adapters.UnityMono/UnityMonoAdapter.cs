using GalA11yForge.Adapters.UnityMono.Internal;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Detection;
using GalA11yForge.Generators;

namespace GalA11yForge.Adapters.UnityMono;

/// <summary>
/// Unity Mono 适配器（引擎 id <c>unity_mono</c>）。
/// detect 复用 Forge.Detection；analyze 用 Cecil 只读扫描；plan 生成 hook 计划；
/// generate 委托 Forge.Generators.UnityMonoGenerator。
/// </summary>
public sealed class UnityMonoAdapter : IEngineDetector, IEngineAnalyzer, IPlanner, ICodeGenerator
{
    public const string EngineId = "unity_mono";
    public const string AdapterName = "unity_mono";
    public const string AdapterVersion = "0.1.0.0";

    private readonly IEngineDetector _detector;
    private readonly IEngineAnalyzer _analyzer;
    private readonly IPlanner _planner;
    private readonly ICodeGenerator _generator;

    public UnityMonoAdapter()
        : this(
            new UnityMonoDetector(new EngineDetector()),
            new UnityMonoAnalyzer(),
            new UnityMonoPlanner(),
            new UnityMonoGenerationBridge(new UnityMonoGenerator()))
    {
    }

    /// <summary>测试/CLI 注入用：只替换生成器，其余用默认实现。</summary>
    public UnityMonoAdapter(ICodeGenerator generator)
        : this(
            new UnityMonoDetector(new EngineDetector()),
            new UnityMonoAnalyzer(),
            new UnityMonoPlanner(),
            new UnityMonoGenerationBridge(generator))
    {
    }

    /// <summary>测试/CLI 注入用。</summary>
    public UnityMonoAdapter(IEngineDetector detector, ICodeGenerator generator)
        : this(detector, new UnityMonoAnalyzer(), new UnityMonoPlanner(), new UnityMonoGenerationBridge(generator))
    {
    }

    /// <summary>完全注入（测试用）。</summary>
    public UnityMonoAdapter(
        IEngineDetector detector,
        IEngineAnalyzer analyzer,
        IPlanner planner,
        ICodeGenerator generator)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public async Task<ForgeResult<EngineDetection>> DetectAsync(
        GameContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _detector.DetectAsync(context, cancellationToken).ConfigureAwait(false);
            return result ?? ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.Internal,
                "检测器返回了空结果。");
        }
        catch (OperationCanceledException)
        {
            return ForgeResult<EngineDetection>.Failure(ForgeErrorCodes.Internal, "检测已取消。");
        }
        catch (Exception ex)
        {
            return ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 检测异常：{ex.Message}",
                "保留游戏目录不变，把该消息交给开发者。");
        }
    }

    public async Task<ForgeResult<AnalysisReport>> AnalyzeAsync(
        GameContext context,
        EngineDetection detection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _analyzer.AnalyzeAsync(context, detection, cancellationToken).ConfigureAwait(false);
            return result ?? ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.Internal,
                "分析器返回了空结果。");
        }
        catch (OperationCanceledException)
        {
            return ForgeResult<AnalysisReport>.Failure(ForgeErrorCodes.Internal, "分析已取消。");
        }
        catch (Exception ex)
        {
            return ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 分析异常：{ex.Message}",
                "保留游戏目录不变，把该消息交给开发者。");
        }
    }

    public async Task<ForgeResult<PatchPlan>> PlanAsync(
        GameContext context,
        AnalysisReport analysis,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _planner.PlanAsync(context, analysis, cancellationToken).ConfigureAwait(false);
            return result ?? ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.Internal,
                "规划器返回了空结果。");
        }
        catch (OperationCanceledException)
        {
            return ForgeResult<PatchPlan>.Failure(ForgeErrorCodes.Internal, "规划已取消。");
        }
        catch (Exception ex)
        {
            return ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 规划异常：{ex.Message}",
                "把该消息和 analysis 报告交给开发者。");
        }
    }

    public async Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _generator.GenerateAsync(context, plan, outputDirectory, cancellationToken)
                .ConfigureAwait(false);
            return result ?? ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Internal,
                "生成器返回了空结果。");
        }
        catch (OperationCanceledException)
        {
            return ForgeResult<GenerationResult>.Failure(ForgeErrorCodes.Internal, "生成已取消。");
        }
        catch (Exception ex)
        {
            return ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 生成异常：{ex.Message}",
                "保留输出目录现场，把该消息交给开发者。");
        }
    }
}
