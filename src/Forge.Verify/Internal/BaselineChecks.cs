using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Baseline;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>
/// 基线核对（m-07）：证明“账本之外的原文件”没有被顺手改掉；回滚模式下核对全部基线文件，
/// 证明“当前状态 == 安装前状态”。
/// 安装模式跳过账本已记账的路径（安装后它们本应变化），避免与 m-04/m-05 重复报错；
/// 回滚模式必须逐条核对，包括本次部署目标（新增文件应仍然不存在，替换/删除文件应已还原）。
/// </summary>
internal static class BaselineChecks
{
    public static async Task RunAsync(
        VerificationRun run,
        PreparedContext context,
        VerifyOptions options,
        DeployManifest manifest,
        CancellationToken cancellationToken)
    {
        var baseline = options.Baseline;
        string? baselinePath = null;

        if (baseline is null)
        {
            baselinePath = context.BaselinePath;
            if (baselinePath is null)
            {
                run.Skip(
                    "m-07",
                    "账本外原文件未变",
                    VerifyPriority.P0,
                    "未配置基线路径，无法证明账本之外的原文件未被修改。");
                run.Unverified("未提供基线（baseline.json），账本之外的原文件是否被修改未验证。");
                return;
            }

            if (!File.Exists(baselinePath))
            {
                if (context.BaselinePathExplicit)
                {
                    run.Fail(
                        "m-07",
                        "账本外原文件未变",
                        VerifyPriority.P0,
                        $"显式指定的基线不存在：{baselinePath}");
                }
                else
                {
                    run.Skip(
                        "m-07",
                        "账本外原文件未变",
                        VerifyPriority.P0,
                        $"默认基线不存在：{baselinePath}。未采集基线，无法证明账本之外的原文件未被修改。");
                    run.Unverified("未找到默认基线 baseline.json，账本之外的原文件是否被修改未验证。");
                }

                return;
            }

            var load = await GameBaselineStore.LoadAsync(baselinePath, cancellationToken).ConfigureAwait(false);
            if (!load.Ok)
            {
                run.Fail(
                    "m-07",
                    "账本外原文件未变",
                    VerifyPriority.P0,
                    $"基线读取失败：{load.Error?.Message}");
                return;
            }

            baseline = load.Value!;
        }

        if (baseline.SchemaVersion != 1)
        {
            run.Fail(
                "m-07",
                "账本外原文件未变",
                VerifyPriority.P0,
                $"基线 schemaVersion={baseline.SchemaVersion}，当前只支持 1。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(baseline.Artifact)
            && !string.Equals(baseline.Artifact, GameBaseline.ArtifactName, StringComparison.OrdinalIgnoreCase))
        {
            run.Fail(
                "m-07",
                "账本外原文件未变",
                VerifyPriority.P0,
                $"基线 artifact={baseline.Artifact}，当前只支持 {GameBaseline.ArtifactName}。");
            return;
        }

        if (baseline.Files is null || baseline.Files.Count == 0)
        {
            run.Fail(
                "m-07",
                "账本外原文件未变",
                VerifyPriority.P0,
                "基线没有记录任何文件，无法证明原文件未被修改。");
            return;
        }

        if (!string.IsNullOrWhiteSpace(baseline.GamePath))
        {
            try
            {
                var baselineGame = PathSafety.NormalizeRoot(baseline.GamePath);
                var actualGame = PathSafety.NormalizeRoot(context.GameDirectory);
                if (!string.Equals(baselineGame, actualGame, StringComparison.OrdinalIgnoreCase))
                {
                    run.Fail(
                        "m-07",
                        "账本外原文件未变",
                        VerifyPriority.P0,
                        $"基线 gamePath 与当前游戏目录不一致（基线：{baselineGame}）。");
                    return;
                }
            }
            catch (ForgePathException ex)
            {
                run.Fail(
                    "m-07",
                    "账本外原文件未变",
                    VerifyPriority.P0,
                    $"基线 gamePath 非法：{ex.Error.Message}");
                return;
            }
        }

        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (manifest.Files is not null)
        {
            foreach (var entry in manifest.Files)
            {
                if (entry is null)
                {
                    continue;
                }

                var normalized = ManifestRules.NormalizeRelative(entry.RelativePath);
                if (normalized is not null)
                {
                    accounted.Add(normalized);
                }
            }
        }

        var issues = new List<string>();
        var checkedFiles = 0;

        foreach (var file in baseline.Files)
        {
            if (file is null)
            {
                issues.Add("基线含空条目");
                continue;
            }

            var relative = ManifestRules.NormalizeRelative(file.RelativePath);
            if (relative is null)
            {
                issues.Add($"基线路径非法：{file.RelativePath}");
                continue;
            }

            if (context.Mode != VerifyMode.Rollback && accounted.Contains(relative))
            {
                continue;
            }

            if (!ManifestRules.TryResolveGamePath(context.GameDirectory, relative, out var fullPath, out var error))
            {
                issues.Add($"{relative}（路径非法：{error}）");
                continue;
            }

            var probe = await FileFacts.ProbeAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!file.Exists)
            {
                checkedFiles++;
                if (probe.Exists)
                {
                    issues.Add($"{relative} 基线记录为不存在，但当前存在（期望：不存在；实际：存在）");
                }

                continue;
            }

            if (!probe.Exists)
            {
                issues.Add($"{relative} 缺失（期望：存在；实际：不存在）");
                continue;
            }

            if (probe.Error is not null || probe.Sha256 is null)
            {
                issues.Add($"{relative} 读取失败（{probe.Error}）");
                continue;
            }

            checkedFiles++;
            if (probe.Size != file.Size)
            {
                issues.Add($"{relative} 大小变化（基线 {file.Size}，实际 {probe.Size}）");
                continue;
            }

            var expected = FileFacts.NormalizeSha256(file.Sha256);
            if (expected is null || !FileFacts.IsValidSha256(expected))
            {
                issues.Add($"{relative} 的基线 sha256 格式非法");
                continue;
            }

            if (!string.Equals(probe.Sha256, expected, StringComparison.Ordinal))
            {
                issues.Add($"{relative} 哈希变化（期望：{expected}；实际：{probe.Sha256}）");
            }
        }

        var evidence = baselinePath is null ? new[] { "调用方传入内存基线" } : new[] { $"基线文件：{baselinePath}" };
        if (baselinePath is not null)
        {
            run.Evidence($"基线：{baselinePath}");
        }

        if (checkedFiles == 0 && issues.Count == 0)
        {
            run.Skip(
                "m-07",
                "账本外原文件未变",
                VerifyPriority.P0,
                "基线中的文件全部已被账本记账，没有需要额外核对的账本外原文件。",
                evidence);
            return;
        }

        run.Add(
            "m-07",
            "账本外原文件未变",
            VerifyPriority.P0,
            issues.Count == 0 ? VerifyCheckStatus.Pass : VerifyCheckStatus.Fail,
            issues.Count == 0
                ? context.Mode == VerifyMode.Rollback
                    ? $"{checkedFiles} 个基线文件与安装前状态一致。"
                    : $"{checkedFiles} 个账本外原文件与基线一致。"
                : ManifestRules.Summarize(issues),
            evidence,
            new Dictionary<string, double>
            {
                ["baselineCheckedFiles"] = checkedFiles,
                ["baselineMismatches"] = issues.Count,
            });

        run.Unverified("基线核对只覆盖基线中记录过的文件；基线之外新增的运行期文件（日志、缓存等）不作为篡改判定。");
    }
}
