using System.Globalization;
using System.Text;
using System.Text.Json;
using GalA11yForge.Core;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Generators.Internal;
using GalA11yForge.Generators.Templates;

namespace GalA11yForge.Generators;

/// <summary>
/// Unity Mono（BepInEx 5 / HarmonyX）补丁工程生成器。
///
/// <para>
/// 硬性边界：只写 <c>outputDirectory</c> 内部；不写游戏目录；不引用/生成任何游戏资源；
/// 钩子目标只能来自 <see cref="PatchPoint.Target"/>，解析不了就返回 <c>E_ARG_INVALID</c>，
/// 绝不猜测类名。所有输入相同则输出字节相同（没有时间戳、没有随机 GUID）。
/// </para>
/// </summary>
public sealed class UnityMonoGenerator : ICodeGenerator
{
    public const string EngineId = "unity_mono";

    public const string GeneratorName = "unity_mono";

    private const int MaxFrameworkSearchDepth = 8;

    private readonly IVoiceBridgeSource? _bridgeSource;
    private readonly GeneratorOptions _options;

    public UnityMonoGenerator()
        : this(null, null)
    {
    }

    /// <param name="bridgeSource">语音桥来源；测试可注入，默认从仓库 Framework 目录读取。</param>
    /// <param name="options">生成参数覆盖（命名空间/标题/版本/GUID/Framework 目录）。</param>
    public UnityMonoGenerator(IVoiceBridgeSource? bridgeSource, GeneratorOptions? options = null)
    {
        _bridgeSource = bridgeSource;
        _options = options ?? new GeneratorOptions();
    }

    public Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ForgeResult<GenerationResult> result;
        try
        {
            result = Generate(context, plan, outputDirectory, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = Failure(GeneratorErrors.Io(
                "生成已取消。",
                "重新执行 generate；已写入的文件由 forge.manifest.json 记录，重复生成会覆盖而不是叠加。"));
        }
        catch (ForgePathException ex)
        {
            result = Failure(ex.Error);
        }
        catch (IOException ex)
        {
            result = Failure(GeneratorErrors.Io(
                "写盘失败：" + HookTargetParser.Preview(ex.Message, 200),
                "检查磁盘空间与输出目录权限；确认输出目录不在游戏目录里。"));
        }
        catch (UnauthorizedAccessException ex)
        {
            result = Failure(GeneratorErrors.Io(
                "没有写权限：" + HookTargetParser.Preview(ex.Message, 200),
                "换一个工作区目录，或用有写权限的账户运行。"));
        }
        catch (Exception ex)
        {
            result = Failure(GeneratorErrors.Internal(
                "生成器内部错误：" + ex.GetType().Name + "：" + HookTargetParser.Preview(ex.Message, 200),
                "这是生成器的 bug；请把 plan.json 与这条消息一起反馈，不要手工改生成物来绕过。"));
        }

        return Task.FromResult(result);
    }

    private ForgeResult<GenerationResult> Generate(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!GeneratorSettings.TryCreate(context, plan, _options, out var settings, out var error))
        {
            return Failure(error!);
        }

        if (!TryResolveOutputDirectory(context, settings!, outputDirectory, out var outputFull, out error))
        {
            return Failure(error!);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryLoadPreviousManifest(outputFull, settings!, out var previous, out error))
        {
            return Failure(error!);
        }

        if (!TryReadVoiceBridge(out var bridgeFiles, out error))
        {
            return Failure(error!);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var fingerprint = DeterministicIds.FrameworkFingerprint(bridgeFiles);

        if (!TryBuildFiles(settings!, plan!, bridgeFiles, fingerprint, out var files, out error))
        {
            return Failure(error!);
        }

        var result = new GenerationResult
        {
            ProjectDirectory = outputFull,
            Artifacts = files
                .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
                .Select(f => f.ToArtifact())
                .ToList(),
            Metadata = BuildMetadata(settings!, files, context.DryRun, fingerprint),
        };

        if (context.DryRun)
        {
            return ForgeResult<GenerationResult>.Success(result);
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteFiles(outputFull, files, previous);
        return ForgeResult<GenerationResult>.Success(result);
    }

    private static ForgeResult<GenerationResult> Failure(ForgeError error)
        => ForgeResult<GenerationResult>.Failure(error);

    // ---------------------------------------------------------------- 输出目录

    private static bool TryResolveOutputDirectory(
        GameContext context,
        GeneratorSettings settings,
        string outputDirectory,
        out string outputFull,
        out ForgeError? error)
    {
        outputFull = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            error = GeneratorErrors.Argument(
                "输出目录为空：生成阶段必须给出一个工程目录。",
                "用 --out 指定工作区里的目录，例如 %LOCALAPPDATA%\\GalA11yForge\\work\\<任务id>\\project。");
            return false;
        }

        string root;
        try
        {
            root = PathSafety.NormalizeRoot(context.WorkspaceRoot);
        }
        catch (ForgePathException ex)
        {
            error = ex.Error;
            return false;
        }

        try
        {
            var trimmed = outputDirectory.Trim();
            string candidate;
            if (Path.IsPathFullyQualified(trimmed))
            {
                candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            }
            else
            {
                candidate = PathSafety.ResolveUnder(root, trimmed);
            }

            // 先拦游戏目录，再拦工作区越界；顺序由 PathSafety 保证。
            candidate = PathSafety.EnsureWriteAllowed(root, candidate, settings.GamePath);

            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                error = GeneratorErrors.Argument(
                    "输出目录不能是工作区根目录本身：生成器会在里面建文件，混在工作区里无法清理。",
                    "改成工作区下的子目录，例如 work\\<任务id>\\project。");
                return false;
            }

            if (File.Exists(candidate))
            {
                error = GeneratorErrors.Argument(
                    $"输出路径已存在同名文件：「{HookTargetParser.Preview(candidate, 120)}」。",
                    "删掉这个文件，或换一个目录名。");
                return false;
            }

            outputFull = candidate;
            return true;
        }
        catch (ForgePathException ex)
        {
            error = ex.Error;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = GeneratorErrors.Argument(
                "输出目录不合法：" + HookTargetParser.Preview(ex.Message, 160),
                "路径里不要有引号、竖线、通配符；相对路径不要包含 ..。");
            return false;
        }
    }

    /// <summary>
    /// 输出目录已有内容时：只接受本生成器写过的目录（有 forge.manifest.json），否则拒绝覆盖用户文件。
    /// </summary>
    private static bool TryLoadPreviousManifest(
        string outputFull,
        GeneratorSettings settings,
        out GeneratorManifest? previous,
        out ForgeError? error)
    {
        previous = null;
        error = null;

        if (!Directory.Exists(outputFull))
        {
            return true;
        }

        var entries = Directory.EnumerateFileSystemEntries(outputFull).ToList();
        if (entries.Count == 0)
        {
            return true;
        }

        var manifestPath = Path.Combine(outputFull, GeneratorManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            error = GeneratorErrors.Argument(
                $"输出目录已有 {entries.Count} 个条目，且没有 {GeneratorManifest.FileName}：为避免覆盖你的文件，生成器拒绝写入。",
                "换一个空目录；如果这是旧的生成物且已不需要，手动清空或移走整个目录后重试。");
            return false;
        }

        GeneratorManifest? manifest;
        try
        {
            manifest = GeneratorManifest.TryRead(manifestPath);
        }
        catch (JsonException ex)
        {
            error = GeneratorErrors.ArtifactInvalid(
                "既有的 forge.manifest.json 不是有效 JSON：" + HookTargetParser.Preview(ex.Message, 160),
                "把整个输出目录移走或清空，再重新生成；不要手工改 manifest。");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = GeneratorErrors.Io(
                "读取既有 manifest 失败：" + HookTargetParser.Preview(ex.Message, 160),
                "检查文件权限；确认没有别的进程占用这个目录。");
            return false;
        }

        if (manifest is null)
        {
            error = GeneratorErrors.ArtifactInvalid(
                "既有的 forge.manifest.json 读不出来（内容为空或不是 JSON 对象）。",
                "把整个输出目录移走或清空，再重新生成。");
            return false;
        }

        if (manifest.SchemaVersion != GeneratorManifest.CurrentSchemaVersion)
        {
            error = GeneratorErrors.ArtifactInvalid(
                $"既有 manifest 的 schemaVersion 是 {manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)}，本生成器只认 {GeneratorManifest.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}。",
                "用同版本的生成器重新生成，或清空输出目录。");
            return false;
        }

        if (!string.Equals(manifest.Generator, GeneratorName, StringComparison.Ordinal))
        {
            error = GeneratorErrors.ArtifactInvalid(
                $"输出目录是其它生成器（{HookTargetParser.Preview(manifest.Generator)}）的工程，拒绝覆盖。",
                "换一个目录。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(manifest.GameSlug)
            && !string.Equals(manifest.GameSlug, settings.GameSlug, StringComparison.OrdinalIgnoreCase))
        {
            error = GeneratorErrors.ArtifactInvalid(
                $"输出目录属于游戏 slug「{manifest.GameSlug}」，与本次「{settings.GameSlug}」不同。",
                "换一个目录，避免两个游戏的文件混在一起。");
            return false;
        }

        var fileEntries = manifest.Files ?? new List<ManifestFileEntry>();
        foreach (var entry in fileEntries)
        {
            if (entry is null)
            {
                error = GeneratorErrors.ArtifactInvalid(
                    "既有 manifest 的 files 里有 null 项。",
                    "清空输出目录后重新生成。");
                return false;
            }

            try
            {
                PathSafety.ValidateRelative(entry.Path);
            }
            catch (ForgePathException ex)
            {
                error = GeneratorErrors.ArtifactInvalid(
                    $"既有 manifest 里的路径「{HookTargetParser.Preview(entry.Path)}」不安全：{ex.Error.Message}",
                    "清空输出目录后重新生成；不要手工改 manifest。");
                return false;
            }
        }

        previous = manifest;
        return true;
    }

    // ---------------------------------------------------------------- 语音桥

    private bool TryReadVoiceBridge(out IReadOnlyList<VoiceBridgeFile> files, out ForgeError? error)
    {
        files = Array.Empty<VoiceBridgeFile>();
        error = null;

        IVoiceBridgeSource source;
        if (_bridgeSource is not null)
        {
            source = _bridgeSource;
        }
        else
        {
            var frameworkDirectory = ResolveFrameworkDirectory(_options.FrameworkDirectory);
            if (frameworkDirectory is null)
            {
                error = GeneratorErrors.DependencyMissing(
                    "找不到 Framework 目录（需要其中的 platform/*.cs 作为内嵌语音桥）。",
                    "设置环境变量 GALA11Y_FRAMEWORK_DIR 指向仓库的 Framework 目录，或在 GeneratorOptions.FrameworkDirectory 里显式指定。");
                return false;
            }

            source = new FrameworkVoiceBridgeSource(frameworkDirectory);
        }

        IReadOnlyList<VoiceBridgeFile> read;
        try
        {
            read = source.ReadFiles();
        }
        catch (DirectoryNotFoundException ex)
        {
            error = GeneratorErrors.DependencyMissing(
                "语音桥目录不存在：" + HookTargetParser.Preview(ex.Message, 200),
                "确认 Framework 目录没被移动；或用 GALA11Y_FRAMEWORK_DIR 指向正确位置。");
            return false;
        }
        catch (FileNotFoundException ex)
        {
            error = GeneratorErrors.DependencyMissing(
                "语音桥缺少文件：" + HookTargetParser.Preview(ex.Message, 200),
                "确认 Framework 的 platform/*.cs 与 contract/A11yHost.cs 齐全。");
            return false;
        }
        catch (InvalidDataException ex)
        {
            error = GeneratorErrors.DependencyMissing(
                "语音桥源码不完整：" + HookTargetParser.Preview(ex.Message, 300),
                "确认 Framework 目录完整（tools/l1-files.txt 与 platform/*.cs 一致），不要混用不同版本。");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = GeneratorErrors.Io(
                "读取语音桥失败：" + HookTargetParser.Preview(ex.Message, 200),
                "检查文件权限；确认 Framework 目录可读。");
            return false;
        }

        if (read is null || read.Count == 0)
        {
            error = GeneratorErrors.DependencyMissing(
                "语音桥没有返回任何文件。",
                "用 GALA11Y_FRAMEWORK_DIR 指向含 platform/Speech.cs 的 Framework 目录。");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in read)
        {
            if (file is null)
            {
                error = GeneratorErrors.ArtifactInvalid(
                    "语音桥里有 null 文件项。",
                    "这是语音桥来源的 bug；检查 IVoiceBridgeSource 的实现。");
                return false;
            }

            string relative;
            try
            {
                relative = PathSafety.ValidateRelative(file.RelativePath);
            }
            catch (ForgePathException ex)
            {
                error = GeneratorErrors.ArtifactInvalid(
                    $"语音桥文件路径不合法：{ex.Error.Message}",
                    "语音桥文件只能放在输出目录的 Platform/ 下。");
                return false;
            }

            if (!relative.StartsWith("Platform/", StringComparison.Ordinal))
            {
                error = GeneratorErrors.ArtifactInvalid(
                    $"语音桥文件必须放在 Platform/ 下，实际是「{HookTargetParser.Preview(relative)}」。",
                    "检查语音桥来源的相对路径。");
                return false;
            }

            if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = GeneratorErrors.ArtifactInvalid(
                    $"语音桥只接受 .cs 文件，实际是「{HookTargetParser.Preview(relative)}」。",
                    "不要往语音桥里塞配置文件或二进制。");
                return false;
            }

            if (!seen.Add(relative))
            {
                error = GeneratorErrors.ArtifactInvalid(
                    $"语音桥文件路径重复：「{relative}」。",
                    "同一路径会被后写的覆盖，必须先去重。");
                return false;
            }
        }

        if (!seen.Contains("Platform/Speech.cs") || !seen.Contains("Platform/A11yHost.cs"))
        {
            error = GeneratorErrors.DependencyMissing(
                "语音桥至少需要 Platform/Speech.cs 与 Platform/A11yHost.cs。",
                "确认 Framework/platform 与 contract 文件齐全后再生成。");
            return false;
        }

        files = read.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
        return true;
    }

    private static string? ResolveFrameworkDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return TryNormalizeFrameworkDirectory(configured);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("GALA11Y_FRAMEWORK_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var resolved = TryNormalizeFrameworkDirectory(fromEnvironment);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var fromBase = TryFindFrameworkUpwards(AppContext.BaseDirectory);
        if (fromBase is not null)
        {
            return fromBase;
        }

        return TryFindFrameworkUpwards(Environment.CurrentDirectory);
    }

    private static string? TryNormalizeFrameworkDirectory(string directory)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
            return LooksLikeFramework(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool LooksLikeFramework(string directory)
        => File.Exists(Path.Combine(directory, "platform", "Speech.cs"))
            && File.Exists(Path.Combine(directory, "contract", "A11yHost.cs"));

    private static string? TryFindFrameworkUpwards(string start)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(start));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (var depth = 0; depth < MaxFrameworkSearchDepth && current is not null; depth++)
        {
            var candidate = Path.Combine(current.FullName, "Framework");
            if (LooksLikeFramework(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool TryRewriteBridgeNamespace(
        string text,
        string ns,
        string relativePath,
        out string rewritten,
        out ForgeError? error)
    {
        rewritten = text;
        error = null;

        if (!text.Contains("namespace A11yFramework", StringComparison.Ordinal))
        {
            error = GeneratorErrors.ArtifactInvalid(
                $"语音桥文件「{relativePath}」里找不到「namespace A11yFramework」，不能安全替换命名空间。",
                "确认 Framework 的 platform/*.cs 属于 A11yFramework 命名空间；不要混用改过的副本。");
            return false;
        }

        rewritten = TextFileCodec.ToCrLf(text
            .Replace("A11yFramework.", ns + ".", StringComparison.Ordinal)
            .Replace("namespace A11yFramework", "namespace " + ns, StringComparison.Ordinal)
            .Replace("using A11yFramework;", "using " + ns + ";", StringComparison.Ordinal));
        return true;
    }

    // ---------------------------------------------------------------- 渲染

    private static bool TryBuildFiles(
        GeneratorSettings settings,
        PatchPlan plan,
        IReadOnlyList<VoiceBridgeFile> bridge,
        string fingerprint,
        out List<GeneratedFile> files,
        out ForgeError? error)
    {
        files = new List<GeneratedFile>();
        error = null;

        var tokens = CreateTokens(settings, fingerprint);

        if (!TryAddRendered(files, "Plugin.cs", ArtifactKind.Source,
                "插件入口：配置、语音桥初始化、钩子挂载、重读键", PluginTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        if (!TryAddRendered(files, settings.AssemblyName + ".csproj", ArtifactKind.Project,
                "插件工程（netstandard2.1，BepInEx 5 / HarmonyX）", CsprojTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        foreach (var hook in settings.Hooks)
        {
            var hookTokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal)
            {
                ["POINT_ID"] = hook.Point.Id.Trim(),
                ["TARGET_DISPLAY"] = hook.Target.Display,
                ["TARGET_CS"] = GeneratorSettings.EscapeCSharpString(hook.Target.AccessToolsTarget),
                ["PARAMS"] = hook.Target.ParameterArrayExpression,
                ["CLASS"] = hook.ClassName,
                ["CANDIDATE_COMMENT"] = CandidateComment(hook.Point.CandidateId),
                ["POSTFIX_SIGNATURE"] = hook.ChoiceList
                    ? "object[] __args, object __instance"
                    : hook.TextParameter is not null
                        ? "string " + hook.TextParameter
                        : hook.TextPropertyChain.Count > 0 || hook.InstanceTextFields.Count > 0
                            ? "object __instance"
                            : string.Empty,
                ["POSTFIX_BODY"] = BuildPostfixBody(hook),
            };

            if (!TryAddRendered(files, hook.FilePath, ArtifactKind.Source,
                    "Hook：" + hook.Target.Display, HookTemplate.Text, hookTokens, false, out error))
            {
                return false;
            }
        }

        if (!TryAddRendered(files, "Platform/SpeechGate.cs", ArtifactKind.Source,
                "朗读闸门：去重、空白归一，主钩子朗读/辅助钩子只进缓冲", SpeechGateTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        if (settings.Hooks.Any(h => h.ChoiceList) &&
            !TryAddRendered(files, "Platform/ChoiceReader.cs", ArtifactKind.Source,
                "选项朗读与数字键选择通道（选项期间游戏禁用鼠标，必须由补丁代选）", ChoiceReaderTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        foreach (var source in bridge)
        {
            if (!TryRewriteBridgeNamespace(source.Text, settings.Namespace, source.RelativePath, out var rewritten, out error))
            {
                return false;
            }

            if (!TryAddText(files, source.RelativePath, ArtifactKind.Source,
                    "内嵌语音桥（" + source.SourceId + "）", rewritten, source.Utf8Bom, out error))
            {
                return false;
            }
        }

        var profile = BuildProfile(settings, plan, fingerprint);
        if (!TryAddText(files, "profile.json", ArtifactKind.Config,
                "生成结果清单：钩子、跳过的补丁点、待人工计数", TextFileCodec.ToCrLf(GeneratedJson.Serialize(profile)), false, out error))
        {
            return false;
        }

        if (!TryAddRendered(files, "build.ps1", ArtifactKind.Script,
                "只编译、不写游戏目录的构建脚本", BuildScriptTemplate.Text, tokens, true, out error))
        {
            return false;
        }

        if (!TryAddRendered(files, "README.md", ArtifactKind.Documentation,
                "工程说明与能力边界（说人话）", ReadmeTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        if (!TryAddRendered(files, "安装说明.md", ArtifactKind.Documentation,
                "手动安装步骤", InstallGuideTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        if (!TryAddRendered(files, "卸载说明.md", ArtifactKind.Documentation,
                "卸载步骤（不动游戏本体）", UninstallGuideTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        if (!TryAddRendered(files, "NOTICE.md", ArtifactKind.Documentation,
                "第三方来源与许可", NoticeTemplate.Text, tokens, false, out error))
        {
            return false;
        }

        files.Add(BuildManifest(settings, bridge, fingerprint, files));
        return true;
    }

    private static bool TryAddRendered(
        List<GeneratedFile> list,
        string relativePath,
        ArtifactKind kind,
        string description,
        string template,
        IReadOnlyDictionary<string, string> tokens,
        bool utf8Bom,
        out ForgeError? error)
    {
        // 模板源码里是 LF，统一成 CRLF 再渲染；token 值里也可能带换行，渲染后再统一一次。
        var text = TextFileCodec.ToCrLf(TemplateRenderer.Render(TextFileCodec.ToCrLf(template), tokens));
        return TryAddText(list, relativePath, kind, description, text, utf8Bom, out error);
    }

    private static bool TryAddText(
        List<GeneratedFile> list,
        string relativePath,
        ArtifactKind kind,
        string description,
        string text,
        bool utf8Bom,
        out ForgeError? error)
    {
        error = null;

        var problems = ArtifactTextValidator.Validate(relativePath, text);
        if (problems.Count > 0)
        {
            error = GeneratorErrors.ArtifactInvalid(
                $"生成物 {relativePath} 校验失败：" + string.Join("；", problems),
                "这是模板或输入数据的问题：确认 plan.json 的 target/id 里没有塞台词、说明或占位符残留。");
            return false;
        }

        list.Add(new GeneratedFile(relativePath, kind, description, TextFileCodec.Encode(text, utf8Bom)));
        return true;
    }

    private static Dictionary<string, string> CreateTokens(GeneratorSettings settings, string fingerprint)
        => new(StringComparer.Ordinal)
        {
            ["GENERATOR_VERSION"] = ForgeInfo.Version,
            ["NS"] = settings.Namespace,
            ["ASSEMBLY"] = settings.AssemblyName,
            ["VERSION"] = settings.PluginVersion,
            ["GUID"] = settings.PluginGuid,
            ["DISPLAY"] = settings.DisplayName,
            ["DISPLAY_CS"] = settings.DisplayNameCs,
            ["GAME_NAME"] = settings.GameName,
            ["SLUG"] = settings.GameSlug,
            ["DATADIR"] = settings.DataDirectory,
            ["GAME_PATH_XML"] = EscapeXml(settings.GamePath),
            ["GAME_PATH_PS"] = settings.GamePath.Replace("'", "''", StringComparison.Ordinal),
            ["FRAMEWORK_FINGERPRINT"] = fingerprint,
            ["HOOK_COUNT"] = settings.Hooks.Count.ToString(CultureInfo.InvariantCulture),
            ["SKIPPED_COUNT"] = settings.Skipped.Count.ToString(CultureInfo.InvariantCulture),
            ["PATCH_POINT_TABLE"] = BuildPatchPointTable(settings),
            ["GATE_UPDATE"] = "            SpeechGate.Update();\n            SpeechGate.TickWatcher();",
            ["WATCHER_INIT"] = "            SpeechGate.ConfigureWatcher(Config);",
            ["CHOICE_UPDATE"] = BuildChoiceUpdate(settings),
        };

    /// <summary>
    /// 插件 Update 里的选项选择通道：只有 plan 里出现「选项列表」钩子时才注入。
    /// 选项期间游戏自己禁了鼠标，所以补丁必须用数字键代选。
    /// </summary>
    private static string BuildChoiceUpdate(GeneratorSettings settings)
    {
        if (!settings.Hooks.Any(h => h.ChoiceList))
        {
            return string.Empty;
        }

        return """
                    try
                    {
                        ChoiceReader.Update();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("[选项] 选择通道出错：" + ex.Message);
                    }
        """;
    }

    private static string BuildPatchPointTable(GeneratorSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append("| 补丁点 | 目标 | 状态 |").Append('\n');
        builder.Append("|---|---|---|").Append('\n');

        foreach (var hook in settings.Hooks)
        {
            var status = hook.ChoiceList
                ? "已接线（选项列表 → ChoiceReader + 数字键选择）"
                : hook.TextParameter is null && hook.TextPropertyChain.Count > 0
                    ? "已接线（读实例属性链兜底）"
                    : hook.TextParameter is null && hook.InstanceTextFields.Count > 0
                        ? "已接线（读实例文本字段兜底）"
                    : hook.TextParameter is null
                        ? "已生成骨架（无文本参数，仅计数）"
                        : hook.AutoSpeak
                        ? "已接线朗读（主通道 → SpeechGate.Say）"
                        : "已接线（只进朗读缓冲，防重复）";
            builder.Append("| ").Append(MarkdownCell(hook.Point.Id.Trim()))
                .Append(" | ").Append(MarkdownCell(hook.Target.Display))
                .Append(" | ").Append(status).Append(" |").Append('\n');
        }

        foreach (var skipped in settings.Skipped)
        {
            builder.Append("| ").Append(MarkdownCell(skipped.Id))
                .Append(" | — | 未实现（").Append(GeneratorSettings.KindToSnake(skipped.Kind))
                .Append("），需人工 |").Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string MarkdownCell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string CandidateComment(string? candidateId)
    {
        var id = candidateId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > 80)
        {
            return string.Empty;
        }

        var valid = id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
        return valid ? "// 候选：" + id + "（对应分析阶段 textCandidate.id）\n" : string.Empty;
    }

    /// <summary>
    /// 按补丁点证据里的字符串参数生成 Postfix 方法体：主钩子朗读，其余只进重读缓冲；
    /// 没有字符串参数就只记命中。文本参数名来自重编译证据，不猜游戏字段。
    /// </summary>
    private static string BuildPostfixBody(HookSetting hook)
    {
        if (hook.ChoiceList)
        {
            return "            ChoiceReader.OnChoice(__args, __instance);";
        }

        if (hook.TextParameter is null && hook.TextPropertyChain.Count > 0)
        {
            var chain = string.Join(", ", hook.TextPropertyChain.Select(f => "\"" + f + "\""));
            var call = hook.AutoSpeak
                ? "SpeechGate.SayPropertyChain(__instance, new[] { " + chain + " });"
                : "SpeechGate.NotePropertyChain(__instance, new[] { " + chain + " });";
            return "            A11yHost.Diag(\"[Hook:" + hook.Point.Id.Trim() +
                   "] 命中 \" + Target + \"；按实例属性链兜底朗读。\");" + "\n            " + call;
        }

        if (hook.TextParameter is null && hook.InstanceTextFields.Count > 0)
        {
            var fields = string.Join(", ", hook.InstanceTextFields.Select(f => "\"" + f + "\""));
            var call = hook.AutoSpeak
                ? "SpeechGate.SayFields(__instance, new[] { " + fields + " });"
                : "SpeechGate.NoteFields(__instance, new[] { " + fields + " });";
            return "            A11yHost.Diag(\"[Hook:" + hook.Point.Id.Trim() +
                   "] 命中 \" + Target + \"；按实例文本字段兜底朗读。\");" + "\n            " + call;
        }

        if (hook.TextParameter is null)
        {
            return "            A11yHost.Diag(\"[Hook:" + hook.Point.Id.Trim() +
                   "] 命中 \" + Target + \"；该目标没有可提取的文本参数，只能做调用计数。\");";
        }

        return hook.AutoSpeak
            ? "            SpeechGate.Say(" + hook.TextParameter + ");"
            : "            SpeechGate.Note(" + hook.TextParameter + ");";
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

    // ---------------------------------------------------------------- profile / manifest

    private static GeneratedProfile BuildProfile(GeneratorSettings settings, PatchPlan plan, string fingerprint)
        => new()
        {
            GeneratorVersion = ForgeInfo.Version,
            GameSlug = settings.GameSlug,
            GameName = settings.GameName,
            Title = settings.Title,
            Namespace = settings.Namespace,
            AssemblyName = settings.AssemblyName,
            PluginGuid = settings.PluginGuid,
            PluginVersion = settings.PluginVersion,
            FrameworkFingerprint = fingerprint,
            Hooks = settings.Hooks
                .Select(h => new ProfileHookEntry
                {
                    Id = h.Point.Id.Trim(),
                    Target = h.Target.Display,
                    File = h.FilePath,
                    Confidence = h.Point.Confidence,
                    Status = h.ChoiceList
                        ? "choices"
                        : h.AutoSpeak
                            ? "speaks"
                            : h.TextParameter is not null || h.TextPropertyChain.Count > 0 || h.InstanceTextFields.Count > 0
                                ? "buffered"
                                : "skeleton_unwired",
                })
                .ToList(),
            SkippedPatchPoints = settings.Skipped
                .Select(s => new ProfileSkippedEntry
                {
                    Id = s.Id,
                    Kind = GeneratorSettings.KindToSnake(s.Kind),
                })
                .ToList(),
            ManualTodo = new ProfileTodoSummary
            {
                Count = plan.ManualTodo?.Count ?? 0,
                Blocking = plan.ManualTodo?.Count(t => t is not null && t.Blocking) ?? 0,
            },
            Risks = new ProfileRiskSummary
            {
                Count = plan.Risks?.Count ?? 0,
                // 风险没有「阻塞」概念，这里用高严重度条数，便于上层判断要不要人工确认。
                Blocking = plan.Risks?.Count(r => r is not null && r.Severity == RiskSeverity.High) ?? 0,
            },
            Verification = new List<string>
            {
                "启动游戏后，BepInEx 日志里应出现「已挂载」与每个钩子的命中记录；没有命中就不能算生效。",
                "对话时应能听到朗读；日志里应出现 `[Speech] ...` 行。只出现 `[Hook:...] 命中` 而没有 `[Speech]`，说明这个目标没有可提取的文本参数。",
                "按「=」应重读上一段文本；改键位置在 BepInEx\\config\\<GUID>.cfg 的「快捷键 → 重读」。",
                "日志里应打印语音后端（NVDA / Tolk / 系统语音）；为空说明本机语音库不可用，按 NOTICE.md 自查。",
                "若日志提示目标不在当前游戏程序集里，说明游戏版本与分析证据不一致，需要重新分析生成。",
                "自动朗读只覆盖「分析证据里带字符串参数」的钩子；选项、菜单、图片与打字机中间态仍需人工或后续版本。",
            },
        };

    private static GeneratedFile BuildManifest(
        GeneratorSettings settings,
        IReadOnlyList<VoiceBridgeFile> bridge,
        string fingerprint,
        IReadOnlyList<GeneratedFile> filesWithoutManifest)
    {
        var manifest = new GeneratorManifest
        {
            GeneratorVersion = ForgeInfo.Version,
            FrameworkFingerprint = fingerprint,
            GameSlug = settings.GameSlug,
            Namespace = settings.Namespace,
            PluginGuid = settings.PluginGuid,
            PluginVersion = settings.PluginVersion,
            Engine = settings.Engine,
            Files = filesWithoutManifest
                .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
                .Select(f => new ManifestFileEntry
                {
                    Path = f.RelativePath,
                    Sha256 = DeterministicIds.Sha256Hex(f.Content),
                    Kind = f.Kind,
                })
                .ToList(),
            Sources = bridge
                .OrderBy(f => f.SourceId, StringComparer.Ordinal)
                .Select(f => new ManifestSourceEntry { Id = f.SourceId, Sha256 = f.SourceSha256 })
                .ToList(),
        };

        var json = TextFileCodec.ToCrLf(GeneratedJson.Serialize(manifest));
        return new GeneratedFile(
            GeneratorManifest.FileName,
            ArtifactKind.Other,
            "生成器写盘账本：本次写了哪些文件、各自 SHA-256，重新生成时据此清理旧文件",
            TextFileCodec.Utf8NoBom(json));
    }

    // ---------------------------------------------------------------- 写盘

    private static void WriteFiles(string outputFull, IReadOnlyList<GeneratedFile> files, GeneratorManifest? previous)
    {
        Directory.CreateDirectory(outputFull);

        if (previous is not null)
        {
            var current = new HashSet<string>(files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in previous.Files ?? new List<ManifestFileEntry>())
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || current.Contains(entry.Path))
                {
                    continue;
                }

                string stale;
                try
                {
                    stale = PathSafety.ResolveUnder(outputFull, entry.Path);
                }
                catch (ForgePathException)
                {
                    // 旧 manifest 里的路径不安全：宁可不删，也不越界。
                    continue;
                }

                if (File.Exists(stale))
                {
                    File.Delete(stale);
                }
            }
        }

        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var full = PathSafety.ResolveUnder(outputFull, file.RelativePath);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(full, file.Content);
        }
    }

    private static Dictionary<string, string> BuildMetadata(
        GeneratorSettings settings,
        IReadOnlyList<GeneratedFile> files,
        bool dryRun,
        string fingerprint)
        => new(StringComparer.Ordinal)
        {
            ["generator"] = GeneratorName,
            ["generatorVersion"] = ForgeInfo.Version,
            ["engine"] = settings.Engine,
            ["namespace"] = settings.Namespace,
            ["pluginGuid"] = settings.PluginGuid,
            ["version"] = settings.PluginVersion,
            ["gameSlug"] = settings.GameSlug,
            ["hookCount"] = settings.Hooks.Count.ToString(CultureInfo.InvariantCulture),
            ["skippedPatchPointCount"] = settings.Skipped.Count.ToString(CultureInfo.InvariantCulture),
            ["fileCount"] = files.Count.ToString(CultureInfo.InvariantCulture),
            ["dryRun"] = dryRun ? "true" : "false",
            ["frameworkFingerprint"] = fingerprint,
            ["manifest"] = GeneratorManifest.FileName,
        };
}
