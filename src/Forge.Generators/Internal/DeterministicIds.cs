using System.Security.Cryptography;
using System.Text;

namespace GalA11yForge.Generators.Internal;

/// <summary>
/// 确定性标识：同样的 slug / 命名空间永远得到同样的 GUID、程序集名与内容指纹。
/// 不使用 <see cref="Guid.NewGuid"/>，否则同一输入重复生成会产出不同文件，golden 对比和幂等性都无从谈起。
/// </summary>
internal static class DeterministicIds
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Utf8.GetBytes(text));

    /// <summary>
    /// 语音桥内容指纹：按来源 id 排序后取每项「id:sha256」再哈希。
    /// Framework 没有版本号文件，只能用内容指纹如实描述「这份源码来自哪一版 Framework」。
    /// </summary>
    public static string FrameworkFingerprint(IEnumerable<VoiceBridgeFile> files)
    {
        var lines = files
            .OrderBy(f => f.SourceId, StringComparer.Ordinal)
            .Select(f => f.SourceId + ":" + f.SourceSha256);
        return Sha256Hex(string.Join("\n", lines));
    }

    /// <summary>由 slug 派生默认命名空间，例如 <c>forge-golden</c> → <c>GalA11y.ForgeGolden</c>。</summary>
    public static string DefaultNamespace(string slug)
    {
        var builder = new StringBuilder();
        foreach (var part in slug.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = new string(part.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (cleaned.Length == 0)
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(cleaned[0]));
            if (cleaned.Length > 1)
            {
                builder.Append(cleaned.AsSpan(1));
            }
        }

        var name = builder.Length == 0 ? "Game" : builder.ToString();
        if (char.IsAsciiDigit(name[0]))
        {
            name = "G" + name;
        }

        return "GalA11y." + name;
    }

    /// <summary>程序集名取命名空间末段，例如 <c>GalA11y.ForgeGolden</c> → <c>ForgeGolden.A11y</c>。</summary>
    public static string AssemblyNameFromNamespace(string ns)
    {
        var lastDot = ns.LastIndexOf('.');
        var last = lastDot >= 0 ? ns[(lastDot + 1)..] : ns;
        return last + ".A11y";
    }

    /// <summary>v5 形状的确定性 GUID：由 slug 与命名空间共同决定，换 slug 就换 GUID。</summary>
    public static string PluginGuid(string slug, string ns)
    {
        var hash = SHA256.HashData(Utf8.GetBytes("gala11y-forge|plugin|" + slug + "|" + ns));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes).ToString("D");
    }

    /// <summary>把补丁点 id 变成安全的 C# 类名片段（调用方负责在冲突时加后缀）。</summary>
    public static string TypeNameFromId(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (builder.Length == 0)
        {
            builder.Append('X');
        }

        if (!char.IsAsciiLetter(builder[0]) && builder[0] != '_')
        {
            builder.Insert(0, 'N');
        }

        if (builder.Length > 48)
        {
            builder.Length = 48;
        }

        return builder.ToString();
    }
}
