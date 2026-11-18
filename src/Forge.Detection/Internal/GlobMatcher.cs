using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace GalA11yForge.Detection.Internal;

/// <summary>
/// 指纹库 glob 匹配器。语义对齐参考实现 detect.py 使用的 Python fnmatch：
/// - 大小写不敏感（pattern 与路径都先 lower）；
/// - <c>*</c> 匹配任意字符**包括 '/'**（因此 <c>*.rpa</c> 能命中 game/audio.rpa）；
/// - <c>?</c> 匹配单个字符，<c>[...]</c> 为字符类，<c>[!...]</c> 取反；
/// - 整串锚定（不是子串匹配）；
/// - pattern 先做 Python 的 <c>lstrip("./")</c>（去掉开头所有 '.' 与 '/'）。
/// 与 detect.py 的差异：这里带 3 秒正则超时，防止恶意/写错的 pattern 卡死。
/// </summary>
internal static class GlobMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(3);

    public static bool IsMatch(string relativePath, string globPattern)
    {
        if (string.IsNullOrEmpty(globPattern))
        {
            return false;
        }

        var regex = Cache.GetOrAdd(globPattern, Build);
        try
        {
            return regex.IsMatch(relativePath ?? string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            // 超时视为不命中；指纹库是我们自己的数据，正常不会触发。
            return false;
        }
    }

    private static Regex Build(string pattern)
    {
        var normalized = pattern.ToLowerInvariant().TrimStart('.', '/');
        return new Regex(
            Translate(normalized),
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);
    }

    /// <summary>复刻 fnmatch.translate 的结构（含字符类边缘情况）。</summary>
    private static string Translate(string pattern)
    {
        var builder = new System.Text.StringBuilder("\\A");
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i++];
            switch (c)
            {
                case '*':
                    builder.Append(".*");
                    break;

                case '?':
                    builder.Append('.');
                    break;

                case '[':
                    var j = i;
                    if (j < pattern.Length && pattern[j] == '!')
                    {
                        j++;
                    }

                    if (j < pattern.Length && pattern[j] == ']')
                    {
                        j++;
                    }

                    while (j < pattern.Length && pattern[j] != ']')
                    {
                        j++;
                    }

                    if (j >= pattern.Length)
                    {
                        // 未闭合的 '[' 按字面量处理（Python 同样行为）。
                        builder.Append("\\[");
                    }
                    else
                    {
                        var stuff = pattern.Substring(i, j - i).Replace("\\", "\\\\");
                        i = j + 1;
                        if (stuff.Length > 0 && stuff[0] == '!')
                        {
                            stuff = "^" + stuff.Substring(1);
                        }
                        else if (stuff.Length > 0 && stuff[0] == '^')
                        {
                            stuff = "\\" + stuff;
                        }

                        builder.Append('[').Append(stuff).Append(']');
                    }

                    break;

                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        builder.Append("\\z");
        return builder.ToString();
    }
}
