using System.Text.RegularExpressions;

namespace GalA11yForge.Generators.Internal;

/// <summary>
/// 单遍占位符替换。单遍的用意：替换进去的值里即使含有 <c>{{XXX}}</c> 也不会被二次替换，
/// 从而让「残留占位符」检查能如实报错，而不是悄悄吞掉。
/// </summary>
internal static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{([A-Z0-9_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        return TokenPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return values.TryGetValue(key, out var value) ? value : match.Value;
        });
    }
}
