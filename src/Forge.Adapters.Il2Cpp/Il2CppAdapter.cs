using GalA11yForge.Adapters.Il2Cpp.Internal;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Il2Cpp;

/// <summary>
/// Unity IL2CPP 适配器（BepInEx 6 + Il2CppInterop + HarmonyX 路线）。
///
/// <para>
/// 四个接口一次实现，方便 CLI 以单个类型注册。任何异常都在适配器内部转成
/// <see cref="ForgeResult{T}"/> 的失败结果，不向调用方抛。
/// </para>
/// <para>
/// 能力边界（务必如实对外）：静态分析只能拿到类型/方法/参数名/属性/事件 adder，
/// 拿不到方法体与字符串常量；运行期是否真的被调用必须靠真机探针（R18 的 10 条通道表）。
/// </para>
/// </summary>
public sealed class Il2CppAdapter : IEngineDetector, IEngineAnalyzer, IPlanner, ICodeGenerator
{
    public const string EngineId = "unity_il2cpp";
    public const string AdapterName = "Unity IL2CPP (BepInEx 6)";
    public const string AdapterVersion = "0.1.0.0";

    private readonly Il2CppDetector _detector;
    private readonly Il2CppAnalysisService _analysis = new();
    private readonly Il2CppPlanner _planner = new();
    private readonly Il2CppGenerationBridge _bridge = new();

    public Il2CppAdapter()
        : this(new Il2CppDetector())
    {
    }

    internal Il2CppAdapter(Il2CppDetector detector)
    {
        _detector = detector ?? new Il2CppDetector();
    }

    public Task<ForgeResult<EngineDetection>> DetectAsync(GameContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return _detector.DetectAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.Internal, "IL2CPP 检测异常：" + ex.Message,
                "这是适配器缺陷，请附上游戏目录与日志反馈。"));
        }
    }

    public Task<ForgeResult<AnalysisReport>> AnalyzeAsync(
        GameContext context,
        EngineDetection detection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (context is null)
            {
                return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                    ForgeErrorCodes.ArgumentInvalid, "上下文为空，无法分析 IL2CPP 文本管线。"));
            }

            if (detection is null)
            {
                return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                    ForgeErrorCodes.ArgumentInvalid, "引擎识别结果为空，无法分析 IL2CPP 文本管线。",
                    "先跑 detect 步骤。"));
            }

            if (!File.Exists(Path.Combine(context.GameDirectory, "GameAssembly.dll")))
            {
                return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                    ForgeErrorCodes.EngineUnsupported,
                    "目录里没有 GameAssembly.dll，不是 IL2CPP 游戏目录：" + context.GameDirectory,
                    "确认 --game-dir 指向游戏根目录。"));
            }

            var options = Il2CppOptions.From(context);
            var layout = _analysis.Probe(context);
            cancellationToken.ThrowIfCancellationRequested();

            if (!layout.HasInterop && !layout.HasDump)
            {
                if (!options.AutoDump)
                {
                    var disabled = Il2CppAnalysisService.NoStaticInput(context);
                    return Task.FromResult(ForgeResult<AnalysisReport>.Failure(disabled));
                }

                // 0.2.11：没有任何静态输入时先自动跑 Il2CppDumper——把“用户自己去装工具/导 dump”
                // 变成流水线内一步。产物只写工作区，游戏目录仍只读。
                var attempt = Il2CppAutoDumper.Produce(context, options, layout, cancellationToken);
                if (attempt.DumpPath is null)
                {
                    var error = Il2CppAutoDumper.NoInputError(layout, attempt);
                    return Task.FromResult(ForgeResult<AnalysisReport>.Failure(error));
                }

                layout = _analysis.ProbeWithDump(context, attempt.DumpPath);
                var note = attempt.Note ?? "已自动生成 dump.cs。";
                var report = _analysis.AnalyzeWithLayout(context, detection, layout, cancellationToken);
                report.Unverified.Insert(0, note + " 自动 dump 的产物未经人工核对；如分析结果异常，请先人工看一遍 dump.cs。");
                return Task.FromResult(ForgeResult<AnalysisReport>.Success(report));
            }

            var reportWithInput = _analysis.AnalyzeWithLayout(context, detection, layout, cancellationToken);
            return Task.FromResult(ForgeResult<AnalysisReport>.Success(reportWithInput));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.Internal, "IL2CPP 分析异常：" + ex.Message,
                "这是适配器缺陷，请附上游戏目录与日志反馈。"));
        }
    }

    public Task<ForgeResult<PatchPlan>> PlanAsync(
        GameContext context,
        AnalysisReport analysis,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (context is null)
            {
                return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                    ForgeErrorCodes.ArgumentInvalid, "上下文为空，无法生成 IL2CPP 补丁方案。"));
            }

            if (analysis is null)
            {
                return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                    ForgeErrorCodes.ArgumentInvalid, "分析结果为空，无法生成 IL2CPP 补丁方案。",
                    "先跑 analyze 步骤。"));
            }

            var options = Il2CppOptions.From(context);
            var layout = _analysis.Probe(context);
            var plan = _planner.Plan(context, analysis, analysis.Detection, options, layout);
            return Task.FromResult(ForgeResult<PatchPlan>.Success(plan));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.Internal, "IL2CPP 规划异常：" + ex.Message,
                "这是适配器缺陷，请附上分析报告与日志反馈。"));
        }
    }

    public Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return _bridge.GenerateAsync(context, plan, outputDirectory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Internal, "IL2CPP 代码生成异常：" + ex.Message,
                "这是适配器缺陷，请附上补丁方案与日志反馈。"));
        }
    }
}
