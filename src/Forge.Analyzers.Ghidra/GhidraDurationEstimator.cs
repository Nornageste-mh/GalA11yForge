namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>
/// 按可执行文件体积粗估 Ghidra headless 的耗时。
/// 仅用于“大概还要等多久 / 要不要把超时调大”的提示，不参与任何判定；
/// 系数来自本机 3 个样本的实测（Lilith 2.2MB→223s、To the Moon 8MB→超 600s、小 exe 61KB→51s）。
/// </summary>
public static class GhidraDurationEstimator
{
    /// <summary>Ghidra 启动 + 工程初始化的经验耗时（秒）。</summary>
    public const double StartupSeconds = 30.0;

    /// <summary>每 MB 可执行文件的分析耗时经验值（秒）。</summary>
    public const double SecondsPerMegabyte = 85.0;

    public const double MinimumSeconds = 30.0;

    public const double MaximumSeconds = 3600.0;

    /// <summary>粗估需要多少秒；sizeBytes 未知（&lt;=0）时只返回启动耗时。</summary>
    public static double EstimateSeconds(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return StartupSeconds;
        }

        var megabytes = sizeBytes / (1024.0 * 1024.0);
        var seconds = StartupSeconds + megabytes * SecondsPerMegabyte;
        return Math.Clamp(seconds, MinimumSeconds, MaximumSeconds);
    }

    /// <summary>给提示用：四舍五入到整秒的预估。</summary>
    public static int EstimateRoundedSeconds(long sizeBytes) =>
        (int)Math.Round(EstimateSeconds(sizeBytes), MidpointRounding.AwayFromZero);

    /// <summary>把预估转成人话。</summary>
    public static string Describe(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return "无法粗估（不知道可执行文件大小）";
        }

        var seconds = EstimateRoundedSeconds(sizeBytes);
        return seconds >= 600 ? $"约 {seconds / 60} 分钟" : $"约 {seconds} 秒";
    }

    /// <summary>
    /// 超时后建议的超时上限：粗估 + 60 秒余量，且至少比当前值多 30 秒；夹在 30–3600 秒之间。
    /// 只用于给出可复制的命令建议，不参与判定。
    /// </summary>
    public static int SuggestTimeoutSeconds(long sizeBytes, int currentTimeoutSeconds)
    {
        var estimate = EstimateRoundedSeconds(sizeBytes);
        var suggested = Math.Max(estimate + 60, currentTimeoutSeconds + 30);
        return (int)Math.Clamp(suggested, MinimumSeconds, MaximumSeconds);
    }
}
