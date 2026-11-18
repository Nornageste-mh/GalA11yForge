using System.Text.RegularExpressions;

namespace GalA11yForge.Generators.Internal;

/// <summary>
/// 生成物自检：任何一条命中都说明模板或语音桥有问题，生成器应返回 E_ARTIFACT_INVALID，
/// 而不是把坏产物交给用户。
/// </summary>
internal static partial class ArtifactTextValidator
{
    [GeneratedRegex(@"\{\{[A-Z0-9_]+\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex LeftoverToken();

    public static IReadOnlyList<string> Validate(string relativePath, string text)
    {
        var problems = new List<string>();

        foreach (Match match in LeftoverToken().Matches(text))
        {
            problems.Add($"{relativePath} 含未替换的占位符 {match.Value}");
        }

        if (text.Contains("namespace A11yFramework", StringComparison.Ordinal))
        {
            problems.Add($"{relativePath} 仍保留 A11yFramework 命名空间（语音桥替换失败）");
        }

        if (text.Contains("@@ROUTE", StringComparison.Ordinal)
            || text.Contains("@@ENDROUTE", StringComparison.Ordinal))
        {
            problems.Add($"{relativePath} 含未裁剪的路线标记 @@ROUTE");
        }

        return problems;
    }
}
