namespace GalA11yForge.Gui.Core;

/// <summary>
/// 高级分析（Ghidra）耗时的粗略提示，只用于界面提示，不参与判定。
///
/// 注意：公式与 <c>src/Forge.Analyzers.Ghidra/GhidraDurationEstimator.cs</c> 保持一致
/// （启动 30 秒 + 约 85 秒/MB，夹在 30–3600 秒）。GUI 核心库刻意不引用 Forge.Core /
/// Forge.Analyzers.Ghidra，所以这里保留一份最小实现；改公式时两边一起改，
/// 两边都有单元测试覆盖同一组样例数字。
/// </summary>
public static class AnalysisDurationHint
{
    public const double StartupSeconds = 30.0;

    public const double SecondsPerMegabyte = 85.0;

    public const double MinimumSeconds = 30.0;

    public const double MaximumSeconds = 3600.0;

    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>估算秒数（未取整），夹在 30–3600 秒。</summary>
    public static double EstimateSeconds(long executableSizeBytes)
    {
        if (executableSizeBytes <= 0)
        {
            return StartupSeconds;
        }

        var megabytes = executableSizeBytes / BytesPerMegabyte;
        var seconds = StartupSeconds + SecondsPerMegabyte * megabytes;
        return Math.Clamp(seconds, MinimumSeconds, MaximumSeconds);
    }

    /// <summary>四舍五入到整秒。</summary>
    public static int EstimateRoundedSeconds(long executableSizeBytes)
        => (int)Math.Round(EstimateSeconds(executableSizeBytes), MidpointRounding.AwayFromZero);

    /// <summary>人话描述；≥600 秒读成“约 N 分钟”。</summary>
    public static string Describe(long executableSizeBytes)
    {
        if (executableSizeBytes <= 0)
        {
            return "无法粗估（不知道可执行文件大小）";
        }

        var seconds = EstimateRoundedSeconds(executableSizeBytes);
        return seconds >= 600 ? $"约 {seconds / 60} 分钟" : $"约 {seconds} 秒";
    }
}
