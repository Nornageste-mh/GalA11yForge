using System.Diagnostics;
using System.Text;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 降级路线：解析 Il2CppDumper 的 <c>dump.cs</c>（纯文本签名）。
///
/// <para>
/// 只有在没装 BepInEx 6 / 没生成 interop 时才用。它比 interop 多出 RVA 与虚表槽位，
/// 但没有属性/事件结构，也没有任何 IL，因此打分上限与置信度都要低一档。
/// 解析规则完全按 R18 实测的 dump.cs 版式（<c>// Namespace:</c> + <c>// RVA:</c> + <c>{ }</c> 空体）。
/// </para>
/// </summary>
internal sealed class Il2CppDumpParser
{
    internal const int MaxMethods = 200_000;
    internal const int MaxTypes = 40_000;
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(25);

    private static readonly string[] NumericTypeNames =
    [
        "Single", "Int32", "Int64", "UInt32", "UInt64", "Double", "Int16", "UInt16", "Byte", "SByte",
        "float", "int", "long", "uint", "ulong", "double", "short", "ushort", "byte", "sbyte",
    ];

    private static readonly string[] StringTypeNames = ["String", "string", "Il2CppSystem.String"];

    /// <summary>C# 别名 → System.* 全名。dump.cs 用别名，interop 元数据用全名，统一成后者。</summary>
    private static readonly Dictionary<string, string> PrimitiveAliases = new(StringComparer.Ordinal)
    {
        ["string"] = "System.String",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["float"] = "System.Single",
        ["double"] = "System.Double",
        ["bool"] = "System.Boolean",
        ["char"] = "System.Char",
        ["object"] = "System.Object",
        ["decimal"] = "System.Decimal",
    };

    private static string NormalizePrimitive(string typeName)
        => PrimitiveAliases.TryGetValue(typeName, out var full) ? full : typeName;

    private static string TrimParameterModifiers(string typeName)
    {
        foreach (var prefix in (string[])["ref ", "out ", "in ", "params "])
        {
            if (typeName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return typeName.Substring(prefix.Length).Trim();
            }
        }

        return typeName;
    }

    public Il2CppScanOutcome Parse(Il2CppLayout layout, CancellationToken cancellationToken)
    {
        var outcome = new Il2CppScanOutcome { Source = Il2CppSourceKind.Dump, Layout = layout };
        var stopwatch = Stopwatch.StartNew();

        if (layout.DumpPath is null)
        {
            outcome.Warnings.Add("没有 dump.cs：既没有 interop 也没有 dump.cs，无法做任何静态分析。");
            outcome.Elapsed = stopwatch.Elapsed;
            return outcome;
        }

        var pendingAttributes = new List<string>();
        var pendingRva = 0L;
        var pendingSlot = 0;
        var currentNamespace = string.Empty;
        var currentAssembly = Path.GetFileName(layout.DumpPath);
        Il2CppTypeFact? currentType = null;

        try
        {
            foreach (var rawLine in File.ReadLines(layout.DumpPath))
            {
                if ((outcome.MethodCount & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stopwatch.Elapsed > MaxDuration)
                    {
                        outcome.Warnings.Add("warn: dump.cs 解析超过 " + MaxDuration.TotalSeconds + "s，已截断（结果可能不完整）");
                        break;
                    }
                }

                var line = rawLine.TrimEnd();
                if (line.Length == 0)
                {
                    continue;
                }

                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("// Image ", StringComparison.Ordinal))
                {
                    currentAssembly = ParseImageName(trimmed) ?? currentAssembly;
                    continue;
                }

                if (trimmed.StartsWith("// Namespace:", StringComparison.Ordinal))
                {
                    currentNamespace = trimmed.Substring("// Namespace:".Length).Trim();
                    continue;
                }

                if (trimmed.StartsWith("// RVA:", StringComparison.Ordinal))
                {
                    (pendingRva, pendingSlot) = ParseRva(trimmed);
                    continue;
                }

                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed == "}" || trimmed == "{")
                {
                    if (trimmed == "}" && !line.StartsWith('\t'))
                    {
                        currentType = null;
                    }

                    continue;
                }

                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    pendingAttributes.Add(trimmed.Trim('[', ']'));
                    continue;
                }

                if (!line.StartsWith('\t'))
                {
                    // 顶层声明：类型。
                    var typeInfo = TryParseType(trimmed);
                    if (typeInfo is null)
                    {
                        continue;
                    }

                    var name = typeInfo.Value.Name;
                    var fullName = string.IsNullOrEmpty(currentNamespace) || name.Contains('.')
                        ? (string.IsNullOrEmpty(currentNamespace) ? name : currentNamespace + "." + name)
                        : currentNamespace + "." + name;

                    var facts = new Il2CppTypeFact
                    {
                        AssemblyFile = currentAssembly,
                        FullName = fullName,
                        Name = name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name,
                        Namespace = currentNamespace,
                        BaseTypeName = typeInfo.Value.BaseType,
                        IsInterface = typeInfo.Value.IsInterface,
                        IsAbstract = typeInfo.Value.IsAbstract,
                    };

                    facts.FrameworkId = Il2CppPatterns.DetectFramework(currentAssembly, fullName);
                    facts.IsUiType = currentNamespace.StartsWith("Naninovel.UI", StringComparison.Ordinal)
                                     || currentNamespace.StartsWith("UnityEngine", StringComparison.Ordinal)
                                     || facts.Name.Contains("UI", StringComparison.Ordinal)
                                     || facts.Name.Contains("Panel", StringComparison.Ordinal);

                    foreach (var attribute in pendingAttributes)
                    {
                        facts.Attributes.Add(attribute);
                    }

                    pendingAttributes.Clear();
                    if (outcome.Types.Count < MaxTypes)
                    {
                        outcome.Types[fullName] = facts;
                        outcome.TypeCount++;
                    }

                    currentType = facts;
                    continue;
                }

                // 缩进行：字段或方法。字段形如 "public float BaseRevealSpeed; // 0x10"。
                if (currentType is null)
                {
                    continue;
                }

                if (trimmed.EndsWith(';') || trimmed.Contains("; // 0x", StringComparison.Ordinal))
                {
                    AddFieldFact(currentType, trimmed);
                    continue;
                }

                if (!trimmed.EndsWith("}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (outcome.MethodCount >= MaxMethods)
                {
                    outcome.Warnings.Add("warn: dump.cs 方法数超过上限 " + MaxMethods + "，已截断");
                    break;
                }

                var method = TryParseMethod(trimmed, currentAssembly, currentType, pendingRva, pendingSlot);
                pendingRva = 0;
                pendingSlot = 0;
                pendingAttributes.Clear();
                if (method is null)
                {
                    continue;
                }

                outcome.Methods.Add(method);
                outcome.MethodCount++;
                currentType.MethodCount++;
                if (method.PropertyName is not null && method.PropertyTypeName is not null)
                {
                    var member = method.PropertyName + ":" + method.PropertyTypeName;
                    if (!currentType.TextMemberNames.Contains(member) &&
                        (Il2CppPatterns.LooksLikeTextMember(method.PropertyName) ||
                         Il2CppPatterns.IsTextLikeType(method.PropertyTypeName)))
                    {
                        currentType.TextMemberNames.Add(member);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome.Warnings.Add("warn: dump.cs 读取失败：" + ex.Message);
        }

        outcome.AssemblyCount = 1;
        outcome.Elapsed = stopwatch.Elapsed;
        return outcome;
    }

    private static void AddFieldFact(Il2CppTypeFact type, string trimmed)
    {
        // 形如：public string SomeText; // 0x18
        var withoutOffset = trimmed;
        var offsetIndex = trimmed.IndexOf("; // 0x", StringComparison.Ordinal);
        if (offsetIndex > 0)
        {
            withoutOffset = trimmed.Substring(0, offsetIndex);
        }
        else if (trimmed.EndsWith(';'))
        {
            withoutOffset = trimmed.TrimEnd(';');
        }

        var space = withoutOffset.LastIndexOf(' ');
        if (space <= 0)
        {
            return;
        }

        var fieldName = withoutOffset.Substring(space + 1).Trim();
        var rest = withoutOffset.Substring(0, space).Trim();
        var typeName = rest.Contains(' ') ? rest.Substring(rest.LastIndexOf(' ') + 1) : rest;
        var simple = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;

        if (Il2CppPatterns.LooksLikeTextMember(fieldName) && Il2CppPatterns.IsTextLikeType(simple))
        {
            type.TextMemberNames.Add(fieldName + ":" + simple);
        }
    }

    private static Il2CppMethodFact? TryParseMethod(
        string trimmed,
        string assemblyFile,
        Il2CppTypeFact type,
        long rva,
        int slot)
    {
        var body = trimmed;
        var brace = body.IndexOf('{', StringComparison.Ordinal);
        if (brace > 0)
        {
            body = body.Substring(0, brace).TrimEnd();
        }

        var open = body.IndexOf('(');
        if (open <= 0)
        {
            return null;
        }

        var close = FindMatchingParen(body, open);
        if (close < 0)
        {
            return null;
        }

        var head = body.Substring(0, open).Trim();
        var parameterText = body.Substring(open + 1, close - open - 1).Trim();

        var spaceIndex = head.LastIndexOf(' ');
        if (spaceIndex <= 0)
        {
            return null;
        }

        var name = head.Substring(spaceIndex + 1).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var parameters = new List<Il2CppParameterFact>();
        foreach (var part in SplitParameters(parameterText))
        {
            var piece = part.Trim();
            if (piece.Length == 0)
            {
                continue;
            }

            // dump.cs 会带默认值："float speed = 1"、"AsyncToken token = default"。
            // 必须先把默认值切掉，否则 "1" 会被当成形参名。
            var defaultIndex = piece.IndexOf(" = ", StringComparison.Ordinal);
            if (defaultIndex < 0)
            {
                defaultIndex = piece.IndexOf('=');
            }

            if (defaultIndex > 0)
            {
                piece = piece.Substring(0, defaultIndex).Trim();
            }

            var lastSpace = piece.LastIndexOf(' ');
            var pType = lastSpace > 0 ? piece.Substring(0, lastSpace).Trim() : piece;
            var pName = lastSpace > 0 ? piece.Substring(lastSpace + 1).Trim() : string.Empty;

            pType = TrimParameterModifiers(pType);
            pType = NormalizePrimitive(pType);

            var simple = pType.Contains('.') ? pType.Substring(pType.LastIndexOf('.') + 1) : pType;
            simple = simple.TrimEnd('&', '[', ']');
            parameters.Add(new Il2CppParameterFact
            {
                Name = pName,
                TypeName = pType,
                IsString = StringTypeNames.Contains(simple, StringComparer.Ordinal),
                IsNumeric = NumericTypeNames.Contains(simple, StringComparer.Ordinal),
            });
        }

        var isConstructor = name is ".ctor" or ".cctor";
        var propertyName = name.Length > 4 && name.StartsWith("set_", StringComparison.Ordinal)
            ? name.Substring(4)
            : null;
        var propertyTypeName = propertyName is not null && parameters.Count == 1
            ? parameters[0].TypeName
            : null;

        // dump.cs 不输出 event 定义，只能从 add_X(...) 方法反推事件通道；
        // 事件订阅仍然要人工，但计划里必须把它列成“推荐通道”。
        var eventName = name.Length > 4 && name.StartsWith("add_", StringComparison.Ordinal)
            ? name.Substring(4)
            : null;

        var signature = type.FullName + "::" + name + "(" +
                        string.Join(", ", parameters.Select(p => p.TypeName)) + ")";

        return new Il2CppMethodFact
        {
            AssemblyFile = assemblyFile,
            TypeFullName = type.FullName,
            TypeName = type.Name,
            Namespace = type.Namespace,
            Name = name,
            FullName = type.FullName + "." + name,
            Signature = signature,
            IsConstructor = isConstructor,
            IsStatic = head.Contains("static ", StringComparison.Ordinal),
            IsVirtual = head.Contains("virtual ", StringComparison.Ordinal) || head.Contains("override ", StringComparison.Ordinal),
            IsPropertyMember = propertyName is not null,
            PropertyName = propertyName,
            PropertyTypeName = propertyTypeName,
            IsEventAdder = eventName is not null,
            EventName = eventName,
            Parameters = parameters,
            TextParameterName = Il2CppPatterns.PickTextParameter(parameters),
            Rva = rva,
            Slot = slot,
        };
    }

    private static int FindMatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitParameters(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '<' or '[' or '(')
            {
                depth++;
            }
            else if (c is '>' or ']' or ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return text.Substring(start, i - start);
                start = i + 1;
            }
        }

        yield return text.Substring(start);
    }

    private static (string Name, string? BaseType, bool IsInterface, bool IsAbstract)? TryParseType(string trimmed)
    {
        // 例：public class TextPrinterManager : OrthoActorManager`1 // TypeDefIndex: 4533
        var commentIndex = trimmed.IndexOf("//", StringComparison.Ordinal);
        if (commentIndex > 0)
        {
            trimmed = trimmed.Substring(0, commentIndex).TrimEnd();
        }

        var isInterface = trimmed.Contains(" interface ", StringComparison.Ordinal);
        var isAbstract = trimmed.Contains(" abstract ", StringComparison.Ordinal);

        var keywordIndex = -1;
        foreach (var keyword in new[] { " class ", " struct ", " interface ", " enum " })
        {
            var index = trimmed.IndexOf(keyword, StringComparison.Ordinal);
            if (index >= 0 && (keywordIndex < 0 || index < keywordIndex))
            {
                keywordIndex = index;
                if (keyword == " enum ")
                {
                    return null;
                }
            }
        }

        if (keywordIndex < 0)
        {
            return null;
        }

        var rest = trimmed.Substring(keywordIndex + 1).Trim();
        var spaceInRest = rest.IndexOf(' ');
        var nameAndBase = spaceInRest > 0 ? rest.Substring(spaceInRest + 1).Trim() : string.Empty;
        if (nameAndBase.Length == 0)
        {
            return null;
        }

        var colon = nameAndBase.IndexOf(':');
        string name;
        string? baseType = null;
        if (colon > 0)
        {
            name = nameAndBase.Substring(0, colon).Trim();
            baseType = nameAndBase.Substring(colon + 1).Trim();
        }
        else
        {
            name = nameAndBase.Trim();
        }

        // 去掉泛型参数：Foo`1 / Foo<T>
        var genericIndex = name.IndexOfAny(['<', '`']);
        if (genericIndex > 0)
        {
            name = name.Substring(0, genericIndex);
        }

        if (name.Length == 0 || name.Contains(' '))
        {
            return null;
        }

        return (name, baseType, isInterface, isAbstract);
    }

    private static string? ParseImageName(string trimmed)
    {
        // // Image 8: Assembly-CSharp.dll - 9192
        var colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var rest = trimmed.Substring(colon + 1).Trim();
        var dash = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        return (dash > 0 ? rest.Substring(0, dash) : rest).Trim();
    }

    private static (long Rva, int Slot) ParseRva(string trimmed)
    {
        long rva = 0;
        var slot = 0;
        var rvaIndex = trimmed.IndexOf("RVA: 0x", StringComparison.Ordinal);
        if (rvaIndex >= 0)
        {
            var start = rvaIndex + "RVA: 0x".Length;
            var end = start;
            while (end < trimmed.Length && Uri.IsHexDigit(trimmed[end]))
            {
                end++;
            }

            if (end > start)
            {
                long.TryParse(trimmed.AsSpan(start, end - start), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out rva);
            }
        }

        var slotIndex = trimmed.IndexOf("Slot: ", StringComparison.Ordinal);
        if (slotIndex >= 0)
        {
            var start = slotIndex + "Slot: ".Length;
            var end = start;
            while (end < trimmed.Length && char.IsAsciiDigit(trimmed[end]))
            {
                end++;
            }

            if (end > start && int.TryParse(trimmed.AsSpan(start, end - start), out var parsed))
            {
                slot = parsed;
            }
        }

        return (rva, slot);
    }

    internal static string DumpDescription(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var sb = new StringBuilder();
            sb.Append(Path.GetFileName(path)).Append(' ').Append(info.Length).Append("B");
            return sb.ToString();
        }
        catch (IOException)
        {
            return Path.GetFileName(path);
        }
    }
}
