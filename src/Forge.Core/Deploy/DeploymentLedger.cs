using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Core.Deploy;

/// <summary>
/// 部署账本校验与回滚计划。规则：
/// 1) 账本内路径必须是安全的相对路径；
/// 2) 需要还原的条目必须有备份，且备份位于工作区 backup/ 下；
/// 3) 回滚顺序与部署顺序相反，Order 从 1 递增。
/// </summary>
public static class DeploymentLedger
{
    public static ForgeResult<IReadOnlyList<DeployRollbackStep>> BuildRollbackPlan(
        ForgeWorkspace workspace,
        DeployManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(manifest);

        var steps = new List<DeployRollbackStep>();
        for (var index = manifest.Files.Count - 1; index >= 0; index--)
        {
            var file = manifest.Files[index];
            string relative;
            try
            {
                relative = PathSafety.ValidateRelative(file.RelativePath);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<IReadOnlyList<DeployRollbackStep>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"部署账本包含不安全路径：{file.RelativePath}。",
                    "拒绝回滚；手工检查 05-deploy/deploy.json。",
                    new Dictionary<string, string> { ["code"] = ex.Error.Code }));
            }

            if (file.Action == DeployFileAction.Remove || file.ExistedBefore)
            {
                if (string.IsNullOrWhiteSpace(file.BackupPath))
                {
                    return ForgeResult<IReadOnlyList<DeployRollbackStep>>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactMissing,
                        $"部署账本缺少备份路径：{relative}。",
                        "无法安全回滚；从 backup/ 找对应文件并手工恢复。"));
                }

                try
                {
                    workspace.EnsureBackupPath(file.BackupPath);
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<IReadOnlyList<DeployRollbackStep>>.Failure(ex.Error);
                }

                steps.Add(new DeployRollbackStep
                {
                    Order = steps.Count + 1,
                    Action = DeployRollbackAction.Restore,
                    TargetRelativePath = relative,
                    BackupPath = Path.GetFullPath(file.BackupPath),
                });
            }
            else
            {
                steps.Add(new DeployRollbackStep
                {
                    Order = steps.Count + 1,
                    Action = DeployRollbackAction.Delete,
                    TargetRelativePath = relative,
                });
            }
        }

        return ForgeResult<IReadOnlyList<DeployRollbackStep>>.Success(steps);
    }
}
