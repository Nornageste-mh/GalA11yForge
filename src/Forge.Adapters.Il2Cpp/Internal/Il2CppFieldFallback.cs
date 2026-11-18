namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>字段兜底的字段类型：字符串直接读，组件包装后读 <c>text</c> 属性。</summary>
internal enum Il2CppFieldKind
{
    /// <summary>System.String 字段（例如 <c>DialogueNode.text</c>）。</summary>
    String = 0,

    /// <summary>Unity UGUI / TextMeshPro 组件（例如 <c>DialogueBubbleUI._dialogueText</c>）。</summary>
    Component = 1,

    /// <summary>当前版本不支持的游戏自有组件类型，生成时跳过。</summary>
    Unsupported = 2,
}

/// <summary>一条“可字段兜底”的候选字段。</summary>
internal sealed class Il2CppTextField
{
    public Il2CppTextField(string name, string simpleType, Il2CppFieldKind kind, string? csharpType)
    {
        Name = name;
        SimpleType = simpleType;
        Kind = kind;
        CSharpType = csharpType;
    }

    /// <summary>字段原名（含私有前缀，例如 <c>_dialogueText</c>）。</summary>
    public string Name { get; }

    /// <summary>元数据里的简单类型名（<c>string</c> / <c>TextMeshProUGUI</c> / …）。</summary>
    public string SimpleType { get; }

    public Il2CppFieldKind Kind { get; }

    /// <summary>组件字段的 C# 全名（<c>TMPro.TextMeshProUGUI</c>）；字符串字段为 null。</summary>
    public string? CSharpType { get; }
}

/// <summary>
/// IL2CPP 字段兜底的“证据格式”约定。
///
/// <para>
/// 分析器在候选证据里写一行 <c>类型文本字段：_dialogueText:TextMeshProUGUI/…（实例方法）</c>，
/// 计划器把这行读出来生成字段兜底补丁点，生成器再从补丁点描述里读回来。
/// 这样三段（分析/计划/生成）之间只依赖**人可读的标记文本**，不需要给核心模型加 IL2CPP 专用字段，
/// 也不影响 Unity Mono / Ren'Py 的既有契约。
/// </para>
///
/// <para>
/// 真实运行期原型（research/50，task-57）证明：Il2CppInterop 代理类型只有
/// <c>static NativeFieldInfoPtr_*</c>，C# 反射 <c>GetField</c> 恒 null；必须用
/// <c>il2cpp_object_get_class</c> + <c>il2cpp_class_get_field_from_name</c> +
/// <c>il2cpp_field_get_value_object</c> 从原生对象读，因此生成代码里不能写死 offset。
/// </para>
/// </summary>
internal static class Il2CppFieldFallback
{
    /// <summary>证据/描述里的字段标记前缀（英文冒号是格式的一部分，不要改）。</summary>
    internal const string Marker = "类型文本字段：";

    private static readonly HashSet<string> StringTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "String", "System.String", "Il2CppSystem.String",
    };

    /// <summary>当前版本只支持这三类 Unity/TMP 组件（其余类型生成不了可靠的包装代码）。</summary>
    private static readonly Dictionary<string, string> ComponentTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TextMeshProUGUI"] = "TMPro.TextMeshProUGUI",
        ["TextMeshPro"] = "TMPro.TextMeshPro",
        ["TMP_Text"] = "TMPro.TMP_Text",
        ["Text"] = "UnityEngine.UI.Text",
    };

    /// <summary>解析证据里的字段行；没有标记、或标记为静态方法时返回空列表。</summary>
    public static List<Il2CppTextField> ParseEvidence(IEnumerable<string> evidence)
    {
        foreach (var line in evidence)
        {
            if (!line.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("静态方法", StringComparison.Ordinal))
            {
                return [];
            }

            return ParseFields(line[Marker.Length..]);
        }

        return [];
    }

    /// <summary>解析计划描述里的字段标记（与证据同一格式）。</summary>
    public static List<Il2CppTextField> ParseDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return [];
        }

        var index = description.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return [];
        }

        if (description.Contains("静态方法", StringComparison.Ordinal))
        {
            return [];
        }

        return ParseFields(description[(index + Marker.Length)..]);
    }

    /// <summary>把字段列表拼成证据行（调用方负责补“（实例方法）”后缀）。</summary>
    public static string FormatFields(IEnumerable<Il2CppTextField> fields)
        => Marker + string.Join("/", fields.Select(f => f.Name + ":" + f.SimpleType));

    /// <summary>解析类型事实里的字段列表（<c>名:简单类型</c>），用于分析阶段预判能否字段兜底。</summary>
    public static List<Il2CppTextField> ParseMembers(IReadOnlyList<string> members)
        => members.Count == 0 ? [] : ParseFields(string.Join("/", members));

    /// <summary>至少有一个当前版本能生成的字段（字符串或已知组件）。</summary>
    public static bool HasSupportedField(IReadOnlyList<Il2CppTextField> fields)
        => fields.Any(f => f.Kind is Il2CppFieldKind.String or Il2CppFieldKind.Component);

    private static List<Il2CppTextField> ParseFields(string text)
    {
        // 去掉“（实例方法）”这类后缀，只留 “a:type/b:type”。
        var cut = text.IndexOf('（');
        if (cut >= 0)
        {
            text = text[..cut];
        }

        cut = text.IndexOf('(');
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var fields = new List<Il2CppTextField>();
        foreach (var raw in text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0 || colon >= raw.Length - 1)
            {
                continue;
            }

            var name = raw[..colon].Trim();
            var simpleType = raw[(colon + 1)..].Trim();
            if (name.Length == 0 || simpleType.Length == 0)
            {
                continue;
            }

            // 调试/状态/路径类字段不是台词，读出来只会吵（0.2.12 真机 _diag 被当台词）。
            if (Il2CppPatterns.FieldNameExcludeRx.IsMatch(name))
            {
                continue;
            }

            if (StringTypeNames.Contains(simpleType))
            {
                fields.Add(new Il2CppTextField(name, simpleType, Il2CppFieldKind.String, null));
                continue;
            }

            if (ComponentTypeNames.TryGetValue(simpleType, out var csharpType))
            {
                fields.Add(new Il2CppTextField(name, simpleType, Il2CppFieldKind.Component, csharpType));
                continue;
            }

            fields.Add(new Il2CppTextField(name, simpleType, Il2CppFieldKind.Unsupported, null));
        }

        return fields;
    }
}
