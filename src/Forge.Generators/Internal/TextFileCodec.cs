using System.Text;

namespace GalA11yForge.Generators.Internal;

/// <summary>
/// 文本文件的编码约定：.cs / .json / .md 为 UTF-8 无 BOM；.ps1 为 UTF-8 带 BOM
/// （Windows PowerShell 5.1 对无 BOM 的 UTF-8 会按 ANSI 读，中文会乱码）。
/// </summary>
internal static class TextFileCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(byte[] bytes, out bool hadBom)
    {
        hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hadBom ? 3 : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    public static byte[] Encode(string text, bool bom)
    {
        var body = StrictUtf8.GetBytes(text);
        if (!bom)
        {
            return body;
        }

        var result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }

    public static byte[] Utf8NoBom(string text) => Encode(text, bom: false);

    /// <summary>把任意换行统一成 CRLF，保证 Windows 下的生成物稳定且对编辑器友好。</summary>
    public static string ToCrLf(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
}
