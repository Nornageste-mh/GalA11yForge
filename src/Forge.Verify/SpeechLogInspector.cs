using System.Text;
using System.Text.RegularExpressions;

namespace GalA11yForge.Verify;

/// <summary>
/// 语音日志摘要。只统计数量与重复情况，不保存朗读文本——
/// 报告会被复制/发出去，台词不能写进报告。
/// </summary>
public sealed class SpeechLogSummary
{
    public string Path { get; init; } = string.Empty;

    public long FileSize { get; init; }

    /// <summary>日志超过上限时只读尾部（对话在尾部），此时为 true。</summary>
    public bool Truncated { get; init; }

    public int ScannedLines { get; init; }

    /// <summary>命中“朗读行”标记的总行数（含占位噪声）。</summary>
    public int SpeechLines { get; init; }

    /// <summary>去掉占位噪声后的有效朗读行数。</summary>
    public int ValidLines { get; init; }

    /// <summary>有效朗读文本按原文去重后的条数。</summary>
    public int UniqueLines { get; init; }

    /// <summary>被过滤掉的占位文本行数（Empty/None 等）。</summary>
    public int PlaceholderLines { get; init; }

    /// <summary>[朗读] 回声行：同一句已经由 [Speech]/[a11y-speech] 计过，不再重复计入有效行。</summary>
    public int EchoLines { get; init; }

    /// <summary>同一句最多连续/累计出现次数。</summary>
    public int MaxRepeat { get; init; }

    /// <summary>钩子命中行数（[Hook:...] 命中）。</summary>
    public int HookHitLines { get; init; }

    /// <summary>钩子成功挂载行数（[Hook] 已挂载），0.2.84。</summary>
    public int HookMountedLines { get; init; }

    /// <summary>钩子解析失败行数（Ambiguous match / 解析目标时出错），0.2.84。</summary>
    public int HookResolveErrorLines { get; init; }

    /// <summary>兜底轮询读到行数（[Speech:watch] 兜底读到）。</summary>
    public int WatchLines { get; init; }

    /// <summary>Ren'Py 覆盖层播报行数（[A11y对话]/[A11y屏幕]/[A11y菜单]）。</summary>
    public int RenpyLines { get; init; }

    /// <summary>日志里的错误行数（[A11y错误] 或 Exception），0.2.78。</summary>
    public int ErrorLines { get; init; }
}

/// <summary>
/// 只读语音日志检查器。支持三类补丁日志：
/// - Unity Mono / IL2CPP：BepInEx 日志里的 <c>[Speech] …</c>、<c>[a11y-speech] …</c>；
/// - 旧版 Unity 框架：<c>[朗读] …</c>；
/// - Ren'Py 覆盖层：<c>… backend=SAPI …</c>。
/// 只输出统计，不输出文本。
/// </summary>
public static class SpeechLogInspector
{
    private const int DefaultMaxBytes = 4 * 1024 * 1024;

    private static readonly Regex SpeechLineRx = new(
        @"\[(?:a11y-speech|Speech|朗读)\][ ]+(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RenpyBackendRx = new(
        @"backend=SAPI[ ]+(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RenpyChannelRx = new(
        @"\[A11y(?:对话|屏幕|菜单)\][ ]+(?:(?:[+-]?\d+(?:\.\d+)?s)[ ]+)?(?:backend=\S+[ ]+)?(?<text>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HookHitRx = new(
        @"\[Hook(?::[^\]]*)?\][ ]+命中",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 0.2.84：插件启动时的挂载日志 `[Hook] 已挂载 Hook_hook_001 → …`。
    private static readonly Regex HookMountedRx = new(
        @"\[Hook\][ ]+已挂载",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 0.2.84：解析失败日志，例如 `[Hook] Hook_hook_002 解析目标时出错：Ambiguous match found.`。
    private static readonly Regex HookResolveErrorRx = new(
        @"(?:AmbiguousMatchException|Ambiguous match|解析目标时出错|无法解析目标)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorRx = new(
        @"\[A11y错误\]|Exception[: ]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WatchReadRx = new(
        @"\[Speech:watch\][ ]+兜底读到",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlaceholderRx = new(
        @"^(?:\[\d+\][ ]*)?(?:empty|none|null|n/a|未设置|无内容)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<SpeechLogSummary> InspectAsync(
        string path,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        maxBytes = Math.Clamp(maxBytes, 4 * 1024, 64 * 1024 * 1024);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var length = stream.Length;
        var truncated = length > maxBytes;
        if (truncated)
        {
            stream.Seek(length - maxBytes, SeekOrigin.Begin);
        }

        var buffer = new byte[truncated ? maxBytes : length];
        if (buffer.Length > 0)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        var text = Encoding.UTF8.GetString(buffer);
        if (truncated)
        {
            // 丢掉从行中间开始的第一个片段。
            var firstNewline = text.IndexOf('\n');
            text = firstNewline >= 0 ? text[(firstNewline + 1)..] : string.Empty;
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var scanned = 0;
        var speech = 0;
        var valid = 0;
        var placeholders = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var hookHits = 0;
        var hookMounted = 0;
        var hookResolveErrors = 0;
        var watchReads = 0;
        var echoes = 0;
        var renpyLines = 0;
        var errorLines = 0;
        var spoken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            scanned++;

            if (HookHitRx.IsMatch(raw))
            {
                hookHits++;
            }

            if (HookMountedRx.IsMatch(raw))
            {
                hookMounted++;
            }

            if (HookResolveErrorRx.IsMatch(raw))
            {
                hookResolveErrors++;
            }

            if (WatchReadRx.IsMatch(raw))
            {
                watchReads++;
            }

            if (ErrorRx.IsMatch(raw))
            {
                errorLines++;
            }

            var match = SpeechLineRx.Match(raw);
            var isRenpy = false;
            if (!match.Success)
            {
                // Ren'Py 的第二形态：带 backend= 的播报行，同样属于覆盖层通道（0.2.73 修正统计）。
                match = RenpyBackendRx.Match(raw);
                isRenpy = match.Success;
            }

            if (!match.Success)
            {
                match = RenpyChannelRx.Match(raw);
                isRenpy = match.Success;
            }

            if (!match.Success)
            {
                continue;
            }

            speech++;
            var value = match.Groups["text"].Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (PlaceholderRx.IsMatch(value))
            {
                placeholders++;
                continue;
            }

            var isEcho = raw.Contains("[朗读]", StringComparison.Ordinal)
                || (isRenpy && raw.Contains("backend=", StringComparison.Ordinal));
            if (isEcho && spoken.Contains(value))
            {
                echoes++;
                continue;
            }

            valid++;
            if (isRenpy)
            {
                renpyLines++;
            }

            spoken.Add(value);
            counts.TryGetValue(value, out var times);
            counts[value] = times + 1;
        }

        return new SpeechLogSummary
        {
            Path = path,
            FileSize = length,
            Truncated = truncated,
            ScannedLines = scanned,
            SpeechLines = speech,
            ValidLines = valid,
            UniqueLines = counts.Count,
            PlaceholderLines = placeholders,
            EchoLines = echoes,
            MaxRepeat = counts.Count == 0 ? 0 : counts.Values.Max(),
            HookHitLines = hookHits,
            HookMountedLines = hookMounted,
            HookResolveErrorLines = hookResolveErrors,
            WatchLines = watchReads,
            RenpyLines = renpyLines,
            ErrorLines = errorLines,
        };
    }
}
