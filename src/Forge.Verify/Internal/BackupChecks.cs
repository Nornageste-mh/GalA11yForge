using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>备份完整性与位置检查（k-01..k-03）。备份只读探测，不复制、不修复。</summary>
internal static class BackupChecks
{
    public static async Task RunAsync(
        VerificationRun run,
        PreparedContext context,
        DeployManifest manifest,
        CancellationToken cancellationToken)
    {
        var entries = manifest.Files ?? new List<DeployFileEntry>();
        var manifestBackupRoot = TryNormalize(manifest.BackupRoot);

        var required = entries
            .Where(entry => entry is not null && entry.ExistedBefore)
            .ToList();

        var presenceIssues = new List<string>();
        foreach (var entry in required)
        {
            var resolved = ResolveBackupPath(context, manifestBackupRoot, entry);
            if (resolved.Error is not null)
            {
                presenceIssues.Add($"{entry.RelativePath}：{resolved.Error}");
                continue;
            }

            var probe = await FileFacts.ProbeAsync(resolved.FullPath!, cancellationToken).ConfigureAwait(false);
            if (!probe.Exists)
            {
                presenceIssues.Add($"{entry.RelativePath}：备份文件不存在");
            }
            else if (probe.Error is not null)
            {
                presenceIssues.Add($"{entry.RelativePath}：备份文件读取失败（{probe.Error}）");
            }
        }

        if (required.Count == 0)
        {
            run.Skip(
                "k-01",
                "备份文件存在",
                VerifyPriority.P0,
                "账本没有需要备份的修改/删除条目（全部为新增文件）。");
        }
        else
        {
            run.Add(
                "k-01",
                "备份文件存在",
                VerifyPriority.P0,
                presenceIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
                presenceIssues.Count == 0
                    ? $"{required.Count} 个原有文件的备份均存在。"
                    : ManifestRules.Summarize(presenceIssues),
                metrics: new Dictionary<string, double>
                {
                    ["backupsRequired"] = required.Count,
                    ["backupsMissing"] = presenceIssues.Count,
                });
        }

        var hashEntries = entries
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Sha256Before))
            .ToList();

        var hashIssues = new List<string>();
        var hashed = 0;
        foreach (var entry in hashEntries)
        {
            var expected = FileFacts.NormalizeSha256(entry.Sha256Before);
            if (expected is null || !FileFacts.IsValidSha256(expected))
            {
                hashIssues.Add($"{entry.RelativePath} 的 sha256Before 格式非法");
                continue;
            }

            var resolved = ResolveBackupPath(context, manifestBackupRoot, entry);
            if (resolved.Error is not null)
            {
                hashIssues.Add($"{entry.RelativePath}：{resolved.Error}");
                continue;
            }

            var probe = await FileFacts.ProbeAsync(resolved.FullPath!, cancellationToken).ConfigureAwait(false);
            if (!probe.Exists)
            {
                hashIssues.Add($"{entry.RelativePath}：备份文件不存在");
                continue;
            }

            if (probe.Sha256 is null)
            {
                hashIssues.Add($"{entry.RelativePath}：备份文件读取失败（{probe.Error}）");
                continue;
            }

            hashed++;
            if (!string.Equals(probe.Sha256, expected, StringComparison.Ordinal))
            {
                hashIssues.Add($"{entry.RelativePath}：备份哈希与 sha256Before 不一致");
            }
        }

        if (hashEntries.Count == 0)
        {
            run.Skip(
                "k-02",
                "备份哈希一致",
                VerifyPriority.P0,
                "账本没有记录任何修改前哈希，无法核对备份内容。");
        }
        else
        {
            run.Add(
                "k-02",
                "备份哈希一致",
                VerifyPriority.P0,
                hashIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
                hashIssues.Count == 0
                    ? $"{hashed} 个备份文件的 SHA-256 与 sha256Before 一致。"
                    : ManifestRules.Summarize(hashIssues),
                metrics: new Dictionary<string, double>
                {
                    ["backupHashesChecked"] = hashed,
                    ["backupHashMismatches"] = hashIssues.Count,
                });
        }

        var pathEntries = entries
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.BackupPath))
            .ToList();

        var locationIssues = new List<string>();
        foreach (var entry in pathEntries)
        {
            var resolved = ResolveBackupPath(context, manifestBackupRoot, entry);
            if (resolved.Error is not null)
            {
                locationIssues.Add($"{entry.RelativePath}：{resolved.Error}");
            }
        }

        if (pathEntries.Count == 0)
        {
            run.Skip(
                "k-03",
                "备份位置合规",
                VerifyPriority.P0,
                "账本没有记录备份路径，无法核对备份位置。");
        }
        else
        {
            run.Add(
                "k-03",
                "备份位置合规",
                VerifyPriority.P0,
                locationIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
                locationIssues.Count == 0
                    ? $"{pathEntries.Count} 条备份路径均位于账本 backupRoot 与工作区 backup/ 下。"
                    : ManifestRules.Summarize(locationIssues),
                metrics: new Dictionary<string, double>
                {
                    ["backupPathsChecked"] = pathEntries.Count,
                    ["backupPathIssues"] = locationIssues.Count,
                });
        }
    }

    private static (string? FullPath, string? Error) ResolveBackupPath(
        PreparedContext context,
        string? manifestBackupRoot,
        DeployFileEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.BackupPath))
        {
            return (null, "backupPath 为空");
        }

        if (!Path.IsPathRooted(entry.BackupPath))
        {
            return (null, "backupPath 不是绝对路径");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(entry.BackupPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"backupPath 非法：{ex.Message}");
        }

        if (manifestBackupRoot is null)
        {
            return (null, "账本未记录可用的 backupRoot");
        }

        try
        {
            if (!PathSafety.IsUnder(manifestBackupRoot, fullPath))
            {
                return (null, "备份路径不在账本 backupRoot 下");
            }

            if (!PathSafety.IsUnder(context.Workspace.BackupRoot, fullPath))
            {
                return (null, "备份路径不在工作区 backup/ 下");
            }
        }
        catch (ForgePathException ex)
        {
            return (null, ex.Error.Message);
        }

        return (fullPath, null);
    }

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return PathSafety.NormalizeRoot(path);
        }
        catch (ForgePathException)
        {
            return null;
        }
    }
}
