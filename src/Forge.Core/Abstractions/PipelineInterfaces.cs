using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Abstractions;

/// <summary>步骤一：识别引擎。实现必须给出证据；无法确认时返回低置信度并声明未验证项，而不是猜测。</summary>
public interface IEngineDetector
{
    Task<ForgeResult<EngineDetection>> DetectAsync(GameContext context, CancellationToken cancellationToken = default);
}

/// <summary>步骤二：分析文本管线，产出候选与覆盖声明。</summary>
public interface IEngineAnalyzer
{
    Task<ForgeResult<AnalysisReport>> AnalyzeAsync(
        GameContext context,
        EngineDetection detection,
        CancellationToken cancellationToken = default);
}

/// <summary>步骤三：把分析结果变成可执行的补丁方案（含风险与人工待办）。</summary>
public interface IPlanner
{
    Task<ForgeResult<PatchPlan>> PlanAsync(
        GameContext context,
        AnalysisReport analysis,
        CancellationToken cancellationToken = default);
}

/// <summary>步骤四：生成补丁工程源码。产物必须落在 outputDirectory 之内。</summary>
public interface ICodeGenerator
{
    Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>步骤五：构建补丁工程。缺少编译器/依赖时返回 E_DEPENDENCY_MISSING，不得假成功。</summary>
public interface IBuilder
{
    Task<ForgeResult<BuildResult>> BuildAsync(
        GameContext context,
        GenerationResult generation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 步骤六：部署。实现必须先备份、记账（DeployManifest），再写游戏目录；
/// 任何写入游戏目录的动作都要经过工作区路径校验。
/// </summary>
public interface IDeployer
{
    Task<ForgeResult<DeployResult>> DeployAsync(
        GameContext context,
        BuildResult build,
        CancellationToken cancellationToken = default);

    /// <summary>按账本逆序回滚。</summary>
    Task<ForgeResult<DeployRollbackResult>> RollbackAsync(
        GameContext context,
        DeployManifest manifest,
        CancellationToken cancellationToken = default);
}

/// <summary>步骤七：初检。只能声明“检查了什么”，未观察到的行为必须进 Unverified。</summary>
public interface IVerifier
{
    Task<ForgeResult<VerificationReport>> VerifyAsync(
        GameContext context,
        DeployResult? deployment,
        CancellationToken cancellationToken = default);
}
