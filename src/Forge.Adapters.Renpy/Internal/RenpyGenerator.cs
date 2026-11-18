using System.Globalization;
using System.Text;
using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>
/// 步骤四：把 9 个模板渲染到工作区目录，并生成 a11y_manifest.json / deploy_plan.json。
/// 只写 outputDirectory；不写游戏目录，不覆盖已存在的同名覆盖层文件。
/// </summary>
internal sealed class RenpyGenerator
{
    private readonly Func<DateTimeOffset> _clock;

    public RenpyGenerator(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (plan is null)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArgumentInvalid, "补丁方案为空，无法生成。", "先运行 plan 生成 PatchPlan。"));
        }

        if (plan.Route == PlanRoute.Unsupported)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "补丁方案标记为不支持，拒绝生成。",
                "检查 detect/analyze 结果，或换用支持的引擎。"));
        }

        if (plan.Target is null
            || (!string.IsNullOrWhiteSpace(plan.Target.Engine)
                && !string.Equals(plan.Target.Engine, RenpyAdapter.EngineId, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "补丁方案的目标引擎不是 Ren'Py，无法用本适配器生成。",
                "使用与 plan.target.engine 匹配的适配器。"));
        }

        if (string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "工作区根目录为空，无法确定可写位置。",
                "用 --workspace 指定一个可写目录。"));
        }

        if (string.IsNullOrWhiteSpace(context.GameDirectory) || !Directory.Exists(context.GameDirectory))
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{context.GameDirectory}",
                "确认游戏仍安装在原路径；生成需要读取引擎版本与目录信息。"));
        }

        var gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
        var facts = RenpyInspector.Inspect(gameRoot);
        if (!facts.IsRenpy)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "游戏目录缺少 renpy/ 或 game/，无法生成 Ren'Py 覆盖层。",
                "确认 --game 指向游戏根目录。"));
        }

        RenpyGenerationSettings settings;
        try
        {
            settings = RenpyGenerationSettings.From(context.Profile);
        }
        catch (RenpyOptionException ex)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArgumentInvalid, ex.Message, ex.Suggestion));
        }

        string requested = string.IsNullOrWhiteSpace(outputDirectory)
            ? RenpyPaths.DefaultGeneratedDirectory(context)
            : outputDirectory;

        string outputRoot;
        try
        {
            var full = Path.GetFullPath(requested);
            outputRoot = PathSafety.EnsureWriteAllowed(context.WorkspaceRoot, full, gameRoot);
        }
        catch (ForgePathException ex)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(ex.Error));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"输出目录不是合法路径：{requested}",
                "使用工作区内的绝对路径，例如 <workspace>/games/<slug>/generated。"));
        }

        var now = _clock();
        var tokens = BuildTokens(facts, plan, settings, now);

        List<RenderedFile> rendered;
        try
        {
            rendered = RenderAll(tokens);
        }
        catch (RenpyTemplateMissingException ex)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArtifactMissing,
                ex.Message,
                "重新构建 Forge.Adapters.Renpy，确认 Templates/ 下 11 个模板已作为 EmbeddedResource 打进程序集。"));
        }
        catch (RenpyTemplateTokenException ex)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.ArtifactInvalid,
                ex.Message,
                "模板与生成器版本不匹配；检查 Templates/ 中未定义的 @@TOKEN@@。"));
        }

        // 先整体预检，再落盘：避免写到一半才发现同名文件冲突。
        // 允许覆盖“上一次本工具生成、且未被改动”的文件（哈希与旧清单一致），
        // 这样重复 generate 是幂等的；用户改过的文件仍然拒绝覆盖。
        var priorHashes = ReadPriorManifestHashes(outputRoot);
        var skipped = 0;
        var overwritten = 0;
        var overwriteAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in rendered)
            {
                var target = TargetPath(outputRoot, file.File.RelativePath);
                if (Directory.Exists(target))
                {
                    return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                        ForgeErrorCodes.ArtifactInvalid,
                        $"输出位置被目录占用：{target}",
                        "换一个输出目录，或先移除同名目录。"));
                }

                if (!File.Exists(target))
                {
                    continue;
                }

                var existing = File.ReadAllBytes(target);
                if (existing.AsSpan().SequenceEqual(file.Bytes))
                {
                    skipped++;
                    continue;
                }

                var existingHash = RenpySupport.Sha256Hex(existing);
                if (priorHashes.TryGetValue(file.File.RelativePath, out var priorHash)
                    && string.Equals(priorHash, existingHash, StringComparison.OrdinalIgnoreCase))
                {
                    overwriteAllowed.Add(file.File.RelativePath);
                    overwritten++;
                    continue;
                }

                return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                    ForgeErrorCodes.ArtifactInvalid,
                    $"拒绝覆盖已存在且被改动过的文件：{target}",
                    "输出目录里已有非本工具生成的内容；换一个 --out 目录，或先备份/移走该文件。"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Io,
                $"读取输出目录失败：{ex.Message}",
                "检查目录权限与磁盘空间。"));
        }

        try
        {
            Directory.CreateDirectory(outputRoot);
            foreach (var file in rendered)
            {
                var target = TargetPath(outputRoot, file.File.RelativePath);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (File.Exists(target) && !overwriteAllowed.Contains(file.File.RelativePath))
                {
                    continue;
                }

                File.WriteAllBytes(target, file.Bytes);
            }

            var manifest = BuildManifest(facts, settings, now, outputRoot);
            var planJson = BuildDeployPlan(facts, manifest);
            WriteText(
                Path.Combine(outputRoot, RenpyOverlayCatalog.ManifestFileName),
                RenpyManifestJson.Serialize(manifest));
            WriteText(
                Path.Combine(outputRoot, RenpyOverlayCatalog.DeployPlanFileName),
                RenpyManifestJson.Serialize(planJson));

            var result = new GenerationResult
            {
                ProjectDirectory = outputRoot,
                Artifacts = RenpyOverlayCatalog.CreateArtifacts().ToList(),
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["generator"] = manifest.Generator,
                    ["generatorVersion"] = RenpyAdapter.AdapterVersion,
                    ["patchVersion"] = settings.PatchVersion,
                    ["engineVersion"] = facts.EngineVersion ?? "unknown",
                    ["pythonMajor"] = facts.PythonMajor.ToString(CultureInfo.InvariantCulture),
                    ["isSteam"] = facts.IsSteam.ToString().ToLowerInvariant(),
                    ["conflictOverlay"] = facts.HasExistingOverlay.ToString().ToLowerInvariant(),
                    ["createdAt"] = manifest.CreatedAt,
                    ["templateSource"] = "embedded",
                    ["manifest"] = Path.Combine(outputRoot, RenpyOverlayCatalog.ManifestFileName),
                    ["deployPlan"] = Path.Combine(outputRoot, RenpyOverlayCatalog.DeployPlanFileName),
                    ["skippedExisting"] = skipped.ToString(CultureInfo.InvariantCulture),
                    ["overwrittenExisting"] = overwritten.ToString(CultureInfo.InvariantCulture),
                },
            };

            return Task.FromResult(ForgeResult<GenerationResult>.Success(result));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ForgeResult<GenerationResult>.Failure(
                ForgeErrorCodes.Io,
                $"写入生成物失败：{ex.Message}",
                "检查输出目录权限、磁盘空间，以及是否被其他程序占用。"));
        }
    }

    /// <summary>读取上一次生成的清单，得到“本工具产物”的哈希白名单；没有/损坏则返回空表。</summary>
    private static Dictionary<string, string> ReadPriorManifestHashes(string outputRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(outputRoot, RenpyOverlayCatalog.ManifestFileName);
            if (!File.Exists(path))
            {
                return map;
            }

            var manifest = RenpyManifestJson.Deserialize<RenpyOverlayManifest>(File.ReadAllText(path));
            if (manifest is null
                || !string.Equals(manifest.Schema, "gala11y.renpy.overlay/1", StringComparison.Ordinal))
            {
                return map;
            }

            foreach (var file in manifest.Files)
            {
                if (!string.IsNullOrWhiteSpace(file.Path) && !string.IsNullOrWhiteSpace(file.Sha256))
                {
                    map[file.Path] = file.Sha256;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            map.Clear();
        }

        return map;
    }

    private static List<RenderedFile> RenderAll(IReadOnlyDictionary<string, string> tokens)
    {
        var list = new List<RenderedFile>();
        foreach (var file in RenpyOverlayCatalog.ContentFiles)
        {
            var template = RenpyTemplateRenderer.Load(file.EmbeddedName);
            var text = RenpyTemplateRenderer.Render(template, tokens, file.EmbeddedName);
            var encoding = file.Utf8Bom ? RenpySupport.Utf8Bom : RenpySupport.Utf8NoBom;
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            list.Add(new RenderedFile(file, bytes));
        }

        return list;
    }

    private static Dictionary<string, string> BuildTokens(
        RenpyGameFacts facts,
        PatchPlan plan,
        RenpyGenerationSettings settings,
        DateTimeOffset now)
    {
        var gameName = RenpySupport.EscapePythonString(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(facts.Root)));
        var engineVersion = RenpySupport.EscapePythonString(
            facts.EngineVersion ?? plan.Target.EngineVersion ?? "未知");
        var pythonMajor = facts.PythonMajor > 0
            ? facts.PythonMajor.ToString(CultureInfo.InvariantCulture)
            : "0";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GAME_NAME"] = gameName,
            ["RENPY_VERSION"] = engineVersion,
            ["PY_MAJOR"] = pythonMajor,
            ["PATCH_VERSION"] = settings.PatchVersion,
            ["TTS_PREF"] = settings.TtsPreference,
            ["SAPI_RATE"] = settings.SapiRate.ToString(CultureInfo.InvariantCulture),
            ["DATE"] = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["BUILD"] = $"gala11y-forge-renpy {RenpyAdapter.AdapterVersion} ({now:yyyy-MM-dd})",
        };
    }

    private static RenpyOverlayManifest BuildManifest(
        RenpyGameFacts facts,
        RenpyGenerationSettings settings,
        DateTimeOffset now,
        string outputRoot)
    {
        var manifest = new RenpyOverlayManifest
        {
            PatchVersion = settings.PatchVersion,
            CreatedAt = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            Game = new RenpyManifestGame
            {
                Root = facts.Root,
                Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(facts.Root)),
                Engine = RenpyAdapter.EngineId,
                PythonMajor = facts.PythonMajor,
                IsSteam = facts.IsSteam,
            },
        };

        foreach (var file in RenpyOverlayCatalog.ContentFiles)
        {
            var target = TargetPath(outputRoot, file.RelativePath);
            var bytes = File.ReadAllBytes(target);
            manifest.Files.Add(new RenpyManifestFile
            {
                Path = file.RelativePath,
                Role = file.Role,
                Bytes = bytes.LongLength,
                Sha256 = RenpySupport.Sha256Hex(bytes),
            });
        }

        return manifest;
    }

    private static RenpyDeployPlan BuildDeployPlan(RenpyGameFacts facts, RenpyOverlayManifest manifest)
    {
        var plan = new RenpyDeployPlan
        {
            GameRoot = facts.Root,
            Guard = "游戏目录按只读处理：本生成器不写游戏目录。请把生成目录内容复制到游戏根目录；"
                    + "若已有 a11y_* 补丁，先按旧 a11y_manifest.json 卸载，避免混用两套覆盖层。",
            ConflictOverlay = facts.HasExistingOverlay,
            Steps =
            {
                new RenpyDeployStep
                {
                    Op = "mkdir",
                    Path = RenpyOverlayCatalog.PlatformDirectory,
                },
            },
            Verify =
            {
                "离线静态检查：forge verify（校验 11 个文件、manifest SHA256 与 .rpy 结构，不启动游戏）",
                "人工真机：启动游戏，确认游戏根目录 a11y_speech.log 出现「覆盖层已加载」",
                "人工真机：NVDA 下检查对话、旁白、选项、主菜单、存读档，以及 F5/F8/F9/F10/F11",
                "卸载演练：在游戏副本上运行 uninstall.ps1，确认只删除清单内且哈希一致的文件，"
                    + "并清掉 game/a11y_platform 的 .rpyc 与空目录",
            },
        };

        foreach (var file in manifest.Files)
        {
            plan.Steps.Add(new RenpyDeployStep
            {
                Op = "copy",
                Source = file.Path,
                Target = file.Path,
                Sha256 = file.Sha256,
            });
        }

        return plan;
    }

    private static string TargetPath(string root, string relativePath)
    {
        var relative = PathSafety.ValidateRelative(relativePath);
        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathSafety.IsUnder(root, combined))
        {
            throw new ForgePathException(ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"生成物路径越界：{relative}",
                "这是适配器内部错误，请报告。"));
        }

        return combined;
    }

    private static void WriteText(string path, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllBytes(path, RenpySupport.Utf8NoBom.GetPreamble()
            .Concat(RenpySupport.Utf8NoBom.GetBytes(normalized))
            .ToArray());
    }

    private sealed record RenderedFile(RenpyTemplateFile File, byte[] Bytes);
}
