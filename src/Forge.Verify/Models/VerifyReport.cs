using System.Text.Json.Serialization;
using GalA11yForge.Core;

namespace GalA11yForge.Verify.Models;

/// <summary>
/// 整体结论。除 docs/09 已有的 pass/partial/fail 外，增加 pass_with_skips：
/// 有检查被跳过（没测）但所有实际执行的检查都通过时使用，避免把“没测”写成 pass。
/// </summary>
public static class VerifyOverall
{
    public const string Pass = "pass";
    public const string PassWithSkips = "pass_with_skips";
    public const string Partial = "partial";
    public const string Fail = "fail";

    public static bool IsKnown(string? value) =>
        value is Pass or PassWithSkips or Partial or Fail;
}

/// <summary>
/// 安装/卸载清理结果，字段与 docs/09 cleanup 对齐并做少量扩展。
/// </summary>
public sealed class CleanupReport
{
    public bool Required { get; set; }

    public bool Uninstalled { get; set; }

    public List<string> Restored { get; set; } = new();

    /// <summary>
    /// 有哈希可比对且全部一致=true；有任何不一致=false；没有可比对的哈希=null。
    /// </summary>
    public bool? HashMatch { get; set; }

    public string? Detail { get; set; }
}

/// <summary>
/// verify.json 的完整文档。与 docs/09 的公共信封保持一致。
/// </summary>
public sealed class VerifyReport
{
    public int SchemaVersion { get; set; } = 1;

    public string Artifact { get; set; } = "verify";

    public string ToolVersion { get; set; } = ForgeInfo.Version;

    public string GeneratedAt { get; set; } = string.Empty;

    public string? Slug { get; set; }

    public string? GamePath { get; set; }

    public string? DeploymentId { get; set; }

    public string? ManifestPath { get; set; }

    public VerifyMode Mode { get; set; } = VerifyMode.Install;

    public string Overall { get; set; } = VerifyOverall.Fail;

    public List<VerifyCheck> Checks { get; set; } = new();

    public CleanupReport Cleanup { get; set; } = new();

    public List<string> Unverified { get; set; } = new();

    public Dictionary<string, double> Metrics { get; set; } = new();

    public EnvironmentSnapshot? Environment { get; set; }

    public List<string> Evidence { get; set; } = new();

    [JsonIgnore]
    public int PassCount => Checks.Count(check => check.Status == VerifyCheckStatus.Pass);

    [JsonIgnore]
    public int FailCount => Checks.Count(check => check.Status == VerifyCheckStatus.Fail);

    [JsonIgnore]
    public int SkipCount => Checks.Count(check => check.Status == VerifyCheckStatus.Skip);

    [JsonIgnore]
    public int P0FailCount => Checks.Count(check =>
        check.Status == VerifyCheckStatus.Fail
        && string.Equals(check.Priority, VerifyPriority.P0, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 计算整体结论：
    /// 1) 没有检查项 -> fail（不能空着说通过）；
    /// 2) 任一 P0 失败 -> fail；
    /// 3) 任一其他失败 -> partial；
    /// 4) 全部通过 -> pass；
    /// 5) 其余（只有 pass/skip）-> pass_with_skips。
    /// </summary>
    public static string ComputeOverall(IEnumerable<VerifyCheck>? checks)
    {
        var list = checks?.ToList() ?? new List<VerifyCheck>();
        if (list.Count == 0)
        {
            return VerifyOverall.Fail;
        }

        var failed = list.Where(check => check.Status == VerifyCheckStatus.Fail).ToList();
        if (failed.Any(check =>
                string.Equals(check.Priority, VerifyPriority.P0, StringComparison.OrdinalIgnoreCase)))
        {
            return VerifyOverall.Fail;
        }

        if (failed.Count > 0)
        {
            return VerifyOverall.Partial;
        }

        return list.Any(check => check.Status == VerifyCheckStatus.Skip)
            ? VerifyOverall.PassWithSkips
            : VerifyOverall.Pass;
    }
}
