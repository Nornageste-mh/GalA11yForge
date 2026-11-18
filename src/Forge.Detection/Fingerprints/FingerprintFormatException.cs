using System.Text;

namespace GalA11yForge.Detection.Fingerprints;

/// <summary>
/// 指纹库损坏 / 不合约时抛出的明确异常。
/// 设计目标：绝不静默降级——否则「识别不到」会被误读成「不是 Galgame」。
/// 一次抛错会带上全部校验问题（最多 20 条），便于集中修库。
/// </summary>
public sealed class FingerprintFormatException : Exception
{
    public FingerprintFormatException(string sourcePath, IReadOnlyList<string> problems, Exception? innerException = null)
        : base(BuildMessage(sourcePath, problems), innerException)
    {
        SourcePath = sourcePath;
        Problems = problems;
    }

    public FingerprintFormatException(string sourcePath, string problem, Exception? innerException = null)
        : this(sourcePath, new[] { problem }, innerException)
    {
    }

    /// <summary>出问题的库来源（磁盘路径或 embedded:资源名）。</summary>
    public string SourcePath { get; }

    /// <summary>全部校验问题；可直接写进日志或错误报告。</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string BuildMessage(string sourcePath, IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder();
        sb.Append("引擎指纹库无效（").Append(sourcePath).Append("）：");
        var count = Math.Min(problems.Count, 20);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append("；");
            }

            sb.Append(problems[i]);
        }

        if (problems.Count > 20)
        {
            sb.Append("；…（共 ").Append(problems.Count).Append(" 项问题）");
        }

        return sb.ToString();
    }
}
