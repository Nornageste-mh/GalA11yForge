using System.Text;
using System.Text.RegularExpressions;

namespace GalA11yForge.ExternalReader;

/// <summary>一条“观测到可能是台词”的文本。NodeId 用于跨轮次追踪同一个元素。</summary>
public sealed record TextObservation(string Source, string NodeId, string? ControlType, string Text, bool Offscreen)
{
    public string Describe() => $"{Source}:{NodeId}:{ControlType}";
}

/// <summary>
/// 文本归一化。R9/R12 已经踩过的坑都在这里处理：
///   * Windows 中文 OCR 会在每个汉字之间插空格（"「 今 天"）——必须去 CJK 间空格；
///   * UIA 的 Name 常带换行/制表/不可见字符；
///   * 超长文本（整篇 DOM）要截断，否则 SAPI 会念很久、日志也会爆。
/// </summary>
public static partial class TextNormalizer
{
    private static readonly Regex CjkSpace = new(
        @"(?<=[\u3000-\u303F\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF])[ \t]+(?=[\u3000-\u303F\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFF00-\uFFEF])",
        RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex MultiNewline = new(@"\n{2,}", RegexOptions.Compiled);
    private static readonly Regex ZeroWidth = new(@"[\u200B-\u200F\u202A-\u202E\u2060\uFEFF\uFFFD]", RegexOptions.Compiled);
    private static readonly Regex Control = new(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", RegexOptions.Compiled);

    public static string Normalize(string? raw, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var s = raw.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Replace('\u00A0', ' ');
        s = ZeroWidth.Replace(s, string.Empty);
        s = Control.Replace(s, string.Empty);
        s = CjkSpace.Replace(s, string.Empty);
        s = MultiSpace.Replace(s, " ");
        s = MultiNewline.Replace(s, "\n");
        s = s.Trim();
        if (maxLen > 0 && s.Length > maxLen)
        {
            var cut = s[..maxLen];
            var lastBreak = cut.LastIndexOfAny(['\n', '。', '！', '？', '，', ' ', '.', '!', '?']);
            if (lastBreak > maxLen / 2) cut = cut[..(lastBreak + 1)];
            s = cut.TrimEnd();
        }
        return s;
    }

    /// <summary>日志/报告用的脱敏拍照：长度 + 前 6 字 + 短哈希，用来对时间线而不落全文。</summary>
    public static (int Len, string Head, string Hash) Fingerprint(string text)
    {
        var head = text.Length <= 6 ? text : text[..6];
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8].ToLowerInvariant();
        return (text.Length, head, hash);
    }
}
