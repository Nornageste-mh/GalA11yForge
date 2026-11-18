using GalA11yForge.Core.Models;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>账本路径、安装态文件与哈希检查（m-03..m-06）。</summary>
internal static class LedgerChecks
{
    public static void RunManifestPaths(VerificationRun run, DeployManifest manifest)
    {
        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = 0;

        if (manifest.Files is not null)
        {
            foreach (var entry in manifest.Files)
            {
                if (entry is null)
                {
                    issues.Add("账本含空条目");
                    continue;
                }

                var normalized = ManifestRules.NormalizeRelative(entry.RelativePath);
                if (normalized is null)
                {
                    issues.Add($"路径不安全或非法：{entry.RelativePath}");
                    continue;
                }

                valid++;
                if (!seen.Add(normalized))
                {
                    issues.Add($"重复记账：{normalized}");
                }
            }
        }

        var status = issues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail;
        var detail = issues.Count == 0
            ? $"{valid} 条记录路径均安全且无重复。"
            : ManifestRules.Summarize(issues);

        run.Add(
            "m-03",
            "账本路径安全与唯一性",
            VerifyPriority.P0,
            status,
            detail,
            metrics: new Dictionary<string, double>
            {
                ["ledgerEntries"] = valid,
                ["pathIssues"] = issues.Count,
            });
    }

    public static async Task RunInstallFilesAsync(
        VerificationRun run,
        PreparedContext context,
        DeployManifest manifest,
        CancellationToken cancellationToken)
    {
        var entries = manifest.Files ?? new List<DeployFileEntry>();

        var missing = new List<string>();
        var unexpected = new List<string>();
        var present = 0;

        foreach (var entry in entries)
        {
            if (entry is null)
            {
                continue;
            }

            if (!ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out var error))
            {
                missing.Add($"{entry.RelativePath}（路径非法：{error}）");
                continue;
            }

            var probe = await FileFacts.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (entry.Action == DeployFileAction.Remove)
            {
                if (probe.Exists)
                {
                    unexpected.Add(entry.RelativePath);
                }
            }
            else if (!probe.Exists)
            {
                missing.Add(entry.RelativePath);
            }
            else
            {
                present++;
            }
        }

        var installIssues = new List<string>();
        if (missing.Count > 0)
        {
            installIssues.Add($"应有但缺失：{ManifestRules.Summarize(missing)}");
        }

        if (unexpected.Count > 0)
        {
            installIssues.Add($"应删除但仍存在：{ManifestRules.Summarize(unexpected)}");
        }

        run.Add(
            "m-04",
            "安装态文件在位",
            VerifyPriority.P0,
            installIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
            installIssues.Count == 0
                ? $"{present} 个安装文件在位，删除项均不存在。"
                : string.Join("；", installIssues),
            metrics: new Dictionary<string, double>
            {
                ["presentFiles"] = present,
                ["missingFiles"] = missing.Count,
                ["unexpectedFiles"] = unexpected.Count,
            });

        var hashIssues = new List<string>();
        var hashed = 0;
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Sha256After))
            {
                continue;
            }

            var expected = FileFacts.NormalizeSha256(entry.Sha256After);
            if (expected is null || !FileFacts.IsValidSha256(expected))
            {
                hashIssues.Add($"{entry.RelativePath} 的 sha256After 格式非法");
                continue;
            }

            if (entry.Action == DeployFileAction.Remove)
            {
                continue;
            }

            if (!ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out var error))
            {
                hashIssues.Add($"{entry.RelativePath}（路径非法：{error}）");
                continue;
            }

            var probe = await FileFacts.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!probe.Exists)
            {
                hashIssues.Add($"{entry.RelativePath} 缺失，无法核对哈希");
                continue;
            }

            if (probe.Sha256 is null)
            {
                hashIssues.Add($"{entry.RelativePath} 读取失败：{probe.Error}");
                continue;
            }

            hashed++;
            if (!string.Equals(probe.Sha256, expected, StringComparison.Ordinal))
            {
                hashIssues.Add($"{entry.RelativePath} 哈希不一致（期望 {Short(expected)}，实际 {Short(probe.Sha256)}）");
            }
        }

        if (hashed == 0 && hashIssues.Count == 0)
        {
            run.Skip(
                "m-05",
                "安装态哈希一致",
                VerifyPriority.P0,
                "账本没有记录任何安装后哈希，无法核对文件内容。");
        }
        else
        {
            run.Add(
                "m-05",
                "安装态哈希一致",
                VerifyPriority.P0,
                hashIssues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
                hashIssues.Count == 0
                    ? $"{hashed} 个文件的 SHA-256 与账本一致。"
                    : ManifestRules.Summarize(hashIssues),
                metrics: new Dictionary<string, double>
                {
                    ["hashedFiles"] = hashed,
                    ["hashMismatches"] = hashIssues.Count,
                });
        }

        var emptyFiles = new List<string>();
        var sizeChecked = 0;
        foreach (var entry in entries)
        {
            if (entry is null
                || entry.Action == DeployFileAction.Remove
                || string.IsNullOrWhiteSpace(entry.Sha256After))
            {
                continue;
            }

            if (!ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out _))
            {
                continue;
            }

            var probe = await FileFacts.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!probe.Exists)
            {
                continue;
            }

            sizeChecked++;
            if (probe.Size == 0)
            {
                emptyFiles.Add(entry.RelativePath);
            }
        }

        if (sizeChecked == 0)
        {
            run.Skip(
                "m-06",
                "安装文件非空",
                VerifyPriority.P0,
                "没有可检查大小的安装文件（账本未记录哈希或文件均缺失）。");
        }
        else
        {
            run.Add(
                "m-06",
                "安装文件非空",
                VerifyPriority.P0,
                emptyFiles.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
                emptyFiles.Count == 0
                    ? $"{sizeChecked} 个安装文件大小均大于 0。"
                    : $"以下文件为 0 字节：{ManifestRules.Summarize(emptyFiles)}",
                metrics: new Dictionary<string, double>
                {
                    ["sizeCheckedFiles"] = sizeChecked,
                    ["emptyFiles"] = emptyFiles.Count,
                });
        }

        run.Unverified("安装账本没有文件大小字段：m-06 只能确认安装文件非空，无法核对精确字节数。");
    }

    private static string Short(string sha256) => sha256.Length <= 12 ? sha256 : sha256[..12];
}
