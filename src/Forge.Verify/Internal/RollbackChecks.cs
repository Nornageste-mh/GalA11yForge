using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>
/// 卸载回滚后检查（r-01..r-03）：原有文件是否还原、新增文件是否删除、清理结论是否自洽。
/// 只读取磁盘事实，不执行删除/还原。
/// </summary>
internal static class RollbackChecks
{
    public static async Task RunAsync(
        VerificationRun run,
        PreparedContext context,
        DeployManifest manifest,
        CancellationToken cancellationToken)
    {
        var entries = manifest.Files ?? new List<DeployFileEntry>();
        var restoreEntries = entries.Where(entry => entry is not null && entry.ExistedBefore).ToList();
        var deleteEntries = entries.Where(entry => entry is not null && !entry.ExistedBefore).ToList();

        var restoreIssues = new List<string>();
        var restoredCount = 0;
        var hashCompared = 0;
        var hashMismatches = 0;
        var noHash = 0;

        foreach (var entry in restoreEntries)
        {
            if (!ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out var error))
            {
                restoreIssues.Add($"{entry.RelativePath}（路径非法：{error}）");
                continue;
            }

            var probe = await FileFacts.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!probe.Exists)
            {
                restoreIssues.Add($"{entry.RelativePath} 未还原（文件缺失）");
                continue;
            }

            restoredCount++;
            var expected = FileFacts.NormalizeSha256(entry.Sha256Before);
            if (expected is not null && FileFacts.IsValidSha256(expected))
            {
                hashCompared++;
                if (probe.Sha256 is null)
                {
                    restoreIssues.Add($"{entry.RelativePath} 读取失败（{probe.Error}）");
                }
                else if (!string.Equals(probe.Sha256, expected, StringComparison.Ordinal))
                {
                    hashMismatches++;
                    restoreIssues.Add($"{entry.RelativePath} 内容与原文件哈希不一致");
                }

                continue;
            }

            var backup = await ProbeBackupAsync(context, entry, cancellationToken).ConfigureAwait(false);
            if (backup.Sha256 is not null)
            {
                hashCompared++;
                if (probe.Sha256 is null)
                {
                    restoreIssues.Add($"{entry.RelativePath} 读取失败（{probe.Error}）");
                }
                else if (!string.Equals(probe.Sha256, backup.Sha256, StringComparison.Ordinal))
                {
                    hashMismatches++;
                    restoreIssues.Add($"{entry.RelativePath} 内容与备份文件哈希不一致");
                }
            }
            else
            {
                noHash++;
            }
        }

        if (restoreEntries.Count == 0)
        {
            run.Skip(
                "r-01",
                "原有文件已还原",
                VerifyPriority.P0,
                "账本没有需要还原的原文件（全部为新增文件）。");
        }
        else
        {
            run.Add(
                "r-01",
                "原有文件已还原",
                VerifyPriority.P0,
                restoreIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
                restoreIssues.Count == 0
                    ? $"{restoredCount} 个原文件已还原；哈希比对 {hashCompared} 项，{noHash} 项无可用安装前哈希。"
                    : ManifestRules.Summarize(restoreIssues),
                metrics: new Dictionary<string, double>
                {
                    ["restoreEntries"] = restoreEntries.Count,
                    ["restoredFiles"] = restoredCount,
                    ["restoreHashCompared"] = hashCompared,
                    ["restoreHashMismatches"] = hashMismatches,
                    ["restoreNoHash"] = noHash,
                });
        }

        var deleteIssues = new List<string>();
        var deletedCount = 0;
        foreach (var entry in deleteEntries)
        {
            if (!ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out var error))
            {
                deleteIssues.Add($"{entry.RelativePath}（路径非法：{error}）");
                continue;
            }

            var probe = await FileFacts.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (probe.Exists)
            {
                deleteIssues.Add($"{entry.RelativePath} 仍有残留");
            }
            else
            {
                deletedCount++;
            }
        }

        if (deleteEntries.Count == 0)
        {
            run.Skip(
                "r-02",
                "新增文件已删除",
                VerifyPriority.P0,
                "账本没有新增文件，无需检查删除结果。");
        }
        else
        {
            run.Add(
                "r-02",
                "新增文件已删除",
                VerifyPriority.P0,
                deleteIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
                deleteIssues.Count == 0
                    ? $"{deletedCount} 个新增文件均已删除。"
                    : ManifestRules.Summarize(deleteIssues),
                metrics: new Dictionary<string, double>
                {
                    ["deleteEntries"] = deleteEntries.Count,
                    ["deletedFiles"] = deletedCount,
                    ["leftoverFiles"] = deleteIssues.Count,
                });
        }

        var restored = new List<string>();
        foreach (var entry in restoreEntries)
        {
            if (ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out _)
                && File.Exists(fullPath))
            {
                restored.Add($"{entry.RelativePath} 已还原");
            }
            else
            {
                restored.Add($"{entry.RelativePath} 未还原（缺失）");
            }
        }

        foreach (var entry in deleteEntries)
        {
            if (ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out _)
                && File.Exists(fullPath))
            {
                restored.Add($"{entry.RelativePath} 仍有残留");
            }
            else
            {
                restored.Add($"{entry.RelativePath} 已删除");
            }
        }

        var uninstalled = restoreIssues.Count == 0 && deleteIssues.Count == 0;
        bool? hashMatch = hashCompared == 0 ? null : hashMismatches == 0;
        run.Report.Cleanup = new CleanupReport
        {
            Required = true,
            Uninstalled = uninstalled,
            Restored = restored,
            HashMatch = hashMatch,
            Detail = uninstalled
                ? $"回滚后检查通过：还原 {restoredCount} 项，删除 {deletedCount} 项，哈希比对 {hashCompared} 项。"
                : $"回滚后检查未通过：还原 {restoredCount} 项，删除 {deletedCount} 项，{restoreIssues.Count + deleteIssues.Count} 项异常。",
        };

        var cleanupIssues = restoreIssues.Concat(deleteIssues).ToList();
        run.Add(
            "r-03",
            "清理结论自洽",
            VerifyPriority.P0,
            cleanupIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
            run.Report.Cleanup.Detail!,
            metrics: new Dictionary<string, double>
            {
                ["cleanupRestored"] = restoredCount,
                ["cleanupDeleted"] = deletedCount,
                ["cleanupIssues"] = cleanupIssues.Count,
            });

        if (noHash > 0)
        {
            run.Unverified($"回滚校验中有 {noHash} 个文件没有可比对的安装前哈希，只能确认文件存在，不能确认内容完全还原。");
        }

        if (hashCompared == 0 && restoreEntries.Count > 0)
        {
            run.Unverified("回滚校验没有可用的安装前哈希，只能确认文件存在/删除状态。");
        }
    }

    private static async Task<FileProbe> ProbeBackupAsync(
        PreparedContext context,
        DeployFileEntry entry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.BackupPath) || !Path.IsPathRooted(entry.BackupPath))
        {
            return new FileProbe(false, 0, null, null);
        }

        try
        {
            var fullPath = Path.GetFullPath(entry.BackupPath);
            if (!PathSafety.IsUnder(context.Workspace.BackupRoot, fullPath))
            {
                return new FileProbe(false, 0, null, null);
            }

            return await FileFacts.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or ForgePathException)
        {
            return new FileProbe(false, 0, null, null);
        }
    }
}
