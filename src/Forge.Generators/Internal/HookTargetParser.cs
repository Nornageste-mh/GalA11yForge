using System.Text;

namespace GalA11yForge.Generators.Internal;

/// <summary>一个已经过校验的 Harmony 目标：类型名 + 方法名 + 可选参数类型。</summary>
internal sealed class HookTarget
{
    public HookTarget(string typeName, string methodName, IReadOnlyList<string> parameterTypes)
    {
        TypeName = typeName;
        MethodName = methodName;
        ParameterTypes = parameterTypes;
    }

    public string TypeName { get; }

    public string MethodName { get; }

    public IReadOnlyList<string> ParameterTypes { get; }

    /// <summary>AccessTools.Method 接受的形式：<c>Type:Method</c>。</summary>
    public string AccessToolsTarget => TypeName + ":" + MethodName;

    /// <summary>写进注释/文档的展示形式。</summary>
    public string Display => ParameterTypes.Count == 0
        ? TypeName + "::" + MethodName
        : TypeName + "::" + MethodName + "(" + string.Join(", ", ParameterTypes) + ")";

    /// <summary>第二个实参：没有参数时传 null，有参数时传 typeof 数组。</summary>
    public string ParameterArrayExpression => ParameterTypes.Count == 0
        ? "null"
        : "new[] { " + string.Join(", ", ParameterTypes.Select(t => "typeof(" + t + ")")) + " }";
}

/// <summary>
/// 把 plan.json 里的 <see cref="GalA11yForge.Core.Models.PatchPoint.Target"/> 解析成可编译的钩子目标。
///
/// <para>
/// 之所以这么严：生成器不允许编造游戏类名。目标只能原样来自分析证据，任何解析不了的东西
/// 都要以 E_ARG_INVALID 退回给上游，而不是猜一个名字写进代码。
/// </para>
/// </summary>
internal static class HookTargetParser
{
    private const int MaxTargetLength = 300;

    private static readonly string[] AssemblyPrefixes =
    [
        "Assembly-CSharp.dll!/",
        "Assembly-CSharp!/",
    ];

    private static readonly HashSet<string> AllowedParameterTypes = new(StringComparer.Ordinal)
    {
        // C# 关键字形式
        "string", "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
        "float", "double", "bool", "char", "object", "decimal",
        // 全限定形式（计划里可能直接抄反编译输出）
        "System.String", "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
        "System.Int16", "System.UInt16", "System.Byte", "System.SByte", "System.Single",
        "System.Double", "System.Boolean", "System.Char", "System.Object", "System.Decimal",
    };

    public static bool TryParse(string? raw, out HookTarget? target, out string error)
    {
        target = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "补丁点目标为空：Unity Mono 钩子必须给出「类型:方法」形式的目标（来自反编译证据）。";
            return false;
        }

        var text = raw.Trim();
        if (text.Length > MaxTargetLength)
        {
            error = $"补丁点目标过长（{text.Length} 字符）：这里只接受类型与方法名，不要把台词、路径或整段说明放进来。";
            return false;
        }

        text = StripAssemblyPrefix(text);

        var parameterTypes = new List<string>();
        var open = text.IndexOf('(');
        var head = text;
        if (open < 0)
        {
            if (text.IndexOf(')') >= 0)
            {
                error = "补丁点目标的参数列表缺少左括号。";
                return false;
            }
        }
        else
        {
            if (!text.EndsWith(')'))
            {
                error = "补丁点目标的参数列表没有闭合的右括号。";
                return false;
            }

            head = text[..open].Trim();
            var inner = text[(open + 1)..^1];
            if (inner.IndexOf('(') >= 0 || inner.IndexOf(')') >= 0)
            {
                error = "补丁点目标的参数列表不支持嵌套括号。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(inner))
            {
                foreach (var token in inner.Split(','))
                {
                    var parameter = token.Trim();
                    if (parameter.Length == 0)
                    {
                        error = "补丁点目标的参数列表里有空项（多了逗号？）。";
                        return false;
                    }

                    if (!AllowedParameterTypes.Contains(parameter))
                    {
                        error = $"补丁点目标的参数类型「{Preview(parameter)}」不在允许清单里：目前只接受基础类型，或 System.* 全名。";
                        return false;
                    }

                    parameterTypes.Add(parameter);
                }
            }
        }

        if (!TrySplit(head, out var typeName, out var methodName, out error))
        {
            return false;
        }

        if (!IsTypeName(typeName))
        {
            error = $"补丁点目标的类型名不合法：「{Preview(typeName)}」；只接受字母/数字/下划线构成的类型名（可含 . + 与 `n 泛型参数）。";
            return false;
        }

        if (!IsIdentifier(methodName))
        {
            error = $"补丁点目标的方法名不合法：「{Preview(methodName)}」；只接受字母/数字/下划线构成的方法名。";
            return false;
        }

        target = new HookTarget(typeName, methodName, parameterTypes);
        return true;
    }

    private static string StripAssemblyPrefix(string text)
    {
        foreach (var prefix in AssemblyPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text[prefix.Length..].Trim();
            }
        }

        return text;
    }

    private static bool TrySplit(string head, out string typeName, out string methodName, out string error)
    {
        typeName = string.Empty;
        methodName = string.Empty;
        error = string.Empty;

        var doubleColon = head.IndexOf("::", StringComparison.Ordinal);
        if (doubleColon >= 0)
        {
            if (head.IndexOf(':') != doubleColon || head.IndexOf(':', doubleColon + 2) >= 0)
            {
                error = "补丁点目标里的冒号过多：只接受「类型:方法」或「类型::方法」。";
                return false;
            }

            typeName = head[..doubleColon].Trim();
            methodName = head[(doubleColon + 2)..].Trim();
        }
        else
        {
            var colon = head.IndexOf(':');
            if (colon >= 0)
            {
                if (head.IndexOf(':', colon + 1) >= 0)
                {
                    error = "补丁点目标里的冒号过多：只接受「类型:方法」或「类型::方法」。";
                    return false;
                }

                typeName = head[..colon].Trim();
                methodName = head[(colon + 1)..].Trim();
            }
            else
            {
                var dot = head.LastIndexOf('.');
                if (dot <= 0 || dot == head.Length - 1)
                {
                    error = "补丁点目标缺少分隔：请写成「类型:方法」「类型::方法」或「类型.方法」。";
                    return false;
                }

                typeName = head[..dot].Trim();
                methodName = head[(dot + 1)..].Trim();
            }
        }

        if (typeName.Length == 0 || methodName.Length == 0)
        {
            error = "补丁点目标缺少类型或方法名。";
            return false;
        }

        return true;
    }

    /// <summary>类型名允许命名空间与嵌套类型：<c>Ns.Outer+Inner</c>。</summary>
    private static bool IsTypeName(string value)
    {
        var segments = value.Split(['.', '+'], StringSplitOptions.None);
        return segments.Length > 0 && segments.All(IsIdentifier);
    }

    /// <summary>标识符，可带泛型参数后缀 <c>`2</c>。</summary>
    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var arityIndex = value.IndexOf('`');
        var name = arityIndex >= 0 ? value[..arityIndex] : value;
        if (arityIndex >= 0)
        {
            var digits = value[(arityIndex + 1)..];
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(name[i]) && name[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>错误消息里只回显截断后的预览，避免把整段文本写进报告。</summary>
    public static string Preview(string? raw, int max = 60)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "<空>";
        }

        var builder = new StringBuilder();
        foreach (var ch in raw)
        {
            if (builder.Length >= max)
            {
                builder.Append('…');
                break;
            }

            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return builder.ToString();
    }
}
