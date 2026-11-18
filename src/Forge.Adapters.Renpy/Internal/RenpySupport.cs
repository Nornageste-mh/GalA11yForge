using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>适配器内部的小工具：只读文本、SHA256、编码、证据格式化。</summary>
internal static class RenpySupport
{
    /// <summary>生成物统一使用 UTF-8（无 BOM）；只有 uninstall.ps1 例外（带 BOM，便于 Windows PowerShell 5.1 识别）。</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static string? ReadTextOrNull(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // 有些老游戏脚本不是严格 UTF-8；用替换字符容忍，避免识别阶段因一行注释失败。
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string[] ReadLinesOrEmpty(string path)
    {
        var text = ReadTextOrNull(path);
        return string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    public static byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string path) => Sha256Hex(File.ReadAllBytes(path));

    /// <summary>格式化成 "207,974,393 B"；不换算 MiB，便于和资源管理器/研究记录对齐。</summary>
    public static string FormatBytes(long bytes) => bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";

    /// <summary>证据里的可读体积："198.3 MiB"。仅用于摘要，括号里仍附精确字节。</summary>
    public static string FormatMiB(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 MiB";
        }

        var mib = bytes / 1024d / 1024d;
        return mib.ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
    }

    public static int LineNumberAt(string text, int index)
    {
        var line = 1;
        var limit = Math.Clamp(index, 0, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    public static List<int> FindLineNumbers(string[] lines, string needle, int limit = 12)
    {
        var result = new List<int>();
        for (var i = 0; i < lines.Length && result.Count < limit; i++)
        {
            if (lines[i].Contains(needle, StringComparison.Ordinal))
            {
                result.Add(i + 1);
            }
        }

        return result;
    }

    public static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string ShortList(IEnumerable<string> values, int max = 6)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (list.Count == 0)
        {
            return "（无）";
        }

        var head = string.Join("、", list.Take(max));
        return list.Count > max ? head + "…" : head;
    }

    /// <summary>Python 双引号字符串转义；用于 GAME_NAME / RENPY_VERSION 注入模板。</summary>
    public static string EscapePythonString(string value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>把目录名里的换行/控制字符压平，避免写进 JSON 的证据出现坏行。</summary>
    public static string OneLine(string value)
        => Regex.Replace(value ?? string.Empty, @"[\r\n\t]+", " ").Trim();
}
