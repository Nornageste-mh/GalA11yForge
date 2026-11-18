using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 一个可打补丁的 IL2CPP 目标（声明类型 + 方法 + 参数类型表）。
///
/// <para>
/// 序列化格式 <c>il2cpp:{AssemblyFile}!{TypeFullName}::{MethodName}({T1;T2})</c>。
/// 参数之间用 <b>分号</b> 而不是逗号：Cecil 的泛型全名里带逗号
/// （<c>Dictionary`2&lt;System.String, System.Int32&gt;</c>），用逗号会切坏。
/// </para>
/// <para>
/// 为什么必须带声明类型：R18 实测 IL2CPP + HarmonyX 下
/// <c>HarmonyPatch(typeof(子类),"基类方法")</c> 会走 <c>AccessTools.DeclaredMethod</c> 并抛
/// <c>Patching exception in method null</c>；只有打在**声明该方法的类型**上才成功。
/// </para>
/// </summary>
internal sealed class Il2CppTarget
{
    public string AssemblyFile { get; init; } = string.Empty;

    public string TypeFullName { get; init; } = string.Empty;

    public string MethodName { get; init; } = string.Empty;

    public List<string> ParameterTypes { get; init; } = [];

    /// <summary>与 <see cref="ParameterTypes"/> 一一对应的形参名（可能为空串）。</summary>
    public List<string> ParameterNames { get; init; } = [];

    /// <summary>C# 里可写的类型名（Cecil 嵌套类型用 <c>/</c>，C# 要用 <c>.</c>）。</summary>
    public string CSharpTypeName => TypeFullName.Replace('/', '.');

    public bool IsEventAdder => MethodName.StartsWith("add_", StringComparison.Ordinal);

    public bool IsPropertySetter => MethodName.StartsWith("set_", StringComparison.Ordinal);

    /// <summary>
    /// 能否生成可编译的补丁：类型不是泛型（泛型开放类型不能 typeof），
    /// 且至少有一个带名字的 string 形参（补丁方法只能声明简单类型、且必须按名字注入）。
    /// </summary>
    public bool CanGenerateHook
    {
        get
        {
            if (TypeFullName.Contains('`') || TypeFullName.Contains('<') || TypeFullName.Contains('/'))
            {
                return false;
            }

            for (var i = 0; i < ParameterTypes.Count; i++)
            {
                if (ParameterTypes[i] == "System.String" &&
                    i < ParameterNames.Count &&
                    !string.IsNullOrWhiteSpace(ParameterNames[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 目标签名里是否带 string 形参（不论形参有没有名字）。
    /// 字段兜底只收“没有 string 形参”的目标：像 DOTween 的 set_xxx(string) 这类属性写入
    /// 虽然形参名缺失导致 CanGenerateHook 为 false，但它本来就在写字符串，不该走字段兜底。
    /// </summary>
    public bool HasStringParameter =>
        ParameterTypes.Any(static t => string.Equals(t, "System.String", StringComparison.Ordinal));

    /// <summary>打补丁时展示给用户的可读目标名。</summary>
    public string Display => CSharpTypeName + "::" + MethodName + "(" + string.Join(";", ParameterTypes) + ")";

    public string Source => Il2CppTargets.SourcePrefix + Serialize();

    public string Target => Serialize();

    /// <summary>形如 <c>{assembly}!{type}::{method}({Type|name;Type|name})</c>。</summary>
    private string Serialize()
    {
        var parameters = new List<string>(ParameterTypes.Count);
        for (var i = 0; i < ParameterTypes.Count; i++)
        {
            var name = i < ParameterNames.Count ? ParameterNames[i] : string.Empty;
            parameters.Add(ParameterTypes[i] + "|" + name);
        }

        return AssemblyFile + "!" + TypeFullName + "::" + MethodName + "(" + string.Join(";", parameters) + ")";
    }
}

/// <summary>目标的序列化/反序列化与工程产物清单。解析失败一律返回 null，不抛异常。</summary>
internal static class Il2CppTargets
{
    internal const string SourcePrefix = "il2cpp:";
    internal const int MaxTargetLength = 400;

    /// <summary>Harmony 补丁方法能安全声明的参数类型（Cecil 简单名）。</summary>
    private static readonly HashSet<string> SimpleTypeNames = new(StringComparer.Ordinal)
    {
        "System.String", "System.Boolean", "System.Char", "System.Object",
        "System.Int32", "System.Int64", "System.UInt32", "System.UInt64",
        "System.Int16", "System.UInt16", "System.Byte", "System.SByte",
        "System.Single", "System.Double", "System.Decimal",
    };

    public static bool IsSimpleType(string typeName) => SimpleTypeNames.Contains(typeName);

    /// <summary>
    /// 能自动生成 Harmony 补丁：至少有一个 string 形参。
    /// 复杂形参（AsyncToken / ChoiceState / 泛型委托）不注入到补丁方法里即可，
    /// 补丁方法按参数名只声明简单类型的那几个（Harmony 支持声明原方法的参数子集）。
    /// </summary>
    public static bool IsHookable(Il2CppMethodFact method) => method.Parameters.Any(p => p.IsString);

    /// <summary>补丁方法可以安全声明的形参（简单类型）。</summary>
    public static IEnumerable<Il2CppParameterFact> SimpleParameters(Il2CppMethodFact method)
        => method.Parameters.Where(p => IsSimpleType(p.TypeName));

    public static string BuildSource(Il2CppMethodFact method, string assemblyFile)
    {
        var parameters = method.Parameters.Select(p => p.TypeName + "|" + p.Name);
        return SourcePrefix + assemblyFile + "!" + method.TypeFullName + "::" + method.Name + "(" +
               string.Join(";", parameters) + ")";
    }

    /// <summary>从候选 <c>Source</c> 或补丁点 <c>Target</c> 解析；两种都接受（一个带前缀）。</summary>
    public static Il2CppTarget? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTargetLength)
        {
            return null;
        }

        var value = text.Trim();
        if (value.StartsWith(SourcePrefix, StringComparison.Ordinal))
        {
            value = value[SourcePrefix.Length..];
        }

        var bang = value.IndexOf('!');
        if (bang <= 0 || bang >= value.Length - 1)
        {
            return null;
        }

        var assembly = value[..bang].Trim();
        var rest = value[(bang + 1)..];

        var openParen = rest.IndexOf('(');
        var closeParen = rest.LastIndexOf(')');
        if (openParen <= 0 || closeParen != rest.Length - 1 || closeParen < openParen)
        {
            return null;
        }

        var typeAndMethod = rest[..openParen];
        var paramText = rest[(openParen + 1)..closeParen];

        var split = typeAndMethod.LastIndexOf("::", StringComparison.Ordinal);
        if (split <= 0 || split >= typeAndMethod.Length - 2)
        {
            return null;
        }

        var typeName = typeAndMethod[..split].Trim();
        var methodName = typeAndMethod[(split + 2)..].Trim();
        if (typeName.Length == 0 || methodName.Length == 0)
        {
            return null;
        }

        var rawParameters = paramText.Length == 0
            ? []
            : paramText.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        var parameterTypes = new List<string>(rawParameters.Count);
        var parameterNames = new List<string>(rawParameters.Count);
        foreach (var raw in rawParameters)
        {
            var bar = raw.IndexOf('|');
            if (bar < 0)
            {
                parameterTypes.Add(raw);
                parameterNames.Add(string.Empty);
                continue;
            }

            parameterTypes.Add(raw[..bar].Trim());
            parameterNames.Add(raw[(bar + 1)..].Trim());
        }

        return new Il2CppTarget
        {
            AssemblyFile = assembly,
            TypeFullName = typeName,
            MethodName = methodName,
            ParameterTypes = parameterTypes,
            ParameterNames = parameterNames,
        };
    }

    /// <summary>
    /// 生成工程产物清单。<c>RelativePath</c> 全部是相对输出目录的 <c>/</c> 分隔路径，
    /// 由生成器负责真正写盘并把关 <see cref="GalA11yForge.Core.Workspaces.PathSafety"/>。
    /// </summary>
    public static List<GeneratedArtifact> BuildArtifacts(string slug, IReadOnlyList<PatchPoint> points)
    {
        var hooks = points.Count(p => p.Kind == PatchPointKind.Hook);
        var artifacts = new List<GeneratedArtifact>
        {
            new()
            {
                Name = slug + ".il2cpp.a11y.csproj",
                Kind = ArtifactKind.Project,
                RelativePath = slug + ".il2cpp.a11y.csproj",
                Description = "BepInEx 6 (be.788) IL2CPP 插件工程：net6.0，引用 BepInEx.Unity.IL2CPP / Il2CppInterop.Runtime / 游戏 interop 程序集。",
            },
            new()
            {
                Name = "Plugin.cs",
                Kind = ArtifactKind.Source,
                RelativePath = "Plugin.cs",
                Description = "BepInEx 6 入口：HarmonyX PatchAll + A11yRuntime 初始化。",
            },
            new()
            {
                Name = "A11yRuntime.cs",
                Kind = ArtifactKind.Source,
                RelativePath = Path.Combine("A11y", "A11yRuntime.cs"),
                Description = "朗读与清洗核心：空串过滤、跨通道去重（同一行会从 6 个通道重复触发）、Tolk 输出与日志兜底。",
            },
            new()
            {
                Name = "Tolk.cs",
                Kind = ArtifactKind.Source,
                RelativePath = Path.Combine("A11y", "Tolk.cs"),
                Description = "Tolk 屏幕阅读器桥（P/Invoke，运行期加载 Tolk.dll；缺 DLL 时降级为日志）。",
            },
        };

        for (var i = 0; i < hooks; i++)
        {
            var name = "Hook_" + (i + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".cs";
            artifacts.Add(new GeneratedArtifact
            {
                Name = name,
                Kind = ArtifactKind.Source,
                RelativePath = Path.Combine("Hooks", name),
                Description = "Harmony 补丁：声明类型解析 + 只声明简单类型参数的 Postfix。",
            });
        }

        var hasFieldFallback = points.Any(p =>
            p.Kind == PatchPointKind.Hook &&
            !string.IsNullOrEmpty(p.Description) &&
            p.Description.Contains(Il2CppFieldFallback.Marker, StringComparison.Ordinal));
        if (hasFieldFallback)
        {
            artifacts.Add(new GeneratedArtifact
            {
                Name = "A11yFieldReader.cs",
                Kind = ArtifactKind.Source,
                RelativePath = Path.Combine("A11y", "A11yFieldReader.cs"),
                Description = "IL2CPP 原生字段读取器：按字段名沿继承链查找，读 string 字段或 TMP/UGUI 组件的 text 属性（不写死 offset）。",
            });
        }

        artifacts.Add(new GeneratedArtifact
        {
            Name = "README.md",
            Kind = ArtifactKind.Documentation,
            RelativePath = "README.md",
            Description = "安装/构建/验证步骤与已知限制（含“未验证”声明）。",
        });
        artifacts.Add(new GeneratedArtifact
        {
            Name = "build.ps1",
            Kind = ArtifactKind.Script,
            RelativePath = "build.ps1",
            Description = "一条命令构建（-p:GameDir=… 指向已装 BepInEx 6 的游戏目录）。",
        });

        return artifacts;
    }

    public static string NormalizeSlug(string? slug, string gameDirectory)
    {
        if (!string.IsNullOrWhiteSpace(slug) && GalA11yForge.Core.Workspaces.PathSafety.IsValidSlug(slug))
        {
            return slug;
        }

        var derived = GalA11yForge.Core.Workspaces.PathSafety.SanitizeSlug(
            GalA11yForge.Core.Workspaces.PathSafety.SlugFromGameDirectory(gameDirectory));
        return string.IsNullOrWhiteSpace(derived) ? "il2cpp-game" : derived;
    }

    internal static string Describe(IReadOnlyList<Il2CppTarget> targets)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < targets.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("; ");
            }

            sb.Append(targets[i].Display);
        }

        return sb.ToString();
    }
}
