using System.Globalization;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 生成入口的守卫：所有产物必须落在工作区内、绝不允许写进游戏目录。
/// 与 Unity Mono 适配器同一套 <see cref="PathSafety"/> 规则（PathSafety.cs:45 先查游戏目录再查越界）。
/// </summary>
internal sealed class Il2CppGenerationBridge
{
    private readonly Il2CppGenerator _generator = new();

    public Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return Failure(ForgeErrorCodes.ArgumentInvalid, "上下文为空，无法生成 IL2CPP 补丁工程。");
        }

        if (plan is null)
        {
            return Failure(ForgeErrorCodes.ArgumentInvalid, "补丁方案为空，无法生成 IL2CPP 补丁工程。");
        }

        if (string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return Failure(ForgeErrorCodes.ArgumentInvalid, "工作区根目录为空，无法校验产物路径。",
                "请设置 --workspace 或配置工作区。");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return Failure(ForgeErrorCodes.ArgumentInvalid, "输出目录为空，无法生成 IL2CPP 补丁工程。");
        }

        var workspaceRoot = PathSafety.NormalizeRoot(context.WorkspaceRoot);
        string resolved;
        try
        {
            resolved = Path.IsPathRooted(outputDirectory)
                ? Path.GetFullPath(outputDirectory)
                : PathSafety.ResolveUnder(workspaceRoot, outputDirectory);
        }
        catch (ForgePathException ex)
        {
            return Failure(ex.Error.Code, ex.Error.Message, ex.Error.Suggestion);
        }
        catch (ArgumentException)
        {
            return Failure(ForgeErrorCodes.PathUnsafe, "输出目录不是合法路径：" + outputDirectory);
        }

        try
        {
            PathSafety.EnsureWriteAllowed(workspaceRoot, resolved, context.GameDirectory);
        }
        catch (ForgePathException ex)
        {
            return Failure(ex.Error.Code, ex.Error.Message, ex.Error.Suggestion);
        }

        var normalizedResolved = PathSafety.NormalizeRoot(resolved);
        if (string.Equals(normalizedResolved, workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(ForgeErrorCodes.PathUnsafe, "输出目录不能是工作区根目录本身：" + resolved,
                "请给一个子目录，例如 patches/" + (plan.GameSlug ?? "game") + "。");
        }

        if (!string.IsNullOrWhiteSpace(context.GameDirectory) &&
            PathSafety.IsUnder(PathSafety.NormalizeRoot(context.GameDirectory), normalizedResolved))
        {
            // EnsureWriteAllowed 已经拦过，这里再兜一层：防止 workspace 恰好套在游戏目录里。
            return Failure(ForgeErrorCodes.GameWriteDenied, "输出目录落在游戏目录内：" + resolved,
                "补丁工程只能生成到工作区，安装到游戏目录要单独走部署步骤。");
        }

        var hookPoints = plan.PatchPoints.Where(p => p.Kind == PatchPointKind.Hook).ToList();

        try
        {
            var options = Il2CppOptions.From(context);
            var layout = Il2CppLayoutProbe.Probe(context.GameDirectory, options.AsDictionary());
            var result = _generator.Generate(context, plan, resolved, layout, options);

            // 0.2.12：字段兜底钩子没有 string 形参，CanGenerateHook 为 false，
            // 但生成器已按描述里的字段标记写出 Hook + A11yFieldReader。
            // 所以这里只认生成结果的 hook_count，不能再按“有无 string 形参”判空。
            var hookCount = result.Metadata.TryGetValue("hook_count", out var count) &&
                            int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

            if (hookCount == 0)
            {
                // 骨架工程已经落盘（可编译、但没有补丁），如实报 Partial 而不是 Success。
                return Failure(ForgeErrorCodes.Partial,
                    "没有可自动生成的补丁点：已写出骨架工程 " + result.ProjectDirectory + "（不含补丁，安装后不会朗读）。",
                    "先让 BepInEx 6 启动一次生成 interop，再重跑 analyze/plan/generate；" +
                    "或按计划里的人工待办手工接入事件通道。" +
                    (hookPoints.Count == 0 ? "（当前 hook 点为 0）" : "（当前 hook 点 " + hookPoints.Count + " 个，均不可自动生成）"));
            }

            return Task.FromResult(ForgeResult<GenerationResult>.Success(result));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(ForgeErrorCodes.Io, "写补丁工程失败（" + ex.GetType().Name + "：" + ex.Message + "）",
                "检查输出目录是否被占用/只读。");
        }
        catch (Exception ex)
        {
            return Failure(ForgeErrorCodes.Internal,
                "IL2CPP 代码生成异常（" + ex.GetType().Name + "：" + ex.Message + "）",
                "这是适配器缺陷，请把输出目录和本段日志一起反馈。");
        }
    }

    private static Task<ForgeResult<GenerationResult>> Failure(string code, string message, string? suggestion = null)
        => Task.FromResult(ForgeResult<GenerationResult>.Failure(code, message, suggestion));
}
