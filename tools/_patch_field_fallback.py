# -*- coding: utf-8 -*-
"""v0.2.5 字段兜底朗读：把候选证据里的实例文本字段接进生成器。"""
import io
import sys


def patch(path, old, new, count=1):
    s = io.open(path, encoding="utf-8").read()
    if s.count(old) < count:
        print("MISS in", path)
        print("OLD:", old[:120])
        sys.exit(1)
    s = s.replace(old, new, count)
    io.open(path, "w", encoding="utf-8", newline="").write(s)
    print("patched", path)


PLANNER = "src/Forge.Adapters.UnityMono/Internal/UnityMonoPlanner.cs"
patch(
    PLANNER,
    '''            "Hook 目标省略参数类型以兼容生成器白名单，运行期若遇到同名重载需人工指定。";
    }
''',
    '''            "Hook 目标省略参数类型以兼容生成器白名单，运行期若遇到同名重载需人工指定。" +
            DescribeTextFields(view);
    }

    /// <summary>
    /// 把候选证据里的实例文本字段名带进 plan：方法没有字符串参数时，生成器据此读实例字段兜底朗读。
    /// 只复制字段名，不读台词原文。
    /// </summary>
    private static string DescribeTextFields(UnityMonoCandidateView view)
    {
        var line = view.Evidence.FirstOrDefault(e => e.StartsWith("类型文本字段：", StringComparison.Ordinal));
        if (line is null)
        {
            return string.Empty;
        }

        var names = line["类型文本字段：".Length..].Trim().TrimEnd('。');
        return string.IsNullOrWhiteSpace(names) ? string.Empty : $" 类型文本字段：{names}。";
    }
''',
)

SETTINGS = "src/Forge.Generators/Internal/GeneratorSettings.cs"
patch(
    SETTINGS,
    "using System.Text;\nusing System.Text.RegularExpressions;\n",
    "using System.Collections.Generic;\nusing System.Text;\nusing System.Text.RegularExpressions;\n",
)

patch(
    SETTINGS,
    '''    /// <summary>从补丁点证据里的完整 Cecil 签名解析出的字符串参数名（没有则为 null）。</summary>
    public string? TextParameter { get; init; }
''',
    '''    /// <summary>从补丁点证据里的完整 Cecil 签名解析出的字符串参数名（没有则为 null）。</summary>
    public string? TextParameter { get; init; }

    /// <summary>
    /// 没有字符串参数时，候选证据给出的实例文本字段（TMP_Text/UI.Text 组件字段名）。
    /// 生成器会生成 Postfix(object __instance) 反射读字段兜底朗读；只复制字段名，不读台词原文。
    /// </summary>
    public IReadOnlyList<string> InstanceTextFields { get; init; } = Array.Empty<string>();
''',
)

patch(
    SETTINGS,
    '''    /// <summary>是否作为主朗读钩子（只允许一个：第一个带字符串参数的钩子）。</summary>
    public bool AutoSpeak { get; set; }''',
    '''    /// <summary>是否作为主朗读钩子（只允许一个：优先第一个带字符串参数的钩子，其次带实例文本字段的钩子）。</summary>
    public bool AutoSpeak { get; set; }''',
)

patch(
    SETTINGS,
    '''    // 证据文本里的形状：「完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::AnimateText(...)」
    [GeneratedRegex(@"完整签名（Cecil）：\\s*\\[[^\\]]+\\]\\s*[^\\(\\)]+\\((?<args>[^\\(\\)]*)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex CecilSignaturePattern();
''',
    '''    // 证据文本里的形状：「完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::AnimateText(...)」
    [GeneratedRegex(@"完整签名（Cecil）：\\s*\\[[^\\]]+\\]\\s*[^\\(\\)]+\\((?<args>[^\\(\\)]*)\\)", RegexOptions.CultureInvariant)]
    private static partial Regex CecilSignaturePattern();

    // 证据文本里的形状：「类型文本字段：mainText/mainName。」（无 string 参数时的兜底朗读来源）
    [GeneratedRegex(@"类型文本字段：(?<fields>[^。\\r\\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TextFieldPattern();
''',
)

patch(
    SETTINGS,
    '''                TextParameter = TryFindTextParameter(point),
                ChoiceList = IsChoiceListPoint(point),''',
    '''                TextParameter = TryFindTextParameter(point),
                InstanceTextFields = TryFindInstanceTextFields(point),
                ChoiceList = IsChoiceListPoint(point),''',
)

patch(
    SETTINGS,
    '''        // 主朗读出口只设一个：plan 已按置信度排序，取第一个带字符串参数的钩子。
        var primary = hooks.FirstOrDefault(h => h.TextParameter is not null);''',
    '''        // 主朗读出口只设一个：优先第一个带字符串参数的钩子，其次第一个带实例文本字段的钩子。
        var primary = hooks.FirstOrDefault(h => h.TextParameter is not null)
            ?? hooks.FirstOrDefault(h => h.InstanceTextFields.Count > 0);''',
)

patch(
    SETTINGS,
    '''    /// <summary>按顶层分隔符切分（忽略泛型 &lt;&gt; 里的逗号）。</summary>
    private static IEnumerable<string> SplitTopLevel(string text, char separator)''',
    '''    /// <summary>
    /// 从补丁点说明里的「类型文本字段：a/b/c。」解析实例文本字段名（只保留合法标识符）。
    /// 解析不出来返回空列表（退化为只记命中）。
    /// </summary>
    private static IReadOnlyList<string> TryFindInstanceTextFields(PatchPoint point)
    {
        var description = point.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            return Array.Empty<string>();
        }

        var match = TextFieldPattern().Match(description);
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        var fields = new List<string>();
        foreach (var raw in match.Groups["fields"].Value.Split('/'))
        {
            var name = raw.Trim();
            if (name.Length == 0 || !IdentifierPattern().IsMatch(name) || ReservedKeywords.Contains(name) ||
                fields.Contains(name))
            {
                continue;
            }

            fields.Add(name);
        }

        return fields;
    }

    /// <summary>按顶层分隔符切分（忽略泛型 &lt;&gt; 里的逗号）。</summary>
    private static IEnumerable<string> SplitTopLevel(string text, char separator)''',
)

print("done")
