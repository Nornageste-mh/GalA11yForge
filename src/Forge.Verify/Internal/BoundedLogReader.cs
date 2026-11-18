using System.Text;

namespace GalA11yForge.Verify.Internal;

/// <summary>有界日志采样：只读、不把整份日志读进内存、不把原文写入报告。</summary>
internal sealed class LogSample
{
    public long FileSize { get; init; }

    public IReadOnlyList<string> HeadLines { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TailLines { get; init; } = Array.Empty<string>();

    public bool Truncated { get; init; }

    public IEnumerable<string> AllLines => HeadLines.Concat(TailLines);
}

internal static class BoundedLogReader
{
    private const int HeadBytes = 64 * 1024;
    private const int TailBytes = 512 * 1024;
    private const int WholeFileLimit = 256 * 1024;
    private const int MaxHeadLines = 200;

    /// <summary>
    /// 读取日志首部与尾部。BepInEx 的初始化与插件加载行在文件首部；
    /// 只看尾部会在长会话日志里漏掉关键行，因此首尾都采样（仍然有界）。
    /// </summary>
    public static async Task<LogSample> ReadAsync(
        string path,
        int tailLines,
        CancellationToken cancellationToken)
    {
        tailLines = Math.Clamp(tailLines, 20, 5000);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var length = stream.Length;
        if (length == 0)
        {
            return new LogSample { FileSize = 0 };
        }

        if (length <= WholeFileLimit)
        {
            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            var lines = SplitLines(Encoding.UTF8.GetString(buffer));
            return new LogSample
            {
                FileSize = length,
                HeadLines = lines.Take(MaxHeadLines).ToList(),
                TailLines = lines.TakeLast(tailLines).ToList(),
                Truncated = false,
            };
        }

        var headBuffer = new byte[(int)Math.Min(HeadBytes, length)];
        await stream.ReadExactlyAsync(headBuffer, cancellationToken).ConfigureAwait(false);
        var headText = Encoding.UTF8.GetString(headBuffer);

        // 丢掉可能被截断的最后一行。
        var lastNewline = headText.LastIndexOf('\n');
        headText = lastNewline >= 0 ? headText[..lastNewline] : string.Empty;
        var headLines = SplitLines(headText).Take(MaxHeadLines).ToList();

        var tailLength = (int)Math.Min(TailBytes, length);
        stream.Seek(length - tailLength, SeekOrigin.Begin);
        var tailBuffer = new byte[tailLength];
        await stream.ReadExactlyAsync(tailBuffer, cancellationToken).ConfigureAwait(false);
        var tailText = Encoding.UTF8.GetString(tailBuffer);

        // 丢掉可能从行中间开始的第一个片段。
        var firstNewline = tailText.IndexOf('\n');
        tailText = firstNewline >= 0 ? tailText[(firstNewline + 1)..] : string.Empty;
        var tailLinesList = SplitLines(tailText).TakeLast(tailLines).ToList();

        return new LogSample
        {
            FileSize = length,
            HeadLines = headLines,
            TailLines = tailLinesList,
            Truncated = true,
        };
    }

    private static List<string> SplitLines(string text)
        => text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd('\uFEFF'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
}
