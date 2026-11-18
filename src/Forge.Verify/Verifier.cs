using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Verify.Abstractions;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify;

/// <summary>
/// 把 Forge.Verify 的自检报告适配成 Core 的 IVerifier 契约。
/// 只有 overall=pass 才置 Passed=true；pass_with_skips 不等于通过。
/// </summary>
public sealed class Verifier : IVerifier
{
    private readonly VerificationService _service;

    public Verifier(VerificationService? service = null, IEnvironmentProbe? environmentProbe = null)
    {
        _service = service ?? new VerificationService(environmentProbe);
    }

    public async Task<ForgeResult<VerificationReport>> VerifyAsync(
        GameContext context,
        DeployResult? deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.GameDirectory) || string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return ForgeResult<VerificationReport>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "GameContext 必须包含游戏目录与工作区目录。",
                "用 GameContext.Create(gameDirectory, workspaceRoot, slug) 构造上下文。"));
        }

        var options = new VerifyOptions
        {
            GameDirectory = context.GameDirectory,
            WorkspaceRoot = context.WorkspaceRoot,
            Slug = context.Slug,
            Manifest = deployment?.Manifest,
            Mode = VerifyMode.Install,
            ProbeEnvironment = true,
        };

        var result = await _service.VerifyAsync(options, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return ForgeResult<VerificationReport>.Failure(result.Error!);
        }

        return ForgeResult<VerificationReport>.Success(ToContract(result.Value!));
    }

    private static VerificationReport ToContract(VerifyReport report)
    {
        var contract = new VerificationReport
        {
            Passed = report.Overall == VerifyOverall.Pass,
            Unverified = report.Unverified.ToList(),
        };

        foreach (var check in report.Checks)
        {
            var prefix = check.Status switch
            {
                VerifyCheckStatus.Pass => "pass",
                VerifyCheckStatus.Fail => "fail",
                VerifyCheckStatus.Skip => "skip",
                _ => "unknown",
            };

            contract.Checks.Add(new VerificationCheck
            {
                Name = check.Name,
                Passed = check.Status == VerifyCheckStatus.Pass,
                Detail = string.IsNullOrWhiteSpace(check.Detail)
                    ? prefix
                    : $"{prefix}: {check.Detail}",
            });

            if (check.Status == VerifyCheckStatus.Fail)
            {
                var blocking = check.Priority == VerifyPriority.P0;
                contract.ManualTodo.Add(new ManualTodoItem
                {
                    Id = $"verify-{check.Id}",
                    Title = (blocking ? "必须修复：" : "建议修复：") + check.Name,
                    Detail = check.Detail,
                    Blocking = blocking,
                });
            }
        }

        if (report.Overall == VerifyOverall.PassWithSkips)
        {
            contract.ManualTodo.Add(new ManualTodoItem
            {
                Id = "verify-unverified",
                Title = "完成验证报告中列出的未验证项",
                Detail = "部分检查被跳过；跳过不等于通过，需按 verify.md 的未验证项人工实测。",
                Blocking = false,
            });
        }

        return contract;
    }
}
