using System.Globalization;
using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 生成 BepInEx 6（IL2CPP）插件工程。
///
/// <para>
/// 生成的代码**只在编译期引用 BepInEx 6 / Il2CppInterop / 游戏 interop 程序集**，
/// 不含任何游戏原始资源、台词文本或图片（AGENTS.md 版权红线）。
/// 补丁方法只声明简单类型形参，因此即使游戏里的参数是 <c>AsyncToken</c>、<c>ChoiceState</c>
/// 这类结构体，也不需要把它们的类型写进生成代码。
/// </para>
/// </summary>
internal sealed class Il2CppGenerator
{
    internal const int MaxHooks = Il2CppPlanner.MaxHookPoints;

    /// <summary>形参名 → 台词的常见命名，按优先级排列（R18：PrintTextAsync 的三个 string 形参里只有 text 是台词）。</summary>
    private static readonly string[] TextParameterPreference =
    [
        "text", "value", "line", "message", "content", "body", "caption", "sentence", "utterance", "dialogue", "label",
    ];

    public GenerationResult Generate(
        GameContext context,
        PatchPlan plan,
        string projectDirectory,
        Il2CppLayout layout,
        Il2CppOptions options,
        IReadOnlyList<string>? unverified = null)
    {
        Directory.CreateDirectory(projectDirectory);

        var slug = Il2CppTargets.NormalizeSlug(plan.GameSlug ?? context.Slug, context.GameDirectory);
        var assemblyName = slug + ".il2cpp.a11y";
        var rootNamespace = "GalA11yForge.Generated." + ToPascal(slug);
        var guid = "gala11y.il2cpp." + slug;

        var hooks = new List<(Il2CppTarget Target, List<Il2CppParameterFact> Parameters, string TextParameterName, string Kind,
            List<Il2CppTextField>? Fields)>();
        var fieldHooks = 0;
        var assemblyFiles = new List<string>();
        var needsUnityUi = false;
        var needsTextMeshPro = false;
        var skipped = 0;

        // 没有 interop 就没有游戏程序集可引用：任何 `typeof(global::游戏类型)` 都编译不过。
        // 这种情况下只生成骨架工程（运行时 + 说明），把补丁点全部记成 skipped，绝不假装成功。
        var canEmitHooks = layout.BestSource == Il2CppSourceKind.Interop;

        foreach (var point in plan.PatchPoints.Where(p => p.Kind == PatchPointKind.Hook))
        {
            if (hooks.Count >= MaxHooks)
            {
                break;
            }

            if (!canEmitHooks)
            {
                skipped++;
                continue;
            }

            var target = Il2CppTargets.TryParse(point.Target);
            if (target is null)
            {
                skipped++;
                continue;
            }

            if (!target.CanGenerateHook)
            {
                // 0.2.12：没有 string 形参 ≠ 无解。计划器把实例文本字段写在描述里，
                // 这里生成原生字段读取补丁（运行时读，游戏版本漂移时字段名查不到只会静默跳过）。
                var fields = Il2CppFieldFallback.ParseDescription(point.Description);
                if (!CanGenerateFieldHook(target, fields))
                {
                    skipped++;
                    continue;
                }

                foreach (var field in fields)
                {
                    if (field.Kind != Il2CppFieldKind.Component || field.CSharpType is null)
                    {
                        continue;
                    }

                    if (field.CSharpType.StartsWith("UnityEngine.UI.", StringComparison.Ordinal))
                    {
                        needsUnityUi = true;
                    }

                    if (field.CSharpType.StartsWith("TMPro.", StringComparison.Ordinal))
                    {
                        needsTextMeshPro = true;
                    }
                }

                hooks.Add((target, [], string.Empty, IsSpeakerChannel(target) ? "speaker" : "dialogue", fields));
                fieldHooks++;
                // 字段补丁的 Postfix 是强类型 __instance：实例类型常常继承 UIBehaviour（Naninovel ChatMessage 等），
                // 即使字段本身不是 UGUI 组件，也需要 UnityEngine.UI 引用才能编译。引用带 Condition，缺了不报错。
                needsUnityUi = true;
                if (!assemblyFiles.Contains(target.AssemblyFile, StringComparer.OrdinalIgnoreCase))
                {
                    assemblyFiles.Add(target.AssemblyFile);
                }

                continue;
            }

            if (target.CSharpTypeName.StartsWith("UnityEngine.UI.", StringComparison.Ordinal))
            {
                needsUnityUi = true;
            }

            if (target.CSharpTypeName.StartsWith("TMPro.", StringComparison.Ordinal))
            {
                needsTextMeshPro = true;
            }

            var parameters = new List<Il2CppParameterFact>();
            for (var i = 0; i < target.ParameterTypes.Count; i++)
            {
                if (!Il2CppTargets.IsSimpleType(target.ParameterTypes[i]))
                {
                    continue;
                }

                parameters.Add(new Il2CppParameterFact
                {
                    Name = i < target.ParameterNames.Count ? target.ParameterNames[i] : string.Empty,
                    TypeName = target.ParameterTypes[i],
                    IsString = target.ParameterTypes[i] == "System.String",
                    IsNumeric = target.ParameterTypes[i] is "System.Single" or "System.Int32" or "System.Double" or "System.Int64",
                });
            }

            var stringParameters = parameters
                .Where(p => p.IsString && !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            // 方法可能有多个 string 形参（R18：PrintTextAsync(printerId, text, authorId, speed)），
            // 必须选“内容是台词”的那个，否则会去朗读 printerId。
            var textParameter = stringParameters.FirstOrDefault(
                                    p => TextParameterPreference.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)))
                                ?? stringParameters.LastOrDefault();

            if (textParameter is null || string.IsNullOrWhiteSpace(textParameter.Name))
            {
                skipped++;
                continue;
            }

            var kind = IsSpeakerChannel(target) ? "speaker" : "dialogue";
            hooks.Add((target, parameters, textParameter.Name, kind, null));
            if (!assemblyFiles.Contains(target.AssemblyFile, StringComparer.OrdinalIgnoreCase))
            {
                assemblyFiles.Add(target.AssemblyFile);
            }
        }

        var artifacts = new List<GeneratedArtifact>();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine"] = "unity_il2cpp",
            ["source_kind"] = layout.BestSource.ToString(),
            ["hook_count"] = hooks.Count.ToString(CultureInfo.InvariantCulture),
            ["field_fallback_hooks"] = fieldHooks.ToString(CultureInfo.InvariantCulture),
            ["skipped_points"] = skipped.ToString(CultureInfo.InvariantCulture),
            ["interop_assembly_hash"] = layout.InteropAssemblyHash ?? string.Empty,
            ["project_name"] = assemblyName,
            ["guid"] = guid,
        };

        void Write(string relative, ArtifactKind kind, string description, string content)
        {
            var full = Path.Combine(projectDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(full, content, new UTF8Encoding(false));
            artifacts.Add(new GeneratedArtifact
            {
                Name = Path.GetFileName(relative),
                Kind = kind,
                RelativePath = relative.Replace('\\', '/'),
                Description = description,
            });
        }

        Write(assemblyName + ".csproj", ArtifactKind.Project,
            "BepInEx 6 IL2CPP 插件工程（net6.0，引用 $(GameDir)\\BepInEx 下的 core 与 interop）。",
            Il2CppTemplates.ProjectFile(assemblyName, rootNamespace, context.GameDirectory, assemblyFiles,
                needsUnityUi, needsTextMeshPro, layout.HasInterop));

        Write("Plugin.cs", ArtifactKind.Source, "BepInEx 6 入口：初始化运行时 + HarmonyX PatchAll。",
            Il2CppTemplates.Plugin(guid, slug + " 无障碍补丁", rootNamespace));

        Write("A11y/A11yRuntime.cs", ArtifactKind.Source,
            "朗读运行时：空串过滤、250ms 跨通道去重、富文本清洗、日志兜底。",
            Il2CppTemplates.Runtime(rootNamespace, options.EnableSpeech));

        Write("A11y/Tolk.cs", ArtifactKind.Source, "Tolk P/Invoke 桥（运行期加载 Tolk.dll，缺失即降级）。",
            Il2CppTemplates.Tolk(rootNamespace));

        if (fieldHooks > 0)
        {
            Write("A11y/A11yFieldReader.cs", ArtifactKind.Source,
                "IL2CPP 原生字段读取器：按字段名沿继承链查找，读 string 字段或 TMP/UGUI 组件的 text 属性（不写死 offset）。",
                Il2CppTemplates.FieldReader(rootNamespace));
        }

        for (var i = 0; i < hooks.Count; i++)
        {
            var hook = hooks[i];
            var name = "Hooks/Hook_" + (i + 1).ToString("000", CultureInfo.InvariantCulture) + ".cs";
            if (hook.Fields is not null)
            {
                Write(name, ArtifactKind.Source, "字段兜底补丁：" + hook.Target.Display,
                    Il2CppTemplates.FieldHook(i + 1, rootNamespace, hook.Target, hook.Fields));
            }
            else
            {
                Write(name, ArtifactKind.Source, "补丁：" + hook.Target.Display,
                    Il2CppTemplates.Hook(i + 1, rootNamespace, hook.Target, hook.Parameters, hook.TextParameterName, hook.Kind));
            }
        }

        var readmePoints = hooks.Select((h, i) => new PatchPoint
        {
            Id = "hook-" + (i + 1).ToString("000", CultureInfo.InvariantCulture),
            Kind = PatchPointKind.Hook,
            Target = h.Target.Target,
            Confidence = 1.0,
            Description = h.Fields is null
                ? h.Target.Display + "（" + h.Kind + "）"
                : h.Target.Display + "（字段兜底：" + string.Join("/", h.Fields.Where(f => f.Kind != Il2CppFieldKind.Unsupported).Select(f => f.Name)) + "）",
        }).ToList();

        Write("README.md", ArtifactKind.Documentation, "构建/安装/验证步骤与已知限制。",
            Il2CppTemplates.Readme(slug, guid, context.GameDirectory, layout.InteropAssemblyHash ?? string.Empty,
                layout.BestSource.ToString(), readmePoints, unverified ?? []));

        Write("build.ps1", ArtifactKind.Script, "一条命令构建（-GameDir 指向已装 BepInEx 6 的游戏目录）。",
            Il2CppTemplates.BuildScript());

        return new GenerationResult
        {
            ProjectDirectory = projectDirectory,
            Artifacts = artifacts,
            Metadata = metadata,
        };
    }

    /// <summary>字段兜底要求声明类型能写进 typeof(...)，且至少有一个当前版本能读的字段。</summary>
    private static bool CanGenerateFieldHook(Il2CppTarget target, IReadOnlyList<Il2CppTextField> fields)
    {
        var type = target.TypeFullName;
        if (type.Contains('`') || type.Contains('<') || type.Contains('/'))
        {
            return false;
        }

        return Il2CppFieldFallback.HasSupportedField(fields);
    }

    private static bool IsSpeakerChannel(Il2CppTarget target)
    {
        var name = target.MethodName;
        return name.Contains("author", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("speaker", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("NameText", StringComparison.Ordinal);
    }

    private static string ToPascal(string slug)
    {
        var builder = new StringBuilder(slug.Length);
        var upper = true;
        foreach (var ch in slug)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }

        return builder.Length == 0 ? "Game" : builder.ToString();
    }
}
