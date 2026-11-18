using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using GalA11yForge.Core;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Generators.Internal;

/// <summary>一个已确认可以生成的 Hook 补丁点。</summary>
internal sealed class HookSetting
{
    public required PatchPoint Point { get; init; }

    public required HookTarget Target { get; init; }

    public required string ClassName { get; init; }

    public required string FilePath { get; init; }

    /// <summary>从补丁点证据里的完整 Cecil 签名解析出的字符串参数名（没有则为 null）。</summary>
    public string? TextParameter { get; init; }

    /// <summary>
    /// 没有字符串参数时，候选证据给出的实例文本字段（TMP_Text/UI.Text 组件字段名）。
    /// 生成器会生成 Postfix(object __instance) 反射读字段兜底朗读；只复制字段名，不读台词原文。
    /// </summary>
    public IReadOnlyList<string> InstanceTextFields { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 0.2.48：没有字符串参数、也没有文本组件字段时，候选证据给出的实例属性链
    /// （如 Utage 的 <c>Text.OriginalText</c>）。生成器会生成反射读取链路的补丁。
    /// </summary>
    public IReadOnlyList<string> TextPropertyChain { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 0.2.83：证据里完整 Cecil 签名的形参类型全名（如 <c>System.String</c>、
    /// <c>UnityEngine.Events.UnityAction</c>）。目标文本本身带参数类型时为空（走旧的
    /// AccessTools + typeof 路径）；不带时生成器改走运行期 MethodResolver，按名字挑唯一重载，
    /// 避免 Harmony 对同名重载抛 AmbiguousMatchException 导致整个钩子挂不上。
    /// </summary>
    public IReadOnlyList<string> ParameterTypes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 是否为「选项列表」钩子：Cecil 签名带 ChoiceAsset、且没有字符串参数。
    /// 这类钩子不能只计数——选项界面会禁用鼠标，必须由补丁自己朗读并用数字键选择。
    /// </summary>
    public bool ChoiceList { get; init; }

    /// <summary>是否作为主朗读钩子（只允许一个：优先第一个带字符串参数的钩子，其次带实例文本字段的钩子）。</summary>
    public bool AutoSpeak { get; set; }
}

/// <summary>本生成器不实现、原样交回人工的补丁点。</summary>
internal sealed class SkippedSetting
{
    public required string Id { get; init; }

    public required PatchPointKind Kind { get; init; }
}

/// <summary>
/// 校验并归一化生成输入。所有「缺必填」「目标不合法」都在这里变成稳定错误码，
/// 后面的渲染阶段就可以假设输入是干净的。
/// </summary>
internal sealed partial class GeneratorSettings
{
    private const string DefaultVersion = ForgeInfo.Version;
    private const int MaxTitleLength = 120;
    private const int MaxVersionPart = 65534;

    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[0-9]{1,5}(\\.[0-9]{1,5}){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();

    // 证据文本里的形状：「完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::AnimateText(...)」
    [GeneratedRegex(@"完整签名（Cecil）：\s*\[[^\]]+\]\s*[^\(\)]+\((?<args>[^\(\)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex CecilSignaturePattern();

    // 证据文本里的形状：「类型文本字段：mainText/mainName。」（无 string 参数时的兜底朗读来源）
    [GeneratedRegex(@"类型文本字段：(?<fields>[^。\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex TextFieldPattern();

    // 证据文本里的形状：「文本属性链：Text.OriginalText。」（没有 string 参数时的属性链读取来源）
    [GeneratedRegex(@"文本属性链：(?<chain>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyChainPattern();

    // 0.2.83：Cecil 形参类型名只允许这些字符（全名、泛型实参、数组、byref、嵌套类型分隔符）。
    // 出现别的字符（引号、反斜杠、台词、中文…）就整体放弃，退回旧解析路径。
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.`+<>\[\],&/]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterTypePattern();

    public string GameSlug { get; private init; } = string.Empty;

    public string Namespace { get; private init; } = string.Empty;

    public string AssemblyName { get; private init; } = string.Empty;

    public string PluginGuid { get; private init; } = string.Empty;

    public string PluginVersion { get; private init; } = string.Empty;

    public string DisplayName { get; private init; } = string.Empty;

    public string DisplayNameCs { get; private init; } = string.Empty;

    public string GameName { get; private init; } = string.Empty;

    public string GamePath { get; private init; } = string.Empty;

    public string DataDirectory { get; private init; } = string.Empty;

    public string Engine { get; private init; } = "unity_mono";

    public string Title { get; private init; } = string.Empty;

    public List<HookSetting> Hooks { get; private init; } = new();

    public List<SkippedSetting> Skipped { get; private init; } = new();

    public static bool TryCreate(
        GameContext? context,
        PatchPlan? plan,
        GeneratorOptions? options,
        out GeneratorSettings? settings,
        out ForgeError? error)
    {
        settings = null;
        error = null;

        if (plan is null)
        {
            error = GeneratorErrors.Argument(
                "plan 为空：生成阶段需要 02-decide/plan.json 的补丁点与目标信息。",
                "先运行决定阶段产出 plan.json，再执行 generate；不要用空对象占位。");
            return false;
        }

        if (context is null)
        {
            error = GeneratorErrors.Argument(
                "缺少流水线上下文（游戏目录/工作区）。",
                "用 CLI 重新执行 generate，让流水线注入上下文。");
            return false;
        }

        var slug = string.IsNullOrWhiteSpace(plan.GameSlug) ? context.Slug : plan.GameSlug;
        slug = slug?.Trim() ?? string.Empty;
        if (slug.Length == 0)
        {
            error = GeneratorErrors.Argument(
                "缺少游戏 slug：plan.gameSlug 与 context.slug 都是空的。",
                "在 plan.json 里填写 gameSlug，或用 --slug 指定一个只含小写字母/数字/点/下划线/连字符的短名。");
            return false;
        }

        if (!PathSafety.IsValidSlug(slug))
        {
            error = GeneratorErrors.Argument(
                $"游戏 slug 不合法：「{HookTargetParser.Preview(slug)}」。",
                "只允许小写字母、数字、点、下划线、连字符，长度 1–64，且不能包含 ..");
            return false;
        }

        var route = plan.Route;
        if (route == PlanRoute.Unsupported)
        {
            error = GeneratorErrors.EngineUnsupported(
                "plan 的路线是 unsupported（分析阶段判定为不可支持）。",
                "先补齐分析证据，把 plan.json 的 route 改成 semi_automatic 或 automatic，再生成。");
            return false;
        }

        var engine = string.IsNullOrWhiteSpace(plan.Target?.Engine) ? "unknown" : plan.Target!.Engine.Trim();
        if (!string.Equals(engine, "unity_mono", StringComparison.OrdinalIgnoreCase))
        {
            error = GeneratorErrors.EngineUnsupported(
                $"引擎「{HookTargetParser.Preview(engine)}」不在本生成器的支持范围（当前只有 unity_mono）。",
                "Unity Mono 请把 plan.target.engine 填成 unity_mono；其它引擎等对应生成器实现后再生成。");
            return false;
        }

        var gamePath = string.IsNullOrWhiteSpace(plan.Target?.GamePath)
            ? context.GameDirectory
            : plan.Target!.GamePath!;
        gamePath = gamePath?.Trim() ?? string.Empty;
        if (gamePath.Length == 0)
        {
            error = GeneratorErrors.Argument(
                "缺少游戏安装目录：plan.target.gamePath 与 context.gameDirectory 都是空的。",
                "在 plan.json 里填写目标游戏路径，或用 --game 指定。");
            return false;
        }

        string normalizedGamePath;
        try
        {
            normalizedGamePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = GeneratorErrors.Argument(
                $"游戏路径不合法：「{HookTargetParser.Preview(gamePath)}」。",
                "检查路径里是否有多余字符（引号、竖线、通配符）。");
            return false;
        }

        var gameName = SanitizeName(Path.GetFileName(normalizedGamePath));
        if (gameName.Length == 0)
        {
            error = GeneratorErrors.Argument(
                "无法从游戏路径推断游戏名（路径看起来指向磁盘根目录）。",
                "把 plan.target.gamePath 指向游戏安装目录本身，例如 ...\\steamapps\\common\\游戏名。");
            return false;
        }

        if (plan.PatchPoints is null || plan.PatchPoints.Count == 0)
        {
            error = GeneratorErrors.Argument(
                "plan 里没有任何补丁点：生成空工程只会产出无法朗读的壳。",
                "让分析阶段至少给出一个 Hook 补丁点（附反编译证据），再重新生成。");
            return false;
        }

        if (!TryBuildHooks(plan.PatchPoints, out var hooks, out var skipped, out error))
        {
            return false;
        }

        var ns = string.IsNullOrWhiteSpace(options?.Namespace)
            ? DeterministicIds.DefaultNamespace(slug)
            : options!.Namespace!.Trim();
        if (!NamespacePattern().IsMatch(ns))
        {
            error = GeneratorErrors.Argument(
                $"命名空间不合法：「{HookTargetParser.Preview(ns)}」。",
                "只允许字母/数字/下划线组成的段，段之间用点分隔，不能以数字开头。");
            return false;
        }

        if (ns.Split('.').Any(ReservedKeywords.Contains))
        {
            error = GeneratorErrors.Argument(
                $"命名空间里含有 C# 关键字段：「{HookTargetParser.Preview(ns)}」。",
                "换一个命名空间段，例如把 class 改成 classes。");
            return false;
        }

        var version = string.IsNullOrWhiteSpace(options?.Version) ? DefaultVersion : options!.Version!.Trim();
        if (!IsValidVersion(version))
        {
            error = GeneratorErrors.Argument(
                $"版本号不合法：「{HookTargetParser.Preview(version)}」。",
                "版本号用 2–4 段纯数字（例如 0.1.0.0），每段不超过 65534。");
            return false;
        }

        var title = string.IsNullOrWhiteSpace(options?.Title) ? gameName + " 无障碍补丁" : options!.Title!.Trim();
        if (!IsValidTitle(title))
        {
            error = GeneratorErrors.Argument(
                $"标题不合法：「{HookTargetParser.Preview(title)}」。",
                $"标题 1–{MaxTitleLength} 字，不能含换行/控制字符，也不能含 {{{{ }}}} 占位符片段。");
            return false;
        }

        var guid = string.Empty;
        if (!string.IsNullOrWhiteSpace(options?.PluginGuid))
        {
            if (!Guid.TryParse(options!.PluginGuid, out var parsed))
            {
                error = GeneratorErrors.Argument(
                    $"插件 GUID 不合法：「{HookTargetParser.Preview(options.PluginGuid)}」。",
                    "用标准 GUID 文本（例如 8-4-4-4-12 位十六进制）；留空则由 slug 确定性派生。");
                return false;
            }

            guid = parsed.ToString("D");
        }
        else
        {
            guid = DeterministicIds.PluginGuid(slug, ns);
        }

        settings = new GeneratorSettings
        {
            GameSlug = slug,
            Namespace = ns,
            AssemblyName = DeterministicIds.AssemblyNameFromNamespace(ns),
            PluginGuid = guid,
            PluginVersion = version,
            DisplayName = title,
            DisplayNameCs = EscapeCSharpString(title),
            Title = title,
            GameName = gameName,
            GamePath = normalizedGamePath,
            DataDirectory = ResolveDataDirectory(normalizedGamePath, gameName),
            Engine = "unity_mono",
            Hooks = hooks,
            Skipped = skipped,
        };
        return true;
    }

    private static string ResolveDataDirectory(string gamePath, string gameName)
    {
        var preferred = gameName + "_Data";
        if (Directory.Exists(Path.Combine(gamePath, preferred)))
        {
            return preferred;
        }

        try
        {
            var candidates = Directory.GetDirectories(gamePath, "*_Data");
            if (candidates.Length == 1)
            {
                return Path.GetFileName(candidates[0]);
            }

            var withManaged = candidates
                .Where(candidate => Directory.Exists(Path.Combine(candidate, "Managed")))
                .ToArray();
            if (withManaged.Length == 1)
            {
                return Path.GetFileName(withManaged[0]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录不可读时退回约定名；构建阶段若仍找不到会用 MSBuild 错误提示。
        }

        return preferred;
    }

    private static bool TryBuildHooks(
        List<PatchPoint> points,
        out List<HookSetting> hooks,
        out List<SkippedSetting> skipped,
        out ForgeError? error)
    {
        hooks = new List<HookSetting>();
        skipped = new List<SkippedSetting>();
        error = null;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point is null)
            {
                error = GeneratorErrors.Argument(
                    $"plan.patchPoints 第 {i + 1} 项是 null。",
                    "删掉空项或补上完整补丁点（id/kind/target/confidence）。");
                return false;
            }

            var id = point.Id?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                error = GeneratorErrors.Argument(
                    $"plan.patchPoints 第 {i + 1} 项缺少 id。",
                    "每个补丁点都要有稳定 id（字母/数字/点/下划线/连字符），写进生成的类名与文档。");
                return false;
            }

            if (!IsValidPointId(id))
            {
                error = GeneratorErrors.Argument(
                    $"补丁点 id 不合法：「{HookTargetParser.Preview(id)}」。",
                    "id 只允许字母/数字/点/下划线/连字符，长度 1–64，不能以点结尾，也不能包含 ..");
                return false;
            }

            if (!seenIds.Add(id))
            {
                error = GeneratorErrors.Argument(
                    $"补丁点 id 重复：「{id}」。",
                    "同一个 plan 里的补丁点 id 必须唯一（不区分大小写），否则生成文件会互相覆盖。");
                return false;
            }

            if (point.Kind != PatchPointKind.Hook)
            {
                skipped.Add(new SkippedSetting { Id = id, Kind = point.Kind });
                continue;
            }

            if (!HookTargetParser.TryParse(point.Target, out var target, out var why))
            {
                error = GeneratorErrors.Argument(
                    $"补丁点「{id}」的钩子目标不合法：{why}",
                    "Unity Mono 的目标必须来自反编译证据，写成「命名空间.类型:方法」；不要写猜测的类名或台词。");
                return false;
            }

            var className = DeterministicIds.TypeNameFromId(id);
            if (!seenClasses.Add(className))
            {
                className = className + "_" + (hooks.Count + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
                if (!seenClasses.Add(className))
                {
                    error = GeneratorErrors.Argument(
                        $"补丁点「{id}」转成类名后与其它补丁点冲突，且加后缀仍冲突。",
                        "把 id 改得更可区分（例如加模块前缀）后重新生成。");
                    return false;
                }
            }

            hooks.Add(new HookSetting
            {
                Point = point,
                Target = target!,
                ClassName = className,
                FilePath = "Hooks/Hook_" + className + ".cs",
                TextParameter = TryFindTextParameter(point),
                InstanceTextFields = TryFindInstanceTextFields(point),
                TextPropertyChain = TryFindPropertyChain(point),
                ParameterTypes = TryFindParameterTypes(point),
                ChoiceList = IsChoiceListPoint(point),
            });
        }

        if (hooks.Count == 0)
        {
            error = GeneratorErrors.Argument(
                "plan 里没有任何 kind=hook 的补丁点：本生成器只生成 Harmony 钩子骨架，其它类型暂不实现。",
                "补上 Hook 补丁点，或等配置/资源类生成器；不要期望生成器凭空编造钩子。");
            return false;
        }

        // 主朗读出口只设一个：优先属性链钩子（已知正文入口，如 Utage 的 Text.OriginalText），
        // 其次字符串参数钩子，最后实例文本字段钩子。
        var primary = hooks.FirstOrDefault(h => h.TextPropertyChain.Count > 0)
            ?? hooks.FirstOrDefault(h => h.TextParameter is not null)
            ?? hooks.FirstOrDefault(h => h.InstanceTextFields.Count > 0);
        if (primary is not null)
        {
            primary.AutoSpeak = true;
        }

        return true;
    }

    /// <summary>
    /// 判断是不是「选项列表」钩子：证据里的完整 Cecil 签名含 <c>ChoiceAsset</c> 参数，
    /// 且没有 <c>System.String</c> 参数。只认证据，不按方法名猜。
    /// </summary>
    private static bool IsChoiceListPoint(PatchPoint point)
    {
        var description = point.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var match = CecilSignaturePattern().Match(description);
        if (!match.Success)
        {
            return false;
        }

        var args = match.Groups["args"].Value;
        if (args.Contains("System.String", StringComparison.Ordinal) ||
            args.Contains(" string ", StringComparison.Ordinal))
        {
            return false;
        }

        return args.Contains("ChoiceAsset", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从补丁点证据文本里的「完整签名（Cecil）：[...] 返回类型 类型::方法(参数...)」解析出
    /// 第一个 <c>System.String</c> 参数的参数名。解析不出来就返回 null（退化为只记命中）。
    /// </summary>
    private static string? TryFindTextParameter(PatchPoint point)
    {
        var description = point.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = CecilSignaturePattern().Match(description);
        if (!match.Success)
        {
            return null;
        }

        var args = match.Groups["args"].Value;
        if (args.Trim().Length == 0)
        {
            return null;
        }

        foreach (var raw in SplitTopLevel(args, ','))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var space = token.LastIndexOf(' ');
            if (space <= 0 || space == token.Length - 1)
            {
                continue;
            }

            var parameterType = token[..space].Trim();
            var parameterName = token[(space + 1)..].Trim();
            if (parameterType is not ("System.String" or "string"))
            {
                continue;
            }

            if (IdentifierPattern().IsMatch(parameterName) && !ReservedKeywords.Contains(parameterName))
            {
                return parameterName;
            }
        }

        return null;
    }

    /// <summary>
    /// 0.2.83：从「完整签名（Cecil）」里解析出全部形参类型全名（不含参数名）。
    /// Harmony 的 <c>AccessTools.Method(目标, null)</c> 在目标有同名重载时会抛
    /// AmbiguousMatchException，整个钩子挂不上；生成器改把这些类型名交给运行期
    /// MethodResolver 挑唯一重载。任何一段解析不出来就整体返回空（退回旧路径，不猜）。
    /// </summary>
    private static IReadOnlyList<string> TryFindParameterTypes(PatchPoint point)
    {
        const int maxParameters = 16;
        const int maxTypeNameLength = 200;

        var description = point.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            return Array.Empty<string>();
        }

        var match = CecilSignaturePattern().Match(description);
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        var args = match.Groups["args"].Value;
        if (args.Trim().Length == 0)
        {
            return Array.Empty<string>();
        }

        var types = new List<string>();
        foreach (var raw in SplitTopLevel(args, ','))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                return Array.Empty<string>();
            }

            var space = token.LastIndexOf(' ');
            if (space <= 0 || space == token.Length - 1)
            {
                return Array.Empty<string>();
            }

            var parameterType = token[..space].Trim();
            if (parameterType.Length is 0 or > maxTypeNameLength || !ParameterTypePattern().IsMatch(parameterType))
            {
                return Array.Empty<string>();
            }

            types.Add(parameterType);
            if (types.Count > maxParameters)
            {
                return Array.Empty<string>();
            }
        }

        return types;
    }

    /// <summary>
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

    /// <summary>
    /// 从补丁点说明里的「文本属性链：Text.OriginalText。」解析属性/字段链（只保留合法标识符段）。
    /// 0.2.48：Utage 这类框架的正文入口没有 string 形参，文本在实例属性链上。
    /// </summary>
    private static IReadOnlyList<string> TryFindPropertyChain(PatchPoint point)
    {
        var description = point.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            return Array.Empty<string>();
        }

        var match = PropertyChainPattern().Match(description);
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        var chain = new List<string>();
        foreach (var raw in match.Groups["chain"].Value.Split('.'))
        {
            var name = raw.Trim();
            if (name.Length == 0 || !IdentifierPattern().IsMatch(name) || ReservedKeywords.Contains(name))
            {
                return Array.Empty<string>();
            }

            chain.Add(name);
        }

        return chain.Count == 0 ? Array.Empty<string>() : chain;
    }

    /// <summary>按顶层分隔符切分（忽略泛型 &lt;&gt; 里的逗号）。</summary>
    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var angleDepth = 0;
        var bracketDepth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '<')
            {
                angleDepth++;
            }
            else if (ch == '>')
            {
                if (angleDepth > 0)
                {
                    angleDepth--;
                }
            }
            else if (ch == '[')
            {
                // 0.2.83：多维数组类型（System.String[,]）的逗号不算顶层分隔符。
                bracketDepth++;
            }
            else if (ch == ']')
            {
                if (bracketDepth > 0)
                {
                    bracketDepth--;
                }
            }
            else if (ch == separator && angleDepth == 0 && bracketDepth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static bool IsValidPointId(string id)
    {
        if (id.Length is 0 or > 64 || id[0] is '.' or '-' || id[^1] == '.')
        {
            return false;
        }

        if (id.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    private static bool IsValidVersion(string version)
    {
        if (!VersionPattern().IsMatch(version))
        {
            return false;
        }

        return version.Split('.').All(part => int.TryParse(part, out var value) && value <= MaxVersionPart);
    }

    private static bool IsValidTitle(string title)
    {
        if (title.Length is 0 or > MaxTitleLength)
        {
            return false;
        }

        if (title.Contains("{{", StringComparison.Ordinal) || title.Contains("}}", StringComparison.Ordinal))
        {
            return false;
        }

        return title.All(ch => !char.IsControl(ch));
    }

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            var invalid = char.IsControl(ch) || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';
            builder.Append(invalid ? '_' : ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>只处理 C# 字符串字面量需要的转义；中文原样保留（源文件是 UTF-8）。</summary>
    public static string EscapeCSharpString(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    public static string KindToSnake(PatchPointKind kind) => kind switch
    {
        PatchPointKind.Hook => "hook",
        PatchPointKind.Config => "config",
        PatchPointKind.Script => "script",
        PatchPointKind.Asset => "asset",
        PatchPointKind.Injection => "injection",
        _ => "other",
    };
}
