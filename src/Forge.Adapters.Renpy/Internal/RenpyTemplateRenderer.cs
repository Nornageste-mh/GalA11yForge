using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>模板资源缺失。生成器把它转成 E_ARTIFACT_MISSING，绝不静默跳过。</summary>
internal sealed class RenpyTemplateMissingException : Exception
{
    public RenpyTemplateMissingException(string logicalName)
        : base($"程序集内缺少模板资源：{logicalName}")
    {
        LogicalName = logicalName;
    }

    public string LogicalName { get; }
}

/// <summary>渲染后仍残留 @@TOKEN@@，说明模板与生成器版本不匹配。</summary>
internal sealed class RenpyTemplateTokenException : Exception
{
    public RenpyTemplateTokenException(string fileName, string token)
        : base($"模板 {fileName} 渲染后仍残留占位符 @@{token}@@。")
    {
        FileName = fileName;
        Token = token;
    }

    public string FileName { get; }

    public string Token { get; }
}

/// <summary>读取嵌入资源并按 @@TOKEN@@ 参数化。模板文件同时放在 Templates/ 便于审查。</summary>
internal static class RenpyTemplateRenderer
{
    private static readonly Assembly Assembly = typeof(RenpyTemplateRenderer).Assembly;

    private static readonly Regex LeftoverToken = new(
        "@@(?<token>[A-Z0-9_]+)@@",
        RegexOptions.CultureInvariant);

    public static string Load(string logicalName)
    {
        using var stream = Assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
        {
            throw new RenpyTemplateMissingException(logicalName);
        }

        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return NormalizeNewlines(reader.ReadToEnd());
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> tokens, string fileName)
    {
        var rendered = template;
        foreach (var (key, value) in tokens)
        {
            rendered = rendered.Replace("@@" + key + "@@", value ?? string.Empty, StringComparison.Ordinal);
        }

        var leftover = LeftoverToken.Match(rendered);
        if (leftover.Success)
        {
            throw new RenpyTemplateTokenException(fileName, leftover.Groups["token"].Value);
        }

        return rendered;
    }

    public static string NormalizeNewlines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
