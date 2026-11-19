namespace GalA11yForge.Generators.Templates;

/// <summary>
/// 0.2.83：运行期重载解析器。为什么需要它：Harmony 的 <c>AccessTools.Method(目标, null)</c>
/// 在目标类型有同名重载时会抛 AmbiguousMatchException，整个钩子都挂不上（真机日志：
/// <c>Hook_hook_002 … Ambiguous match found</c>）。分析阶段其实已经拿到了完整 Cecil 签名里的
/// 形参类型，这里按名字挑唯一重载；挑不出来就抛带候选清单的异常，让日志能说清为什么没挂上。
/// 只用反射，不引用 Assembly-CSharp，也不包含任何台词或游戏资源。
/// </summary>
internal static class MethodResolverTemplate
{
    internal const string Text = """
// 本文件由 Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo）生成，生成器版本 {{GENERATOR_VERSION}}。
// 作用：按分析证据里的形参类型名挑唯一重载，避免 Harmony 对同名重载抛 AmbiguousMatchException
// 导致整个钩子挂不上。只用反射，不引用 Assembly-CSharp，不包含任何台词或游戏资源。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace {{NS}}
{
    internal static class MethodResolver
    {
        private const BindingFlags AllMethods =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly HashSet<string> TypeMisses = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 解析目标（「命名空间.类型:方法」）。parameterTypeNames 来自分析证据里完整签名的形参类型全名；
        /// 为空时退化为「同名且唯一才返回」，同名多载仍抛异常（与 Harmony 默认行为一致，但消息更清楚）。
        /// </summary>
        internal static MethodBase Resolve(string target, string[] parameterTypeNames)
        {
            if (string.IsNullOrEmpty(target))
            {
                return null;
            }

            var separator = target.IndexOf(':');
            if (separator <= 0 || separator >= target.Length - 1)
            {
                return AccessTools.Method(target, null);
            }

            var typeName = target.Substring(0, separator);
            var methodName = target.Substring(separator + 1);
            var type = FindType(typeName);
            if (type == null)
            {
                return null;
            }

            MethodInfo[] candidates;
            try
            {
                candidates = type.GetMethods(AllMethods);
            }
            catch (Exception ex)
            {
                A11yHost.Diag("[Hook:resolve] 枚举 " + typeName + " 的方法失败（" + ex.GetType().Name + "），退回 Harmony 默认解析。");
                return AccessTools.Method(target, null);
            }

            var named = new List<MethodInfo>();
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate != null && string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
                {
                    named.Add(candidate);
                }
            }

            if (named.Count == 0)
            {
                return null;
            }

            if (named.Count == 1)
            {
                return named[0];
            }

            if (parameterTypeNames != null && parameterTypeNames.Length > 0)
            {
                var exact = MatchByName(named, parameterTypeNames, true);
                if (exact.Count == 1)
                {
                    A11yHost.Diag("[Hook:resolve] " + target + " 有 " + named.Count + " 个同名重载，按证据里的参数类型选中：" + Signature(exact[0]) + "。");
                    return exact[0];
                }

                if (exact.Count == 0)
                {
                    var loose = MatchByName(named, parameterTypeNames, false);
                    if (loose.Count == 1)
                    {
                        A11yHost.Diag("[Hook:resolve] " + target + " 按完整类型名没命中，按简名选中：" + Signature(loose[0]) + "。");
                        return loose[0];
                    }
                }
            }

            throw new AmbiguousMatchException(DescribeAmbiguity(target, parameterTypeNames, named));
        }

        private static List<MethodInfo> MatchByName(List<MethodInfo> named, string[] parameterTypeNames, bool exact)
        {
            var matched = new List<MethodInfo>();
            for (var i = 0; i < named.Count; i++)
            {
                var parameters = named[i].GetParameters();
                if (parameters.Length != parameterTypeNames.Length)
                {
                    continue;
                }

                var ok = true;
                for (var p = 0; p < parameters.Length; p++)
                {
                    if (!TypeNameMatches(parameters[p].ParameterType, parameterTypeNames[p], exact))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    matched.Add(named[i]);
                }
            }

            return matched;
        }

        private static bool TypeNameMatches(Type type, string expected, bool exact)
        {
            var actual = Normalize(RenderTypeName(type));
            var wanted = Normalize(expected);
            if (actual.Length == 0 || wanted.Length == 0)
            {
                return false;
            }

            if (string.Equals(actual, wanted, StringComparison.Ordinal))
            {
                return true;
            }

            return !exact && string.Equals(SimpleName(actual), SimpleName(wanted), StringComparison.Ordinal);
        }

        /// <summary>把反射类型渲染成 Cecil 风格的全名（嵌套用 /、泛型用 &lt;&gt;、数组 []、byref &amp;）。</summary>
        private static string RenderTypeName(Type type)
        {
            if (type == null)
            {
                return string.Empty;
            }

            var byRef = type.IsByRef;
            var arrayRank = 0;
            if (byRef)
            {
                type = type.GetElementType();
                if (type == null)
                {
                    return string.Empty;
                }
            }

            if (type.IsArray)
            {
                arrayRank = type.GetArrayRank();
                type = type.GetElementType();
                if (type == null)
                {
                    return string.Empty;
                }
            }

            string name;
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var definition = type.GetGenericTypeDefinition();
                name = definition.FullName ?? definition.Name;
                var arguments = type.GetGenericArguments();
                var builder = new StringBuilder(name).Append('<');
                for (var i = 0; i < arguments.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(RenderTypeName(arguments[i]));
                }

                name = builder.Append('>').ToString();
            }
            else
            {
                name = type.FullName ?? type.Name;
            }

            if (arrayRank == 1)
            {
                name += "[]";
            }
            else if (arrayRank > 1)
            {
                name += "[" + new string(',', arrayRank - 1) + "]";
            }

            if (byRef)
            {
                name += "&";
            }

            return name;
        }

        /// <summary>去掉空白，并把嵌套类型分隔符统一成 +（Cecil 用 /，反射用 +）。</summary>
        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(name.Length);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                builder.Append(ch == '/' ? '+' : ch);
            }

            return builder.ToString();
        }

        private static string SimpleName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var angle = name.IndexOf('<');
            var head = angle >= 0 ? name.Substring(0, angle) : name;
            var bracket = head.IndexOf('[');
            if (bracket >= 0)
            {
                head = head.Substring(0, bracket);
            }

            var tick = head.IndexOf('`');
            if (tick >= 0)
            {
                head = head.Substring(0, tick);
            }

            var dot = head.LastIndexOfAny(new[] { '.', '+', '/' });
            return dot >= 0 ? head.Substring(dot + 1) : head;
        }

        private static string DescribeAmbiguity(string target, string[] parameterTypeNames, List<MethodInfo> methods)
        {
            var builder = new StringBuilder();
            builder.Append("目标 ").Append(target).Append(" 有 ").Append(methods.Count).Append(" 个同名重载，按证据里的参数类型");
            if (parameterTypeNames == null || parameterTypeNames.Length == 0)
            {
                builder.Append("（证据里没有形参类型）");
            }
            else
            {
                builder.Append("（");
                for (var i = 0; i < parameterTypeNames.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(parameterTypeNames[i]);
                }

                builder.Append("）");
            }

            builder.Append("没能唯一确定，钩子已跳过。候选：");
            var shown = 0;
            for (var i = 0; i < methods.Count && shown < 6; i++)
            {
                if (shown > 0)
                {
                    builder.Append("；");
                }

                builder.Append(Signature(methods[i]));
                shown++;
            }

            if (methods.Count > shown)
            {
                builder.Append(" …");
            }

            return builder.ToString();
        }

        private static string Signature(MethodBase method)
        {
            var builder = new StringBuilder(method.Name).Append('(');
            var parameters = method.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(Normalize(RenderTypeName(parameters[i].ParameterType)));
            }

            return builder.Append(')').ToString();
        }

        private static Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            Type cached;
            if (TypeCache.TryGetValue(typeName, out cached))
            {
                return cached;
            }

            if (TypeMisses.Contains(typeName))
            {
                return null;
            }

            var type = Lookup(typeName);
            if (type == null && typeName.IndexOf('/') >= 0)
            {
                type = Lookup(typeName.Replace('/', '+'));
            }

            if (type == null)
            {
                type = ScanAssemblies(typeName);
            }

            if (type != null)
            {
                TypeCache[typeName] = type;
            }
            else
            {
                TypeMisses.Add(typeName);
            }

            return type;
        }

        private static Type Lookup(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type != null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // AccessTools 在个别畸形类型名上会抛异常；继续用 Type.GetType 兜底。
            }

            try
            {
                return Type.GetType(typeName, false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Type ScanAssemblies(string typeName)
        {
            var wanted = typeName.Replace('/', '+');
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (Exception)
                {
                    // 有类型加载失败的插件程序集：跳过，不影响其它程序集搜索。
                    continue;
                }

                for (var t = 0; t < types.Length; t++)
                {
                    var candidate = types[t];
                    if (candidate != null && string.Equals(candidate.FullName, wanted, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }
    }
}
""";
}
