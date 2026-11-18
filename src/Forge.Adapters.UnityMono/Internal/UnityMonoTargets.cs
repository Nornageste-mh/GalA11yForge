using System.Globalization;
using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>候选 Source 的解析结果：程序集文件 + Cecil 类型全名 + 方法名。</summary>
internal sealed class UnityMonoParsedSource
{
    public required string AssemblyFile { get; init; }

    public required string TypeFullName { get; init; }

    public required string MethodName { get; init; }

    public bool IsPrimaryAssembly =>
        AssemblyFile.Equals("Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase) ||
        AssemblyFile.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 把候选/计划映射成生成器能解析的 HookTarget，并复刻生成器的确定性命名。
/// 生成器 <c>HookTargetParser</c> 只允许基础类型参数，所以 Target 一律用 <c>Type:Method</c>，
/// 完整签名放在 Description/Evidence，绝不把 TMPro/自定义结构塞进 Target。
/// </summary>
internal static class UnityMonoTargets
{
    private static readonly string[] HookPriority =
    {
        "AnimateText", "DOText", "Choice", "HandleMessageText", "HandleMainText",
    };

    public static bool TryParseSource(string? source, out UnityMonoParsedSource? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var text = source.Trim();
        var assemblyFile = "Assembly-CSharp.dll";

        if (text.StartsWith("assembly:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["assembly:".Length..].Trim();
        }

        var bang = text.IndexOf("!/", StringComparison.Ordinal);
        if (bang > 0)
        {
            assemblyFile = text[..bang].Trim();
            text = text[(bang + 2)..].Trim();
        }

        var open = text.IndexOf('(');
        if (open > 0)
        {
            text = text[..open].Trim();
        }

        var separator = text.LastIndexOf("::", StringComparison.Ordinal);
        string typeName;
        string methodName;
        if (separator > 0)
        {
            typeName = text[..separator].Trim();
            methodName = text[(separator + 2)..].Trim();
        }
        else
        {
            var dot = text.LastIndexOf('.');
            if (dot <= 0 || dot == text.Length - 1)
            {
                return false;
            }

            typeName = text[..dot].Trim();
            methodName = text[(dot + 1)..].Trim();
        }

        // Cecil 用 '/' 表示嵌套类型，生成器只认 '+'。
        typeName = typeName.Replace('/', '+');
        if (!IsValidTypeName(typeName) || !IsValidIdentifier(methodName))
        {
            return false;
        }

        parsed = new UnityMonoParsedSource
        {
            AssemblyFile = assemblyFile,
            TypeFullName = typeName,
            MethodName = methodName,
        };
        return true;
    }

    public static bool IsKnownHookMethod(string methodName) =>
        HookPriority.Contains(methodName, StringComparer.Ordinal);

    public static int PriorityOf(string methodName)
    {
        var index = Array.IndexOf(HookPriority, methodName);
        return index >= 0 ? index : HookPriority.Length;
    }

    /// <summary>生成器 HookTargetParser 能解析的目标；主程序集带官方前缀，附加程序集只写类型。</summary>
    public static string BuildHookTarget(UnityMonoParsedSource source)
    {
        var head = source.TypeFullName + ":" + source.MethodName;
        return source.IsPrimaryAssembly ? "Assembly-CSharp.dll!/" + head : head;
    }

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

    public static string AssemblyNameFromNamespace(string ns)
    {
        var lastDot = ns.LastIndexOf('.');
        var last = lastDot >= 0 ? ns[(lastDot + 1)..] : ns;
        return last + ".A11y";
    }

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

    public static List<GeneratedArtifact> BuildArtifacts(string slug, IReadOnlyList<PatchPoint> hooks)
    {
        var ns = DefaultNamespace(slug);
        var assemblyName = AssemblyNameFromNamespace(ns);
        var artifacts = new List<GeneratedArtifact>
        {
            new()
            {
                Name = "Plugin.cs",
                Kind = ArtifactKind.Source,
                RelativePath = "Plugin.cs",
                Description = "插件入口：配置、语音桥初始化、钩子挂载、重读键",
            },
            new()
            {
                Name = assemblyName + ".csproj",
                Kind = ArtifactKind.Project,
                RelativePath = assemblyName + ".csproj",
                Description = "BepInEx5 / HarmonyX 工程（netstandard2.1）",
            },
        };

        foreach (var hook in hooks)
        {
            var className = TypeNameFromId(hook.Id);
            artifacts.Add(new GeneratedArtifact
            {
                Name = "Hooks/Hook_" + className + ".cs",
                Kind = ArtifactKind.Source,
                RelativePath = "Hooks/Hook_" + className + ".cs",
                Description = "Hook 骨架：" + hook.Target,
            });
        }

        artifacts.AddRange(
        [
            Artifact("Platform/Speech.cs", ArtifactKind.Source, "内嵌语音桥：Speech"),
            Artifact("Platform/A11yHost.cs", ArtifactKind.Source, "内嵌语音桥：A11yHost 契约层"),
            Artifact("profile.json", ArtifactKind.Config, "生成结果清单：钩子、跳过的补丁点、待人工计数"),
            Artifact("build.ps1", ArtifactKind.Script, "只编译、不写游戏目录的构建脚本"),
            Artifact("README.md", ArtifactKind.Documentation, "工程说明与能力边界"),
            Artifact("安装说明.md", ArtifactKind.Documentation, "手动安装步骤"),
            Artifact("卸载说明.md", ArtifactKind.Documentation, "卸载步骤（不动游戏本体）"),
            Artifact("NOTICE.md", ArtifactKind.Documentation, "第三方来源与许可"),
            Artifact("forge.manifest.json", ArtifactKind.Other, "生成账本（幂等/覆盖判定）"),
        ]);

        return artifacts;
    }

    private static GeneratedArtifact Artifact(string path, ArtifactKind kind, string description) => new()
    {
        Name = path,
        Kind = kind,
        RelativePath = path,
        Description = description,
    };

    private static bool IsValidTypeName(string value)
    {
        var segments = value.Split(['.', '+'], StringSplitOptions.None);
        return segments.Length > 0 && segments.All(IsValidIdentifier);
    }

    private static bool IsValidIdentifier(string value)
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

    public static string InspectParameter(UnityMonoCandidateView view)
    {
        if (view.MethodName is "AnimateText")
        {
            return "text（System.String，本局台词/简中文本）";
        }

        if (view.MethodName is "DOText")
        {
            return "charaStatus（UIText/CharaStatus，按值嵌套结构；用于取台词/原稿层）";
        }

        if (view.MethodName is "Choice")
        {
            return "cs（ChoiceAsset）+ hideMask（System.Int32）；选项文本与回显需运行期确认";
        }

        if (view.MethodName is "HandleMessageText" or "HandleMainText")
        {
            return "charaStatus（按值嵌套结构）+ valueTime（System.Single）";
        }

        return view.StringParameter is not null
            ? $"{view.StringParameter}（System.String）"
            : "方法参数（无 string 参数时只能做调用计数，不能直接朗读）";
    }
}

/// <summary>plan 阶段从候选文本里能安全复用的最小信息（不解析台词）。</summary>
internal sealed class UnityMonoCandidateView
{
    public required string CandidateId { get; init; }

    public required string MethodName { get; init; }

    public required string TypeFullName { get; init; }

    public required string Signature { get; init; }

    public string? StringParameter { get; init; }

    public double Score { get; init; }

    public string Confidence { get; init; } = "low";

    public List<string> Evidence { get; init; } = new();
}
