using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Generators;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>
/// GenerateAsync 的薄桥：只做输出目录安全校验，然后委托 UnityMonoGenerator。
/// 生成器的参数/钩子/幂等校验保持唯一事实来源，这里不重复实现。
/// </summary>
internal sealed class UnityMonoGenerationBridge : ICodeGenerator
{
    private readonly ICodeGenerator _generator;

    public UnityMonoGenerationBridge(ICodeGenerator generator)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public async Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (context is null)
            {
                return ForgeResult<GenerationResult>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    "GameContext 不能为空。");
            }

            if (plan is null)
            {
                return ForgeResult<GenerationResult>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    "PatchPlan 不能为空。");
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return ForgeResult<GenerationResult>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    "输出目录不能为空。",
                    "传工作区内的相对路径（如 games/<slug>/05-generate），或工作区内的绝对路径。");
            }

            if (string.IsNullOrWhiteSpace(context.WorkspaceRoot))
            {
                return ForgeResult<GenerationResult>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    "WorkspaceRoot 不能为空。",
                    "由 CLI 创建 GameContext 时指定工作区。");
            }

            string resolved;
            if (Path.IsPathRooted(outputDirectory))
            {
                resolved = Path.GetFullPath(outputDirectory);
                PathSafety.EnsureWriteAllowed(context.WorkspaceRoot, resolved, context.GameDirectory);
            }
            else
            {
                resolved = PathSafety.ResolveUnder(context.WorkspaceRoot, outputDirectory);
                PathSafety.EnsureWriteAllowed(context.WorkspaceRoot, resolved, context.GameDirectory);
            }

            var root = PathSafety.NormalizeRoot(context.WorkspaceRoot);
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(resolved),
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase))
            {
                return ForgeResult<GenerationResult>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    "输出目录不能等于工作区根目录。",
                    "指定工作区下的子目录，例如 games/<slug>/05-generate。");
            }

            if (!string.IsNullOrWhiteSpace(context.GameDirectory))
            {
                var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
                if (PathSafety.IsUnder(game, resolved))
                {
                    return ForgeResult<GenerationResult>.Failure(
                        ForgeErrorCodes.GameWriteDenied,
                        "输出目录位于游戏目录内，拒绝写入。",
                        "把输出目录放到工作区（WorkspaceRoot）下；游戏目录保持只读。");
                }
            }

            var result = await _generator.GenerateAsync(context, plan, resolved, cancellationToken)
                .ConfigureAwait(false);
            return result ?? ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Internal,
                "生成器返回了空结果。",
                "检查生成器版本；不要吞掉异常。");
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<GenerationResult>.Failure(ex.Error);
        }
        catch (OperationCanceledException)
        {
            return ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Internal,
                "生成已取消。",
                "重试。");
        }
        catch (Exception ex)
        {
            return ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 生成失败：{ex.Message}",
                "保留输出目录现场，把该消息交给开发者。");
        }
    }
}
