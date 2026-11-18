namespace UnityMonoAnalyzer.Analysis;

public sealed class RankedCandidate
{
    public DiscoveryCandidate Candidate { get; init; } = null!;
    public double Score { get; init; }
    public string Reason { get; init; } = "";
    public bool NameMatched { get; init; }
}

/// <summary>
/// 多 `*_Data` 候选时的选择器。
/// 教训（research/01-norna）：启动器的 set_text 数量可能**多于**真游戏，
/// 所以不能按"写入点数量"排序，必须用「对话框架痕迹 + 对话命名 + CJK + 启动器惩罚」复合排序。
/// </summary>
public static class CandidateRanker
{
    public static List<RankedCandidate> Rank(IReadOnlyList<DiscoveryCandidate> candidates, string rootName)
    {
        var list = new List<RankedCandidate>();
        foreach (var c in candidates)
        {
            var m = c.QuickMetrics;
            var reasons = new List<string>();
            double score = 0;

            if (m.KnownFrameworkTypes > 0)
            {
                score += 1000;
                reasons.Add($"+1000 已知对话框架类型×{m.KnownFrameworkTypes}");
            }
            if (m.DialogueNamedTypes > 0)
            {
                var pts = 300.0 * Math.Min(m.DialogueNamedTypes, 4);
                score += pts;
                reasons.Add($"+{pts:0} 对话命名类型×{m.DialogueNamedTypes}");
            }
            if (m.CjkLiteralMethods > 0)
            {
                score += 200;
                reasons.Add($"+200 CJK 方法×{m.CjkLiteralMethods}");
            }
            if (m.TextSinkCallerMethods > 0)
            {
                var pts = 15.0 * Math.Min(m.TextSinkCallerMethods, 20);
                score += pts;
                reasons.Add($"+{pts:0} 文本写入方法×{m.TextSinkCallerMethods}");
            }

            bool nameMatched = NameMatches(c, rootName);
            if (nameMatched)
            {
                score += 150;
                reasons.Add("+150 目录名与输入目录匹配");
            }

            if (m.LauncherSuspicionTypes > 0)
            {
                var pts = 150.0 * Math.Min(m.LauncherSuspicionTypes, 6);
                score -= pts;
                reasons.Add($"-{pts:0} 启动器/更新器可疑类型×{m.LauncherSuspicionTypes}");
            }
            if (TextPatterns.HasLauncherSuspicion(c.DataDirName))
            {
                score -= 1000;
                reasons.Add($"-1000 数据目录名 {c.DataDirName} 像启动器/更新器");
            }
            else if (!string.IsNullOrEmpty(c.ExePath) && TextPatterns.HasLauncherSuspicion(Path.GetFileNameWithoutExtension(c.ExePath)))
            {
                score -= 300;
                reasons.Add($"-300 启动 exe 名 {Path.GetFileName(c.ExePath)} 像启动器");
            }
            if (!m.Scanned)
            {
                score -= 5000;
                reasons.Add("-5000 扫描失败");
            }

            list.Add(new RankedCandidate
            {
                Candidate = c,
                Score = score,
                Reason = string.Join("；", reasons),
                NameMatched = nameMatched,
            });
        }

        return list
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.NameMatched)
            .ThenBy(r => r.Candidate.Depth)
            .ThenBy(r => r.Candidate.DataDir, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool NameMatches(DiscoveryCandidate c, string rootName)
    {
        if (string.IsNullOrWhiteSpace(rootName)) return false;
        var gameName = new DirectoryInfo(c.GameDir).Name;
        if (string.Equals(gameName, rootName, StringComparison.OrdinalIgnoreCase)) return true;
        var stem = c.DataDirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) ? c.DataDirName[..^5] : c.DataDirName;
        if (string.Equals(stem, rootName, StringComparison.OrdinalIgnoreCase)) return true;
        // 允许 "To the Moon" ⊂ "To the Moon (SerenityForge)" 这类前缀匹配
        return gameName.StartsWith(rootName, StringComparison.OrdinalIgnoreCase) &&
               gameName.Length - rootName.Length <= 24;
    }

    /// <summary>Top1 与 Top2 分数相同 → 无法自动决断。</summary>
    public static bool IsAmbiguous(IReadOnlyList<RankedCandidate> ranked) =>
        ranked.Count >= 2 && Math.Abs(ranked[0].Score - ranked[1].Score) < 0.001;
}
